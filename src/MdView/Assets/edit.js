(() => {
  // Edit mode. Every top-level block carries data-md-start/-end: the exact slice of the file
  // it was rendered from. Clicking a block swaps it for a textarea holding that slice; leaving
  // it splices the text back into `source` and re-renders. Nothing is converted from HTML back
  // to Markdown, and bytes outside the blocks the user touched are never rewritten.
  const reader = document.querySelector('#reader-content');
  const lock = document.querySelector('.edit-lock');
  const saveItem = document.querySelector('[data-command="file.save"]');
  const config = window.mdviewToggle;
  const baseTitle = document.title;
  const storageKey = `mdview-editing:${config.id}`;
  let pageHash = window.mdviewEdit?.hash ?? '';
  let active = false;
  let source = '';   // The file's text as edited so far, in the file's own line endings.
  let saved = '';    // The text last known to be on disk.
  let hash = '';     // Hash of the on-disk version `saved` came from; a save must still match it.
  let newline = '\n';
  let stale = false; // The file changed on disk while there were unsaved edits.
  let editor = null;
  let queue = Promise.resolve();

  // Serialise everything that reads or replaces the reader content.
  const enqueue = task => (queue = queue.then(task, task));

  function remember(value) {
    try {
      if (value) sessionStorage.setItem(storageKey, '1');
      else sessionStorage.removeItem(storageKey);
    } catch { /* Storage is a convenience: without it a reload simply comes back locked. */ }
  }

  function remembered() {
    try { return sessionStorage.getItem(storageKey) === '1'; } catch { return false; }
  }

  const post = (route, body) => fetch(`/${route}/${encodeURIComponent(config.id)}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token: config.token, ...body })
  });

  const isDirty = () => active && (source !== saved || Boolean(editor && editor.textarea.value !== editor.original));
  const toFileNewlines = text => (newline === '\r\n' ? text.replace(/\r?\n/g, '\r\n') : text);

  function updateChrome() {
    const dirty = isDirty();
    document.documentElement.toggleAttribute('data-editing', active);
    lock.setAttribute('aria-checked', String(active));
    lock.textContent = !active ? 'Locked' : stale ? 'Editing (changed on disk)' : 'Editing';
    lock.toggleAttribute('data-dirty', dirty);
    lock.title = active ? 'Lock the file against edits (Ctrl+E)' : 'Unlock to edit this file (Ctrl+E)';
    saveItem?.setAttribute('aria-disabled', String(!dirty));
    document.title = dirty ? `*${baseTitle}` : baseTitle;
  }

  function prepareContent() {
    reader.querySelectorAll('input[data-line]').forEach(box => { box.disabled = active; });
    // Placeholders stand in for source that renders nothing (frontmatter, link definitions).
    // They show their raw text while editing and hold none while reading, so Find and Copy
    // never meet them.
    reader.querySelectorAll('.md-source-only').forEach(element => {
      element.textContent = active ? source.slice(+element.dataset.mdStart, +element.dataset.mdEnd) : '';
    });
  }

  function replaceContent(html) {
    // Server-rendered through the same sanitizer as the page itself; the page CSP applies.
    reader.innerHTML = html;
    reader.querySelectorAll('pre code').forEach(block => window.hljs?.highlightElement(block));
    prepareContent();
    window.mdviewFold?.refresh();
    reader.dispatchEvent(new CustomEvent('mdview:content-replaced'));
  }

  async function render() {
    const response = await post('render', { text: source });
    if (!response.ok) throw new Error('Render failed.');
    replaceContent((await response.json()).html);
  }

  function fit(textarea) {
    textarea.style.height = 'auto';
    textarea.style.height = `${textarea.scrollHeight + 2}px`;
  }

  // Where in the block's source did the user click? Match the text around the click in the
  // source, and prefer the occurrence nearest the click's proportional position.
  function caretFor(element, x, y, text) {
    let node = null;
    let offset = 0;
    const position = document.caretPositionFromPoint?.(x, y);
    if (position) {
      node = position.offsetNode;
      offset = position.offset;
    } else {
      const range = document.caretRangeFromPoint?.(x, y);
      if (range) {
        node = range.startContainer;
        offset = range.startOffset;
      }
    }
    if (!node || node.nodeType !== Node.TEXT_NODE || !element.contains(node)) return text.length;
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    let before = 0;
    let total = 0;
    while (walker.nextNode()) {
      if (walker.currentNode === node) before = total + offset;
      total += walker.currentNode.data.length;
    }
    const estimate = total ? Math.round((before / total) * text.length) : text.length;
    const lead = node.data.slice(Math.max(0, offset - 16), offset);
    const trail = node.data.slice(offset, offset + 16);
    const candidates = [];
    for (const [needle, shift] of [[lead, lead.length], [trail, 0]]) {
      if (needle.trim().length < 2) continue;
      for (let index = text.indexOf(needle); index >= 0; index = text.indexOf(needle, index + 1))
        candidates.push(index + shift);
      if (candidates.length) break;
    }
    if (!candidates.length) return Math.min(estimate, text.length);
    return candidates.reduce((best, candidate) =>
      (Math.abs(candidate - estimate) < Math.abs(best - estimate) ? candidate : best));
  }

  function openEditor(element, caret, append = false) {
    const start = append ? source.length : +element.dataset.mdStart;
    const end = append ? source.length : +element.dataset.mdEnd;
    const original = source.slice(start, end).replace(/\r\n/g, '\n');
    const textarea = document.createElement('textarea');
    textarea.className = 'md-editor';
    textarea.spellcheck = true;
    textarea.setAttribute('aria-label', append ? 'New text at the end of the document' : 'Markdown source of this block');
    textarea.value = original;
    if (append) reader.append(textarea);
    else {
      element.before(textarea);
      element.setAttribute('data-edit-hidden', '');
    }
    editor = { textarea, element, start, end, original, append };
    fit(textarea);
    textarea.focus({ preventScroll: true });
    const position = Math.max(0, Math.min(caret ?? original.length, original.length));
    textarea.setSelectionRange(position, position);
    textarea.addEventListener('input', () => {
      fit(textarea);
      updateChrome();
    });
    textarea.addEventListener('keydown', event => {
      if (event.key === 'Escape' || (event.key === 'Enter' && event.ctrlKey)) {
        event.preventDefault();
        enqueue(commit);
      } else if (event.key === 'Tab' && !event.shiftKey && !event.ctrlKey && !event.altKey) {
        // Indentation matters in Markdown lists; Escape is the way out of the editor.
        event.preventDefault();
        if (!document.execCommand('insertText', false, '  ')) textarea.setRangeText('  ', textarea.selectionStart, textarea.selectionEnd, 'end');
      }
    });
    updateChrome();
  }

  // Close the open editor, splicing its text into `source`. Returns how offsets after the
  // edited block moved, so a pending click can find its block again after the re-render.
  async function commit() {
    if (!editor) return null;
    const { textarea, element, start, end, original, append } = editor;
    const value = textarea.value;
    editor = null;
    if (value === original || (append && !value.trim())) {
      textarea.remove();
      element?.removeAttribute('data-edit-hidden');
      updateChrome();
      return { end, delta: 0 };
    }
    let replacement = toFileNewlines(value);
    if (append) {
      const separator = source === '' || /(\r?\n){2}$/.test(source) ? '' : /\r?\n$/.test(source) ? newline : newline + newline;
      replacement = separator + replacement + (/\r?\n$/.test(replacement) ? '' : newline);
    }
    source = source.slice(0, start) + replacement + source.slice(end);
    try {
      await render();
    } catch {
      // The splice stands; only the preview is stale. The re-render would have removed these.
      textarea.remove();
      element?.removeAttribute('data-edit-hidden');
      alert('The edit is kept, but the page could not be refreshed. Save to write it to disk.');
    }
    updateChrome();
    return { end, delta: replacement.length - (end - start) };
  }

  async function unlock() {
    const response = await post('source', {});
    if (response.status === 415) {
      alert(await response.text());
      return;
    }
    if (!response.ok) throw new Error('Could not read the file for editing.');
    const data = await response.json();
    source = saved = data.text;
    hash = data.hash;
    newline = data.newline;
    stale = false;
    active = true;
    remember(true);
    // A checkbox toggle rewrites the file without reloading the page, so the page's own
    // ranges may index an older version. Only a matching hash proves they do not.
    if (data.hash !== pageHash) {
      replaceContent(data.html);
      pageHash = data.hash;
    } else prepareContent();
    updateChrome();
  }

  async function save(force = false) {
    await commit();
    if (!active || (!force && source === saved)) return true;
    const text = source;
    let response;
    try {
      response = await post('save', { text, hash, force });
    } catch {
      alert('The file could not be saved.');
      return false;
    }
    if (response.status === 409) {
      if (confirm(`${baseTitle} was changed by another program since you started editing.\n\n` +
          'OK: overwrite it with your version.\nCancel: keep editing without saving.')) return save(true);
      stale = true;
      updateChrome();
      return false;
    }
    if (!response.ok) {
      alert(`The file could not be saved: ${await response.text()}`);
      return false;
    }
    hash = pageHash = (await response.json()).hash;
    saved = text;
    stale = false;
    updateChrome();
    return true;
  }

  async function relock() {
    await commit();
    if (isDirty()) {
      if (!confirm(`Save your changes to ${baseTitle} before locking?\n\nOK: save and lock.\nCancel: keep editing.`)) return;
      if (!await save()) return;
    }
    active = false;
    remember(false);
    prepareContent();
    updateChrome();
  }

  const toggleLock = () => enqueue(async () => {
    try {
      if (active) await relock();
      else await unlock();
    } catch {
      alert('Edit mode could not be changed.');
    }
  });

  // Capture, so the click reaches us before a link navigates or a heading folds.
  reader.addEventListener('click', event => {
    if (!active) return;
    if (editor && event.target === editor.textarea) return;
    event.preventDefault();
    if (!getSelection()?.isCollapsed) return;
    let block = event.target.closest?.('[data-md-start]');
    if (block && block.parentElement !== reader) block = null;
    const last = [...reader.children].filter(child => child !== editor?.textarea).at(-1);
    const append = !block && event.target === reader && (!last || event.clientY > last.getBoundingClientRect().bottom);
    if (!block && !append) {
      enqueue(commit);
      return;
    }
    const start = block ? +block.dataset.mdStart : 0;
    const caret = block ? caretFor(block, event.clientX, event.clientY,
      source.slice(start, +block.dataset.mdEnd).replace(/\r\n/g, '\n')) : null;
    enqueue(async () => {
      const moved = await commit();
      if (append) {
        openEditor(null, null, true);
        return;
      }
      const shifted = moved && start >= moved.end ? start + moved.delta : start;
      const target = reader.querySelector(`:scope > [data-md-start="${shifted}"]`);
      if (target) openEditor(target, caret);
    });
  }, true);

  lock.addEventListener('click', event => {
    event.stopPropagation();
    toggleLock();
  });

  document.addEventListener('keydown', event => {
    const control = event.ctrlKey && !event.altKey && !event.metaKey;
    if (control && !event.shiftKey && event.key.toLowerCase() === 's') {
      // Also stops the browser's own "save page", which would save our rendered HTML.
      event.preventDefault();
      if (active) enqueue(() => save());
    } else if (control && !event.shiftKey && event.key.toLowerCase() === 'e') {
      event.preventDefault();
      toggleLock();
    }
  }, true);

  window.addEventListener('beforeunload', event => {
    if (!isDirty()) return;
    event.preventDefault();
    event.returnValue = '';
  });

  window.mdviewEdit = {
    get active() { return active; },
    save: () => enqueue(() => save()),
    // Before a File command that acts on the file on disk. Resolves false to cancel it.
    settle: command => enqueue(async () => {
      await commit();
      if (!isDirty()) return true;
      if (command === 'file.save-as')
        return confirm(`Save As copies ${baseTitle} as it is on disk. Save your changes first?\n\nOK: save, then Save As.\nCancel: do nothing.`) && save();
      if (command === 'file.exit')
        return confirm(`Discard your unsaved changes to ${baseTitle} and exit?`);
      return true;
    }),
    // A live-reload event. Returns true when it must not reload the page.
    onExternalChange() {
      if (!isDirty()) return false;
      stale = true;
      updateChrome();
      return true;
    }
  };

  updateChrome();
  if (remembered()) toggleLock();
})();
