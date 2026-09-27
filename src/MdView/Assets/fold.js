(() => {
  const reader = document.querySelector('#reader-content');
  const isHeading = element => /^H[1-6]$/.test(element.tagName);
  // Source-only placeholders (edit mode's frontmatter and definitions) have nothing to fold.
  const isContent = element => !element.classList.contains('md-source-only');
  const collapsed = new Set();
  let foldable = [];

  const level = heading => Number(heading.tagName.slice(1));
  const isFoldable = heading => {
    let next = heading.nextElementSibling;
    while (next && !isContent(next)) next = next.nextElementSibling;
    return Boolean(next && (!isHeading(next) || level(next) > level(heading)));
  };

  function apply() {
    const stack = [];
    [...reader.children].forEach(element => {
      const heading = isHeading(element);
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

  // Edit mode replaces the reader's content, so headings are found afresh rather than held.
  function refresh() {
    collapsed.clear();
    foldable = [...reader.children].filter(isHeading).filter(isFoldable);
    foldable.forEach(heading => {
      heading.tabIndex = 0;
      // Keep native heading semantics for document navigation; role="button" would replace them.
      heading.setAttribute('aria-expanded', 'true');
    });
    apply();
  }

  const foldableHeading = target => {
    const heading = target.closest?.('h1, h2, h3, h4, h5, h6');
    return heading && heading.parentElement === reader && foldable.includes(heading) ? heading : null;
  };

  reader.addEventListener('click', event => {
    // In edit mode a click on a heading opens it for editing instead.
    if (document.documentElement.hasAttribute('data-editing')) return;
    const heading = foldableHeading(event.target);
    if (heading && getSelection()?.isCollapsed) toggle(heading);
  });
  reader.addEventListener('keydown', event => {
    if (event.key !== 'Enter' && event.key !== ' ') return;
    const heading = foldableHeading(event.target);
    if (!heading) return;
    event.preventDefault();
    toggle(heading);
  });

  function reveal(node) {
    let child = node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement;
    while (child && child.parentElement !== reader) child = child.parentElement;
    if (!child) return;

    const stack = [];
    for (const element of reader.children) {
      if (element === child) break;
      if (!isHeading(element)) continue;
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

  window.mdviewFold = { reveal, collapseAll, expandAll, refresh };
  refresh();
})();
