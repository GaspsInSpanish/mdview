(() => {
  const bar = document.querySelector('.menu-bar');
  const reader = document.querySelector('#reader-content');
  const menus = [...bar.querySelectorAll('.menu')];
  const buttons = menus.map(menu => menu.querySelector('.menu-button'));
  const copyItem = bar.querySelector('[data-command="edit.copy"]');
  const findBar = document.querySelector('.find-bar');
  const findQuery = findBar.querySelector('.find-query');
  const findStatus = findBar.querySelector('.find-status');
  const highlightNames = ['mdview-find-results', 'mdview-find-current'];
  const canHighlight = Boolean(window.CSS?.highlights && window.Highlight);
  let findRanges = [];
  let currentMatch = -1;
  let searchTimer = 0;
  let openIndex = -1;

  const items = index => [...menus[index].querySelectorAll('.menu-item')];

  function keepPanelInViewport(panel) {
    panel.style.setProperty('--menu-panel-shift', '0px');
    const rectangle = panel.getBoundingClientRect();
    const maximumLeft = Math.max(0, innerWidth - rectangle.width);
    const clampedLeft = Math.min(Math.max(0, rectangle.left), maximumLeft);
    panel.style.setProperty('--menu-panel-shift', `${clampedLeft - rectangle.left}px`);
  }

  function closeMenu(restoreDocument = false) {
    menus.forEach((menu, index) => {
      menu.classList.remove('is-open');
      menu.querySelector('.menu-panel').hidden = true;
      buttons[index].setAttribute('aria-expanded', 'false');
    });
    openIndex = -1;
    if (restoreDocument) reader.focus();
  }

  function openMenu(index, itemIndex = null) {
    menus.forEach((menu, menuIndex) => {
      const open = menuIndex === index;
      menu.classList.toggle('is-open', open);
      menu.querySelector('.menu-panel').hidden = !open;
      buttons[menuIndex].setAttribute('aria-expanded', String(open));
    });
    openIndex = index;
    keepPanelInViewport(menus[index].querySelector('.menu-panel'));
    if (itemIndex !== null) {
      const menuItems = items(index);
      menuItems[(itemIndex + menuItems.length) % menuItems.length].focus();
    }
  }

  function moveMenu(delta, focusItem) {
    const current = openIndex >= 0 ? openIndex : Math.max(0, buttons.indexOf(document.activeElement));
    const next = (current + delta + menus.length) % menus.length;
    if (openIndex >= 0) {
      openMenu(next, focusItem ? 0 : null);
      if (!focusItem) buttons[next].focus();
    } else buttons[next].focus();
  }

  function selectionText() {
    const selection = window.getSelection();
    return selection && !selection.isCollapsed ? selection.toString() : '';
  }

  function updateCopyState() {
    copyItem.setAttribute('aria-disabled', String(selectionText().length === 0));
  }

  async function copySelection() {
    const text = selectionText();
    if (!text) return false;
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      try { return document.execCommand('copy'); }
      catch { return false; }
    }
  }

  function selectDocument() {
    const range = document.createRange();
    range.selectNodeContents(reader);
    const selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
    updateCopyState();
  }

  function clearHighlights() {
    if (canHighlight) highlightNames.forEach(name => CSS.highlights.delete(name));
  }

  function visibleTextNodes() {
    const nodes = [];
    const walker = document.createTreeWalker(reader, NodeFilter.SHOW_TEXT, {
      acceptNode(node) {
        if (!node.data) return NodeFilter.FILTER_REJECT;
        const element = node.parentElement;
        if (!element || element.closest('[hidden], [aria-hidden="true"]')) return NodeFilter.FILTER_REJECT;
        const style = getComputedStyle(element);
        return style.display === 'none' || style.visibility === 'hidden'
          ? NodeFilter.FILTER_REJECT
          : NodeFilter.FILTER_ACCEPT;
      }
    });
    while (walker.nextNode()) nodes.push(walker.currentNode);
    return nodes;
  }

  function collectMatches(query) {
    const nodes = visibleTextNodes();
    const offsets = [];
    let text = '';
    nodes.forEach(node => {
      offsets.push(text.length);
      text += node.data;
    });
    const ranges = [];
    const expression = new RegExp(query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'giu');
    for (const match of text.matchAll(expression)) {
      const found = match.index;
      const end = found + match[0].length;
      let startNode = offsets.length - 1;
      let endNode = offsets.length - 1;
      while (startNode > 0 && offsets[startNode] > found) startNode--;
      while (endNode > 0 && offsets[endNode] >= end) endNode--;
      const range = document.createRange();
      range.setStart(nodes[startNode], found - offsets[startNode]);
      range.setEnd(nodes[endNode], end - offsets[endNode]);
      ranges.push(range);
    }
    return ranges;
  }

  function scrollToCurrent() {
    if (currentMatch < 0) return;
    const rectangle = findRanges[currentMatch].getBoundingClientRect();
    const obstruction = Math.max(bar.getBoundingClientRect().bottom,
      findBar.hidden ? 0 : findBar.getBoundingClientRect().bottom);
    if (rectangle.top < obstruction + 8 || rectangle.bottom > innerHeight - 8)
      scrollBy(0, rectangle.top - obstruction - 8);
  }

  function showCurrentMatch(scroll = true) {
    clearHighlights();
    if (canHighlight && findRanges.length) {
      CSS.highlights.set(highlightNames[0], new Highlight(...findRanges));
      CSS.highlights.set(highlightNames[1], new Highlight(findRanges[currentMatch]));
    }
    if (findRanges.length) {
      findBar.classList.remove('is-no-matches');
      findStatus.textContent = `${currentMatch + 1} / ${findRanges.length}`;
      if (scroll) scrollToCurrent();
    } else {
      findBar.classList.toggle('is-no-matches', findQuery.value.length > 0);
      findStatus.textContent = findQuery.value.length > 0 ? 'No matches' : '0 / 0';
    }
  }

  function runSearch() {
    findRanges = findQuery.value ? collectMatches(findQuery.value) : [];
    currentMatch = findRanges.length ? 0 : -1;
    showCurrentMatch(findRanges.length > 0);
  }

  function scheduleSearch() {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(runSearch, 150);
  }

  function moveMatch(delta) {
    if (!findRanges.length) return;
    currentMatch = (currentMatch + delta + findRanges.length) % findRanges.length;
    showCurrentMatch();
  }

  function openFind() {
    if (!findBar || !findQuery) return false;
    findBar.hidden = false;
    findQuery.focus();
    findQuery.select();
    if (findQuery.value) runSearch();
    return true;
  }

  function closeFind() {
    clearTimeout(searchTimer);
    findBar.hidden = true;
    findQuery.value = '';
    findRanges = [];
    currentMatch = -1;
    clearHighlights();
    findBar.classList.remove('is-no-matches');
    findStatus.textContent = '0 / 0';
    reader.focus();
  }

  buttons.forEach((button, index) => {
    button.addEventListener('click', event => {
      event.stopPropagation();
      if (openIndex === index) closeMenu();
      else openMenu(index);
    });
  });

  menus.forEach((menu, menuIndex) => {
    menu.querySelector('.menu-panel').addEventListener('click', event => event.stopPropagation());
    items(menuIndex).forEach(item => {
      item.addEventListener('click', async () => {
        const command = item.dataset.command;
        let restoreDocument = true;
        if (command.startsWith('theme.')) {
          const theme = command.slice('theme.'.length);
          item.disabled = true;
          try {
            const response = await fetch('/theme', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ theme, token: window.mdviewToggle.token })
            });
            if (!response.ok) throw new Error('Theme change failed.');
            window.mdviewApplyTheme(theme);
          } catch {
            alert('Theme change was not saved.');
          } finally {
            item.disabled = false;
          }
        } else if (command.startsWith('file.')) {
          item.disabled = true;
          try {
            const response = await fetch(`/command/${encodeURIComponent(window.mdviewToggle.id)}`, {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ command, token: window.mdviewToggle.token })
            });
            if (!response.ok) throw new Error('File command failed.');
            if (command === 'file.save-as' && response.status === 200) {
              const result = await response.json();
              location.assign(`/d/${encodeURIComponent(result.id)}`);
            }
          } catch {
            alert('The file command could not be completed.');
          } finally {
            item.disabled = false;
          }
        } else if (command === 'edit.copy') {
          await copySelection();
        } else if (command === 'edit.select-all') {
          selectDocument();
        } else if (command === 'edit.find') {
          restoreDocument = !openFind();
        }
        closeMenu(restoreDocument);
      });
      item.addEventListener('keydown', event => {
        const menuItems = items(menuIndex);
        const index = menuItems.indexOf(event.currentTarget);
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
          event.preventDefault();
          const delta = event.key === 'ArrowDown' ? 1 : -1;
          menuItems[(index + delta + menuItems.length) % menuItems.length].focus();
        } else if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
          event.preventDefault();
          moveMenu(event.key === 'ArrowRight' ? 1 : -1, true);
        } else if (event.key === 'Escape') {
          event.preventDefault();
          closeMenu(true);
        } else if (event.key === 'Enter') {
          event.preventDefault();
          event.currentTarget.click();
        }
      });
    });
  });

  bar.addEventListener('keydown', event => {
    if (!event.target.classList.contains('menu-button')) return;
    const index = buttons.indexOf(event.target);
    if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
      event.preventDefault();
      moveMenu(event.key === 'ArrowRight' ? 1 : -1, false);
    } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      openMenu(index, event.key === 'ArrowDown' ? 0 : items(index).length - 1);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      closeMenu(true);
    }
  });

  document.addEventListener('click', () => closeMenu());
  document.addEventListener('selectionchange', updateCopyState);
  window.addEventListener('resize', () => {
    if (openIndex >= 0) keepPanelInViewport(menus[openIndex].querySelector('.menu-panel'));
  });
  findQuery.addEventListener('input', scheduleSearch);
  findQuery.addEventListener('keydown', event => {
    if (event.key === 'Enter') {
      event.preventDefault();
      moveMatch(event.shiftKey ? -1 : 1);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      closeFind();
    }
  });
  findBar.addEventListener('click', event => {
    const action = event.target.closest('[data-find-action]')?.dataset.findAction;
    if (action === 'previous') moveMatch(-1);
    else if (action === 'next') moveMatch(1);
    else if (action === 'close') closeFind();
  });
  document.addEventListener('keydown', event => {
    const control = event.ctrlKey && !event.altKey && !event.metaKey;
    const editingFind = event.target === findQuery;
    if (control && event.key.toLowerCase() === 'f') {
      if (openFind()) event.preventDefault();
    } else if (control && event.key.toLowerCase() === 'a' && !editingFind) {
      event.preventDefault();
      selectDocument();
    } else if (control && event.key.toLowerCase() === 'c' && !editingFind && selectionText()) {
      event.preventDefault();
      void copySelection();
    } else if (event.key === 'Alt' && !event.repeat) {
      event.preventDefault();
      closeMenu();
      buttons[0].focus();
    } else if (event.key === 'Escape' && openIndex >= 0) {
      event.preventDefault();
      closeMenu(true);
    }
  });
  updateCopyState();
})();
