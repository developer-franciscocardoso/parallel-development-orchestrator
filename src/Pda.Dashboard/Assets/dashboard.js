(() => {
  const live = document.getElementById('live');
  if (!live) return;
  const status = document.getElementById('connection');
  const stream = new EventSource('/api/events?after=' + live.dataset.cursor);
  let pending = false;
  let loading = false;
  function setStatus(text) { status.textContent = text; }
  async function refresh() {
    if (!pending || loading || document.activeElement?.closest('form')) return;
    pending = false;
    loading = true;
    try {
      const response = await fetch(location.href, {cache: 'no-store'});
      if (response.redirected) { location.assign(response.url); return; }
      if (!response.ok) throw Error();
      const page = new DOMParser().parseFromString(await response.text(), 'text/html');
      const next = page.getElementById('live');
      if (!next) throw Error();
      const opened = new Set([...live.querySelectorAll('details[open]')]
        .map(node => node.dataset.key).filter(Boolean));
      for (const node of next.querySelectorAll('details[data-key]'))
        if (opened.has(node.dataset.key)) node.open = true;
      live.replaceChildren(...next.childNodes);
      const picker = document.querySelector('.picker');
      const newPicker = page.querySelector('.picker');
      if (newPicker && picker) picker.replaceWith(newPicker);
      else if (newPicker) live.before(newPicker);
      setStatus('Live');
    } catch {
      pending = true;
      setStatus('Reconnecting');
    } finally {
      loading = false;
      if (pending) setTimeout(refresh, 1500);
    }
  }
  stream.onopen = () => setStatus('Live');
  stream.onerror = () => setStatus('Reconnecting');
  let debounce;
  stream.addEventListener('journal', () => {
    pending = true;
    clearTimeout(debounce);
    debounce = setTimeout(refresh, 250);
  });
  document.addEventListener('focusout', () => setTimeout(refresh, 50));
  window.addEventListener('pagehide', () => stream.close(), {once: true});
})();
