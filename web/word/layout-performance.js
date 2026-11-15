(() => {
  'use strict';
  // Layout-only cache. ProseMirror nodes are persistent immutable values: an
  // edit replaces the changed ancestors but retains untouched child objects.
  // Never change Node.prototype.toJSON or the export/clipboard serializers.
  const nodes = new WeakMap();
  const stats = { snapshots: 0, serializedNodes: 0, reusedNodes: 0, textHits: 0, textMisses: 0 };
  function serialize(node) {
    const cached = nodes.get(node);
    if (cached) { stats.reusedNodes++; return cached; }
    const json = { type: node.type.name };
    for (const key in node.attrs) { json.attrs = node.attrs; break; }
    if (node.content.size) {
      json.content = [];
      node.content.forEach(child => json.content.push(serialize(child)));
    }
    if (node.marks.length) json.marks = node.marks.map(mark => mark.toJSON());
    if (node.isText) json.text = node.text;
    nodes.set(node, json); stats.serializedNodes++;
    return json;
  }
  function snapshot(editor) {
    stats.snapshots++;
    const json = serialize(editor.state.doc);
    // Editor.getJSON also injects the current section defaults, which can
    // change without replacing body nodes. Keep that behavior outside the
    // node cache and return a new root so consumers never mutate the cache.
    const result = { ...json };
    if (editor.converter?.bodySectPr)
      result.attrs = { ...json.attrs, bodySectPr: editor.converter.bodySectPr };
    return result;
  }
  Object.defineProperty(window, 'obsidopfLayoutSnapshot', { value: snapshot });
  // Counters stay on this page and contain no text/document data. They can be
  // inspected later when runtime profiling is authorized; nothing is sent out.
  Object.defineProperty(window, 'obsidopfLayoutStats', { value: stats });
  let stages = {}, sequence = 0, pending = [], timer = null;
  const history = [];
  const flush = () => {
    timer = null;
    if (!pending.length) return;
    window.chrome.webview.postMessage({ type: 'layoutProfile', samples: pending });
    pending = [];
  };
  Object.defineProperty(window, 'obsidopfRecordLayoutStage', { value: message => {
    if (!window.obsidopfConfig?.profileLayout) return;
    const match = /^\[Perf\] ([A-Za-z.]+): ([0-9.]+)ms/.exec(message);
    if (!match) return;
    if (match[1] === 'getJSON') stages = {};
    stages[match[1]] = Number(match[2]);
    if (match[1] !== 'painter.postPaint') return;
    const sample = { sequence: ++sequence, stages: { ...stages }, counters: { ...stats } };
    history.push(sample); if (history.length > 60) history.shift();
    pending.push(sample); if (pending.length > 20) pending.shift();
    if (timer === null) timer = setTimeout(flush, 1500);
  } });
  Object.defineProperty(window, 'obsidopfLayoutProfile', { value: history });

  const nativeMeasure = CanvasRenderingContext2D.prototype.measureText;
  const metrics = new Map();
  const limit = 4096;
  const properties = ['font', 'direction', 'fontKerning', 'fontStretch',
    'fontVariantCaps', 'letterSpacing', 'wordSpacing', 'textRendering', 'lang', 'textAlign', 'textBaseline'];
  CanvasRenderingContext2D.prototype.measureText = function (text) {
    // Forward non-string arguments unchanged to preserve WebIDL conversions
    // and errors (including missing arguments). Avoid retaining long runs.
    if (!(this instanceof CanvasRenderingContext2D) || typeof text !== 'string' || text.length > 2048)
      return nativeMeasure.apply(this, arguments);
    const key = JSON.stringify([document.documentElement.lang, this.canvas.dir,
      ...properties.map(name => this[name]), text]);
    const cached = metrics.get(key);
    if (cached) {
      metrics.delete(key); metrics.set(key, cached); stats.textHits++; return cached;
    }
    const result = nativeMeasure.call(this, text);
    stats.textMisses++; metrics.set(key, result);
    if (metrics.size > limit) metrics.delete(metrics.keys().next().value);
    return result;
  };
  const clearMetrics = () => metrics.clear();
  document.fonts?.addEventListener('loadingdone', clearMetrics);
  document.fonts?.addEventListener('loadingerror', clearMetrics);
})();
