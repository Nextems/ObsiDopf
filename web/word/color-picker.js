// Copyright (c) 2026 Maxim_Dembitsky (Nextems). AGPL-3.0-only.
window.attachColorPicker = (getPresentation, send) => {
  let context = null, pending = false, nextId = 0;
  const waiting = new Map();
  const release = saved => {
    try { if (saved?.handle) saved.editor.releaseSelectionHandle(saved.handle); } catch { }
  };
  const capture = (kind, opener) => {
    release(context); context = null;
    const presentation = getPresentation();
    const editor = presentation?.getEffectiveSelectionContext()?.editor;
    if (!editor || editor.isDestroyed) return;
    const selection = editor.state.selection;
    const handle = editor.captureEffectiveSelectionHandle();
    context = {
      kind, opener, presentation, editor, doc: editor.state.doc,
      bookmark: selection.getBookmark(), empty: !handle.wasNonEmpty,
      marks: editor.state.storedMarks,
      handle,
    };
  };
  const toolbarTarget = event => {
    if (!(event.target instanceof Element)) return;
    const root = event.target.closest('[data-sd-part="toolbar-item"]');
    const button = event.target.closest('[data-item="btn-color"], [data-item="btn-highlight"]')
      || root?.querySelector('[data-item="btn-color"], [data-item="btn-highlight"]');
    if (!button || !button.closest('#toolbar, [data-sd-part="dropdown-menu"]') || button.classList.contains('sd-disabled')) return;
    try { capture(button.dataset.item === 'btn-highlight' ? 'highlight' : 'color', button); } catch { }
  };
  document.addEventListener('pointerdown', event => { if (!pending) toolbarTarget(event); }, true);
  document.addEventListener('keydown', event => {
    const action = event.target instanceof Element && event.target.closest('.obsidopf-color-actions button');
    if (action && ['Enter', ' '].includes(event.key)) {
      event.preventDefault(); event.stopImmediatePropagation(); action.click(); return;
    }
    if (!pending && ['Enter', ' ', 'ArrowDown'].includes(event.key)) toolbarTarget(event);
  }, true);
  window.chrome.webview.addEventListener('message', event => {
    const reply = event.data;
    if (reply?.type !== 'colorPicked' || !waiting.has(reply.id)) return;
    const resolve = waiting.get(reply.id); waiting.delete(reply.id); resolve(reply);
  });
  const initialColor = saved => {
    try {
      const value = saved.editor.getAttributes(saved.kind === 'highlight' ? 'highlight' : 'textStyle')?.color;
      if (/^#[0-9a-f]{6}$/i.test(value || '')) return value;
    } catch { }
    return saved.kind === 'highlight' ? '#FFFF00' : '#000000';
  };
  async function openPicker(menu, eyedropper) {
    if (pending) return;
    const saved = context;
    if (!saved) return;
    pending = true;
    menu.querySelector('.obsidopf-color-error')?.remove();
    const oldDisplay = menu.style.display;
    menu.style.display = 'none';
    try {
      // Let the composited WebView paint the document before a screen sample.
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
      const id = 'palette-' + (++nextId);
      const result = await new Promise(resolve => {
        waiting.set(id, resolve);
        send('pickColor', { id, kind: saved.kind, color: initialColor(saved), eyedropper });
      });
      if (result.error) throw new Error(result.error);
      if (!result.accepted) return;
      if (saved.editor.isDestroyed || getPresentation() !== saved.presentation)
        throw new Error('Документ уже закрыт.');
      const value = result.color;
      if (value !== null && !/^#[0-9a-f]{6}$/i.test(value)) throw new Error('Некорректный цвет.');
      const scroller = saved.presentation.scrollContainer || document.scrollingElement;
      const top = scroller.scrollTop, left = scroller.scrollLeft;
      const range = saved.editor.resolveSelectionHandle(saved.handle);
      if (!range) throw new Error('Исходное выделение больше недоступно. Выделите текст ещё раз.');
      if (!saved.empty) {
        const receipt = saved.editor.doc.format.apply({ target: range.target, inline: { [saved.kind]: value } });
        if (receipt?.success === false) throw new Error(receipt.failure?.message || 'Не удалось применить цвет.');
      } else {
        if (saved.editor.state.doc !== saved.doc) throw new Error('Документ изменился. Выберите цвет ещё раз.');
        let chain = saved.editor.chain().command(({ tr }) => {
          tr.setSelection(saved.bookmark.resolve(tr.doc));
          if (saved.marks) tr.setStoredMarks(saved.marks);
          return true;
        });
        chain = saved.kind === 'highlight'
          ? (value === null ? chain.unsetHighlight() : chain.setHighlight(value))
          : (value === null ? chain.unsetColor() : chain.setColor(value));
        if (!chain.run()) throw new Error('Не удалось применить цвет.');
      }
      // Close the original Vue dropdown through its own trigger.
      saved.opener?.closest('[data-sd-part="dropdown-trigger"]')?.click();
      saved.presentation.focus();
      scroller.scrollTo({ top, left, behavior: 'instant' });
      requestAnimationFrame(() => scroller.scrollTo({ top, left, behavior: 'instant' }));
    } catch (error) {
      if (menu.isConnected) {
        const message = document.createElement('div'); message.className = 'obsidopf-color-error';
        message.setAttribute('role', 'status'); message.textContent = String(error?.message || error);
        menu.querySelector('.obsidopf-color-actions')?.append(message);
      }
    } finally {
      if (menu.isConnected) menu.style.display = oldDisplay;
      pending = false; release(saved); if (context === saved) context = null;
      // Cancellation leaves the original swatch menu open and usable again.
      if (menu.isConnected && getPresentation() === saved.presentation && !saved.editor.isDestroyed) {
        try { capture(saved.kind, saved.opener); } catch { }
      }
    }
  }
  const enhance = () => {
    const grids = [...document.querySelectorAll('[data-sd-part="dropdown-menu"] .options-grid-wrap')];
    if (!pending && grids.length === 0) { release(context); context = null; }
    for (const grid of grids) {
      if (grid.querySelector('.obsidopf-color-actions')) continue;
      if (!context) {
        try { capture(grid.querySelector('.none-option') ? 'highlight' : 'color', null); } catch { continue; }
      }
      const actions = document.createElement('div'); actions.className = 'obsidopf-color-actions';
      for (const [text, eyedropper] of [['Другие цвета…', false], ['⌖ Пипетка', true]]) {
        const button = document.createElement('button'); button.type = 'button'; button.textContent = text;
        button.addEventListener('pointerdown', event => { event.preventDefault(); event.stopPropagation(); });
        button.addEventListener('keydown', event => event.stopPropagation());
        button.addEventListener('click', event => { event.stopPropagation(); openPicker(grid.closest('[data-sd-part="dropdown-menu"]'), eyedropper); });
        actions.append(button);
      }
      grid.append(actions);
    }
  };
  // Vue teleports menus directly to body. Avoid observing document-page paints.
  const observer = new MutationObserver(() => requestAnimationFrame(enhance));
  observer.observe(document.body, { childList: true });
  window.addEventListener('pagehide', () => { observer.disconnect(); release(context); waiting.clear(); }, { once: true });
};
