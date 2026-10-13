/* Shared navigation for the static design pages. Labels, targets and icons have
   one source of truth; page bodies retain their own state and locale controls. */
(function () {
  'use strict';
  var root = document.documentElement;
  var KEY = 'evidgate.menuCollapsed';
  var preference = null;
  try { preference = localStorage.getItem(KEY); } catch (e) { /* storage optional */ }
  var collapsed = preference === '1' || (preference !== '0' && innerWidth < 1280);
  root.classList.toggle('eg-menu-collapsed', collapsed);
  function lang() { return (root.lang || 'en').slice(0, 2) === 'vi' ? 'vi' : 'en'; }
  // Each item owns a small, distinct stroke icon; no external icon dependency.
  var groups = [
    ['Tổng quan', 'Overview', [
      ['overview','Tổng quan','Overview','24-project-connection/evidgate-project-overview.html','M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z'],
      ['quality','Chất lượng','Quality','22-quality-overview/evidgate-quality-overview.html','m5 12 4 4L19 6M4 4h16v16H4z'],
      ['onboarding','Bắt đầu','Onboarding','23-onboarding-wizard/evidgate-onboarding.html','M12 3v18M3 12h18m-6-6 6 6-6 6']
    ]],
    ['Kiểm thử', 'QA', [
      ['catalog','Danh mục kiểm thử','Test catalog','25-test-catalog/evidgate-test-catalog.html','M4 4h16v16H4zM8 8h8M8 12h8M8 16h5'],
      ['mapping','Ánh xạ kết quả','Mapping resolution','26-mapping-resolution/evidgate-mapping-resolution.html','M3 5h5v5H3zM16 14h5v5h-5zM8 7h7v9h1m-4-3 3 3-3 3'],
      ['runs','Lượt chạy','Runs','03-run-list/evidgate-runs.html','m8 4 12 8-12 8z'],
      ['failures','Hộp thư lỗi','Failure Inbox','08-failure-inbox/evidgate-failure-inbox.html','M3 5h18v15H3zM3 14h5l2 3h4l2-3h5M12 8v3m0 2h.01']
    ]],
    ['Sửa cơ chế kiểm thử', 'Healing', [
      ['healing','Xem xét sửa lỗi','Healing Review','12-healing-queue/evidgate-healing-review.html','m14 6 4 4M3 21l9-9m2-6a6 6 0 0 1 7-3l-4 4 2 2 4-4a6 6 0 0 1-8 7L6 21H3v-3l9-9'],
      ['knowledge','Tri thức và thu hồi','Knowledge lineage','15-knowledge-revocation/evidgate-knowledge-lineage.html','M12 7v14M3 4c4-1 6 0 9 3 3-3 5-4 9-3v14c-4-1-6 0-9 3-3-3-5-4-9-3z'],
      ['availability','Trạng thái sửa lỗi','Availability','16-healing-availability/evidgate-healing-availability.html','M2 12h4l3-7 6 14 3-7h4']
    ]],
    ['Test Studio V2','Test Studio V2',[
      ['studio','Test Studio','Test Studio','17-studio-template-gallery/evidgate-test-studio.html','M4 4h16v16H4zM4 9h16M9 9v11'],
      ['suites','Bộ kiểm thử','Suite composition','20-suite-composition/evidgate-suite-composition.html','M3 3h7v7H3zM14 3h7v7h-7zM8 14h8v7H8zM6 10v3h12v-3M12 13v1']
    ]],
    ['Phát hành','Release',[
      ['releases','Bản phát hành','Releases','21-release-view/evidgate-release-view.html','M5 3h14v18H5zM8 7h8M8 11h8m-7 5 2 2 4-4'],
      ['policies','Chính sách phát hành','Release policies','30-policy-revisions/evidgate-policy-revisions.html','m12 2 9 4v6c0 5-5 8-9 10-4-2-9-5-9-10V6zM8 12l3 3 5-6'],
      ['deploy','Mục tiêu triển khai','Deploy targets','38-deploy-targets/evidgate-deploy-targets.html','M12 3v12m-5-5 5 5 5-5M4 15v6h16v-6']
    ]],
    ['Cài đặt','Settings',[
      ['settings','Chung','General','27-settings-shell/evidgate-settings.html','M4 6h16M4 12h16M4 18h16M8 3v6M16 9v6M10 15v6'],
      ['members','Thành viên và vai trò','Members & roles','28-members-roles/evidgate-members-roles.html','M16 21v-3a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v3M17 4a4 4 0 0 1 0 8m2 3a4 4 0 0 1 3 3v3M13 7a4 4 0 1 1-8 0 4 4 0 0 1 8 0'],
      ['tokens','Mã truy cập API','API tokens','37-api-tokens/evidgate-api-tokens.html','M14 7a5 5 0 1 1-4-5M10 10l-8 8v4h4v-3h3v-3l4-4'],
      ['secrets','Tham chiếu bí mật','Secret references','29-secret-references/evidgate-secret-references.html','M5 10h14v11H5zM8 10V6a4 4 0 0 1 8 0v4M12 14v3'],
      ['integrations','Tích hợp và webhook','Integrations & webhooks','35-integrations-webhooks/evidgate-webhook-integrations.html','M8 2v5m8-5v5M6 7h12v4a6 6 0 0 1-12 0zM12 17v5'],
      ['agents','Nhóm agent','Agent pools','34-agent-pools/evidgate-agents-pools.html','M5 7h14v13H5zM9 11h.01M15 11h.01M9 16h6M12 3v4M2 11h3m14 0h3'],
      ['quota','Hạn mức và lưu giữ','Quota & retention','31-quota-retention/evidgate-usage-retention.html','M4 21V11h4v10m4 0V3h4v18m4 0V7h2v14'],
      ['audit','Nhật ký kiểm toán','Audit log','32-audit-log/evidgate-audit-log.html','M5 3h14v18H5zM8 7h8M8 11h8M8 15h4M8 18h6'],
      ['security','Sự kiện bảo mật','Security events','33-security-events/evidgate-security-events.html','m12 2 9 4v6c0 5-5 8-9 10-4-2-9-5-9-10V6zM12 7v6m0 3h.01'],
      ['support','Quyền truy cập hỗ trợ','Support access','36-support-access/evidgate-support-access.html','M3 14v-3a9 9 0 0 1 18 0v3M3 12h4v7H3zM17 12h4v7h-4zM21 19c0 2-3 3-7 3']
    ]]
  ];
  var brand = '<svg aria-hidden="true" viewBox="0 0 24 28" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 3h18v22H3zM7 8h10M7 14h7M7 20h10"/></svg>';
  function icon(path) { return '<svg aria-hidden="true" focusable="false" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="' + path + '"/></svg>'; }
  function init() {
    var sidebar = document.querySelector('[data-eg-nav]');
    var bar = document.querySelector('header.topbar, .topbar');
    if (!sidebar || !bar) return;
    root.classList.add('eg-nav-ready');
    var shell = sidebar.parentElement, main = sidebar.nextElementSibling;
    shell.setAttribute('data-eg-shell', '');
    main.setAttribute('data-eg-main', '');
    var mobile = window.matchMedia('(max-width: 767px)');
    var drawer = false;
    var btn = document.createElement('button');
    btn.type = 'button'; btn.id = 'menuToggle'; btn.className = 'menu-toggle';
    btn.setAttribute('aria-controls', sidebar.id);
    bar.insertBefore(btn, bar.firstChild);
    var tooltip = document.createElement('div');
    tooltip.id = 'eg-nav-tooltip'; tooltip.className = 'eg-nav-tooltip';
    tooltip.setAttribute('role', 'tooltip'); tooltip.hidden = true;
    document.body.appendChild(tooltip);
    var scrim = document.createElement('button');
    scrim.type = 'button'; scrim.className = 'eg-nav-scrim'; scrim.tabIndex = -1; scrim.hidden = true;
    document.body.appendChild(scrim);
    var tooltipAnchor = null;
    function hideTooltip() {
      if (tooltipAnchor) tooltipAnchor.removeAttribute('aria-describedby');
      tooltipAnchor = null; tooltip.hidden = true;
    }
    function showTooltip(link) {
      if (!collapsed || mobile.matches) return;
      hideTooltip(); tooltipAnchor = link;
      tooltip.textContent = link.getAttribute('aria-label'); tooltip.hidden = false;
      link.setAttribute('aria-describedby', tooltip.id);
      var box = link.getBoundingClientRect();
      tooltip.style.left = (box.right + 10) + 'px';
      tooltip.style.top = Math.max(8, Math.min(innerHeight - tooltip.offsetHeight - 8, box.top + (box.height - tooltip.offsetHeight) / 2)) + 'px';
    }
    function scrollActive() {
      var area = sidebar.querySelector('.eg-nav-scroll'), active = sidebar.querySelector('.active');
      if (active && area) {
        var a = active.getBoundingClientRect(), b = area.getBoundingClientRect();
        if (a.top < b.top || a.bottom > b.bottom) area.scrollTop += a.top - b.top - (b.height - a.height) / 2;
      }
    }
    function paintToggle() {
      var expanded = mobile.matches ? drawer : !collapsed;
      var text = lang() === 'vi' ? (expanded ? 'Thu gọn menu' : 'Mở rộng menu') : (expanded ? 'Collapse menu' : 'Expand menu');
      btn.setAttribute('aria-expanded', String(expanded)); btn.setAttribute('aria-label', text);
      btn.innerHTML = icon(expanded ? 'm14 6-6 6 6 6' : 'm10 6 6 6-6 6') + '<span class="menu-toggle-label">' + text + '</span>';
      scrim.setAttribute('aria-label', lang() === 'vi' ? 'Đóng menu' : 'Close menu');
    }
    function render() {
      hideTooltip();
      var vi = lang() === 'vi', current = root.dataset.nav;
      var focused = document.activeElement && document.activeElement.dataset.egItem;
      var oldScroll = sidebar.querySelector('.eg-nav-scroll');
      var scroll = oldScroll ? oldScroll.scrollTop : null;
      sidebar.setAttribute('aria-label', vi ? 'Điều hướng chính' : 'Primary navigation');
      sidebar.innerHTML = '<div class="eg-nav-brand-row"><a class="eg-nav-brand" href="../01-app-shell/evidgate-workspace-shell.html" aria-label="Evidgate">' + brand + '<span class="eg-nav-wordmark">Evidgate</span></a><button type="button" class="eg-nav-close" aria-label="' + (vi ? 'Đóng menu' : 'Close menu') + '">' + icon('m6 6 12 12M18 6 6 18') + '</button></div>' +
        '<div class="eg-nav-scope"><span class="eg-nav-org-mark" aria-hidden="true">LC</span><div class="eg-nav-scope-text"><strong>Lotus Commerce</strong><span>storefront-web</span></div></div>' +
        '<nav class="eg-nav-scroll" aria-label="' + (vi ? 'Không gian làm việc' : 'Workspace') + '">' + groups.map(function (group) {
          return '<section class="eg-nav-group"><h2 class="eg-nav-group-label">' + group[vi ? 0 : 1] + '</h2>' + group[2].map(function (item) {
            var selected = current === item[0];
            var ownPage = location.pathname.endsWith('/' + item[3]);
            var label = item[vi ? 1 : 2];
            return '<a class="eg-nav-item' + (selected ? ' active' : '') + '" data-eg-item="' + item[0] + '" href="../' + item[3] + '" aria-label="' + label + '"' + (selected ? ' aria-current="' + (ownPage ? 'page' : 'true') + '"' : '') + '>' + icon(item[4]) + '<span class="eg-nav-item-label">' + label + '</span></a>';
          }).join('') + '</section>';
        }).join('') + '</nav><div class="eg-nav-account"><span class="eg-nav-avatar" aria-hidden="true">LN</span><div class="eg-nav-account-text"><strong>Linh Nguyễn</strong><span>qa_engineer</span></div></div>';
      sidebar.querySelector('.eg-nav-close').addEventListener('click', function () { setDrawer(false); });
      sidebar.querySelectorAll('.eg-nav-item').forEach(function (link) {
        link.addEventListener('mouseenter', function () { showTooltip(link); });
        link.addEventListener('mouseleave', hideTooltip);
        link.addEventListener('focus', function () { showTooltip(link); });
        link.addEventListener('blur', hideTooltip);
      });
      sidebar.querySelector('.eg-nav-scroll').addEventListener('scroll', function () {
        // Focus can scroll a rail item after its focus event: keep the tooltip
        // anchored to that focused item rather than hiding it on that scroll.
        if (tooltipAnchor && document.activeElement === tooltipAnchor) showTooltip(tooltipAnchor);
        else hideTooltip();
      });
      if (scroll !== null) sidebar.querySelector('.eg-nav-scroll').scrollTop = scroll;
      if (focused) sidebar.querySelector('[data-eg-item="' + focused + '"]').focus({preventScroll:true});
      paintToggle(); requestAnimationFrame(scrollActive);
    }
    function setDrawer(open, returnFocus) {
      drawer = open && mobile.matches;
      root.classList.toggle('eg-drawer-open', drawer);
      scrim.hidden = !drawer;
      main.inert = drawer;
      if (drawer) {
        sidebar.setAttribute('role', 'dialog'); sidebar.setAttribute('aria-modal', 'true');
        sidebar.inert = false;
        sidebar.querySelector('.eg-nav-item').focus();
      } else {
        sidebar.removeAttribute('role'); sidebar.removeAttribute('aria-modal');
        sidebar.inert = mobile.matches;
        if (returnFocus !== false) btn.focus({preventScroll:true});
      }
      hideTooltip(); paintToggle();
    }
    btn.addEventListener('click', function () {
      if (mobile.matches) { setDrawer(!drawer); return; }
      collapsed = !collapsed; preference = collapsed ? '1' : '0';
      try { localStorage.setItem(KEY, preference); } catch (e) { /* storage optional */ }
      root.classList.toggle('eg-menu-collapsed', collapsed);
      hideTooltip(); paintToggle(); requestAnimationFrame(scrollActive);
    });
    scrim.addEventListener('click', function () { setDrawer(false); });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') { hideTooltip(); if (drawer) { e.preventDefault(); setDrawer(false); } }
      if (drawer && e.key === 'Tab') {
        var links = sidebar.querySelectorAll('a[href],button');
        var first = links[0], last = links[links.length - 1];
        if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
      }
    });
    function resize() {
      if (drawer && !mobile.matches) setDrawer(false, false);
      sidebar.inert = mobile.matches && !drawer;
      if (preference !== '0' && preference !== '1') {
        collapsed = innerWidth < 1280; root.classList.toggle('eg-menu-collapsed', collapsed);
      }
      hideTooltip(); paintToggle(); requestAnimationFrame(scrollActive);
    }
    window.addEventListener('resize', resize);
    new MutationObserver(render).observe(root, {attributes:true, attributeFilter:['lang']});
    render(); resize();
  }
  /* Short tokens (ids, timestamps, hyphenated words) must never break inside a word:
     wrap tokens of 2-24 characters that contain - . : / _ in a nowrap span. Skips code-like
     containers that already forbid wrapping and text inside inputs/scripts. Re-runs when the
     page re-renders (state switcher, locale). */
  var TOKEN = /[A-Za-z0-9_\u2026]+(?:[-\u2013.:/\u2026][A-Za-z0-9_\u2026]+)+[,.;:)]?|[A-Za-z0-9_\u2026]+[-\u2013.:/](?=\s|$)/g;
  var SKIP = /^(SCRIPT|STYLE|TEXTAREA|OPTION|SELECT|NOSCRIPT|TITLE|SVG|INPUT)$/;
  function wrapTokens(rootEl) {
    var walker = document.createTreeWalker(rootEl, NodeFilter.SHOW_TEXT, {
      acceptNode: function (n) {
        var p = n.parentElement;
        if (!p || SKIP.test(p.tagName) || p.closest('.nw,script,style,textarea,select,svg')) return NodeFilter.FILTER_REJECT;
        return /\S[-\u2013.:/\u2026]\S/.test(n.nodeValue) || /[-\u2013.:/]\s/.test(n.nodeValue) ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT;
      }
    });
    var nodes = [], n;
    while ((n = walker.nextNode())) nodes.push(n);
    nodes.forEach(function (node) {
      var text = node.nodeValue, last = 0, m, frag = null;
      TOKEN.lastIndex = 0;
      while ((m = TOKEN.exec(text))) {
        if (m[0].length < 3 || m[0].length > 24) continue;
        frag = frag || document.createDocumentFragment();
        if (m.index > last) frag.appendChild(document.createTextNode(text.slice(last, m.index)));
        var span = document.createElement('span');
        span.className = 'nw';
        span.textContent = m[0];
        frag.appendChild(span);
        last = m.index + m[0].length;
      }
      if (!frag) return;
      if (last < text.length) frag.appendChild(document.createTextNode(text.slice(last)));
      node.parentNode.replaceChild(frag, node);
    });
  }
  var pending = false;
  function schedule() {
    if (pending) return;
    pending = true;
    requestAnimationFrame(function () {
      pending = false;
      obs.disconnect();
      wrapTokens(document.body);
      obs.observe(document.body, { childList: true, subtree: true, characterData: true });
    });
  }
  var obs = new MutationObserver(schedule);
  function startTokens() { wrapTokens(document.body); obs.observe(document.body, { childList: true, subtree: true, characterData: true }); }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', function () { startTokens(); init(); }); else { startTokens(); init(); }
})();
