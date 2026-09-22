(() => {
  const reader = document.querySelector('#reader-content');
  const headings = [...reader.children].filter(element => /^H[1-6]$/.test(element.tagName));
  const collapsed = new Set();

  const level = heading => Number(heading.tagName.slice(1));
  const isFoldable = heading => {
    const next = heading.nextElementSibling;
    return Boolean(next && (!/^H[1-6]$/.test(next.tagName) || level(next) > level(heading)));
  };
  const foldable = headings.filter(isFoldable);

  function apply() {
    const stack = [];
    [...reader.children].forEach(element => {
      const heading = /^H[1-6]$/.test(element.tagName);
      if (heading) {
        const headingLevel = level(element);
        while (stack.length && stack.at(-1).level >= headingLevel) stack.pop();
        element.toggleAttribute('data-fold-hidden', stack.some(entry => entry.isCollapsed));
        stack.push({ level: headingLevel, isCollapsed: collapsed.has(element) });
        if (isFoldable(element)) element.setAttribute('aria-expanded', String(!collapsed.has(element)));
      } else {
        element.toggleAttribute('data-fold-hidden', stack.some(entry => entry.isCollapsed));
      }
    });
  }

  function toggle(heading) {
    if (collapsed.has(heading)) collapsed.delete(heading);
    else collapsed.add(heading);
    apply();
  }

  foldable.forEach(heading => {
    heading.tabIndex = 0;
    // Keep native heading semantics for document navigation; role="button" would replace them.
    heading.setAttribute('aria-expanded', 'true');
    heading.addEventListener('click', () => {
      if (getSelection()?.isCollapsed) toggle(heading);
    });
    heading.addEventListener('keydown', event => {
      if (event.key !== 'Enter' && event.key !== ' ') return;
      event.preventDefault();
      toggle(heading);
    });
  });

  function reveal(node) {
    let child = node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement;
    while (child && child.parentElement !== reader) child = child.parentElement;
    if (!child) return;

    const stack = [];
    for (const element of reader.children) {
      if (element === child) break;
      if (!/^H[1-6]$/.test(element.tagName)) continue;
      const headingLevel = level(element);
      while (stack.length && level(stack.at(-1)) >= headingLevel) stack.pop();
      stack.push(element);
    }
    stack.forEach(heading => collapsed.delete(heading));
    apply();
  }

  function collapseAll() {
    foldable.forEach(heading => collapsed.add(heading));
    apply();
  }

  function expandAll() {
    collapsed.clear();
    apply();
  }

  window.mdviewFold = { reveal, collapseAll, expandAll };
  apply();
})();
