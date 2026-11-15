window.attachGeminiSelection = (getPresentation, send) => {
  const widget = document.createElement('div');
  widget.className = 'gemini-selection-assistant'; widget.hidden = true;
  const button = document.createElement('button');
  button.className = 'gemini-selection-button'; button.title = 'Спросить Gemini по выделению';
  button.setAttribute('aria-label', button.title);
  button.innerHTML = '<svg width="23" height="23" viewBox="0 0 24 24" aria-hidden="true"><defs><linearGradient id="gemini-selection-gradient" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#53a9ff"/><stop offset="1" stop-color="#aa69e8"/></linearGradient></defs><path fill="url(#gemini-selection-gradient)" d="M12 0C13.8 7.2 16.8 10.2 24 12C16.8 13.8 13.8 16.8 12 24C10.2 16.8 7.2 13.8 0 12C7.2 10.2 10.2 7.2 12 0Z"/></svg>';
  const bubble = document.createElement('div'); bubble.className = 'gemini-question-bubble'; bubble.hidden = true;
  const title = document.createElement('div'); title.textContent = 'Gemini · вопрос по выделению'; title.className = 'gemini-question-title';
  const question = document.createElement('textarea'); question.maxLength = 20000; question.placeholder = 'Что спросить по этому тексту?'; question.setAttribute('aria-label', 'Вопрос по выделению');
  const actions = document.createElement('div'); actions.className = 'gemini-question-actions';
  const close = document.createElement('button'); close.textContent = 'Закрыть';
  const submit = document.createElement('button'); submit.textContent = 'Отправить ↑';
  actions.append(close, submit); bubble.append(title, question, actions); widget.append(button, bubble); document.body.append(widget);
  let selected = '', revision = 0;
  const hide = () => { revision++; widget.hidden = true; bubble.hidden = true; button.hidden = false; selected = ''; };
  const place = (x, y) => {
    widget.style.left = Math.max(8, Math.min(x + 12, window.innerWidth - (bubble.hidden ? 52 : 330))) + 'px';
    widget.style.top = Math.max(8, Math.min(y + 12, window.innerHeight - (bubble.hidden ? 52 : 195))) + 'px';
  };
  button.addEventListener('pointerdown', event => event.preventDefault());
  button.addEventListener('click', () => {
    const rect = widget.getBoundingClientRect(); button.hidden = true; bubble.hidden = false;
    place(rect.left - 12, rect.top - 12); question.value = ''; question.focus();
  });
  const ask = () => {
    const prompt = question.value.trim();
    if (!prompt) { question.focus(); return; }
    const text = selected; hide(); send('askGemini', { text, prompt });
  };
  submit.addEventListener('click', ask); close.addEventListener('click', hide);
  question.addEventListener('keydown', event => {
    if (event.key === 'Enter' && event.ctrlKey) { event.preventDefault(); ask(); }
    if (event.key === 'Escape') { event.stopPropagation(); hide(); }
  });
  document.addEventListener('pointerdown', event => { if (!widget.contains(event.target)) hide(); }, true);
  document.addEventListener('pointerup', event => {
    if (widget.contains(event.target) || event.button !== 0) return;
    const presentation = getPresentation();
    if (!presentation?.visibleHost?.contains(event.target)) return;
    const current = ++revision, x = event.clientX, y = event.clientY;
    requestAnimationFrame(() => {
      if (current !== revision) return;
      try {
        const text = presentation.getActiveEditor().doc.selection.current({ includeText: true }).text || '';
        if (!text.trim()) { hide(); return; }
        selected = text; button.hidden = false; bubble.hidden = true; widget.hidden = false; place(x, y);
      } catch { hide(); }
    });
  }, true);
  document.addEventListener('scroll', event => { if (!widget.contains(event.target)) hide(); }, true);
  window.addEventListener('resize', hide);
};
