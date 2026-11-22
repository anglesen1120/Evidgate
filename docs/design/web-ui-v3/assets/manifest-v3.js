/* Only reviewed sibling files belong here. Pages use this same list for shell and login. */
(() => {
  'use strict';
  const screens = [
    ["00-components.html","Components","Thành phần","00"],
    ["01-app-shell.html","Overview","Tổng quan","01"],
    ["02-login.html","Sign in","Đăng nhập",null],
    ["03-run-list.html","Test runs","Lượt kiểm thử","03"],
    ["04-run-detail.html","Run detail","Chi tiết lượt chạy","03"],
    ["05-evidence-panel.html","Evidence panel","Bảng bằng chứng","03"],
    ["06-decision-waivers.html","Decision & waivers","Quyết định và miễn trừ","03"],
    ["07-report-comparison.html","Report comparison","So sánh báo cáo","03"],
    ["08-failure-inbox.html","Failure inbox","Hộp thư lỗi","08"],
    ["09-failure-detail-triage.html","Incident triage","Phân loại sự cố","08"],
    ["10-investigation-history.html","Case history","Lịch sử ca kiểm thử","10"],
    ["11-artifact-viewer.html","Evidence viewer","Trình xem bằng chứng","03"],
    ["12-healing-queue.html","Review queue","Hàng chờ duyệt","12"],
    ["13-healing-shadow-detail.html","Shadow runs","Lượt chạy mô phỏng","13"],
    ["14-healing-approval.html","Healing approval","Duyệt đề xuất sửa","12"],
    ["15-knowledge-revocation.html","Knowledge lineage","Nguồn gốc tri thức","15"],
    ["16-healing-availability.html","Healing availability","Trạng thái sửa cơ chế kiểm thử","12"],
    ["17-studio-template-gallery.html","Template gallery","Thư viện mẫu","40"],
    ["18-studio-draft-editor.html","Draft editor","Trình sửa bản nháp","40"],
    ["19-studio-review-merge.html","Review draft","Xem xét bản nháp","42"],
    ["20-suite-composition.html","Suite composition","Cấu hình bộ kiểm thử","40"],
    ["21-release-view.html","Release gates","Cổng phát hành","21"],
    ["22-quality-overview.html","Quality overview","Tổng quan chất lượng","22"],
    ["23-onboarding-wizard.html","Project onboarding","Thiết lập dự án","24"],
    ["24-project-connection.html","Projects","Dự án","24"],
    ["25-test-catalog.html","Test catalog","Danh mục kiểm thử","24"],
    ["26-mapping-resolution.html","Mapping resolution","Xử lý ánh xạ","24"],
    ["27-settings-shell.html","Settings","Cài đặt","27"],
    ["28-members-roles.html","Members","Thành viên","28"],
    ["29-secret-references.html","Secret references","Tham chiếu bí mật","27"],
    ["30-policy-revisions.html","Release policy","Chính sách phát hành","27"],
    ["31-quota-retention.html","Usage and AI credits","Mức sử dụng và tín dụng AI","27"],
    ["32-audit-log.html","Audit log","Nhật ký kiểm toán","27"],
    ["33-security-events.html","Security events","Sự kiện bảo mật","27"],
    ["34-agent-pools.html","Agent pools","Nhóm tác nhân","27"],
    ["35-integrations-webhooks.html","Webhook integrations","Tích hợp webhook","27"],
    ["36-support-access.html","Support access","Quyền truy cập hỗ trợ","27"],
    ["37-api-tokens.html","API tokens","Mã truy cập API","27"],
    ["38-deploy-targets.html","Deploy targets","Đích triển khai","27"],
    ["39-locale-layout-review.html","Localization review","Xem xét bản địa hóa","00"],
    ["40-authoring-tree.html","Feature tree","Cây chức năng","40"],
    ["41-test-case-editor.html","Test case editor","Trình soạn ca kiểm thử","40"],
    ["42-pending-changes.html","Pending changes","Thay đổi chờ áp dụng","42"],
    ["43-generation.html","Generate tests","Tạo kiểm thử","46"],
    ["44-ai-settings.html","Project AI settings","Cài đặt AI của dự án","27"],
    ["45-import-export.html","Import and export","Nhập và xuất","40"],
    ["46-generated-scripts.html","Generated scripts","Kịch bản được tạo","46"],
    ["47-ui-change-proposal.html","UI-change proposal","Đề xuất do giao diện thay đổi","42"],
  ].map(([file, en, vi, section]) => ({ id: file.slice(0, 2), file, en, vi, section }));
  const groups = [
    ['Workspace', 'Không gian làm việc', [['01','grid'],['24','folder']]],
    ['Quality', 'Chất lượng', [['03','run'],['08','inbox'],['21','shield'],['22','chart']]],
    ['Healing', 'Sửa kiểm thử', [['12','queue'],['13','branch'],['10','history'],['15','file']]],
    ['Authoring', 'Biên soạn', [['40','folder'],['42','queue'],['46','code']]],
    ['Administration', 'Quản trị', [['28','people'],['27','settings'],['00','grid']]],
  ];
  const fixture = {
    org: 'Lotus Commerce', project: 'storefront-web', projectId: 'p-storefront', projectKey: 'SHOP',
    subtitle: ['Quality workspace','Không gian chất lượng'],
    people: {
      minh: ['Minh Trần','MT','Test maintainer','Người bảo trì kiểm thử'],
      linh: ['Linh Nguyễn','LN','QA engineer','Kỹ sư QA'],
      ha: ['Hà Lê','HL','Tenant administrator','Quản trị viên tổ chức'],
    },
  };
  const byId = new Map(screens.map(s => [s.id, s]));
  const byFile = new Map(screens.map(s => [s.file, s]));

  // Validate a sibling name first. Never resolve an untrusted path against the current origin.
  function returnTarget(raw) {
    if (typeof raw !== 'string' || !raw) return null;
    const match = /^([\w-]+\.html)(?:\?([^#]*))?(?:#([\w-]+))?$/.exec(raw);
    const screen = match && byFile.get(match[1]);
    if (!screen || screen.id === '02') return null;
    const params = new URLSearchParams(match[2] || '');
    if (params.size > 24) return null;
    const seen = new Set();
    for (const [key, value] of params) {
      if (!/^[a-z][a-z_]{0,23}$/.test(key) || seen.has(key)) return null;
      if (!value.trim() || value.length > 160 || /\p{Cc}/u.test(value) ||
          value.includes('://') || value.includes('..') || value.startsWith('/')) return null;
      seen.add(key);
    }
    const query = params.toString();
    return {screen, href: screen.file + (query ? '?' + query : '') + (match[3] ? '#' + match[3] : ''), params};
  }
  window.EvidgateV3 = {screens, groups, fixture, byId, returnTarget};
})();
