(() => {
  const root = document.documentElement;
  const titleText = document.querySelector('.title-bar-text');
  const maximize = document.querySelector('[data-window-command="maximize"]');
  const sendColors = () => {
    const style = getComputedStyle(root);
    window.chrome?.webview?.postMessage({
      type: 'frame-colors', face: style.getPropertyValue('--bevel-face').trim(),
      hiOuter: style.getPropertyValue('--bevel-hi-outer').trim(),
      hiInner: style.getPropertyValue('--bevel-hi-inner').trim(),
      loInner: style.getPropertyValue('--bevel-lo-inner').trim(),
      loOuter: style.getPropertyValue('--bevel-lo-outer').trim()
    });
  };
  const applyWindowState = state => {
    root.dataset.windowActive = String(state.active !== false);
    root.dataset.windowMaximized = String(state.maximized === true);
    maximize.dataset.windowCommand = state.maximized ? 'restore' : 'maximize';
    maximize.setAttribute('aria-label', state.maximized ? 'Restore' : 'Maximize');
  };
  window.mdviewApplyWindowState = applyWindowState;
  document.querySelectorAll('.title-bar-button').forEach(button => button.addEventListener('click', () =>
    window.chrome?.webview?.postMessage({ type: 'window', command: button.dataset.windowCommand })));
  window.chrome?.webview?.addEventListener('message', event => {
    if (event.data?.type === 'window-state') applyWindowState(event.data);
  });
  const syncTitle = () => { titleText.textContent = `${document.title} - mdview`; };
  new MutationObserver(syncTitle).observe(document.querySelector('title'), { childList: true, characterData: true, subtree: true });
  const originalApplyTheme = window.mdviewApplyTheme;
  window.mdviewApplyTheme = theme => { originalApplyTheme(theme); sendColors(); };
  const media = matchMedia('(prefers-color-scheme: dark)');
  media.addEventListener?.('change', sendColors);
  addEventListener('load', () => { syncTitle(); sendColors(); });
})();
