(() => {
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
