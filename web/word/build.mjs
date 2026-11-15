import { mkdir, copyFile, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { patchEngine } from './patch-engine.mjs';
const root = path.dirname(fileURLToPath(import.meta.url));
const engineArgument = process.argv.indexOf('--engine-dist');
if (engineArgument >= 0 && !process.argv[engineArgument + 1]) throw new Error('--engine-dist requires a directory');
const customEngine = engineArgument >= 0;
const engineDist = customEngine ? path.resolve(process.argv[engineArgument + 1]) : path.join(root, 'node_modules/superdoc/dist');
const output = path.resolve(root, '../../src/ObsidianDocs/WordAssets');
await mkdir(path.join(output, 'vendor'), { recursive: true });
for (const name of ['index.html', 'editor.js', 'editor.css', 'layout-performance.js', 'selection-assistant.js', 'color-picker.js'])
  await copyFile(path.join(root, name), path.join(output, name));
await copyFile(path.join(engineDist, 'style.css'), path.join(output, 'vendor/style.css'));
const source = await readFile(path.join(engineDist, 'superdoc.min.js'), 'utf8');
if (customEngine && (!source.includes('.obsidopfLayoutSnapshot(') || !source.includes('.obsidopfRecordLayoutStage(')))
  throw new Error('Custom bundle needs the preferred-source ObsiDopf patch before building.');
await writeFile(path.join(output, 'vendor/superdoc.min.js'), customEngine ? source : patchEngine(source));
await copyFile(path.join(root, 'patch-engine.mjs'), path.join(output, 'patch-engine.mjs'));
await copyFile(path.join(root, 'patch-engine-source.mjs'), path.join(output, 'patch-engine-source.mjs'));
await copyFile(path.join(root, 'SUPERDOC-LICENSE.txt'), path.join(output, 'SUPERDOC-LICENSE.txt'));
let notices = `ObsiDopf Word editor\n\nOriginal ObsiDopf integration code and original modifications:\nCopyright (c) 2026 Maxim_Dembitsky (Nextems).\nLicense: AGPL-3.0-only. See LICENSE and COPYRIGHT.md in the application root.\nThird-party copyright notices and licenses remain in effect.\n\nSuperDoc 1.47.2, Harbour Enterprises, Inc.\nLicense: AGPL-3.0\nSource: https://github.com/superdoc/docx-editor\nPackage: https://www.npmjs.com/package/superdoc/v/1.47.2\nModified locally on 2026-11-06: layout JSON serialization uses the persistent-node cache in layout-performance.js. See patch-engine.mjs for the exact version-pinned change; export/clipboard serialization is unchanged. See SUPERDOC-LICENSE.txt.\n\nInstalled dependency notices:\n`;
const lock = JSON.parse(await readFile(path.join(root, 'package-lock.json'), 'utf8'));
for (const [relative, entry] of Object.entries(lock.packages)) {
  if (!relative) continue;
  notices += `\n--- ${relative} ${entry.version || ''} (${entry.license || 'see package'}) ---\n`;
  for (const file of ['LICENSE', 'LICENSE.md', 'LICENSE.txt', 'LICENCE', 'NOTICE']) {
    try { notices += await readFile(path.join(root, relative, file), 'utf8'); notices += '\n'; } catch (error) { if (error.code !== 'ENOENT') throw error; }
  }
}
await writeFile(path.join(output, 'THIRD-PARTY-NOTICES.txt'), notices);
// Direct custom-engine builds must survive the following incremental MSBuild.
// A normal npm run build explicitly restores the pinned release engine.
await writeFile(path.join(output, '.built'), JSON.stringify({ engine: customEngine ? 'preferred-source-custom' : 'superdoc@1.47.2', builtUtc: new Date().toISOString() }));
