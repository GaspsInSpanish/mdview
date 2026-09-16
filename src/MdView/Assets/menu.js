(() => {
  const bar = document.querySelector('.menu-bar');
  const reader = document.querySelector('#reader-content');
  const menus = [...bar.querySelectorAll('.menu')];
  const buttons = menus.map(menu => menu.querySelector('.menu-button'));
  let openIndex = -1;

  const items = index => [...menus[index].querySelectorAll('.menu-item')];

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
        }
        closeMenu(true);
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
  document.addEventListener('keydown', event => {
    if (event.key === 'Alt' && !event.repeat) {
      event.preventDefault();
      closeMenu();
      buttons[0].focus();
    } else if (event.key === 'Escape' && openIndex >= 0) {
      event.preventDefault();
      closeMenu(true);
    }
  });
})();
