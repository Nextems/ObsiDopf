// Copyright (c) 2026 Maxim_Dembitsky (Nextems). AGPL-3.0-only.
import { createHash } from 'node:crypto';
import { readFile, writeFile, mkdir, copyFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { patchEngineSource, presentationPath } from '../../web/word/patch-engine-source.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const project = path.resolve(here, '../..');
const stage = path.resolve(process.argv[2]);
const npmRoot = path.resolve(process.argv[3]);
const lock = JSON.parse(await readFile(path.join(here, 'source-lock.json'), 'utf8'));
const sha = data => createHash('sha256').update(data).digest('hex');
const bundle = await readFile(path.join(npmRoot, 'dist/superdoc.min.js'));
const mapBytes = await readFile(path.join(npmRoot, 'dist/superdoc.min.js.map'));
if (sha(bundle) !== lock.bundleSha256 || sha(mapBytes) !== lock.sourceMapSha256)
  throw new Error('Published package contents do not match source-lock.json');
const map = JSON.parse(mapBytes);
const sourceDirectory = path.join(stage, 'published-sources');
await mkdir(sourceDirectory, { recursive: true });
const index = [];
for (let i = 0; i < map.sources.length; i++) {
  const text = map.sourcesContent[i];
  if (typeof text !== 'string') throw new Error(`Missing source content: ${map.sources[i]}`);
  const basename = path.posix.basename(map.sources[i]).replace(/[<>:"/\\|?*\x00-\x1f]/g, '_').replace(/[. ]+$/, '').slice(0, 100) || 'source.txt';
  const file = `${String(i).padStart(5, '0')}-${basename}`;
  await writeFile(path.join(sourceDirectory, file), text);
  index.push({ source: map.sources[i], file, sha256: sha(text) });
}
await writeFile(path.join(sourceDirectory, 'index.json'), JSON.stringify(index, null, 2) + '\n');
const original = await readFile(path.join(stage, 'upstream', 'PresentationEditor.ts'), 'utf8');
const modifiedFile = path.join(stage, 'modified-source', presentationPath);
await mkdir(path.dirname(modifiedFile), { recursive: true });
await writeFile(modifiedFile, patchEngineSource(original));
const integration = path.join(stage, 'obsidopf-integration');
await mkdir(integration, { recursive: true });
for (const file of ['index.html', 'editor.js', 'editor.css', 'layout-performance.js', 'selection-assistant.js', 'color-picker.js', 'patch-engine.mjs', 'patch-engine-source.mjs', 'build.mjs', 'package.json', 'package-lock.json', 'SUPERDOC-LICENSE.txt', 'README.md'])
  await copyFile(path.join(project, 'web/word', file), path.join(integration, file));
await copyFile(path.join(project, 'LICENSE'), path.join(stage, 'LICENSE'));
await copyFile(path.join(project, 'COPYRIGHT.md'), path.join(stage, 'COPYRIGHT.md'));
await copyFile(path.join(here, 'source-lock.json'), path.join(stage, 'source-lock.json'));
await copyFile(path.join(here, 'SOURCE-INFO.md'), path.join(stage, 'README.md'));
console.log(`Prepared ${map.sources.length} published source entries and modified PresentationEditor.ts`);
