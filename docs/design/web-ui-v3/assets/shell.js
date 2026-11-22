(() => {
  'use strict';
  const root = document.documentElement;
  const model = window.EvidgateV3;
  const page = model?.byId.get(document.body.dataset.page);
  const aside = document.querySelector('[data-v3-sidebar]');
  const header = document.querySelector('[data-v3-topbar]');
  if (page && page.id !== '02' && aside && header) mountShell(page, aside, header);

  function mountShell(page, aside, header) {
    const {fixture, groups, byId, screens} = model;
    // Only manifest and static template data are inserted into markup. No URL parameters.
    const escape = str => str.replaceAll('&','&amp;').replaceAll('"','&quot;').replaceAll('<','&lt;').replaceAll('>','&gt;');
    const copy = (en, vi) => '<span data-en="'+escape(en)+'" data-vi="'+escape(vi)+'">'+escape(en)+'</span>';
    const name = (en, vi) => 'aria-label="'+escape(en)+'" data-label-en="'+escape(en)+'" data-label-vi="'+escape(vi)+'"';
    const tip = (en, vi) => 'title="'+escape(en)+'" data-tip-en="'+escape(en)+'" data-tip-vi="'+escape(vi)+'"';
    const shapes = {
      'grid': '<path d="M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z"/>',
      'folder': '<path d="M3 6h6l2 2h10v12H3z"/>',
      'run': '<path d="M5 3h14v18H5z M10 8l5 4-5 4z"/>',
      'inbox': '<path d="M3 15l3-10h12l3 10v4H3z M3 15h5l2 2h4l2-2h5"/>',
      'shield': '<path d="M12 2l8 3v6c0 5-4 9-8 11-4-2-8-6-8-11V5z M8 12l3 3 5-6"/>',
      'chart': '<path d="M3 4v16h18 M7 16v-4m5 4V8m5 8V5"/>',
      'queue': '<path d="M4 6h13M4 12h10M4 18h7m6-5 3 3-3 3m-4-3h7"/>',
      'branch': '<path d="M6 3v12a4 4 0 0 0 4 4h7 M6 11h4a6 6 0 0 0 6-6 M14 4h4v4"/>',
      'history': '<path d="M4 9a8 8 0 1 1 0 7 M3 3v6h6 M12 8v5l3 1"/>',
      'file': '<path d="M6 3h8l4 4v14H6z M14 3v5h4 M9 12h6m-6 4h5"/>',
      'people': '<path d="M3 20v-2a5 5 0 0 1 10 0v2 M16 14a5 5 0 0 1 5 5 M11 7a3 3 0 1 1-6 0 3 3 0 0 1 6 0 M15 4a3 3 0 0 1 0 6"/>',
      'settings': '<path d="M4 7h10m4 0h2M4 17h2m4 0h10 M14 4v6M8 14v6"/>',
      'code': '<path d="m8 7-5 5 5 5m8-10 5 5-5 5m-3-14-2 18"/>',
      'nav': '<path d="M3 6h18M3 12h18M3 18h18"/>',
      'search': '<path d="M16 10a6 6 0 1 1-12 0 6 6 0 0 1 12 0 M15 15l6 6"/>',
      'bell': '<path d="M4 16h16l-2-4V9a6 6 0 0 0-12 0v3z M10 20h4"/>',
      'moon': '<path d="M20 15A9 9 0 0 1 9 4a9 9 0 1 0 11 11"/>',
      'globe': '<path d="M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0 M3 12h18 M12 3a16 16 0 0 1 0 18 16 16 0 0 1 0-18"/>',
      'logo': '<path d="M3 5l9-4 9 4-9 4z M3 10l9 4 9-4 M3 15l9 4 9-4"/>',
      'clock': '<path d="M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0 M12 7v5l3 2"/>',
      'check': '<path d="m4 12 5 5L20 6"/>',
    };
    const icon = id => '<svg viewBox="0 0 24 24" class="v3-icon" aria-hidden="true" focusable="false">'+shapes[id]+'</svg>';
    function navigation() {
      return groups.map(([en,vi,items]) => '<div class="v3-nav__group"><p class="v3-nav__heading">'+copy(en,vi)+'<span class="v3-count">'+items.length+'</span></p>'+items.map(([id,symbol]) => {
        const item = byId.get(id), active = page.section === id;
        const current = active ? ' aria-current="'+(page.id === id ? 'page' : 'location')+'"' : '';
        const related = screens.filter(screen => screen.id !== id && screen.section === id).length;
        const count = related ? '<span aria-hidden="true" class="v3-count" '+tip(related+' contextual screens',related+' màn hình liên quan')+'>'+related+'</span>' : '';
        return '<a href="'+item.file+'" class="v3-nav__item'+(active?' v3-active':'')+'" '+name(item.en,item.vi)+' '+tip(item.en,item.vi)+current+'>'+icon(symbol)+'<span class="v3-nav__label">'+copy(item.en,item.vi)+'</span>'+count+'</a>';
      }).join('')+'</div>').join('');
    }
    const orgScope = document.body.dataset.scope === 'organization';
    const person = fixture.people[document.body.dataset.persona] ?? fixture.people.minh;
    const nav = navigation();
    aside.id = 'sidebar';
    aside.innerHTML = '<div class="v3-brand"><span class="v3-logo">'+icon('logo')+'</span><span class="v3-brand__copy">Evidgate<small>'+copy(...fixture.subtitle)+'</small></span></div><nav '+name('Workspace navigation','Điều hướng không gian làm việc')+'>'+nav+'</nav>';
    const title = document.querySelector('title');
    const short = (s, fallback) => s ? s.replace(/\s*·\s*Evidgate$/, '') : fallback;
    const titleCopy = copy(short(title?.dataset.en,page.en),short(title?.dataset.vi,page.vi));
    const button = (attrs, label, symbol, classes='v3-btn--icon v3-btn--ghost') => '<button type="button" class="v3-btn '+classes+'" '+attrs+' '+name(...label)+' '+tip(...label)+'>'+icon(symbol)+'</button>';
    const navLabel = ['Navigation','Điều hướng'];
    const scope = (en, vi, value) => '<label><span class="v3-sr-only">'+copy(en,vi)+'</span><select class="v3-scope-switch"><option data-en="'+escape(value)+'" data-vi="'+escape(value)+'">'+escape(value)+'</option></select></label>';
    header.innerHTML = '<div class="v3-inline">'+button('data-v3-collapse aria-controls="sidebar" aria-expanded="true"',navLabel,'nav','v3-btn--icon v3-btn--ghost v3-collapse')+button('popovertarget="nav-pop"',navLabel,'nav','v3-btn--icon v3-btn--ghost v3-mobilemenu')+'<nav class="v3-breadcrumbs" '+name('Breadcrumb','Đường dẫn')+'>'+copy(...fixture.subtitle)+'<span aria-hidden="true">/</span>'+(orgScope?copy('Organization settings','Cài đặt tổ chức')+'<span aria-hidden="true">/</span>':'')+'<b aria-current="page">'+titleCopy+'</b></nav></div>'
      + '<div class="v3-topbar__tools">'+scope('Organization','Tổ chức',fixture.org)+scope('Project','Dự án',fixture.project)
      + '<form role="search" class="v3-shell-search"><button type="submit" '+name('Find a screen','Tìm màn hình')+'>'+icon('search')+'</button><label>'+copy('Search','Tìm kiếm')+'<input type="search" data-shell-query></label><kbd>/</kbd></form>'
      + button('id="theme" aria-pressed="false"',['Dark theme','Giao diện tối'],'moon')+button('popovertarget="alerts-pop"',['Notifications','Thông báo'],'bell')+'<button type="button" id="lang" class="v3-btn" lang="vi" aria-label="Tiếng Việt">'+icon('globe')+'<b>VI</b></button><button type="button" class="v3-btn v3-btn--ghost v3-account" popovertarget="account-pop" '+name('Signed in as '+person[0],'Đang đăng nhập: '+person[0])+'><span class="v3-avatar" aria-hidden="true">'+person[1]+'</span><span class="v3-account__name">'+person[0]+'</span></button></div>';
    const pop = (id, en, vi, body) => '<aside popover id="'+id+'" aria-labelledby="'+id+'-title"><h2 id="'+id+'-title">'+copy(en,vi)+'</h2>'+body+'<button type="button" class="v3-btn" popovertarget="'+id+'" popovertargetaction="hide">'+copy('Close','Đóng')+'</button></aside>';
    header.insertAdjacentHTML('afterend',pop('nav-pop',...navLabel,'<nav class="v3-navpop" '+name(...navLabel)+'>'+nav+'</nav>')
      + pop('alerts-pop','Notifications','Thông báo','<p>'+copy('No new notifications.','Không có thông báo mới.')+'</p>')
      + pop('account-pop','Signed in as','Đang đăng nhập','<p><strong>'+person[0]+'</strong> · '+copy(person[2],person[3])+'</p><p>'+fixture.org+'</p>')
      + pop('search-pop','Find a screen','Tìm màn hình','<label class="v3-field">'+copy('Screen name or number','Tên hoặc số màn hình')+'<input type="search" class="v3-input" data-v3-find aria-controls="screen-links"></label><nav id="screen-links" class="v3-screen-links">'+screens.map(s=>'<a href="'+s.file+'" data-screen="'+s.id+'"><code>'+s.id+'</code> '+copy(s.en,s.vi)+'</a>').join('')+'</nav><p data-v3-no-results hidden>'+copy('No matching screens.','Không có màn hình phù hợp.')+'</p>'));
  }
  const themeButton = document.getElementById('theme');
  const languageButton = document.getElementById('lang');
  const sidebar = document.getElementById('sidebar');
  const collapseButton = document.querySelector('[data-v3-collapse]');
  const osTheme = matchMedia('(prefers-color-scheme: dark)');

  // Storage may be unavailable for local-file previews or private sessions.
  function preference(key, value) {
    try {
      if (value === undefined) return localStorage.getItem(`eg-v3:${key}`);
      localStorage.setItem(`eg-v3:${key}`, value);
    } catch { /* Controls still work for the current document. */ }
  }

  function paintTheme(theme) {
    if (theme === 'light' || theme === 'dark') root.dataset.theme = theme;
    const dark = (root.dataset.theme ?? (osTheme.matches ? 'dark' : 'light')) === 'dark';
    themeButton?.setAttribute('aria-pressed', String(dark));
  }
  paintTheme(preference('theme'));
  osTheme.addEventListener('change', () => paintTheme());
  themeButton?.addEventListener('click', () => {
    const next = themeButton.getAttribute('aria-pressed') === 'true' ? 'light' : 'dark';
    paintTheme(next);
    preference('theme', next);
  });

  function paintLanguage(value) {
    const lang = value === 'vi' ? 'vi' : 'en';
    root.lang = lang;
    // Translate only marked leaves: changing ancestors would destroy controls and focus.
    for (const leaf of document.querySelectorAll('[data-en][data-vi]')) leaf.textContent = leaf.dataset[lang];
    for (const el of document.querySelectorAll('[data-tip-en][data-tip-vi]')) el.title = lang === 'vi' ? el.dataset.tipVi : el.dataset.tipEn;
    for (const el of document.querySelectorAll('[data-label-en][data-label-vi]')) el.setAttribute('aria-label', lang === 'vi' ? el.dataset.labelVi : el.dataset.labelEn);
    if (languageButton) {
      const target = lang === 'en' ? 'vi' : 'en';
      languageButton.lang = target;
      languageButton.title = languageButton.ariaLabel = target === 'vi' ? 'Tiếng Việt' : 'English';
      languageButton.querySelector('b').textContent = target.toUpperCase();
    }
  }
  paintLanguage(preference('language') ?? root.lang);
  languageButton?.addEventListener('click', () => {
    paintLanguage(root.lang === 'vi' ? 'en' : 'vi');
    preference('language', root.lang);
  });

  if (sidebar && collapseButton) {
    const show = expanded => {
      sidebar.hidden = !expanded;
      collapseButton.setAttribute('aria-expanded', String(expanded));
    };
    show(preference('sidebar') !== 'collapsed');
    collapseButton.addEventListener('click', () => {
      show(sidebar.hidden);
      preference('sidebar', sidebar.hidden ? 'collapsed' : 'expanded');
    });
  }

  const find = document.querySelector('[data-v3-find]');
  if (find) {
    const queryField = header.querySelector('[data-shell-query]');
    queryField.form.addEventListener('submit', event => {
      event.preventDefault();
      document.getElementById('search-pop').showPopover({source: queryField});
      find.value = queryField.value;
      find.dispatchEvent(new Event('input'));
      find.focus();
    });
    document.addEventListener('keydown', event => {
      if (event.key !== '/' || event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey || !queryField.getClientRects().length || document.querySelector('dialog[open],:popover-open') || event.target.closest('input,select,textarea,[contenteditable]')) return;
      event.preventDefault();
      queryField.focus();
    });
    find.addEventListener('input', () => {
      const query = find.value.trim().toLocaleLowerCase(root.lang);
      const links = document.querySelectorAll('#screen-links a');
      for (const link of links) {
        const copy = link.querySelector('[data-en]');
        link.hidden = ![copy.dataset.en, copy.dataset.vi, link.dataset.screen ?? ''].some(label => label.toLocaleLowerCase(root.lang).includes(query));
      }
      document.querySelector('[data-v3-no-results]').hidden = [...links].some(link => !link.hidden);
    });
  }

  document.addEventListener('keydown', event => {
    if (event.key === 'Escape') {
      for (const tip of document.querySelectorAll('.v3-tooltip-anchor:is(:focus-within,:hover)')) tip.dataset.dismissed = '';
    }
  });
  for (const tip of document.querySelectorAll('.v3-tooltip-anchor')) {
    const reset = () => delete tip.dataset.dismissed;
    tip.addEventListener('focusin', reset);
    tip.addEventListener('pointerenter', reset);
  }
})();
