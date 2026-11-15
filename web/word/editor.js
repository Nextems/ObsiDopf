(() => {
  'use strict';
  const bridge = window.chrome.webview;
  const send = (type, data = {}) => bridge.postMessage({ type, ...data });
  let instance, ready = false, searchMatches = [], searchIndex = -1, exportsInProgress = 0;
  let lastPageCount = 0, currentZoom = 1;
  let documentRevision = 0, reportedRevision = 0, changeTimer = null;
  let immediateChange = true, searchActive = false;
  const flushChanges = () => {
    clearTimeout(changeTimer); changeTimer = null;
    if (reportedRevision === documentRevision) return;
    reportedRevision = documentRevision;
    send('changed', { revision: documentRevision });
  };
  const notifyChange = () => {
    documentRevision++;
    // Mark the first change immediately (closing must see the dirty state).
    // Coalesce a continuing burst without delaying input or engine transactions.
    if (immediateChange) { immediateChange = false; flushChanges(); return; }
    if (changeTimer === null) changeTimer = setTimeout(flushChanges, 150);
  };
  let historyRevision = 0, savedViewport = null;
  const presentation = () => {
    const doc = instance?.state?.documents?.[0];
    return doc ? instance.getPresentationEditorForDocument(doc.id) : null;
  };
  const reportError = error => {
    // Snapshot reports its own failure through the awaited host request. Do not
    // open a second modal from an exception emitted inside the export callback.
    if (exportsInProgress) return;
    const message = String(error?.message || error || 'Ошибка редактора');
    if (!ready) document.querySelector('#loading').textContent = 'Не удалось открыть документ.\n' + message;
    send('error', { message });
  };
  function history(redo = false) {
    const editor = presentation();
    if (!editor) return false;
    const scroller = editor.scrollContainer || document.scrollingElement;
    savedViewport ||= { scroller, top: scroller.scrollTop, left: scroller.scrollLeft };
    const revision = ++historyRevision;
    const restore = () => {
      if (savedViewport) savedViewport.scroller.scrollTo({ top: savedViewport.top, left: savedViewport.left, behavior: 'instant' });
    };
    const result = redo ? editor.redo() : editor.undo();
    restore();
    requestAnimationFrame(() => {
      if (revision !== historyRevision) return;
      restore();
      requestAnimationFrame(() => {
        if (revision !== historyRevision) return;
        restore(); savedViewport = null;
      });
    });
    return result;
  }
  function search(query) {
    searchActive = !!query;
    searchMatches = instance.search(query) || [];
    searchIndex = searchMatches.length ? 0 : -1;
    if (searchIndex >= 0) instance.goToSearchResult(searchMatches[searchIndex]);
    return { total: searchMatches.length, index: searchIndex };
  }
  function solidTable(tr) {
    const anchor = tr.selection.$from;
    let depth = anchor.depth;
    while (depth > 0 && anchor.node(depth).type.spec.tableRole !== 'table') depth--;
    if (!depth) return false;
    const table = anchor.node(depth), position = anchor.before(depth);
    const borders = {};
    for (const side of ['top', 'bottom', 'left', 'right', 'insideH', 'insideV'])
      borders[side] = { val: 'single', color: '333333', size: 8 };
    tr.setNodeMarkup(position, undefined, { ...table.attrs, borders });
    table.descendants((node, offset) => {
      if (!['cell', 'header_cell'].includes(node.type.spec.tableRole)) return;
      tr.setNodeMarkup(position + 1 + offset, undefined, {
        ...node.attrs, borders, background: { color: 'FFFFFF' },
      });
    });
    return true;
  }
  function tableAction(insert) {
    const status = document.querySelector('#table-status');
    try {
      const editor = presentation()?.getEffectiveSelectionContext()?.editor;
      if (!editor) throw new Error('Поставьте курсор в документ.');
      let chain = editor.chain();
      if (insert) {
        const rows = Number(document.querySelector('#table-rows').value);
        const cols = Number(document.querySelector('#table-cols').value);
        if (!Number.isInteger(rows) || rows < 1 || rows > 100 || !Number.isInteger(cols) || cols < 1 || cols > 20)
          throw new Error('Укажите 1–100 строк и 1–20 столбцов.');
        chain = chain.insertTable({ rows, cols, withHeaderRow: false });
      }
      if (!chain.command(({ tr }) => solidTable(tr)).run()) throw new Error('Поставьте курсор в ячейку таблицы.');
      status.textContent = 'Белая заливка · чёрные границы'; presentation().focus();
    } catch (error) { status.textContent = error.message; }
  }
  for (const [id, insert] of [['insert-solid-table', true], ['style-solid-table', false]]) {
    const button = document.getElementById(id);
    button.addEventListener('pointerdown', event => event.preventDefault());
    button.addEventListener('click', () => tableAction(insert));
  }
  async function command(name, data) {
    if (!ready) throw new Error('Документ ещё открывается.');
    switch (name) {
      case 'snapshot': {
        flushChanges();
        let blob, exportFailure;
        const failed = ({ error }) => { exportFailure ||= error || new Error('Ошибка экспорта DOCX.'); };
        instance.on('exception', failed); exportsInProgress++;
        try {
          // SuperDoc may fall back to the original input Blob after emitting an
          // exception. Treat that as failure; otherwise Ctrl+S would lose edits
          // while incorrectly marking the document saved.
          blob = await instance.export({ triggerDownload: false, exportType: ['docx'] });
          if (exportFailure) throw exportFailure;
        } finally { instance.off('exception', failed); exportsInProgress--; }
        if (!(blob instanceof Blob)) throw new Error('Редактор не вернул DOCX.');
        const base64 = await new Promise((resolve, reject) => {
          const reader = new FileReader();
          reader.onload = () => resolve(String(reader.result).split(',')[1]);
          reader.onerror = () => reject(reader.error);
          reader.readAsDataURL(blob);
        });
        // Flush again before replying so edits during async export cannot be
        // marked clean by the host after saving an older snapshot.
        flushChanges();
        immediateChange = true;
        return { base64 };
      }
      case 'zoom':
        if (Math.abs(data.value - currentZoom) > 0.001) instance.setZoom(data.value * 100);
        return {};
      case 'page': return { found: await presentation().scrollToPage(data.page + 1, 'auto') };
      case 'search': return search(data.query);
      case 'searchStep': {
        if (searchMatches.length) {
          searchIndex = (searchIndex + data.direction + searchMatches.length) % searchMatches.length;
          instance.goToSearchResult(searchMatches[searchIndex]);
        }
        return { total: searchMatches.length, index: searchIndex };
      }
      case 'clearSearch':
        if (searchActive) instance.search('');
        searchActive = false; searchMatches = []; searchIndex = -1; return {};
      case 'undo': return { changed: history(false) };
      case 'redo': return { changed: history(true) };
      case 'focus': presentation()?.focus(); return {};
      case 'selection': {
        // SuperDoc 1.x exposes selection through the active Editor's Document
        // API. instance.ui is a v2 facade and is absent in our pinned engine.
        const editor = presentation()?.getActiveEditor();
        if (!editor?.doc?.selection?.current) throw new Error('Выделение документа пока недоступно.');
        return { text: editor.doc.selection.current({ includeText: true }).text || '' };
      }
      default: throw new Error('Неизвестная команда: ' + name);
    }
  }
  bridge.addEventListener('message', async event => {
    const request = event.data;
    if (typeof request?.command !== 'string') return;
    try { send('reply', { id: request.id, result: await command(request.command, request.data || {}) }); }
    catch (error) { send('reply', { id: request.id, error: String(error?.message || error) }); }
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'F3' || event.key === 'Escape') {
      if (event.key === 'F3') event.preventDefault();
      send('shortcut', { key: event.key.toLowerCase(), shift: event.shiftKey }); return;
    }
    if (!(event.ctrlKey || event.metaKey) || event.altKey) return;
    const key = event.key.toLowerCase();
    const input = event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement;
    if (key === 'z' && !input) {
      event.preventDefault(); event.stopImmediatePropagation(); history(event.shiftKey); return;
    }
    if (key === 'y' && !input) {
      event.preventDefault(); event.stopImmediatePropagation(); history(true); return;
    }
    if (['s', 'o', 'n', 'f', '+', '=', '-', '0'].includes(key)) {
      event.preventDefault(); event.stopImmediatePropagation(); send('shortcut', { key, shift: event.shiftKey });
    }
  }, true);
  document.addEventListener('wheel', event => {
    if (!event.ctrlKey) return;
    event.preventDefault(); send('zoomDelta', { delta: event.deltaY < 0 ? 0.1 : -0.1 });
  }, { capture: true, passive: false });
  window.addEventListener('error', event => reportError(event.error || event.message));
  window.addEventListener('unhandledrejection', event => reportError(event.reason));
  try {
    document.documentElement.classList.add(SuperDoc.createTheme({
      name: 'obsidopf', font: 'Segoe UI, sans-serif',
      colors: { action: '#b39af5', bg: '#26262e', text: '#e4e1eb', textMuted: '#aaa3b6', border: '#42404d', hoverBg: '#37343f', activeBg: '#514267' },
      vars: {
        '--sd-layout-page-bg': '#ffffff',
        '--sd-layout-page-color': '#171717',
        '--sd-layout-page-shadow': '0 2px 12px #00000040',
        '--sd-page-bg': '#ffffff',
      },
    }));
    instance = new SuperDoc({
      selector: '#editor', toolbar: '#toolbar', document: '/document.docx',
      documentMode: 'editing', contained: true, rulers: true,
      measurementUnit: 'cm', viewOptions: { layout: 'print' },
      layoutEngineOptions: { flowMode: 'paginated', virtualization: { enabled: true, window: 5, overscan: 1 } },
      telemetry: { enabled: false }, disablePiniaDevtools: true,
      user: { name: window.obsidopfConfig?.user || 'ObsiDopf' },
      modules: {
        comments: { visible: false },
        toolbar: { fonts: (window.obsidopfConfig?.fonts || []).map(name => ({ key: name, label: name })) },
      },
      onReady: () => {
        ready = true; document.querySelector('#loading').hidden = true; send('ready');
        window.attachGeminiSelection(presentation, send);
        window.attachColorPicker(presentation, send);
        document.querySelector('#insert-solid-table').disabled = false;
        document.querySelector('#style-solid-table').disabled = false;
      },
      onEditorUpdate: () => {
        if (!ready) return;
        searchMatches = []; searchIndex = -1; notifyChange();
      },
      onPaginationUpdate: ({ totalPages }) => {
        if (!Number.isFinite(totalPages) || totalPages === lastPageCount) return;
        lastPageCount = totalPages; send('pages', { count: totalPages });
      },
      onZoomChange: ({ zoom }) => {
        const value = zoom / 100;
        if (!Number.isFinite(value) || Math.abs(value - currentZoom) < 0.001) return;
        currentZoom = value; send('zoom', { value });
      },
      onContentError: ({ error }) => reportError(error),
      onException: ({ error }) => reportError(error),
    });
  } catch (error) { reportError(error); }
})();
