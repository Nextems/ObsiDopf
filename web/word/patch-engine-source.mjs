// Copyright (c) 2026 Maxim_Dembitsky (Nextems). AGPL-3.0-only.
// Preferred-source equivalent of patch-engine.mjs for rebuilding SuperDoc.
import { createHash } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

export const presentationPath = 'packages/super-editor/src/editors/v1/core/presentation-editor/PresentationEditor.ts';
export function patchEngineSource(input) {
  let source = input.replace(/\r\n/g, '\n');
  const digest = createHash('sha256').update(source).digest('hex');
  if (digest !== 'ff9259fa7cdc4a646de05882e7ad5ff3d150b4cebd4e52fc892e122bdbd06c57')
    throw new Error('Unexpected PresentationEditor source; use the pinned upstream snapshot.');
  const anchor = 'docJson = this.#editor.getJSON();';
  if (source.split(anchor).length !== 2) throw new Error('Source layout anchor is not unique.');
  source = source.replace(anchor, 'docJson = (window as any).obsidopfLayoutSnapshot(this.#editor);');
  let count = 0;
  source = source.replace(/perfLog\(\s*(`\[Perf\][^`]+`),?\s*\)/g, (_, message) => {
    count++;
    return `(window as any).obsidopfRecordLayoutStage(${message})`;
  });
  if (count !== 6) throw new Error('Source layout timing anchors changed.');
  return '// Modified for ObsiDopf on 2026-11-15: cached layout snapshot and opt-in numeric profiling.\n' + source;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (!process.argv[2]) throw new Error('Usage: node patch-engine-source.mjs <upstream-source-root>');
  const file = path.join(path.resolve(process.argv[2]), presentationPath);
  await writeFile(file, patchEngineSource(await readFile(file, 'utf8')));
  console.log('Applied ObsiDopf changes to PresentationEditor.ts');
}
