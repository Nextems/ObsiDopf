import { createHash } from 'node:crypto';

// This intentionally narrow patch is for the pinned open-source 1.47.2 UMD
// bundle. Fail on any upgrade instead of accidentally changing another call.
export function patchEngine(source) {
  const digest = createHash('sha256').update(source).digest('hex');
  if (digest !== '5898d2a2f77e791841d540084b75d012a7005ed79e3b2293f9a6dafcfb908a8f')
    throw new Error('Unexpected SuperDoc bundle: review the layout snapshot patch before upgrading.');
  const original = 'n=this.#n.getJSON(),Sx(`[Perf] getJSON:';
  if (source.split(original).length !== 2) throw new Error('Layout getJSON patch anchor is not unique.');
  let result = source.replace(original, 'n=window.obsidopfLayoutSnapshot(this.#n),Sx(`[Perf] getJSON:');
  // Capture the engine's existing numeric stage timings instead of changing
  // its scheduler again without evidence. These six messages contain no text.
  let count = 0;
  result = result.replace(/Sx\((`\[Perf\][^`]+`)\)/g, (_, message) => {
    count++; return `window.obsidopfRecordLayoutStage(${message})`;
  });
  if (count !== 6) throw new Error('Layout timing anchors changed; review the engine patch.');
  return result;
}
