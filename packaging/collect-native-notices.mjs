// Release notice collection only; does not execute any application or tests.
import { readFile, writeFile, mkdir, readdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { homedir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.join(root, 'packaging/notices');
const cache = path.join(root, '.release-work/native-notices');
await mkdir(cache, { recursive: true });
const manifest = [];
async function download(url) {
  const response = await fetch(url, { signal: AbortSignal.timeout(60000) });
  if (!response.ok) throw new Error('HTTP ' + response.status + ': ' + url);
  return Buffer.from(await response.arrayBuffer());
}
async function save(relative, data, source) {
  const file = path.join(output, relative);
  await mkdir(path.dirname(file), { recursive: true });
  await writeFile(file, data);
  manifest.push({ file: relative.replaceAll('\\', '/'), source, sha256: createHash('sha256').update(data).digest('hex') });
}
const registry = path.join(homedir(), '.cargo/registry/src');
const registryPath = path.join(registry, (await readdir(registry)).find(name => name.startsWith('index.crates.io-')));
const repositories = {
  'oar-ocr-core-0.9.2': 'greatv/oar-ocr', 'oar-ocr-derive-0.9.2': 'greatv/oar-ocr',
  'profiling-1.0.18': 'aclysma/profiling', 'profiling-procmacros-1.0.18': 'aclysma/profiling',
  'pulp-wasm-simd-flag-0.1.1': 'sarah-quinones/pulp', 'simd_helpers-0.1.0': 'lu-zero/simd_helpers',
  'zune-inflate-0.2.54': 'etemesi254/zune-image'
};
const trees = new Map();
for (const [id, repository] of Object.entries(repositories)) {
  const vcs = JSON.parse(await readFile(path.join(registryPath, id, '.cargo_vcs_info.json'), 'utf8'));
  const commit = vcs.git.sha1;
  const key = repository + '/' + commit;
  if (!trees.has(key)) trees.set(key, JSON.parse(await download('https://api.github.com/repos/' + repository + '/git/trees/' + commit + '?recursive=1')));
  const tree = trees.get(key);
  if (tree.truncated) throw new Error('Truncated source tree: ' + id);
  const files = tree.tree.filter(entry => entry.type === 'blob' &&
    /(?:^|\/)(?:licen[cs]e|copying|copyright|notice)(?:[._-][^/]*)?$/i.test(entry.path) &&
    (!entry.path.includes('/') || entry.path.startsWith((vcs.path_in_vcs || '_') + '/') || entry.path.startsWith('licenses/')));
  if (!files.length) {
    if (id !== 'simd_helpers-0.1.0') throw new Error('Missing upstream license: ' + id);
    await save('rust-supplement/' + id + '/Cargo.toml',
      await readFile(path.join(registryPath, id, 'Cargo.toml')), 'crates.io:' + id);
    await save('rust-supplement/' + id + '/README.txt', Buffer.from(
      'The exact published crate declares MIT in Cargo.toml. Its source repository at ' + commit +
      ' contains no separate license or copyright notice. Cargo.toml preserves the publisher author and license metadata.\n' +
      'The standard MIT license template is provided separately without inventing an upstream copyright statement.\n'),
      'https://github.com/' + repository + '/tree/' + commit);
    const url = 'https://spdx.org/licenses/MIT.txt';
    await save('rust-supplement/' + id + '/MIT-standard-template.txt', await download(url), url);
  }
  for (const file of files) {
    const url = 'https://raw.githubusercontent.com/' + repository + '/' + commit + '/' + file.path;
    await save('rust-supplement/' + id + '/' + file.path.replaceAll('/', '_'), await download(url), url);
  }
}
// The runtime packs are published alongside the self-contained .NET application.
for (const id of ['microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64']) {
  const packageRoot = path.join(homedir(), '.nuget/packages', id);
  const versions = await readdir(packageRoot);
  const version = versions.sort((a, b) => a.localeCompare(b, undefined, { numeric: true })).at(-1);
  const directory = path.join(packageRoot, version);
  for (const file of await readdir(directory)) {
    if (/^(?:license|third-party-notices)(?:\.|$)/i.test(file))
      await save('dotnet/' + id + '-' + version + '/' + file, await readFile(path.join(directory, file)), 'NuGet:' + id + '/' + version);
  }
}
for (const name of ['LICENSE', 'ThirdPartyNotices.txt']) {
  const url = 'https://raw.githubusercontent.com/microsoft/onnxruntime/v1.28.0/' + name;
  await save('onnxruntime/' + name, await download(url), url);
}
function tar(args) {
  const result = spawnSync('tar.exe', args, { maxBuffer: 16 * 1024 * 1024, windowsHide: true });
  if (result.error || result.status !== 0) throw result.error || new Error(result.stderr.toString());
  return result.stdout;
}
async function archiveNotices(url, filename, folder) {
  const archive = path.join(cache, filename);
  try { await readFile(archive); } catch (error) {
    if (error.code !== 'ENOENT') throw error;
    await writeFile(archive, await download(url));
  }
  const files = tar(['-tf', archive]).toString('utf8').split(/\r?\n/).filter(file => !file.endsWith('/') && (
    /(?:^|\/)(?:licen[cs]e|copying|copyright|notice|third[-_ ]party[-_ ]notices)(?:[._-][^/]*)?$/i.test(file)
    || /(?:^|\/)licenses\//i.test(file)));
  if (!files.length) throw new Error('No notices in ' + filename);
  for (const file of files) {
    if (file.startsWith('/') || file.includes(':') || file.split('/').includes('..') || file.includes('\\')) throw new Error('Unsafe archive path');
    await save(folder + '/' + file.replaceAll('/', '_'), tar(['-xOf', archive, file]), url + ' :: ' + file);
  }
}
await archiveNotices('https://api.nuget.org/v3-flatcontainer/microsoft.ai.directml/1.15.4/microsoft.ai.directml.1.15.4.nupkg',
  'microsoft.ai.directml.1.15.4.nupkg', 'directml');
await archiveNotices('https://github.com/bblanchon/pdfium-binaries/releases/download/chromium%2F7961/pdfium-win-x64.tgz',
  'pdfium-win-x64-7961.tgz', 'pdfium');
await writeFile(path.join(output, 'native-notice-sources.json'), JSON.stringify({ files: manifest }, null, 2) + '\n');
console.log('Collected ' + manifest.length + ' supplemental native/runtime notices.');
