// Collect exact-version notices for installed and source-map-identified packages.
// Package archives stay in the ignored release cache; only notices are shipped.
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const word = path.join(root, 'web/word');
const cache = path.join(root, '.release-work/npm-notices');
const output = path.join(root, 'packaging/notices/npm');
await mkdir(cache, { recursive: true });
await mkdir(output, { recursive: true });
const packages = new Map();
function add(name, version, source) {
  if (!name || !version) throw new Error('Unidentified dependency: ' + source);
  const key = name + '@' + version;
  const entry = packages.get(key) || { name, version, sources: [] };
  if (!entry.sources.includes(source)) entry.sources.push(source);
  packages.set(key, entry);
}
const lock = JSON.parse(await readFile(path.join(word, 'package-lock.json'), 'utf8'));
for (const [location, entry] of Object.entries(lock.packages)) {
  if (location) add(location.split('node_modules/').at(-1), entry.version, 'package-lock.json');
}
const map = JSON.parse(await readFile(path.join(word, 'node_modules/superdoc/dist/superdoc.min.js.map'), 'utf8'));
for (const source of map.sources) {
  if (!source.includes('node_modules/')) continue;
  const encoded = source.match(/\.pnpm\/([^/]+)\/node_modules\//)?.[1];
  if (!encoded) throw new Error('Unidentified bundled dependency: ' + source);
  const match = encoded.match(/^(@?[^@]+)@(\d[^_]*)/);
  if (!match) throw new Error('Unidentified bundled version: ' + encoded);
  add(match[1].replace('+', '/'), match[2], 'superdoc.min.js.map');
}
async function download(url) {
  for (let attempt = 0; attempt < 3; attempt++) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(60000) });
      if (!response.ok) throw new Error('HTTP ' + response.status + ': ' + url);
      return Buffer.from(await response.arrayBuffer());
    } catch (error) { if (attempt === 2) throw error; }
  }
}
async function cached(file, url) {
  try { return await readFile(file); }
  catch (error) {
    if (error.code !== 'ENOENT') throw error;
    const data = await download(url);
    await writeFile(file, data);
    return data;
  }
}
function tar(args) {
  const result = spawnSync('tar.exe', args, { maxBuffer: 16 * 1024 * 1024, windowsHide: true });
  if (result.error || result.status !== 0) throw result.error || new Error(result.stderr.toString());
  return result.stdout.toString('utf8');
}
const manifest = [];
const queue = [...packages.values()].sort((a, b) => (a.name + a.version).localeCompare(b.name + b.version));
await Promise.all(Array.from({ length: 6 }, async () => {
  while (queue.length) {
    const entry = queue.shift();
    const id = entry.name.replace('/', '+') + '@' + entry.version;
    const metadata = JSON.parse(await cached(path.join(cache, id + '.json'),
      'https://registry.npmjs.org/' + encodeURIComponent(entry.name) + '/' + entry.version));
    if (metadata.name !== entry.name || metadata.version !== entry.version) throw new Error('Metadata mismatch: ' + id);
    const archive = path.join(cache, id + '.tgz');
    const data = await cached(archive, metadata.dist.tarball);
    const integrity = metadata.dist.integrity;
    if (integrity) {
      const supported = integrity.split(/\s+/).filter(item => /^(sha512|sha384|sha256|sha1)-/.test(item));
      if (!supported.length || !supported.some(item => {
        const split = item.indexOf('-');
        return createHash(item.slice(0, split)).update(data).digest('base64') === item.slice(split + 1);
      })) throw new Error('Integrity mismatch: ' + id);
    } else if (createHash('sha1').update(data).digest('hex') !== metadata.dist.shasum) {
      throw new Error('Checksum mismatch: ' + id);
    }
    const files = tar(['-tzf', archive]).split(/\r?\n/).filter(Boolean);
    const licenses = files.filter(file => /(?:^|\/)(?:licen[cs]e|copying|copyright|notice)(?:[._-][^/]*)?$/i.test(file));
    const selected = licenses.length ? licenses : files.filter(file => /(?:^|\/)readme(?:\.[^/]*)?$/i.test(file));
    const upstreamNotices = [];
    if (!licenses.length) {
      const repositoryUrl = typeof metadata.repository === 'string' ? metadata.repository : metadata.repository?.url;
      const repository = repositoryUrl?.match(/github\.com[/:]([^/]+\/[^/#]+?)(?:\.git)?$/)?.[1]
        || (entry.name.startsWith('@hocuspocus/') ? 'ueberdosis/hocuspocus' : null);
      if (!repository || !/^[a-f0-9]{40}$/i.test(metadata.gitHead || '')) throw new Error('Missing pinned license repository: ' + id);
      for (const name of ['LICENSE', 'LICENSE.md', 'LICENSE.txt', 'LICENSE-MIT', 'LICENCE']) {
        const url = 'https://raw.githubusercontent.com/' + repository + '/' + metadata.gitHead + '/' + name;
        const response = await fetch(url, { signal: AbortSignal.timeout(60000) });
        if (response.status === 404) continue;
        if (!response.ok) throw new Error('HTTP ' + response.status + ': ' + url);
        const text = await response.text();
        upstreamNotices.push({ url, sha256: createHash('sha256').update(text).digest('hex'), text });
        break;
      }
      if (!upstreamNotices.length) throw new Error('Missing upstream license text: ' + id);
    }
    const chunks = ['Package: ' + entry.name + '@' + entry.version,
      'License metadata: ' + JSON.stringify(metadata.license || metadata.licenses || null),
      'Registry: https://www.npmjs.com/package/' + entry.name + '/v/' + entry.version,
      'Archive SHA-256: ' + createHash('sha256').update(data).digest('hex'),
      'Included because: ' + entry.sources.join(', '), ''];
    for (const file of selected.sort()) {
      if (file.startsWith('/') || file.includes(':') || file.split('/').includes('..') || file.includes('\\')) throw new Error('Unsafe archive path: ' + file);
      chunks.push('--- ' + file + ' ---', tar(['-xOzf', archive, file]), '');
    }
    for (const notice of upstreamNotices) chunks.push('--- ' + notice.url + ' ---', notice.text, '');
    await writeFile(path.join(output, id + '.txt'), chunks.join('\n'));
    manifest.push({ ...entry, license: metadata.license || metadata.licenses || null,
      integrity: metadata.dist.integrity || metadata.dist.shasum, licenseFiles: licenses, noticeFiles: selected,
      upstreamNotices: upstreamNotices.map(({ text, ...notice }) => notice) });
  }
}));
manifest.sort((a, b) => (a.name + a.version).localeCompare(b.name + b.version));
await writeFile(path.join(output, 'manifest.json'), JSON.stringify({
  description: 'Exact-version npm license texts, including dependencies embedded in the SuperDoc bundle.',
  packages: manifest
}, null, 2) + '\n');
console.log('Collected notices for ' + manifest.length + ' exact npm package versions.');
const missing = manifest.filter(entry => !entry.licenseFiles.length);
console.log('Packages using pinned upstream license: ' + missing.map(entry => entry.name + '@' + entry.version).join(', '));
console.log('License identifiers: ' + [...new Set(manifest.map(entry => JSON.stringify(entry.license)))].join(', '));
