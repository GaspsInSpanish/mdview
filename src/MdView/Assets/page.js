(() => {
  window.mdviewApplyTheme = theme => {
    if (theme === 'system') document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', theme);
    document.querySelectorAll('[data-command^="theme."]').forEach(item => {
      item.setAttribute('aria-checked', String(item.dataset.command === `theme.${theme}`));
    });
  };

  if (window.mdviewToggle.id) {
    const events = new EventSource(`/events/${encodeURIComponent(window.mdviewToggle.id)}`);
    // Unsaved edits outrank a reload; edit mode flags the file as changed on disk instead.
    events.addEventListener('reload', () => {
      if (!window.mdviewEdit?.onExternalChange?.()) location.reload();
    });
    events.addEventListener('theme', event => window.mdviewApplyTheme(event.data));
  }

  document.addEventListener('change', async event => {
    const checkbox = event.target;
    if (!checkbox.matches('input[data-line]')) return;
    const oldState = !checkbox.checked;
    checkbox.disabled = true;
    try {
      const response = await fetch('/toggle', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          id: window.mdviewToggle.id,
          line: +checkbox.dataset.line,
          checked: oldState,
          token: window.mdviewToggle.token
        })
      });
      if (response.status === 409) {
        location.reload();
        return;
      }
      if (!response.ok) throw new Error('Toggle failed.');
    } catch {
      checkbox.checked = oldState;
      alert('Checkbox change was not saved.');
    } finally {
      checkbox.disabled = false;
    }
  });

  document.addEventListener('DOMContentLoaded', () => hljs.highlightAll());
})();
