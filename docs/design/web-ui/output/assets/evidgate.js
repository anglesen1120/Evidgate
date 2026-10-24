(function () {
  "use strict";

  var ROUTES = [
    { id: "shell", label: { en: "Workspace", vi: "Không gian làm việc" }, href: "01-app-shell/evidgate-workspace-shell.html", group: "D", phase: "G1", sidebar: false, parentId: "overview", order: 0, icon: "grid" },
    { id: "sign-in", label: { en: "Sign in", vi: "Đăng nhập" }, href: "02-login/evidgate-sign-in.html", group: null, phase: "G1", sidebar: false, parentId: null, order: 0, icon: "lock" },
    { id: "runs", label: { en: "Runs", vi: "Lượt chạy" }, href: "03-run-list/evidgate-runs.html", group: "R", phase: "G2", sidebar: true, parentId: null, order: 4, icon: "play" },
    { id: "run-detail", label: { en: "Run detail", vi: "Chi tiết lượt chạy" }, href: "04-run-detail/evidgate-run-detail.html", group: "R", phase: "G2", sidebar: false, parentId: "runs", order: 0, icon: "file" },
    { id: "evidence-panel", label: { en: "Evidence", vi: "Bằng chứng" }, href: "05-evidence-panel/evidgate-run-detail.html", group: "R", phase: "G2", sidebar: false, parentId: "runs", order: 0, icon: "search" },
    { id: "decision-waivers", label: { en: "Decision & waivers", vi: "Quyết định và miễn trừ" }, href: "06-decision-waivers/evidgate-run-decision.html", group: "X", phase: "G4", sidebar: false, parentId: "runs", order: 0, icon: "flag" },
    { id: "report-comparison", label: { en: "Report comparison", vi: "So sánh báo cáo" }, href: "07-report-comparison/evidgate-report-compare.html", group: "R", phase: "G2", sidebar: false, parentId: "runs", order: 0, icon: "chart" },
    { id: "failures", label: { en: "Failure Inbox", vi: "Hộp thư lỗi" }, href: "08-failure-inbox/evidgate-failure-inbox.html", group: "L", phase: "G3", sidebar: true, parentId: null, order: 1, icon: "alert" },
    { id: "failure-detail", label: { en: "Incident detail", vi: "Chi tiết sự cố" }, href: "09-failure-detail-triage/evidgate-incident-detail.html", group: "L", phase: "G3", sidebar: false, parentId: "failures", order: 0, icon: "file" },
    { id: "investigation-history", label: { en: "Case history", vi: "Lịch sử ca kiểm thử" }, href: "10-investigation-history/evidgate-case-history.html", group: "L", phase: "G3", sidebar: false, parentId: "failures", order: 0, icon: "clock" },
    { id: "artifact-viewer", label: { en: "Artifact viewer", vi: "Xem hiện vật" }, href: "11-artifact-viewer/evidgate-artifact-viewer.html", group: "R", phase: "G2", sidebar: false, parentId: "runs", order: 0, icon: "eye" },
    { id: "repairs", label: { en: "Repair proposals", vi: "Đề xuất sửa" }, href: "12-healing-queue/evidgate-healing-review.html", group: "L", phase: "G3", sidebar: true, parentId: null, order: 2, icon: "wrench" },
    { id: "candidate-detail", label: { en: "Candidate detail", vi: "Chi tiết phương án sửa" }, href: "13-healing-shadow-detail/evidgate-candidate-detail.html", group: "L", phase: "G3", sidebar: false, parentId: "repairs", order: 0, icon: "file" },
    { id: "repair-review", label: { en: "Repair review", vi: "Duyệt đề xuất sửa" }, href: "14-healing-approval/evidgate-candidate-detail.html", group: "L", phase: "G3", sidebar: false, parentId: "repairs", order: 0, icon: "check" },
    { id: "knowledge", label: { en: "Knowledge lineage", vi: "Tri thức và thu hồi" }, href: "15-knowledge-revocation/evidgate-knowledge-lineage.html", group: "L", phase: "G3", sidebar: true, parentId: null, order: 3, icon: "book" },
    { id: "availability", label: { en: "Availability", vi: "Trạng thái sửa lỗi" }, href: "16-healing-availability/evidgate-healing-availability.html", group: "L", phase: "G3", sidebar: true, parentId: null, order: 4, icon: "shield-check" },
    { id: "studio", label: { en: "Test Studio", vi: "Test Studio" }, href: "17-studio-template-gallery/evidgate-test-studio.html", group: "X", phase: "G4", sidebar: true, parentId: null, order: 1, icon: "sliders" },
    { id: "studio-draft", label: { en: "Draft editor", vi: "Soạn bản nháp" }, href: "18-studio-draft-editor/evidgate-test-studio.html", group: "X", phase: "G4", sidebar: false, parentId: "studio", order: 0, icon: "file" },
    { id: "studio-review", label: { en: "Draft review", vi: "Duyệt bản nháp" }, href: "19-studio-review-merge/evidgate-draft-review.html", group: "X", phase: "G4", sidebar: false, parentId: "studio", order: 0, icon: "check" },
    { id: "suites", label: { en: "Suite composition", vi: "Bộ kiểm thử" }, href: "20-suite-composition/evidgate-suite-composition.html", group: "X", phase: "G4", sidebar: true, parentId: null, order: 2, icon: "package" },
    { id: "releases", label: { en: "Releases", vi: "Bản phát hành" }, href: "21-release-view/evidgate-release-view.html", group: "X", phase: "G4", sidebar: true, parentId: null, order: 4, icon: "rocket" },
    { id: "quality", label: { en: "Quality", vi: "Chất lượng" }, href: "22-quality-overview/evidgate-quality-overview.html", group: "X", phase: "G4", sidebar: true, parentId: null, order: 3, icon: "chart" },
    { id: "onboarding", label: { en: "Onboarding", vi: "Bắt đầu" }, href: "23-onboarding-wizard/evidgate-onboarding.html", group: "D", phase: "G1", sidebar: true, parentId: null, order: 5, icon: "compass" },
    { id: "overview", label: { en: "Overview", vi: "Tổng quan" }, href: "24-project-connection/evidgate-project-overview.html", group: "D", phase: "G1", sidebar: true, parentId: null, order: 4, icon: "home" },
    { id: "catalog", label: { en: "Test catalog", vi: "Danh mục kiểm thử" }, href: "25-test-catalog/evidgate-test-catalog.html", group: "R", phase: "G2", sidebar: true, parentId: null, order: 2, icon: "list" },
    { id: "mapping", label: { en: "Mapping resolution", vi: "Ánh xạ kết quả" }, href: "26-mapping-resolution/evidgate-mapping-resolution.html", group: "R", phase: "G2", sidebar: true, parentId: null, order: 3, icon: "map" },
    { id: "settings", label: { en: "General", vi: "Chung" }, href: "27-settings-shell/evidgate-settings.html", group: "S", phase: "G1", sidebar: true, parentId: null, order: 1, icon: "settings" },
    { id: "members", label: { en: "Members & roles", vi: "Thành viên và vai trò" }, href: "28-members-roles/evidgate-members-roles.html", group: "S", phase: "G1", sidebar: true, parentId: null, order: 3, icon: "users" },
    { id: "secrets", label: { en: "Secret references", vi: "Tham chiếu bí mật" }, href: "29-secret-references/evidgate-secret-references.html", group: "S", phase: "G2", sidebar: true, parentId: null, order: 6, icon: "key" },
    { id: "policies", label: { en: "Release policies", vi: "Chính sách phát hành" }, href: "30-policy-revisions/evidgate-policy-revisions.html", group: "X", phase: "G4", sidebar: true, parentId: null, order: 5, icon: "scroll" },
    { id: "quota", label: { en: "Usage & credits", vi: "Mức sử dụng và tín dụng AI" }, href: "31-quota-retention/evidgate-usage-retention.html", group: "S", phase: "G1", sidebar: true, parentId: null, order: 4, icon: "credit" },
    { id: "audit", label: { en: "Audit log", vi: "Nhật ký kiểm toán" }, href: "32-audit-log/evidgate-audit-log.html", group: "S", phase: "G1", sidebar: true, parentId: null, order: 5, icon: "audit" },
    { id: "security", label: { en: "Security events", vi: "Sự kiện bảo mật" }, href: "33-security-events/evidgate-security-events.html", group: "S", phase: "G2", sidebar: true, parentId: null, order: 7, icon: "shield-alert" },
    { id: "agents", label: { en: "Agent pools", vi: "Nhóm agent" }, href: "34-agent-pools/evidgate-agents-pools.html", group: "S", phase: "G4", sidebar: true, parentId: null, order: 9, icon: "bot" },
    { id: "integrations", label: { en: "Integrations & webhooks", vi: "Tích hợp và webhook" }, href: "35-integrations-webhooks/evidgate-webhook-integrations.html", group: "S", phase: "G4", sidebar: true, parentId: null, order: 10, icon: "plug" },
    { id: "support", label: { en: "Support access", vi: "Quyền truy cập hỗ trợ" }, href: "36-support-access/evidgate-support-access.html", group: "S", phase: "G2", sidebar: true, parentId: null, order: 8, icon: "life" },
    { id: "tokens", label: { en: "API tokens", vi: "Mã truy cập API" }, href: "37-api-tokens/evidgate-api-tokens.html", group: "S", phase: "G4", sidebar: true, parentId: null, order: 11, icon: "key" },
    { id: "deploy", label: { en: "Deploy targets", vi: "Mục tiêu triển khai" }, href: "38-deploy-targets/evidgate-deploy-targets.html", group: "X", phase: "G4", sidebar: true, parentId: null, order: 6, icon: "server" },
    { id: "locale-review", label: { en: "Localization review", vi: "Xem xét bản địa hoá" }, href: "39-locale-layout-review/evidgate-localization-review.html", group: null, phase: "G3", sidebar: false, parentId: null, order: 0, icon: "globe" },
    { id: "authoring", label: { en: "Feature tree", vi: "Cây chức năng" }, href: "40-authoring-tree/evidgate-authoring-tree.html", group: "D", phase: "G1", sidebar: true, parentId: null, order: 1, icon: "tree" },
    { id: "test-case-editor", label: { en: "Test case editor", vi: "Soạn ca kiểm thử" }, href: "41-test-case-editor/evidgate-test-case-editor.html", group: "D", phase: "G1", sidebar: false, parentId: "authoring", order: 0, icon: "file" },
    { id: "pending-changes", label: { en: "Pending changes", vi: "Thay đổi chờ áp dụng" }, href: "42-pending-changes/evidgate-pending-changes.html", group: "D", phase: "G1", sidebar: true, parentId: null, order: 3, icon: "layers" },
    { id: "generation", label: { en: "Generate tests", vi: "Tạo kiểm thử" }, href: "43-generation/evidgate-generation.html", group: "D", phase: "G1", sidebar: true, parentId: null, order: 2, icon: "sparkle" },
    { id: "ai-settings", label: { en: "AI settings", vi: "Cài đặt AI" }, href: "44-ai-settings/evidgate-ai-settings.html", group: "S", phase: "G1", sidebar: true, parentId: null, order: 2, icon: "sparkle" },
    { id: "import-export", label: { en: "Import / export", vi: "Nhập / xuất" }, href: "45-import-export/evidgate-import-export.html", group: "D", phase: "G1", sidebar: false, parentId: "authoring", order: 0, icon: "file" },
    { id: "scripts", label: { en: "Generated scripts", vi: "Kịch bản đã tạo" }, href: "46-generated-scripts/evidgate-generated-scripts.html", group: "R", phase: "G2", sidebar: true, parentId: null, order: 1, icon: "terminal" },
    { id: "ui-change-proposal", label: { en: "UI-change review", vi: "Duyệt thay đổi giao diện" }, href: "47-ui-change-proposal/evidgate-ui-change-proposal.html", group: "L", phase: "G3", sidebar: false, parentId: "repairs", order: 0, icon: "flag" }
  ];

  var GROUPS = {
    D: { en: "Test design", vi: "Thiết kế kiểm thử" },
    R: { en: "Scripts & runs", vi: "Kịch bản và lượt chạy" },
    L: { en: "Learn & repair", vi: "Học và đề xuất sửa" },
    X: { en: "Release", vi: "Phát hành" },
    S: { en: "Settings", vi: "Cài đặt" }
  };
  var GROUP_ORDER = ["D", "R", "L", "X", "S"];
  var PHASES = ["G1", "G2", "G3", "G4"];

  var ICONS = {
    grid: '<rect x="3.5" y="3.5" width="7" height="7" rx="1.2"/><rect x="13.5" y="3.5" width="7" height="7" rx="1.2"/><rect x="3.5" y="13.5" width="7" height="7" rx="1.2"/><rect x="13.5" y="13.5" width="7" height="7" rx="1.2"/>',
    tree: '<path d="M12 3v5"/><rect x="8.5" y="8" width="7" height="4" rx="1"/><path d="M6 15v-3h12v3"/><rect x="3.5" y="15" width="5" height="4" rx="1"/><rect x="15.5" y="15" width="5" height="4" rx="1"/>',
    sparkle: '<path d="m12 3 1.6 4.4L18 9l-4.4 1.6L12 15l-1.6-4.4L6 9l4.4-1.6L12 3Z"/><path d="m18.5 15 .8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8.8-2.2Z"/>',
    layers: '<path d="m12 3 9 5-9 5-9-5 9-5Z"/><path d="m3 12 9 5 9-5"/><path d="m3 16 9 5 9-5"/>',
    rocket: '<path d="M14 4c3 1 5 3 6 6-3 1-6 3-8 6l-4-4c3-2 5-5 6-8Z"/><path d="m8 12-3 1 2 2-3 4 4-3 2 2 1-3"/><circle cx="15" cy="8" r="1"/>',
    terminal: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="m7 9 3 3-3 3m6 0h4"/>',
    list: '<path d="M8 6h12M8 12h12M8 18h12"/><path d="M4 6h.01M4 12h.01M4 18h.01"/>',
    map: '<path d="m3 6 6-2 6 2 6-2v14l-6 2-6-2-6 2V6Z"/><path d="M9 4v14M15 6v14"/>',
    play: '<circle cx="12" cy="12" r="8.5"/><path d="m10 8.5 5 3.5-5 3.5v-7Z"/>',
    alert: '<path d="M12 3.5 21 19H3l9-15.5Z"/><path d="M12 9v4.5M12 16.5h.01"/>',
    archive: '<rect x="3.5" y="4.5" width="17" height="4" rx="1"/><path d="M5 8.5V19a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8.5"/><path d="M10 12h4"/>',
    wrench: '<path d="M14.5 6.5a3.5 3.5 0 0 0-4.6 4.6L4 17l3 3 5.9-5.9a3.5 3.5 0 0 0 4.6-4.6l-2.2 2.2-2.3-2.3 2.5-2.9Z"/>',
    book: '<path d="M4 5.5A2.5 2.5 0 0 1 6.5 3H20v16H6.5A2.5 2.5 0 0 0 4 21.5v-16Z"/><path d="M8 7h8M8 11h6"/>',
    "shield-check": '<path d="M12 3 20 6v6c0 5-3.5 8-8 9.5C7.5 20 4 17 4 12V6l8-3Z"/><path d="m8.5 12 2.5 2.5 4.5-5"/>',
    sliders: '<path d="M4 6h7M15 6h5M4 12h3M11 12h9M4 18h9M17 18h3"/><circle cx="13" cy="6" r="2"/><circle cx="9" cy="12" r="2"/><circle cx="15" cy="18" r="2"/>',
    package: '<path d="m12 3 8 4.5v9L12 21l-8-4.5v-9L12 3Z"/><path d="m4 7.5 8 4.5 8-4.5M12 12v9"/>',
    chart: '<path d="M4 20V4"/><path d="M4 20h16"/><path d="M8 16v-4M12 16V8M16 16v-6"/>',
    flag: '<path d="M5 21V4"/><path d="M5 5h11l2 4-2 4H5"/>',
    scroll: '<path d="M6 3h10a2 2 0 0 1 2 2v14a2 2 0 0 0 2 2H6a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z"/><path d="M8 7h6M8 11h6M8 15h4"/>',
    server: '<rect x="3" y="4" width="18" height="6" rx="1.5"/><rect x="3" y="14" width="18" height="6" rx="1.5"/><path d="M7 7h.01M7 17h.01"/>',
    settings: '<circle cx="12" cy="12" r="3"/><path d="M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6 7 7M17 17l1.4 1.4M18.4 5.6 17 7M7 17l-1.4 1.4"/>',
    users: '<circle cx="9" cy="9" r="3"/><path d="M3 20a6 6 0 0 1 12 0"/><path d="M16 6a3 3 0 0 1 0 6M17 20a6 6 0 0 0-2-4.5"/>',
    credit: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3 10h18M7 15h4"/>',
    audit: '<path d="M7 4h10v16H7z"/><path d="M10 8h4M10 12h4M10 16h4"/>',
    key: '<circle cx="8" cy="12" r="3"/><path d="M11 12h9l-2 2m0 0-2-2"/>',
    "shield-alert": '<path d="M12 3 20 6v6c0 5-3.5 8-8 9.5C7.5 20 4 17 4 12V6l8-3Z"/><path d="M12 8v4M12 15.5h.01"/>',
    life: '<circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="3.5"/><path d="m5.5 5.5 3 3M18.5 5.5l-3 3M5.5 18.5l3-3M18.5 18.5l-3-3"/>',
    bot: '<rect x="4" y="8" width="16" height="11" rx="2.5"/><path d="M12 4v4M8 13h.01M16 13h.01M9.5 16h5"/>',
    plug: '<path d="M9 3v4M15 3v4M6 7h12v4a6 6 0 0 1-12 0V7Z"/><path d="M12 17v4"/>',
    globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c2.5 2.5 3.8 5.7 3.8 9s-1.3 6.5-3.8 9c-2.5-2.5-3.8-5.7-3.8-9S9.5 5.5 12 3Z"/>',
    home: '<path d="m3 11 9-7 9 7"/><path d="M5 10v10h14V10"/><path d="M10 20v-6h4v6"/>',
    compass: '<circle cx="12" cy="12" r="9"/><path d="m15.5 8.5-2 5-5 2 2-5 5-2Z"/>',
    file: '<path d="M6 3h8l4 4v14H6z"/><path d="M14 3v4h4M9 12h6M9 16h6"/>',
    search: '<circle cx="10.5" cy="10.5" r="6.5"/><path d="m15.5 15.5 4 4"/>',
    "chevron-down": '<path d="m6 9 6 6 6-6"/>',
    "chevron-right": '<path d="m9 6 6 6-6 6"/>',
    close: '<path d="m6 6 12 12M18 6 6 18"/>',
    check: '<path d="m5 12.5 4.5 4.5L19 7"/>',
    copy: '<rect x="8" y="8" width="11" height="11" rx="1.5"/><path d="M5 15V5a1 1 0 0 1 1-1h9"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M19.1 4.9l-1.4 1.4M6.3 17.7l-1.4 1.4"/>',
    moon: '<path d="M20 14.5A8.5 8.5 0 0 1 9.5 4 8.5 8.5 0 1 0 20 14.5Z"/>',
    clock: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/>',
    menu: '<path d="M4 7h16M4 12h16M4 17h16"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    "arrow-right": '<path d="M4 12h15M14 7l5 5-5 5"/>',
    external: '<path d="M14 4h6v6"/><path d="M20 4 11 13"/><path d="M9 6H5v13h13v-4"/>',
    lock: '<rect x="4" y="10" width="16" height="10" rx="2"/><path d="M8 10V8a4 4 0 0 1 8 0v2"/>',
    eye: '<path d="M2.5 12S6 6 12 6s9.5 6 9.5 6-3.5 6-9.5 6-9.5-6-9.5-6Z"/><circle cx="12" cy="12" r="2.5"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7.5h.01"/>',
    "git-branch": '<circle cx="6" cy="6" r="2.5"/><circle cx="6" cy="18" r="2.5"/><circle cx="18" cy="8" r="2.5"/><path d="M6 8.5v7M18 10.5c0 3-3 3.5-6 3.5H6"/>',
    x: '<path d="m6 6 12 12M18 6 6 18"/>'
  };

  var STATE_LABELS = {
    default: { en: "Default", vi: "Mặc định" },
    loading: { en: "Loading", vi: "Đang tải" },
    empty: { en: "Empty · no records", vi: "Trống · chưa có bản ghi" },
    "no-match": { en: "No filter match", vi: "Không khớp bộ lọc" },
    "no-file": { en: "No file selected · import", vi: "Chưa chọn tệp · nhập" },
    malformed: { en: "Malformed file · import", vi: "Tệp sai cấu trúc · nhập" },
    unsupported: { en: "Unsupported file type · import", vi: "Loại tệp không hỗ trợ · nhập" },
    validation: { en: "Row validation errors · import", vi: "Lỗi kiểm tra dòng · nhập" },
    duplicate: { en: "Duplicate case ids · import", vi: "Mã ca trùng · nhập" },
    mixed: { en: "Mixed valid and invalid rows · import", vi: "Dòng hợp lệ và không hợp lệ · nhập" },
    denied: { en: "Import denied · role", vi: "Nhập bị từ chối · vai trò" },
    "export-empty": { en: "Empty export scope", vi: "Phạm vi xuất trống" },
    "export-unavailable": { en: "Export unavailable · scope", vi: "Xuất không khả dụng · phạm vi" },
    "table-error": { en: "Table error · balance retained", vi: "Lỗi bảng · giữ số dư" },
    error: { en: "Error", vi: "Lỗi" },
    submitting: { en: "Submitting · local specimen", vi: "Đang gửi · mẫu cục bộ" },
    saved: { en: "Saved · local specimen", vi: "Đã lưu · mẫu cục bộ" },
    "validation-error": { en: "Save blocked · over limit", vi: "Lưu bị chặn · vượt giới hạn" },
    "new-case": { en: "New manual case · unassigned", vi: "Ca thủ công mới · chưa gán" },
    created: { en: "Draft created · local specimen", vi: "Đã tạo bản nháp · mẫu cục bộ" },
    403: { en: "403 · Not authorized", vi: "403 · Không có quyền" },
    404: { en: "404 · Not found", vi: "404 · Không tìm thấy" },
    401: { en: "401 · Session expired", vi: "401 · Phiên hết hạn" },
    412: { en: "412 · Conflict", vi: "412 · Xung đột dữ liệu" },
    429: { en: "429 · Rate limited", vi: "429 · Giới hạn yêu cầu" },
    success: { en: "Success · server result", vi: "Thành công · kết quả máy chủ" },
    "success-storefront": { en: "Rotation result · ci-storefront", vi: "Kết quả xoay · ci-storefront" },
    "success-e2e": { en: "Rotation result · ci-evidgate-e2e", vi: "Kết quả xoay · ci-evidgate-e2e" },
    long: { en: "Long content", vi: "Nội dung dài" },
    active: { en: "Active · reference", vi: "Đang hoạt động · tham chiếu" },
    rotated: { en: "Rotated · supplied result", vi: "Đã xoay · kết quả được cung cấp" },
    "expires-soon": { en: "Expires soon · server metadata", vi: "Sắp hết hạn · siêu dữ liệu máy chủ" },
    unavailable: { en: "Metadata unavailable", vi: "Siêu dữ liệu không khả dụng" },
    "read-only": { en: "Read-only · permission denied", vi: "Chỉ đọc · từ chối quyền" },
    stale: { en: "Stale context · reload", vi: "Bối cảnh đã cũ · tải lại" },
    partial: { en: "Partial metadata · unavailable fields", vi: "Siêu dữ liệu một phần · trường không khả dụng" },
    "copy-fail": { en: "Clipboard unavailable · redacted only", vi: "Không dùng được bộ nhớ tạm · chỉ nội dung đã ẩn" },
    "rotation-recorded": { en: "Rotation recorded · value redacted", vi: "Đã ghi nhận xoay · giá trị đã ẩn" },
    exporting: { en: "Export preparing · bounded", vi: "Đang chuẩn bị xuất · có giới hạn" },
    exported: { en: "Export prepared · redacted", vi: "Đã chuẩn bị xuất · đã ẩn" },
    "detail-unavailable": { en: "Detail unavailable · metadata only", vi: "Không có chi tiết · chỉ siêu dữ liệu" },
    restricted: { en: "Restricted events · permission-scoped", vi: "Sự kiện bị hạn chế · theo phạm vi quyền" },
    "chain-verified": { en: "Chain verified · read-only", vi: "Chuỗi đã xác minh · chỉ đọc" },
    "chain-running": { en: "Chain verification running", vi: "Đang xác minh chuỗi" },
    "chain-failed": { en: "Chain verification failed", vi: "Xác minh chuỗi thất bại" },
    "support-requested": { en: "Requested · awaiting approval", vi: "Đang yêu cầu · chờ phê duyệt" },
    "support-approved": { en: "Approved · scoped and time-bound", vi: "Đã duyệt · có phạm vi và thời hạn" },
    "support-expired": { en: "Expired · historical grant", vi: "Đã hết hạn · quyền lịch sử" },
    "support-revoked": { en: "Revoked · historical grant", vi: "Đã thu hồi · quyền lịch sử" },
    "grant-stale": { en: "Grant changed · reason retained", vi: "Quyền đã đổi · giữ lý do" },
    "credits-estimate": { en: "Credit estimate ready", vi: "Ước tính tín dụng sẵn sàng" },
    "credits-low": { en: "Insufficient AI credits", vi: "Không đủ tín dụng AI" },
    "ai-off": { en: "Tenant AI disabled", vi: "Tổ chức đã tắt AI" },
    "unbound-step": { en: "Script step unbound", vi: "Bước kịch bản chưa liên kết" },
    "no-repair": { en: "Drift · proposal only, no repair", vi: "Thay đổi · chỉ đề xuất, không sửa" },
    callback: { en: "Callback completing", vi: "Đang hoàn tất callback" },
    authenticated: { en: "Authenticated · review result", vi: "Đã xác thực · kết quả xem xét" },
    expired: { en: "Session expired", vi: "Phiên đã hết hạn" },
    revoked: { en: "Signed out", vi: "Đã đăng xuất" },
    idp: { en: "Identity provider unavailable", vi: "Nhà cung cấp danh tính không khả dụng" },
    canceled: { en: "Sign-in canceled", vi: "Đã hủy đăng nhập" },
    memberships: { en: "No workspace access", vi: "Không có quyền vào không gian làm việc" },
    "locator-drift": { en: "Locator drift · candidate", vi: "Thay đổi bộ định vị · phương án" },
    "ui-change": { en: "UI change · test-case review", vi: "Thay đổi giao diện · xem xét ca kiểm thử" },
    ambiguous: { en: "Ambiguous comparison · no repair", vi: "So sánh chưa rõ · không sửa" },
    "screens-off": { en: "Screenshots opted out", vi: "Đã tắt ảnh chụp màn hình" },
    "screens-on": { en: "Screenshots opted in · ready metadata", vi: "Đã bật ảnh chụp · siêu dữ liệu sẵn sàng" },
    "screens-restricted": { en: "Screenshot restricted · metadata only", vi: "Ảnh chụp bị hạn chế · chỉ siêu dữ liệu" },
    "negative-control": { en: "Negative control unexpectedly passed", vi: "Đối chứng âm bất ngờ đạt" },
    revalidation: { en: "Rebased · revalidation required", vi: "Đã rebase · cần kiểm chứng lại" },
    observing: { en: "Shadow window observing · no PR", vi: "Đang quan sát mô phỏng · chưa có PR" },
    "shadow-running": { en: "Shadow running · no result", vi: "Mô phỏng đang chạy · chưa có kết quả" },
    "pr-open": { en: "PR open · delivery review", vi: "PR đang mở · duyệt chuyển mã" },
    "pr-merged": { en: "PR merged · historical", vi: "PR đã hợp nhất · lịch sử" },
    "pr-closed": { en: "PR closed · no delivery", vi: "PR đã đóng · chưa chuyển mã" },
    approved: { en: "Approved · supplied result", vi: "Đã duyệt · kết quả được cung cấp" },
    rejected: { en: "Rejected · reason retained", vi: "Đã từ chối · giữ lý do" },
    "reviewer-missing": { en: "Independent reviewer absent", vi: "Thiếu người duyệt độc lập" },
    "stale-review": { en: "Stale review · draft retained", vi: "Duyệt cũ · giữ bản nháp" },
    validated: { en: "Validated proposal", vi: "Đề xuất đã kiểm chứng" },
    proposed: { en: "Proposed · validation pending", vi: "Đề xuất · chờ kiểm chứng" },
    insufficient: { en: "Insufficient validation", vi: "Kiểm chứng chưa đủ" },
    "missing-diff": { en: "Structural diff unavailable", vi: "Không có so sánh cấu trúc" },
    "expired-proposal": { en: "Proposal expired", vi: "Đề xuất đã hết hạn" },
    "screens-scanning": { en: "Screenshot scan pending", vi: "Đang quét ảnh chụp" },
    "one-revision": { en: "One revision available", vi: "Chỉ có một phiên bản" },
    partial: { en: "Collection context partial", vi: "Ngữ cảnh thu thập chưa đầy đủ" },
    dependency: { en: "Dependency unavailable", vi: "Phụ thuộc không khả dụng" },
    "operator-disabled": { en: "Operator disabled", vi: "Đã tắt bởi người vận hành" },
    enabled: { en: "Healing enabled", vi: "Đã bật sửa lỗi" },
    shadow: { en: "Shadow mode", vi: "Chế độ mô phỏng" },
    disabled: { en: "Healing disabled", vi: "Đã tắt sửa lỗi" },
    unavailable: { en: "Availability unavailable", vi: "Không xác định khả dụng" },
    "lineage-active": { en: "Knowledge active", vi: "Tri thức đang hoạt động" },
    "lineage-revoked": { en: "Knowledge revoked", vi: "Tri thức đã thu hồi" },
    "revoke-denied": { en: "Revocation denied", vi: "Thu hồi bị từ chối" },
    "effect-error": { en: "Effect preview unavailable", vi: "Chưa thể xem trước ảnh hưởng" },
    inherited: { en: "Restrictive inherited scope", vi: "Phạm vi kế thừa bị hạn chế" },
    "read-only": { en: "Read-only · permission", vi: "Chỉ đọc · quyền" },
    queueing: { en: "Queueing for review", vi: "Đang đưa vào chờ duyệt" },
    queued: { en: "Queued in pending changes", vi: "Đã đưa vào thay đổi chờ áp dụng" },
    "already-queued": { en: "Already queued · no duplicate", vi: "Đã có trong hàng đợi · không nhân bản" },
    pending: { en: "Pending review", vi: "Chờ xem xét" },
    historical: { en: "Historical · read-only", vi: "Lịch sử · chỉ đọc" },
    "comparison-unavailable": { en: "Comparison unavailable", vi: "Không có so sánh" },
    "stale-base": { en: "Stale base version · review retained", vi: "Phiên bản gốc đã cũ · giữ bản xem xét" },
    "grant-revoked": { en: "Authoring permission revoked", vi: "Quyền soạn đã bị thu hồi" },
    "dependents-empty": { en: "No dependent candidates", vi: "Không có phương án phụ thuộc" },
    "revoke-success": { en: "Revocation recorded · local specimen", vi: "Đã ghi thu hồi · mẫu cục bộ" },
    "missing-only": { en: "Stored INCONCLUSIVE · missing evidence", vi: "INCONCLUSIVE đã lưu · thiếu bằng chứng" },
    "waiver-requested": { en: "Waiver requested · separate lifecycle", vi: "Yêu cầu miễn trừ · vòng đời riêng" },
    "waiver-expired": { en: "Waiver expired · historical", vi: "Miễn trừ đã hết hạn · lịch sử" },
    "waiver-revoked": { en: "Waiver revoked · terminal", vi: "Miễn trừ đã thu hồi · kết thúc" },
    "evaluation-pending": { en: "Evaluation pending · no result", vi: "Đang chờ đánh giá · chưa có kết quả" },
    "evidence-restricted": { en: "Evidence restricted · metadata only", vi: "Bằng chứng bị hạn chế · chỉ siêu dữ liệu" },
    dirty: { en: "Unsaved draft changes", vi: "Thay đổi bản nháp chưa lưu" },
    "dry-running": { en: "Dry-run in progress", vi: "Đang chạy thử" },
    "dry-failed": { en: "Dry-run failed · supplied", vi: "Chạy thử thất bại · được cung cấp" },
    "lint-failed": { en: "Server lint failed", vi: "Lint máy chủ thất bại" },
    "ai-review": { en: "AI suggestion review", vi: "Xem xét gợi ý AI" },
    invalid: { en: "Invalid draft · assertion required", vi: "Bản nháp không hợp lệ · cần khẳng định" },
    readonly: { en: "Read-only · historical draft", vi: "Chỉ đọc · bản nháp lịch sử" },
    editing: { en: "Editing draft · unsaved", vi: "Đang sửa bản nháp · chưa lưu" },
    "no-changes": { en: "No draft changes", vi: "Không có thay đổi bản nháp" },
    "approval-pending": { en: "Awaiting independent approval", vi: "Chờ phê duyệt độc lập" },
    "pr-unavailable": { en: "PR delivery unavailable", vi: "Chuyển mã PR không khả dụng" },
    submitted: { en: "Review requested · local fixture", vi: "Đã yêu cầu duyệt · mẫu cục bộ" },
    "empty-suite": { en: "Empty suite · no variants", vi: "Bộ trống · không có biến thể" },
    "dependency-issue": { en: "Dependency warning · server-supplied", vi: "Cảnh báo phụ thuộc · máy chủ cung cấp" },
    "missing-coverage": { en: "Coverage unavailable · not zero", vi: "Độ phủ không khả dụng · không phải 0" },
    "variant-added": { en: "Variant added to draft", vi: "Đã thêm biến thể vào bản nháp" },
    "missing-evidence": { en: "Missing evidence · no PASS", vi: "Thiếu bằng chứng · không PASS" }
  };

  var root = document.documentElement;
  var scriptEl = document.currentScript;
  var scriptSrc = scriptEl ? scriptEl.getAttribute("src") : "assets/evidgate.js";
  var baseSlice = scriptSrc.indexOf("assets/evidgate.js");
  var BASE = baseSlice >= 0 ? scriptSrc.slice(0, baseSlice) : "";
  var store = {
    read: function (key, fallback) {
      try {
        var value = window.localStorage.getItem(key);
        return value === null ? fallback : value;
      } catch (error) {
        return fallback;
      }
    },
    write: function (key, value) {
      try {
        window.localStorage.setItem(key, value);
      } catch (error) {
        return;
      }
    }
  };

  var state = {
    locale: store.read("evidgate-locale", root.lang === "en" ? "en" : "vi"),
    theme: store.read("evidgate-theme", root.getAttribute("data-theme") === "dark" ? "dark" : "light"),
    phase: store.read("evidgate-phase", "G1"),
    scenario: "default",
    navMode: window.innerWidth >= 1280 ? "expanded" : window.innerWidth >= 768 ? "rail" : "drawer",
    navUserSet: false,
    navQuery: "",
    lastDialogTrigger: null
  };
  if (state.locale !== "en" && state.locale !== "vi") state.locale = "vi";
  if (state.theme !== "light" && state.theme !== "dark") state.theme = "light";
  if (PHASES.indexOf(state.phase) < 0) state.phase = "G1";

  function loc(en, vi) {
    return state.locale === "vi" ? vi : en;
  }

  function getRoute(id) {
    for (var i = 0; i < ROUTES.length; i += 1) {
      if (ROUTES[i].id === id) return ROUTES[i];
    }
    return null;
  }

  function resolve(href) {
    return BASE + href;
  }

  function icon(name, extra) {
    return '<svg class="eg-icon' + (extra ? " " + extra : "") + '" aria-hidden="true" focusable="false"><use href="#eg-i-' + name + '"></use></svg>';
  }

  function injectSprite() {
    if (document.querySelector("[data-eg-sprite]")) return;
    var symbols = Object.keys(ICONS).map(function (name) {
      return '<symbol id="eg-i-' + name + '" viewBox="0 0 24 24">' + ICONS[name] + "</symbol>";
    }).join("");
    var holder = document.createElement("div");
    holder.setAttribute("data-eg-sprite", "");
    holder.setAttribute("aria-hidden", "true");
    holder.innerHTML = '<svg xmlns="http://www.w3.org/2000/svg" focusable="false">' + symbols + "</svg>";
    document.body.insertBefore(holder, document.body.firstChild);
  }

  function navItems() {
    var limit = PHASES.indexOf(state.phase);
    return ROUTES.filter(function (route) {
      return route.sidebar && PHASES.indexOf(route.phase) <= limit;
    });
  }

  function renderNav() {
    var host = document.querySelector("[data-eg-nav]");
    if (!host) return;
    var currentId = root.getAttribute("data-nav") || "";
    var current = getRoute(currentId);
    var activeId = current ? (current.sidebar ? current.id : current.parentId) : null;
    var activeIsPage = !!(current && current.sidebar && current.id === activeId);
    var visible = navItems();
    var html = "";
    html += '<a class="eg-nav__brand" href="' + resolve(getRoute("shell").href) + '" aria-label="' + loc("Evidgate workspace", "Không gian làm việc Evidgate") + '">' + icon("shield-check") + '<span>Evidgate</span></a>';
    html += '<button class="eg-nav__close" type="button" data-eg-nav-close aria-label="' + loc("Close navigation", "Đóng điều hướng") + '">' + icon("close", "eg-icon--sm") + "</button>";
    html += '<div class="eg-nav__scope"><span>' + loc("Organization", "Tổ chức") + "</span><strong>Lotus Commerce</strong><span>" + loc("Project", "Dự án") + "</span><strong>storefront-web</strong></div>";
    html += '<nav class="eg-nav__scroll" aria-label="' + loc("Primary navigation", "Điều hướng chính") + '">';
    GROUP_ORDER.forEach(function (groupKey) {
      var items = visible.filter(function (route) { return route.group === groupKey; }).sort(function (a, b) { return a.order - b.order; });
      if (!items.length) return;
      html += '<div class="eg-nav__group"><p class="eg-nav__label">' + GROUPS[groupKey][state.locale] + "</p>";
      items.forEach(function (route) {
        var isActive = activeId === route.id;
        var currentAttr = isActive ? (activeIsPage ? ' aria-current="page"' : ' aria-current="true"') : "";
        html += '<a class="eg-nav__item" href="' + resolve(route.href) + '" data-eg-nav-label="' + route.label[state.locale].toLowerCase() + '"' + currentAttr + ' title="' + route.label[state.locale] + '">' + icon(route.icon, "eg-icon--sm") + "<span>" + route.label[state.locale] + "</span></a>";
      });
      html += "</div>";
    });
    html += '<p class="eg-nav__label" data-eg-nav-empty hidden>' + loc("No navigation matches this search", "Không có mục điều hướng phù hợp") + "</p>";
    html += "</nav>";
    html += '<div class="eg-nav__account"><span aria-hidden="true">LN</span><span><strong>Linh Nguyễn</strong><small>' + loc("QA engineer", "Kỹ sư QA") + " · Lotus Commerce</small></span></div>";
    host.innerHTML = html;
    applyNavFilter();
  }

  function applyNavFilter() {
    var nav = document.querySelector("[data-eg-nav]");
    if (!nav) return;
    var query = state.navQuery.trim().toLowerCase();
    var items = nav.querySelectorAll(".eg-nav__item");
    var shown = 0;
    items.forEach(function (item) {
      var match = !query || (item.getAttribute("data-eg-nav-label") || "").indexOf(query) >= 0;
      item.hidden = !match;
      if (match) shown += 1;
    });
    nav.querySelectorAll(".eg-nav__group").forEach(function (group) {
      var any = Array.prototype.some.call(group.querySelectorAll(".eg-nav__item"), function (item) { return !item.hidden; });
      group.hidden = !any;
    });
    var empty = nav.querySelector("[data-eg-nav-empty]");
    if (empty) empty.hidden = shown > 0;
  }

  function reviewStates() {
    var host = document.querySelector("[data-eg-review]");
    if (!host) return ["default"];
    var raw = host.getAttribute("data-eg-states") || "default,loading,error";
    return raw.split(",").map(function (value) { return value.trim(); }).filter(function (value) { return !!value; });
  }

  function stateLabel(key) {
    var entry = STATE_LABELS[key];
    return entry ? entry[state.locale] : key;
  }

  function buildReviewControls() {
    var hosts = document.querySelectorAll("[data-eg-review]");
    hosts.forEach(function (host) {
      var states = reviewStates();
      var phaseOptions = PHASES.map(function (phase) {
        var labels = { G1: { en: "G1 · Author", vi: "G1 · Tác giả" }, G2: { en: "G2 · Run", vi: "G2 · Lượt chạy" }, G3: { en: "G3 · Learn & repair", vi: "G3 · Học và sửa" }, G4: { en: "G4 · Expand", vi: "G4 · Mở rộng" } };
        return '<option value="' + phase + '" data-label-en="' + labels[phase].en + '" data-label-vi="' + labels[phase].vi + '">' + labels[phase][state.locale] + "</option>";
      }).join("");
      var stateOptions = states.map(function (key) {
        var labels = STATE_LABELS[key] || { en: key, vi: key };
        return '<option value="' + key + '" data-label-en="' + labels.en + '" data-label-vi="' + labels.vi + '">' + stateLabel(key) + "</option>";
      }).join("");
      host.innerHTML =
        '<section class="eg-review-controls" data-od-id="design-review-controls" aria-labelledby="eg-review-title">' +
          '<div class="eg-review-controls__label"><strong id="eg-review-title"><span data-lang="vi">Điều khiển xem xét thiết kế</span><span data-lang="en">Design review controls</span></strong><span data-lang="vi">Điều khiển cục bộ cho bản xem trước; không phải điều hướng sản phẩm.</span><span data-lang="en">Local preview controls; not product navigation.</span></div>' +
          '<div class="eg-review-controls__fields">' +
            '<div class="eg-field"><label class="eg-field__label" for="eg-review-phase"><span data-lang="vi">Giai đoạn</span><span data-lang="en">Phase</span></label><select class="eg-select" id="eg-review-phase" data-eg-review-phase data-size="sm">' + phaseOptions + "</select></div>" +
            '<div class="eg-field"><label class="eg-field__label" for="eg-review-state"><span data-lang="vi">Trạng thái xem xét</span><span data-lang="en">Review state</span></label><select class="eg-select" id="eg-review-state" data-eg-review-state data-size="sm">' + stateOptions + "</select></div>" +
            '<button class="eg-btn eg-btn--secondary eg-btn--sm" type="button" data-eg-locale-toggle aria-label="' + loc("Language: Vietnamese. Switch to English.", "Ngôn ngữ: Tiếng Việt. Chuyển sang English.") + '">' + icon("globe", "eg-icon--sm") + '<span data-eg-locale-text>' + state.locale.toUpperCase() + "</span></button>" +
            '<button class="eg-btn eg-btn--secondary eg-btn--sm" type="button" data-eg-theme-toggle aria-label="' + loc("Switch to dark theme", "Chuyển sang giao diện tối") + '">' + icon(state.theme === "light" ? "moon" : "sun", "eg-icon--sm") + '<span data-eg-theme-text>' + loc("Dark", "Tối") + "</span></button>" +
          "</div>" +
        "</section>";
    });
  }

  function applyTheme() {
    root.setAttribute("data-theme", state.theme);
    store.write("evidgate-theme", state.theme);
    document.querySelectorAll("[data-eg-theme-toggle]").forEach(function (button) {
      var use = button.querySelector("use");
      if (use) use.setAttribute("href", "#eg-i-" + (state.theme === "light" ? "moon" : "sun"));
      var label = button.querySelector("[data-eg-theme-text]");
      if (label) label.textContent = state.theme === "light" ? loc("Dark", "Tối") : loc("Light", "Sáng");
      button.setAttribute("aria-label", state.theme === "light" ? loc("Switch to dark theme", "Chuyển sang giao diện tối") : loc("Switch to light theme", "Chuyển sang giao diện sáng"));
    });
  }

  function applyLocale() {
    root.lang = state.locale;
    store.write("evidgate-locale", state.locale);
    document.querySelectorAll("[data-en][data-vi]").forEach(function (element) {
      element.textContent = state.locale === "vi" ? element.getAttribute("data-vi") : element.getAttribute("data-en");
    });
    document.querySelectorAll("[data-aria-en][data-aria-vi]").forEach(function (element) {
      element.setAttribute("aria-label", state.locale === "vi" ? element.getAttribute("data-aria-vi") : element.getAttribute("data-aria-en"));
    });
    document.querySelectorAll("[data-placeholder-en][data-placeholder-vi]").forEach(function (element) {
      element.setAttribute("placeholder", state.locale === "vi" ? element.getAttribute("data-placeholder-vi") : element.getAttribute("data-placeholder-en"));
    });
    document.querySelectorAll("[data-title-en][data-title-vi]").forEach(function (element) {
      element.setAttribute("title", state.locale === "vi" ? element.getAttribute("data-title-vi") : element.getAttribute("data-title-en"));
    });
    document.querySelectorAll("[data-label-en][data-label-vi]").forEach(function (element) {
      element.setAttribute("data-label", state.locale === "vi" ? element.getAttribute("data-label-vi") : element.getAttribute("data-label-en"));
    });
    document.querySelectorAll("option[data-label-en]").forEach(function (option) {
      option.textContent = state.locale === "vi" ? option.getAttribute("data-label-vi") : option.getAttribute("data-label-en");
    });
    document.querySelectorAll("[data-value-en][data-value-vi]").forEach(function (element) {
      if (element.getAttribute("data-eg-dirty") === "true") return;
      var value = state.locale === "vi" ? element.getAttribute("data-value-vi") : element.getAttribute("data-value-en");
      if (element.value !== value) element.value = value;
    });
    document.querySelectorAll("[data-eg-locale-toggle]").forEach(function (button) {
      var text = button.querySelector("[data-eg-locale-text]");
      if (text) text.textContent = state.locale.toUpperCase();
      button.setAttribute("aria-label", state.locale === "vi" ? "Ngôn ngữ: Tiếng Việt. Chuyển sang English." : "Language: English. Switch to Vietnamese.");
    });
    refreshCounters();
    renderNav();
    applyTheme();
    syncSkeletonMatches();
    syncDraftComparisons();
    document.dispatchEvent(new CustomEvent("eg:locale", { detail: { locale: state.locale } }));
  }

  function applyPhaseGate() {
    var current = PHASES.indexOf(state.phase);
    document.querySelectorAll("[data-eg-phase]").forEach(function (element) {
      var required = PHASES.indexOf(element.getAttribute("data-eg-phase"));
      element.hidden = required > current;
    });
    ensureTabSelection();
    document.querySelectorAll("[data-eg-phase-note]").forEach(function (element) {
      var required = PHASES.indexOf(element.getAttribute("data-eg-phase-note"));
      element.hidden = required <= current;
    });
    var route = getRoute(root.getAttribute("data-nav") || "");
    var pageMin = PHASES.indexOf(root.getAttribute("data-phase-min") || "G1");
    var pageLater = pageMin > current || !!(route && PHASES.indexOf(route.phase) > current);
    document.querySelectorAll("[data-eg-phase-notice]").forEach(function (element) {
      element.hidden = !pageLater;
    });
    document.querySelectorAll("[data-eg-phase-capability]").forEach(function (element) {
      var required = PHASES.indexOf(element.getAttribute("data-eg-phase-capability"));
      element.hidden = pageLater || required > current;
    });
    document.querySelectorAll("[data-eg-master-detail]").forEach(function (host) {
      if (typeof host._egReconcile === "function") host._egReconcile();
    });
  }

  function applyPhase() {
    store.write("evidgate-phase", state.phase);
    renderNav();
    applyPhaseGate();
    syncSkeletonMatches();
  }

  function pagePhaseBlocked() {
    var current = PHASES.indexOf(state.phase);
    var pageMin = PHASES.indexOf(root.getAttribute("data-phase-min") || "G1");
    var route = getRoute(root.getAttribute("data-nav") || "");
    return pageMin > current || !!(route && PHASES.indexOf(route.phase) > current);
  }

  function mutationStateBlocked() {
    return ["loading", "submitting", "401", "403", "404", "429", "read-only"].indexOf(state.scenario) >= 0;
  }

  function isMobileViewport() {
    return window.matchMedia("(max-width: 767px)").matches;
  }

  var mutationAuthoredCaptured = false;

  function syncMutationTriggers() {
    var blocked = mutationStateBlocked() || pagePhaseBlocked();
    document.querySelectorAll("[data-eg-mutate]").forEach(function (element) {
      if (element.matches('[type="submit"]')) return;
      if (!mutationAuthoredCaptured && (element.disabled || element.getAttribute("aria-disabled") === "true")) {
        element.setAttribute("data-eg-authored-disabled", "true");
      }
      var permanentlyDisabled = element.hasAttribute("data-eg-authored-disabled");
      if (element.matches("button, input, select, textarea")) element.disabled = blocked || permanentlyDisabled;
      element.setAttribute("aria-disabled", blocked || permanentlyDisabled ? "true" : "false");
    });
    mutationAuthoredCaptured = true;
  }

  function setScenario(key, announceResult) {
    state.scenario = key;
    syncSkeletonMatches();
    document.querySelectorAll("[data-eg-scenario-group]").forEach(function (group) {
      var panels = group.querySelectorAll("[data-eg-scenario]");
      var hasMatch = Array.prototype.some.call(panels, function (panel) { return panel.getAttribute("data-eg-scenario") === key; });
      panels.forEach(function (panel) {
        var value = panel.getAttribute("data-eg-scenario");
        panel.hidden = hasMatch ? value !== key : value !== "default";
      });
    });
    var filterTables = {};
    document.querySelectorAll("[data-eg-filter-table]").forEach(function (toolbar) {
      var target = document.querySelector(toolbar.getAttribute("data-eg-filter-table"));
      if (target && target.id) filterTables[target.id] = target;
    });
    document.querySelectorAll("[data-eg-show-state], [data-eg-hide-state]").forEach(function (element) {
      var allowed = true;
      var show = element.getAttribute("data-eg-show-state");
      var hide = element.getAttribute("data-eg-hide-state");
      if (show) allowed = show.split(",").indexOf(key) >= 0;
      if (hide) allowed = allowed && hide.split(",").indexOf(key) < 0;
      var table = element.closest ? element.closest("table") : null;
      if (table && table.id && filterTables[table.id] && element.matches("tbody tr")) {
        element._egScenarioAllowed = allowed;
      } else {
        element.hidden = !allowed;
      }
    });
    Object.keys(filterTables).forEach(function (id) {
      if (filterTables[id]._egApplyFilters) filterTables[id]._egApplyFilters();
    });
    var ro = ["401", "403", "read-only"].indexOf(key) >= 0;
    document.body.setAttribute("data-eg-ro", ro ? "true" : "false");
    document.querySelectorAll("[data-eg-review]").forEach(function (host) {
      host.setAttribute("data-eg-ro", ro ? "true" : "false");
    });
    syncMutationTriggers();
    syncDraftComparisons();
    var select = document.querySelector("[data-eg-review-state]");
    if (select && select.value !== key) select.value = key;
    if (announceResult) announce(stateLabel(key));
  }

  function announce(message) {
    var live = document.querySelector("[data-eg-live]");
    if (!live) return;
    live.textContent = "";
    window.setTimeout(function () { live.textContent = message; }, 40);
  }

  function showToast(message, tone) {
    var toast = document.querySelector("[data-eg-toast]");
    if (!toast) {
      toast = document.createElement("div");
      toast.className = "eg-toast";
      toast.setAttribute("data-eg-toast", "");
      toast.setAttribute("role", "status");
      toast.innerHTML = '<span class="eg-toast__message" data-eg-toast-message></span><button class="eg-toast__close" type="button" data-eg-toast-close aria-label="' + loc("Dismiss message", "Đóng thông báo") + '">' + icon("close", "eg-icon--sm") + "</button>";
      document.body.appendChild(toast);
    }
    toast.className = "eg-toast eg-toast--" + (tone || "info");
    toast.querySelector("[data-eg-toast-message]").textContent = message;
    toast.hidden = false;
    window.clearTimeout(showToast._timer);
    showToast._timer = window.setTimeout(function () { toast.hidden = true; }, 4200);
  }

  function setNavMode(mode, userSet) {
    state.navMode = mode;
    if (userSet) state.navUserSet = true;
    root.setAttribute("data-nav-mode", mode);
    if (mode !== "drawer") {
      root.setAttribute("data-nav-open", "false");
      var main = document.querySelector(".eg-shell__main");
      if (main) main.inert = false;
      setNavModal(false);
    }
    var toggle = document.querySelector("[data-eg-nav-toggle]");
    if (toggle) {
      var expanded = mode === "expanded" || (mode === "drawer" && root.getAttribute("data-nav-open") === "true");
      toggle.setAttribute("aria-expanded", expanded ? "true" : "false");
    }
    setNavInert();
    syncSkeletonMatches();
  }

  function setNavModal(isOpen) {
    var nav = document.querySelector(".eg-nav");
    if (!nav) return;
    if (isOpen) {
      nav.setAttribute("role", "dialog");
      nav.setAttribute("aria-modal", "true");
    } else {
      nav.removeAttribute("role");
      nav.removeAttribute("aria-modal");
    }
  }

  function setNavInert() {
    var nav = document.querySelector(".eg-nav");
    if (!nav) return;
    var open = root.getAttribute("data-nav-open") === "true";
    nav.inert = window.innerWidth < 768 && !open;
  }

  function openDrawer() {
    root.setAttribute("data-nav-open", "true");
    var main = document.querySelector(".eg-shell__main");
    if (main) main.inert = true;
    setNavModal(true);
    var toggle = document.querySelector("[data-eg-nav-toggle]");
    if (toggle) toggle.setAttribute("aria-expanded", "true");
    setNavInert();
    var first = document.querySelector(".eg-nav .eg-nav__close");
    if (first) first.focus();
  }

  function closeDrawer() {
    if (window.innerWidth < 768 && state.navMode !== "drawer") setNavMode("drawer", true);
    root.setAttribute("data-nav-open", "false");
    var main = document.querySelector(".eg-shell__main");
    if (main) main.inert = false;
    setNavModal(false);
    var toggle = document.querySelector("[data-eg-nav-toggle]");
    if (toggle) {
      toggle.setAttribute("aria-expanded", "false");
      toggle.focus();
    }
    setNavInert();
  }

  function trapDrawerFocus(event) {
    if (event.key !== "Tab" || root.getAttribute("data-nav-open") !== "true") return;
    var nav = document.querySelector(".eg-nav");
    if (!nav) return;
    var focusable = nav.querySelectorAll('a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])');
    if (!focusable.length) return;
    var first = focusable[0];
    var last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  function bindNavToggle() {
    var toggle = document.querySelector("[data-eg-nav-toggle]");
    if (!toggle) return;
    toggle.addEventListener("click", function () {
      if (window.innerWidth < 768) {
        if (root.getAttribute("data-nav-open") === "true") closeDrawer();
        else openDrawer();
        return;
      }
      setNavMode(state.navMode === "expanded" ? "rail" : "expanded", true);
    });
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape" && root.getAttribute("data-nav-open") === "true") {
        closeDrawer();
      }
      trapDrawerFocus(event);
    });
    document.addEventListener("click", function (event) {
      if (root.getAttribute("data-nav-open") !== "true") return;
      var nav = document.querySelector(".eg-nav");
      if (nav && !nav.contains(event.target) && !toggle.contains(event.target)) closeDrawer();
    });
    document.addEventListener("click", function (event) {
      if (event.target.closest("[data-eg-nav-close]")) {
        closeDrawer();
        return;
      }
      if (window.innerWidth >= 768) return;
      if (event.target.closest(".eg-nav__item")) closeDrawer();
    });
  }

  function bindNavSearch() {
    var input = document.querySelector("[data-eg-nav-search]");
    if (!input) return;
    input.addEventListener("input", function () {
      state.navQuery = input.value || "";
      applyNavFilter();
    });
  }

  function bindLocaleThemePhase() {
    document.querySelectorAll("[data-eg-locale-toggle]").forEach(function (button) {
      button.addEventListener("click", function () {
        state.locale = state.locale === "vi" ? "en" : "vi";
        applyLocale();
        setScenario(state.scenario, false);
        button.focus();
      });
    });
    document.querySelectorAll("[data-eg-theme-toggle]").forEach(function (button) {
      button.addEventListener("click", function () {
        state.theme = state.theme === "light" ? "dark" : "light";
        applyTheme();
        button.focus();
      });
    });
    document.querySelectorAll("[data-eg-review-phase]").forEach(function (select) {
      select.value = state.phase;
      select.addEventListener("change", function () {
        state.phase = select.value;
        applyPhase();
        setScenario(state.scenario, false);
        select.focus();
        announce(loc("Review phase changed", "Đã đổi giai đoạn xem xét"));
      });
    });
    document.querySelectorAll("[data-eg-review-state]").forEach(function (select) {
      select.value = state.scenario;
      select.addEventListener("change", function () {
        setScenario(select.value, true);
        select.focus();
      });
    });
  }

  function bindMenus() {
    document.querySelectorAll(".eg-menu").forEach(function (menu) {
      var trigger = menu.querySelector(".eg-menu__trigger");
      var panel = menu.querySelector(".eg-menu__panel");
      if (!trigger || !panel) return;
      function close(restore) {
        if (panel.hidden) return;
        panel.hidden = true;
        trigger.setAttribute("aria-expanded", "false");
        if (restore) trigger.focus();
      }
      trigger.addEventListener("click", function () {
        var opening = panel.hidden;
        document.querySelectorAll(".eg-menu__panel").forEach(function (other) {
          if (other !== panel && !other.hidden) {
            other.hidden = true;
            var otherTrigger = other.parentElement.querySelector(".eg-menu__trigger");
            if (otherTrigger) otherTrigger.setAttribute("aria-expanded", "false");
          }
        });
        panel.hidden = !opening;
        trigger.setAttribute("aria-expanded", opening ? "true" : "false");
        if (opening) {
          var first = panel.querySelector(".eg-input, .eg-menu__item");
          if (first) first.focus();
        }
      });
      panel.addEventListener("keydown", function (event) {
        if (event.key === "Escape") {
          event.preventDefault();
          close(true);
          return;
        }
        if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
        var items = Array.prototype.filter.call(panel.querySelectorAll(".eg-menu__item"), function (item) { return !item.hidden; });
        if (!items.length) return;
        event.preventDefault();
        var index = items.indexOf(document.activeElement);
        var next = event.key === "ArrowDown" ? index + 1 : index - 1;
        if (next < 0) next = items.length - 1;
        if (next >= items.length) next = 0;
        items[next].focus();
      });
      document.addEventListener("click", function (event) {
        if (!menu.contains(event.target)) close(false);
      });
      document.addEventListener("keydown", function (event) {
        if (event.key === "Escape" && !panel.hidden) close(true);
      });
    });
  }

  function bindDialogs() {
    document.addEventListener("click", function (event) {
      var opener = event.target.closest("[data-eg-open]");
      if (opener) {
        var dialog = document.getElementById(opener.getAttribute("data-eg-open"));
        if (!dialog) return;
        if (opener.disabled || opener.getAttribute("aria-disabled") === "true") {
          event.preventDefault();
          return;
        }
        if (opener.hasAttribute("data-eg-mutate")) {
          var blockedMessage = "";
          if (pagePhaseBlocked()) blockedMessage = loc("This action is unavailable before G4.", "Thao tác này không khả dụng trước G4.");
          else if (mutationStateBlocked()) blockedMessage = loc("This action is unavailable in the current state.", "Thao tác này không khả dụng ở trạng thái hiện tại.");
          else if (isMobileViewport()) blockedMessage = loc("Editing requires a desktop. Open this page on a larger screen.", "Chỉnh sửa cần máy tính. Mở trang này trên màn hình lớn hơn.");
          if (blockedMessage) {
            event.preventDefault();
            announce(blockedMessage);
            return;
          }
        }
        state.lastDialogTrigger = opener;
        if (typeof dialog.showModal === "function") dialog.showModal();
        else dialog.setAttribute("open", "");
        var focusTarget = dialog.querySelector("button:not([disabled]), [href], input:not([disabled]), select:not([disabled])");
        if (focusTarget) focusTarget.focus();
        return;
      }
      var closer = event.target.closest("[data-eg-close]");
      if (closer) {
        var openDialog = closer.closest("dialog");
        if (openDialog) openDialog.close();
      }
    });
    document.querySelectorAll("dialog").forEach(function (dialog) {
      dialog.addEventListener("click", function (event) {
        if (event.target !== dialog) return;
        var rect = dialog.getBoundingClientRect();
        var inside = event.clientX >= rect.left && event.clientX <= rect.right && event.clientY >= rect.top && event.clientY <= rect.bottom;
        if (!inside) dialog.close();
      });
      dialog.addEventListener("close", function () {
        if (state.lastDialogTrigger && document.contains(state.lastDialogTrigger)) {
          state.lastDialogTrigger.focus();
        }
      });
    });
    var mobile = window.matchMedia("(max-width: 767px)");
    function closeMutationDialogsForMobile() {
      if (!mobile.matches) return;
      document.querySelectorAll("dialog[open]").forEach(function (dialog) {
        if (!dialog.querySelector("form[data-eg-fixture-form]")) return;
        dialog.close();
        var message = loc("Editing requires a desktop. Your draft is retained.", "Chỉnh sửa cần máy tính. Bản nháp của bạn được giữ lại.");
        showToast(message, "info");
        announce(message);
      });
    }
    if (mobile.addEventListener) mobile.addEventListener("change", closeMutationDialogsForMobile);
    else if (mobile.addListener) mobile.addListener(closeMutationDialogsForMobile);
  }

  function ensureTabSelection() {
    document.querySelectorAll("[data-eg-tabs]").forEach(function (tabs) {
      var selected = tabs.querySelector('[role="tab"][aria-selected="true"]');
      if (selected && !selected.hidden && !selected.disabled && !selected.closest("[hidden]")) return;
      var first = Array.prototype.filter.call(tabs.querySelectorAll('[role="tab"]'), function (button) {
        return !button.hidden && !button.disabled && !button.closest("[hidden]");
      })[0];
      if (first) first.click();
    });
  }

  function tabEnabled(button) {
    return !button.hidden && !button.disabled && !button.closest("[hidden]");
  }

  function bindTabs() {
    document.querySelectorAll("[data-eg-tabs]").forEach(function (tabs) {
      var buttons = Array.prototype.slice.call(tabs.querySelectorAll('[role="tab"]'));
      if (!buttons.length) return;
      var select = tabs.querySelector("[data-eg-tab-select]");
      var vertical = tabs.classList.contains("eg-tabs--vertical");
      function enabled() {
        return buttons.filter(tabEnabled);
      }
      function activate(button) {
        if (!button || !tabEnabled(button)) return;
        buttons.forEach(function (item) {
          var selected = item === button;
          item.setAttribute("aria-selected", selected ? "true" : "false");
          item.setAttribute("tabindex", selected ? "0" : "-1");
          var panel = document.getElementById(item.getAttribute("aria-controls"));
          if (panel) panel.hidden = !selected;
        });
        if (select) {
          Array.prototype.forEach.call(select.options, function (option) {
            var target = document.getElementById(option.value);
            option.disabled = !target || !tabEnabled(target);
          });
          if (button.id && select.value !== button.id) select.value = button.id;
        }
      }
      buttons.forEach(function (button) {
        button.addEventListener("click", function () { activate(button); });
        button.addEventListener("keydown", function (event) {
          var list = enabled();
          var index = list.indexOf(button);
          if (index < 0) return;
          var next = null;
          if (event.key === "ArrowRight" || (vertical && event.key === "ArrowDown")) next = list[(index + 1) % list.length];
          if (event.key === "ArrowLeft" || (vertical && event.key === "ArrowUp")) next = list[(index - 1 + list.length) % list.length];
          if (event.key === "Home") next = list[0];
          if (event.key === "End") next = list[list.length - 1];
          if (!next) return;
          event.preventDefault();
          next.focus();
          activate(next);
        });
      });
      if (select) {
        select.addEventListener("change", function () {
          var target = document.getElementById(select.value);
          if (target && target.getAttribute("role") === "tab" && tabEnabled(target)) activate(target);
        });
      }
      var selected = buttons.filter(function (button) {
        return button.getAttribute("aria-selected") === "true" && tabEnabled(button);
      })[0] || enabled()[0];
      activate(selected);
    });
  }

  var counterRegistry = [];

  function refreshCounters() {
    counterRegistry.forEach(function (entry) { entry.update(); });
  }

  function bindCounters() {
    document.querySelectorAll("[data-eg-counter-for]").forEach(function (counter, index) {
      if (/^(INPUT|TEXTAREA|SELECT)$/.test(counter.tagName)) return;
      var target = document.getElementById(counter.getAttribute("data-eg-counter-for"));
      if (!target) return;
      var max = counterMax(counter, target);
      if (!counter.id) counter.id = (target.id || "eg-counter-" + index) + "-counter";
      var described = (target.getAttribute("aria-describedby") || "").split(/\s+/).filter(Boolean);
      if (described.indexOf(counter.id) < 0) described.push(counter.id);
      if (max) {
        var error = document.createElement("span");
        error.className = "eg-field__error";
        error.id = counter.id + "-error";
        error.hidden = true;
        counter.insertAdjacentElement("afterend", error);
        counter._error = error;
        described.push(error.id);
      }
      target.setAttribute("aria-describedby", described.join(" "));
      function update() {
        var value = target.value || "";
        var suffix = counter.getAttribute("data-eg-counter-suffix");
        if (state.locale === "vi" && counter.hasAttribute("data-eg-counter-suffix-vi")) {
          suffix = counter.getAttribute("data-eg-counter-suffix-vi");
        }
        counter.textContent = value.length + (max ? " / " + max : "") + (suffix ? " " + suffix : "");
        updateCounterState(counter, target);
      }
      counter.setAttribute("aria-live", "polite");
      target.addEventListener("input", update);
      counterRegistry.push({ update: update });
      update();
    });
  }

  function treeItemVisible(tree, item) {
    var node = item;
    while (node && node !== tree) {
      if (node.hidden || node.getAttribute("aria-hidden") === "true") return false;
      if (node.style && node.style.display === "none") return false;
      node = node.parentElement;
    }
    return true;
  }

  function visibleTreeItems(tree) {
    return Array.prototype.filter.call(tree.querySelectorAll('[role="treeitem"]'), function (item) {
      return treeItemVisible(tree, item);
    });
  }

  function ensureTreeTabstop(tree, preferred) {
    var visible = visibleTreeItems(tree);
    var current = visible.filter(function (node) { return node.getAttribute("tabindex") === "0"; })[0];
    var keep = preferred && visible.indexOf(preferred) >= 0 ? preferred : (current || visible[0]);
    tree.querySelectorAll('[role="treeitem"]').forEach(function (node) {
      node.setAttribute("tabindex", node === keep ? "0" : "-1");
    });
    return keep;
  }

  function selectTreeItem(tree, item) {
    if (!item || !treeItemVisible(tree, item)) return;
    tree.querySelectorAll('[role="treeitem"]').forEach(function (node) {
      node.setAttribute("aria-selected", node === item ? "true" : "false");
    });
    ensureTreeTabstop(tree, item);
    item.focus();
  }

  function toggleTreeItem(item) {
    var expanded = item.getAttribute("aria-expanded");
    if (expanded === null) return;
    var next = expanded === "true" ? "false" : "true";
    item.setAttribute("aria-expanded", next);
    var children = item.querySelector(".eg-tree__children");
    if (children) {
      children.hidden = next === "false";
      if (next === "false") children.setAttribute("aria-hidden", "true");
      else children.removeAttribute("aria-hidden");
    }
    var toggle = item.querySelector(":scope > .eg-tree__toggle[data-eg-disclosure]");
    if (toggle) toggle.setAttribute("aria-expanded", next);
    var tree = item.closest("[data-eg-tree]");
    if (tree) {
      ensureTreeTabstop(tree, item);
      if (document.activeElement && item.contains(document.activeElement) && !treeItemVisible(tree, document.activeElement)) item.focus();
    }
  }

  function bindTrees() {
    document.querySelectorAll("[data-eg-tree]").forEach(function (tree) {
      var items = tree.querySelectorAll('[role="treeitem"]');
      if (!items.length) return;
      items.forEach(function (item) { item.setAttribute("tabindex", "-1"); });
      items[0].setAttribute("tabindex", "0");
      tree.addEventListener("click", function (event) {
        var toggle = event.target.closest(".eg-tree__toggle");
        var item = event.target.closest('[role="treeitem"]');
        if (!item) return;
        selectTreeItem(tree, item);
        if (toggle) toggleTreeItem(item);
        else if (item.getAttribute("aria-expanded") !== null && event.target.closest(".eg-tree__label")) {
          toggleTreeItem(item);
        }
      });
      tree.addEventListener("keydown", function (event) {
        var item = event.target.closest('[role="treeitem"]');
        if (!item) return;
        var visible = visibleTreeItems(tree);
        var index = visible.indexOf(item);
        if (event.key === "ArrowDown") {
          event.preventDefault();
          if (visible[index + 1]) selectTreeItem(tree, visible[index + 1]);
        } else if (event.key === "ArrowUp") {
          event.preventDefault();
          if (visible[index - 1]) selectTreeItem(tree, visible[index - 1]);
        } else if (event.key === "Home") {
          event.preventDefault();
          if (visible[0]) selectTreeItem(tree, visible[0]);
        } else if (event.key === "End") {
          event.preventDefault();
          selectTreeItem(tree, visible[visible.length - 1]);
        } else if (event.key === "ArrowRight") {
          event.preventDefault();
          if (item.getAttribute("aria-expanded") === "false") toggleTreeItem(item);
          else {
            var next = visible[index + 1];
            if (next && item.contains(next)) selectTreeItem(tree, next);
          }
        } else if (event.key === "ArrowLeft") {
          event.preventDefault();
          if (item.getAttribute("aria-expanded") === "true") {
            toggleTreeItem(item);
          } else {
            var parentChildren = item.parentElement.closest(".eg-tree__children");
            var parent = parentChildren ? parentChildren.parentElement.closest('[role="treeitem"]') : null;
            if (parent) selectTreeItem(tree, parent);
          }
        } else if (event.key === "Enter" && item.getAttribute("aria-expanded") === null) {
          var link = item.querySelector("a[href]");
          selectTreeItem(tree, item);
          if (link) {
            link.click();
            return;
          }
          event.preventDefault();
        } else if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          selectTreeItem(tree, item);
          if (item.getAttribute("aria-expanded") !== null) toggleTreeItem(item);
        }
      });
    });
  }

  function sortTable(table, header, button) {
    var body = table.querySelector("tbody");
    if (!body) return;
    var headers = Array.prototype.slice.call(table.querySelectorAll("th"));
    var index = headers.indexOf(header);
    var direction = header.getAttribute("aria-sort") === "ascending" ? "descending" : "ascending";
    headers.forEach(function (item) { item.setAttribute("aria-sort", "none"); });
    header.setAttribute("aria-sort", direction);
    var groups = [];
    Array.prototype.forEach.call(body.children, function (row) {
      if (row.hasAttribute("data-eg-filter-ignore")) return;
      var group = [row];
      var next = row.nextElementSibling;
      while (next && next.hasAttribute("data-eg-filter-ignore")) {
        group.push(next);
        next = next.nextElementSibling;
      }
      groups.push(group);
    });
    groups.sort(function (a, b) {
      var aCell = a[0].children[index];
      var bCell = b[0].children[index];
      var aValue = aCell ? (aCell.getAttribute("data-sort-value") || aCell.textContent.trim()) : "";
      var bValue = bCell ? (bCell.getAttribute("data-sort-value") || bCell.textContent.trim()) : "";
      return aValue.localeCompare(bValue, state.locale, { numeric: true, sensitivity: "base" }) * (direction === "ascending" ? 1 : -1);
    });
    groups.forEach(function (group) {
      group.forEach(function (row) { body.appendChild(row); });
    });
    button.setAttribute("aria-label", loc("Sorted " + direction, "Đã sắp xếp " + (direction === "ascending" ? "tăng dần" : "giảm dần")));
  }

  function bindTables() {
    document.querySelectorAll("[data-eg-table]").forEach(function (table) {
      table.querySelectorAll(".eg-table__sort").forEach(function (button) {
        button.addEventListener("click", function () {
          var header = button.closest("th");
          if (header) sortTable(table, header, button);
        });
      });
      var all = table.querySelector("[data-eg-select-all]");
      var rows = table.querySelectorAll("[data-eg-select-row]");
      var count = table.closest(".eg-card") ? table.closest(".eg-card").querySelector("[data-eg-select-count]") : null;
      function selectableRows() {
        return Array.prototype.filter.call(rows, function (box) {
          var row = box.closest("tr");
          return !row || !row.hidden;
        });
      }
      function sync() {
        var selected = 0;
        var visibleRows = selectableRows();
        rows.forEach(function (box) {
          var row = box.closest("tr");
          if (row && row.hidden && box.checked) box.checked = false;
          if (box.checked) selected += 1;
          if (row) row.setAttribute("aria-selected", box.checked ? "true" : "false");
        });
        if (all) {
          all.checked = visibleRows.length > 0 && selected === visibleRows.length;
          all.indeterminate = selected > 0 && selected < visibleRows.length;
        }
        if (count) count.textContent = String(selected);
      }
      table._egSyncSelection = sync;
      if (all) {
        all.addEventListener("change", function () {
          selectableRows().forEach(function (box) { box.checked = all.checked; });
          sync();
        });
      }
      rows.forEach(function (box) { box.addEventListener("change", sync); });
      sync();
    });
  }

  function bindFilters() {
    document.querySelectorAll("[data-eg-filter-table]").forEach(function (toolbar) {
      var table = document.querySelector(toolbar.getAttribute("data-eg-filter-table"));
      if (!table) return;
      var controls = Array.prototype.slice.call(toolbar.querySelectorAll("[data-eg-filter]"));
      var emptySelector = toolbar.getAttribute("data-eg-filter-empty");
      var empty = emptySelector ? document.querySelector(emptySelector) : null;
      var hideSelector = toolbar.getAttribute("data-eg-filter-hide");
      var hideTarget = hideSelector ? document.querySelector(hideSelector) : null;
      var summary = toolbar.querySelector("[data-eg-filter-count]");
      var tableSelector = toolbar.getAttribute("data-eg-filter-table");
      var totalTargets = Array.prototype.slice.call(document.querySelectorAll('[data-eg-filter-total="' + tableSelector + '"]'));
      var summaryTargets = Array.prototype.slice.call(document.querySelectorAll('[data-eg-filter-summary="' + tableSelector + '"]'));
      var pagerTargets = Array.prototype.slice.call(document.querySelectorAll("[data-eg-filter-pager]")).filter(function (target) {
        return target.getAttribute("data-eg-filter-pager") === tableSelector;
      });
      var summaryDefaults = summaryTargets.map(function (target) {
        return {
          en: target.getAttribute("data-en"),
          vi: target.getAttribute("data-vi"),
          text: target.textContent
        };
      });
      var resets = Array.prototype.slice.call(toolbar.querySelectorAll("[data-eg-filter-reset]"));
      if (toolbar.id) {
        resets = resets.concat(Array.prototype.slice.call(document.querySelectorAll('[data-eg-filter-reset="#' + toolbar.id + '"]')));
      }
      var isTableTarget = table.tagName === "TABLE";
      var rows = isTableTarget
        ? Array.prototype.slice.call(table.querySelectorAll("tbody tr:not([data-eg-filter-ignore])"))
        : Array.prototype.slice.call(table.querySelectorAll("[data-eg-detail-row]"));
      function apply() {
        var shown = 0;
        if (isTableTarget) {
          table.querySelectorAll("[data-eg-filter-ignore]").forEach(function (row) {
            row.hidden = true;
            if (!row.id) return;
            table.querySelectorAll('[data-eg-disclosure][aria-controls="' + row.id + '"]').forEach(function (button) {
              button.setAttribute("aria-expanded", "false");
            });
          });
        }
        rows.forEach(function (row) {
          var filtered = controls.every(function (control) {
            var value = control.value || "";
            if (!value) return true;
            var actual = row.getAttribute("data-eg-" + control.getAttribute("data-eg-filter")) || "";
            if (control.getAttribute("data-eg-match") === "contains") {
              return actual.toLocaleLowerCase().indexOf(value.toLocaleLowerCase()) !== -1;
            }
            return actual === value;
          });
          var match = filtered && row._egScenarioAllowed !== false;
          row.hidden = !match;
          if (match) shown += 1;
        });
        if (empty) empty.hidden = shown !== 0;
        if (hideTarget) {
          var stateHidden = hideTarget.hasAttribute("data-eg-hide-state") &&
            (hideTarget.getAttribute("data-eg-hide-state") || "").split(",").map(function (part) {
              return part.trim();
            }).indexOf(state.scenario) !== -1;
          hideTarget.hidden = shown === 0 || stateHidden;
        }
        if (summary) summary.textContent = String(shown);
        totalTargets.forEach(function (target) {
          target.textContent = String(shown);
        });
        var activeFilters = controls.map(function (control) {
          return (control.value || "").trim();
        }).filter(Boolean);
        summaryTargets.forEach(function (target, index) {
          var fallback = summaryDefaults[index];
          if (!activeFilters.length) {
            target.textContent = fallback.text;
            if (fallback.en !== null) target.setAttribute("data-en", fallback.en);
            else target.removeAttribute("data-en");
            if (fallback.vi !== null) target.setAttribute("data-vi", fallback.vi);
            else target.removeAttribute("data-vi");
            return;
          }
          var scopeText = activeFilters.join(" · ");
          target.textContent = scopeText;
          if (fallback.en !== null) target.setAttribute("data-en", scopeText);
          if (fallback.vi !== null) target.setAttribute("data-vi", scopeText);
        });
        pagerTargets.forEach(function (target) {
          var template = document.documentElement.lang === "en"
            ? (target.getAttribute("data-pager-en") || "Showing {start}–{end} of {total} · cursor pagination")
            : (target.getAttribute("data-pager-vi") || "Đang hiển thị {start}–{end} trên {total} · phân trang bằng con trỏ");
          var start = shown ? 1 : 0;
          target.textContent = template
            .replace("{start}", String(start))
            .replace("{end}", String(shown))
            .replace("{total}", String(shown));
        });
        if (table._egSyncSelection) table._egSyncSelection();
        if (table._egSyncDetail) table._egSyncDetail();
      }
      table._egApplyFilters = apply;
      controls.forEach(function (control) { control.addEventListener("change", apply); control.addEventListener("input", apply); });
      document.addEventListener("eg:locale", apply);
      resets.forEach(function (reset) {
        reset.addEventListener("click", function () {
          controls.forEach(function (control) { control.value = ""; });
          apply();
          announce(loc("Filters cleared", "Đã xoá bộ lọc"));
          reset.focus();
          if (table._egSyncSelection) table._egSyncSelection();
        });
      });
      apply();
    });
  }

  function bindDisclosures() {
    document.querySelectorAll("[data-eg-disclosure]").forEach(function (button) {
      button.addEventListener("click", function () {
        var target = button.getAttribute("aria-controls") ? document.getElementById(button.getAttribute("aria-controls")) : null;
        if (!target) {
          var item = button.closest("li");
          target = item ? item.querySelector(":scope > ul") : null;
        }
        if (!target) return;
        var expanded = button.getAttribute("aria-expanded") === "true";
        button.setAttribute("aria-expanded", expanded ? "false" : "true");
        target.hidden = expanded;
        if (expanded) target.setAttribute("aria-hidden", "true");
        else target.removeAttribute("aria-hidden");
      });
    });
  }

  function fieldLabel(field) {
    if (field.id) {
      var label = document.querySelector('label[for="' + field.id + '"]');
      if (label) return label.textContent.trim();
    }
    var wrap = field.closest(".eg-field");
    var fallback = wrap ? wrap.querySelector(".eg-field__label") : null;
    return fallback ? fallback.textContent.trim() : field.id || loc("Field", "Trường");
  }

  function ensureFieldError(field, messageEn, messageVi) {
    var wrap = field.closest(".eg-field") || field.parentElement;
    if (!wrap) return null;
    var id = (field.id || "eg-field") + "-error";
    var error = wrap.querySelector(".eg-field__error[data-eg-generated-error]");
    if (!error) {
      error = document.createElement("span");
      error.className = "eg-field__error";
      error.setAttribute("data-eg-generated-error", "");
      error.id = id;
      error.innerHTML = '<svg class="eg-icon eg-icon--sm" aria-hidden="true" focusable="false"><use href="#eg-i-alert"></use></svg><span></span>';
      var hint = wrap.querySelector(".eg-field__hint");
      if (hint) hint.insertAdjacentElement("beforebegin", error);
      else field.insertAdjacentElement("afterend", error);
    }
    error.hidden = false;
    error.querySelector("span").textContent = loc(messageEn || "This field is required before saving.", messageVi || "Trường này là bắt buộc trước khi lưu.");
    var described = (field.getAttribute("aria-describedby") || "").split(/\s+/).filter(Boolean);
    if (described.indexOf(error.id) < 0) described.push(error.id);
    field.setAttribute("aria-describedby", described.join(" "));
    field.setAttribute("aria-invalid", "true");
    var fieldWrap = field.closest(".eg-field");
    if (fieldWrap) fieldWrap.setAttribute("data-state", "invalid");
    return error;
  }

  function ensureGroupError(group, messageEn, messageVi) {
    if (!group) return null;
    var id = (group.id || "eg-permission-group") + "-error";
    var error = group.querySelector(".eg-field__error[data-eg-generated-error]");
    if (!error) {
      error = document.createElement("span");
      error.className = "eg-field__error";
      error.setAttribute("data-eg-generated-error", "");
      error.id = id;
      error.innerHTML = '<svg class="eg-icon eg-icon--sm" aria-hidden="true" focusable="false"><use href="#eg-i-alert"></use></svg><span></span>';
      var legend = group.querySelector("legend");
      if (legend) legend.insertAdjacentElement("afterend", error);
      else group.insertBefore(error, group.firstChild);
    }
    error.hidden = false;
    error.querySelector("span").textContent = loc(messageEn || "Select at least one option.", messageVi || "Chọn ít nhất một tùy chọn.");
    group.setAttribute("data-state", "invalid");
    group.setAttribute("aria-describedby", id);
    return error;
  }

  function clearFieldError(field) {
    var wrap = field.closest(".eg-field") || field.parentElement;
    if (wrap) {
      wrap.querySelectorAll(".eg-field__error[data-eg-generated-error]").forEach(function (error) {
        var described = (field.getAttribute("aria-describedby") || "").split(/\s+/).filter(function (id) { return id && id !== error.id; });
        if (described.length) field.setAttribute("aria-describedby", described.join(" "));
        else field.removeAttribute("aria-describedby");
        error.remove();
      });
      if (wrap.getAttribute("data-state") === "invalid") wrap.removeAttribute("data-state");
    }
    field.removeAttribute("aria-invalid");
  }

  function fixtureFieldValue(field) {
    if (field.tagName === "SELECT") {
      return field.options[field.selectedIndex] ? field.options[field.selectedIndex].text : field.value;
    }
    return field.value || "";
  }

  function fixtureBaseValue(field) {
    var en = field.getAttribute("data-eg-base-en");
    var vi = field.getAttribute("data-eg-base-vi");
    if (en === null && vi === null) return "";
    return state.locale === "vi" ? (vi !== null ? vi : en) : (en !== null ? en : vi);
  }

  function fixtureDraftFields(scope) {
    return Array.prototype.filter.call(scope.querySelectorAll("input, textarea, select"), function (field) {
      return field.id && (field.hasAttribute("data-eg-base-en") || field.hasAttribute("data-eg-base-vi"));
    });
  }

  function dirtyFixtureFields(scope) {
    return fixtureDraftFields(scope).filter(function (field) {
      return field.getAttribute("data-eg-dirty") === "true" && fixtureFieldValue(field) !== fixtureBaseValue(field);
    });
  }

  function syncDraftComparisons() {
    document.querySelectorAll("[data-eg-draft-scope]").forEach(function (host) {
      var scope = document.querySelector(host.getAttribute("data-eg-draft-scope"));
      if (!scope) return;
      var dirty = dirtyFixtureFields(scope);
      var dirtyIds = dirty.map(function (field) { return field.id; });
      host.querySelectorAll("[data-eg-draft-field]").forEach(function (row) {
        var field = document.getElementById(row.getAttribute("data-eg-draft-field"));
        var always = row.hasAttribute("data-eg-draft-always");
        var isDirty = !!(field && dirtyIds.indexOf(field.id) >= 0);
        row.hidden = !(isDirty || always);
        var valueEl = row.querySelector("[data-eg-draft-value]");
        if (valueEl) valueEl.textContent = field ? fixtureFieldValue(field) : "";
        var baseEl = row.querySelector("[data-eg-draft-base]");
        if (baseEl && field) baseEl.textContent = fixtureBaseValue(field);
      });
      host.querySelectorAll("[data-eg-draft-generated]").forEach(function (node) { node.remove(); });
      var generated = document.createElement("div");
      generated.className = "eg-stack";
      generated.setAttribute("data-gap", "2");
      generated.setAttribute("data-eg-draft-generated", "");
      dirty.forEach(function (field) {
        if (host.querySelector('[data-eg-draft-field="' + field.id + '"]')) return;
        var labelEl = scope.querySelector('label[for="' + field.id + '"]');
        var row = document.createElement("div");
        row.className = "eg-kpi__context";
        var strong = document.createElement("strong");
        strong.textContent = labelEl ? labelEl.textContent.trim() : field.id;
        var value = document.createElement("span");
        value.setAttribute("data-eg-draft-value", "");
        value.textContent = fixtureFieldValue(field);
        row.appendChild(strong);
        row.appendChild(document.createTextNode(" · "));
        row.appendChild(value);
        generated.appendChild(row);
      });
      var none = host.querySelector("[data-eg-draft-none]");
      if (generated.children.length) host.insertBefore(generated, none || null);
      if (none) none.hidden = dirty.length > 0;
      var changed = host.querySelector("[data-eg-changed-fields]");
      if (changed) {
        var names = dirty.map(function (field) {
          var row = host.querySelector('[data-eg-draft-field="' + field.id + '"]');
          var label = row ? row.querySelector("strong") : null;
          if (!label) {
            var labelEl = scope.querySelector('label[for="' + field.id + '"]');
            return labelEl ? labelEl.textContent.trim() : field.id;
          }
          return label.textContent.trim();
        });
        if (!names.length) {
          names = Array.prototype.map.call(host.querySelectorAll("[data-eg-draft-field][data-eg-draft-always]"), function (row) {
            var label = row.querySelector("strong");
            return label ? label.textContent.trim() : "";
          }).filter(Boolean);
        }
        changed.textContent = names.length ? loc("Changed fields · ", "Trường đã đổi · ") + names.join(", ") : loc("No changed fields kept", "Không có trường thay đổi nào được giữ");
      }
    });
  }

  function setFormStatus(form, key) {
    form.setAttribute("data-eg-form-state", key);
    var status = form.querySelector("[data-eg-form-status]");
    if (!status) return;
    status.hidden = key === "clean" || key === "saved";
    if (key === "dirty") {
      status.textContent = loc("Unsaved changes", "Thay đổi chưa lưu");
      status.setAttribute("data-state", "dirty");
    } else if (key === "saving") {
      status.textContent = loc("Saving · local specimen", "Đang lưu · mẫu cục bộ");
      status.setAttribute("data-state", "saving");
    } else if (key === "error") {
      status.textContent = loc("Not saved · fix the highlighted fields", "Chưa lưu · sửa các trường được đánh dấu");
      status.setAttribute("data-state", "error");
    }
  }

  function validateFixtureForm(form) {
    var invalid = null;
    form.querySelectorAll(".eg-field__error[data-eg-generated-error]").forEach(function (error) { error.remove(); });
    form.querySelectorAll("input, textarea, select").forEach(function (field) {
      if (field.disabled || field.type === "hidden" || field.closest("[hidden]")) return;
      field.removeAttribute("aria-invalid");
      var required = field.hasAttribute("required") || field.getAttribute("aria-required") === "true";
      var empty = field.type === "checkbox" || field.type === "radio"
        ? !field.checked
        : !(field.value || "").trim();
      if (required && empty) {
        ensureFieldError(field);
        if (!invalid) invalid = field;
      }
    });
    form.querySelectorAll("[data-eg-counter-for]").forEach(function (counter) {
      if (/^(INPUT|TEXTAREA|SELECT)$/.test(counter.tagName)) return;
      var target = document.getElementById(counter.getAttribute("data-eg-counter-for"));
      if (!target) return;
      if (updateCounterState(counter, target) && !invalid) invalid = target;
    });
    var pairs = {};
    form.querySelectorAll("[data-eg-pair]").forEach(function (field) {
      var key = field.getAttribute("data-eg-pair");
      if (!pairs[key]) pairs[key] = [];
      pairs[key].push(field);
    });
    Object.keys(pairs).forEach(function (key) {
      var fields = pairs[key];
      var hasValue = fields.some(function (field) { return !!(field.value || "").trim(); });
      if (!hasValue) return;
      fields.forEach(function (field) {
        if ((field.value || "").trim()) return;
        ensureFieldError(field);
        if (!invalid) invalid = field;
      });
    });
    form.querySelectorAll('[data-eg-validate="safe-url"]').forEach(function (field) {
      if (field.disabled || field.closest("[hidden]")) return;
      var value = (field.value || "").trim();
      if (!value) return;
      var parsed = null;
      try {
        parsed = new URL(value);
      } catch (error) {
        parsed = null;
      }
      var unsafe = !parsed || parsed.protocol !== "https:" || !parsed.hostname || !!(parsed.username || parsed.password);
      if (!unsafe) return;
      ensureFieldError(field, "Enter a safe HTTPS URL without embedded credentials.", "Nhập URL HTTPS an toàn, không kèm thông tin đăng nhập.");
      if (!invalid) invalid = field;
    });
    form.querySelectorAll("[data-eg-min-checked]").forEach(function (group) {
      if (group.closest("[hidden]")) return;
      var minimum = parseInt(group.getAttribute("data-eg-min-checked"), 10) || 1;
      var checked = group.querySelectorAll('input[type="checkbox"]:checked').length;
      if (checked >= minimum) {
        var existing = group.querySelector(".eg-field__error[data-eg-generated-error]");
        if (existing) existing.remove();
        if (group.getAttribute("data-state") === "invalid") group.removeAttribute("data-state");
        return;
      }
      ensureGroupError(group, "Select at least one permission.", "Chọn ít nhất một quyền.");
      var first = group.querySelector('input[type="checkbox"]');
      if (!invalid) invalid = first || group;
    });
    return invalid;
  }

  function bindFixtureForms() {
    document.querySelectorAll("form[data-eg-fixture-form]").forEach(function (form) {
      var errorState = form.getAttribute("data-eg-form-error") || "validation-error";
      var successState = form.getAttribute("data-eg-form-success") || "saved";
      var requiresChange = form.hasAttribute("data-eg-require-change");
      var changeScope = form.getAttribute("data-eg-change-scope");
      var trackedFields = Array.prototype.slice.call(form.querySelectorAll("input, textarea, select"));
      var changeFields = changeScope
        ? trackedFields.filter(function (field) {
            return field.matches(changeScope) || !!field.closest(changeScope);
          })
        : trackedFields;
      function fieldValue(field) {
        if (field.type === "checkbox" || field.type === "radio") return field.checked;
        if (field.tagName === "SELECT") return field.selectedIndex;
        return field.value;
      }
      trackedFields.forEach(function (field) {
        field._egInitialValue = fieldValue(field);
      });
      function hasDraftChange() {
        return changeFields.some(function (field) {
          if (field.matches(":disabled")) return false;
          return fieldValue(field) !== field._egInitialValue;
        });
      }
      var submitControl = form.querySelector('[type="submit"]');
      function requiredReady() {
        var ready = true;
        form.querySelectorAll("input, textarea, select").forEach(function (field) {
          if (!ready || field.disabled || field.type === "hidden" || field.closest("[hidden]")) return;
          var required = field.hasAttribute("required") || field.getAttribute("aria-required") === "true";
          if (!required) return;
          if (field.type === "checkbox" || field.type === "radio") {
            if (!field.checked) ready = false;
            return;
          }
          if (!(field.value || "").trim()) ready = false;
        });
        return ready;
      }
      function syncSubmitGate() {
        if (!requiresChange || !submitControl) return;
        submitControl.disabled = !hasDraftChange() || !requiredReady();
      }
      if (requiresChange) syncSubmitGate();
      function markDirty(event) {
        var field = event.target;
        if (!field || !field.matches || !field.matches("input, textarea, select")) return;
        if (field.form !== form) return;
        field.setAttribute("data-eg-dirty", "true");
        clearFieldError(field);
        form.setAttribute("data-eg-dirty", "true");
        setFormStatus(form, "dirty");
        syncDraftComparisons();
        syncSubmitGate();
      }
      form.addEventListener("input", markDirty);
      form.addEventListener("change", markDirty);
      form.addEventListener("submit", function (event) {
        event.preventDefault();
        if (form._egSubmitting) return;
        if (form.closest("dialog")) {
          if (isMobileViewport()) {
            setFormStatus(form, "error");
            announce(loc("Editing requires a desktop. Your draft is retained.", "Chỉnh sửa cần máy tính. Bản nháp của bạn được giữ lại."));
            return;
          }
          if (pagePhaseBlocked()) {
            setFormStatus(form, "error");
            announce(loc("This action is unavailable before G4.", "Thao tác này không khả dụng trước G4."));
            return;
          }
          if (mutationStateBlocked()) {
            setFormStatus(form, "error");
            announce(loc("This action is unavailable in the current state.", "Thao tác này không khả dụng ở trạng thái hiện tại."));
            return;
          }
        }
        if (requiresChange && !hasDraftChange()) {
          setFormStatus(form, "error");
          announce(loc("Make a draft change before saving", "Hãy thay đổi bản nháp trước khi lưu"));
          return;
        }
        var invalid = validateFixtureForm(form);
        if (invalid) {
          setFormStatus(form, "error");
          invalid.focus();
          announce(loc("Fix the required fields before saving", "Sửa các trường bắt buộc trước khi lưu"));
          return;
        }
        form._egSubmitting = true;
        setFormStatus(form, "saving");
        setScenario("submitting", false);
        var submit = form.querySelector('[type="submit"]');
        if (submit) {
          submit.disabled = true;
          submit.setAttribute("aria-busy", "true");
        }
        form._egSaveTimer = window.setTimeout(function () {
          form._egSaveTimer = null;
          if (submit) {
            submit.disabled = false;
            submit.removeAttribute("aria-busy");
          }
          form._egSubmitting = false;
          if (state.scenario !== "submitting") {
            setFormStatus(form, "dirty");
            return;
          }
          form.removeAttribute("data-eg-dirty");
          trackedFields.forEach(function (field) {
            field._egInitialValue = fieldValue(field);
          });
          syncSubmitGate();
          setFormStatus(form, "saved");
          if (form.hasAttribute("data-eg-form-close")) {
            var dialog = form.closest("dialog");
            if (dialog && typeof dialog.close === "function") dialog.close();
          }
          setScenario(successState, true);
        }, 700);
      });
    });
  }

  function bindDelegatedActions() {
    document.addEventListener("click", function (event) {
      var copyButton = event.target.closest("[data-eg-copy]");
      if (copyButton) {
        var value = copyButton.getAttribute("data-eg-copy") || "";
        var isRedacted = copyButton.hasAttribute("data-eg-redacted");
        if (navigator.clipboard && navigator.clipboard.writeText) {
          navigator.clipboard.writeText(value).then(function () {
            showToast(isRedacted ? loc("Redacted reference copied", "Đã sao chép tham chiếu đã ẩn") : loc("Copied to clipboard", "Đã sao chép"), "success");
          }).catch(function () {
            showToast(loc("Copy is unavailable in this preview", "Không thể sao chép trong bản xem trước này"), "error");
          });
        } else {
          showToast(loc("Copy is unavailable in this preview", "Không thể sao chép trong bản xem trước này"), "error");
        }
      }
      var toastClose = event.target.closest("[data-eg-toast-close]");
      if (toastClose) {
        var toast = toastClose.closest("[data-eg-toast], .eg-toast");
        if (toast) toast.hidden = true;
      }
      var planned = event.target.closest("a[data-planned]");
      if (planned) {
        event.preventDefault();
        announce(loc("Planned route · not generated yet", "Tuyến dự kiến · chưa tạo"));
        return;
      }
      var setState = event.target.closest("[data-eg-set-state]");
      if (setState) {
        setScenario(setState.getAttribute("data-eg-set-state"), true);
        return;
      }
      var tabTarget = event.target.closest("[data-eg-tab-target]");
      if (tabTarget) {
        var targetTab = document.getElementById(tabTarget.getAttribute("data-eg-tab-target"));
        if (targetTab) {
          targetTab.click();
          targetTab.focus();
        }
        return;
      }
      var delayed = event.target.closest("[data-eg-delay-state]");
      if (delayed) {
        if (delayed.disabled) return;
        var target = delayed.getAttribute("data-eg-delay-state");
        var ms = Number(delayed.getAttribute("data-eg-delay-ms") || 1200);
        delayed.disabled = true;
        delayed.setAttribute("aria-busy", "true");
        setScenario("loading");
        window.setTimeout(function () {
          delayed.disabled = false;
          delayed.removeAttribute("aria-busy");
          setScenario(target, true);
        }, ms);
      }
    });
  }

  function bindLocalizedValues() {
    document.querySelectorAll("[data-value-en][data-value-vi]").forEach(function (element) {
      element.addEventListener("input", function () {
        element.setAttribute("data-eg-dirty", "true");
      });
    });
  }

  function counterMax(counter, target) {
    var max = target.getAttribute("maxlength") || counter.getAttribute("data-eg-max");
    return max ? Number(max) : 0;
  }

  function updateCounterState(counter, target) {
    var max = counterMax(counter, target);
    var over = max > 0 && (target.value || "").length > max;
    counter.setAttribute("data-state", over ? "over" : "");
    if (!over) counter.removeAttribute("data-state");
    var error = counter._error;
    if (error) {
      error.hidden = !over;
      error.textContent = over ? (state.locale === "vi" ? (counter.getAttribute("data-eg-error-vi") || target.getAttribute("data-eg-error-vi") || "") : (counter.getAttribute("data-eg-error-en") || target.getAttribute("data-eg-error-en") || "")) : "";
    }
    target.setAttribute("aria-invalid", over ? "true" : "false");
    return over;
  }

  function bindTemplateApply() {
    document.querySelectorAll("[data-eg-template-apply]").forEach(function (button) {
      button.addEventListener("click", function () {
        var scope = document.querySelector(button.getAttribute("data-eg-template-scope") || "form");
        if (!scope) return;
        scope.querySelectorAll("[data-eg-template-en][data-eg-template-vi]").forEach(function (field) {
          field.value = state.locale === "vi" ? field.getAttribute("data-eg-template-vi") : field.getAttribute("data-eg-template-en");
          field.setAttribute("data-eg-dirty", "true");
          field.dispatchEvent(new Event("input", { bubbles: true }));
        });
        announce(loc("Template applied to the draft", "Đã áp mẫu vào bản nháp"));
      });
    });
  }

  function bindBoundaryFill() {
    document.querySelectorAll("[data-eg-fill-target]").forEach(function (button) {
      button.addEventListener("click", function () {
        var target = document.getElementById(button.getAttribute("data-eg-fill-target"));
        if (!target) return;
        var max = Number(target.getAttribute("maxlength") || 0);
        if (!max) return;
        var delta = Number(button.getAttribute("data-eg-fill-delta") || 0);
        var size = Math.max(0, max + delta);
        target.value = "x".repeat(size);
        target.setAttribute("data-eg-dirty", "true");
        target.dispatchEvent(new Event("input", { bubbles: true }));
        target.focus();
      });
    });
  }

  function bindBaseReload() {
    document.querySelectorAll("[data-eg-reload-base]").forEach(function (button) {
      button.addEventListener("click", function () {
        var scope = document.querySelector(button.getAttribute("data-eg-reload-scope") || "form");
        if (!scope) return;
        var form = scope.matches("form") ? scope : scope.querySelector("form[data-eg-fixture-form]");
        if (form) {
          if (form._egSaveTimer) {
            window.clearTimeout(form._egSaveTimer);
            form._egSaveTimer = null;
          }
          form._egSubmitting = false;
          var submit = form.querySelector('[type="submit"]');
          if (submit) {
            submit.disabled = false;
            submit.removeAttribute("aria-busy");
          }
        }
        scope.querySelectorAll("[data-eg-current-en][data-eg-current-vi], [data-eg-base-en][data-eg-base-vi]").forEach(function (field) {
          var en = field.getAttribute("data-eg-current-en") || field.getAttribute("data-eg-base-en");
          var vi = field.getAttribute("data-eg-current-vi") || field.getAttribute("data-eg-base-vi");
          field.value = state.locale === "vi" ? vi : en;
          field.setAttribute("data-eg-dirty", "true");
          field.dispatchEvent(new Event("input", { bubbles: true }));
        });
        setScenario("default", true);
        announce(loc("Current v6 values reloaded; the retained draft was replaced by an explicit reload", "Đã tải lại giá trị v6 hiện tại; bản nháp được giữ đã bị thay bằng thao tác tải lại rõ ràng"));
      });
    });
  }

  function syncSkeletonMatches() {
    document.querySelectorAll("[data-eg-skeleton-match]").forEach(function (host) {
      var source = document.querySelector(host.getAttribute("data-eg-skeleton-match"));
      if (!source) return;
      var wasHidden = source.hidden;
      var ariaHiddenBefore = source.getAttribute("aria-hidden");
      var inertBefore = source.getAttribute("inert");
      if (wasHidden) {
        source.hidden = false;
        source.setAttribute("aria-hidden", "true");
        source.setAttribute("inert", "");
        source.classList.add("eg-skeleton-source");
      }
      var srcCards = source.querySelectorAll(".eg-card");
      var dstCards = host.querySelectorAll(".eg-card");
      dstCards.forEach(function (card) { card.style.removeProperty("--eg-skel-h"); });
      Array.prototype.forEach.call(dstCards, function (card, index) {
        var ref = srcCards[index];
        if (!ref) return;
        var height = Math.round(ref.getBoundingClientRect().height);
        if (height > 0) card.style.setProperty("--eg-skel-h", height + "px");
      });
      var total = Math.round(source.getBoundingClientRect().height);
      if (total > 0) host.style.setProperty("--eg-skel-total", total + "px");
      if (wasHidden) {
        source.hidden = true;
        if (ariaHiddenBefore === null) source.removeAttribute("aria-hidden");
        else source.setAttribute("aria-hidden", ariaHiddenBefore);
        if (inertBefore === null) source.removeAttribute("inert");
        else source.setAttribute("inert", inertBefore);
        source.classList.remove("eg-skeleton-source");
      }
    });
  }

  function bindRetentionPreview() {
    var select = document.querySelector("[data-eg-retention-select]");
    var target = document.querySelector("[data-eg-retention-target]");
    if (!select || !target) return;
    var dialog = target.closest("dialog");
    var ack = dialog ? dialog.querySelector("[data-eg-retention-ack]") : null;
    var confirm = dialog ? dialog.querySelector("[data-eg-retention-confirm]") : null;
    var metrics = dialog ? dialog.querySelector("[data-eg-impact-metrics]") : null;
    var unavailable = dialog ? dialog.querySelector("[data-eg-impact-unavailable]") : null;
    var title = unavailable ? unavailable.querySelector("[data-eg-impact-title]") : null;
    var description = unavailable ? unavailable.querySelector("[data-eg-impact-description]") : null;
    var metricValues = metrics ? Array.prototype.slice.call(metrics.querySelectorAll(".eg-kpi__value")) : [];
    metricValues.forEach(function (node, index) {
      node.setAttribute("data-eg-metric-value", node.textContent);
      node.setAttribute("data-eg-metric-index", String(index));
    });
    function fixture() {
      return dialog ? dialog.querySelector("[data-eg-retention-fixture]:checked") : null;
    }
    function update() {
      var current = target.getAttribute("data-eg-retention-current");
      var option = select.options[select.selectedIndex] || null;
      var fixtureState = fixture() ? fixture().value : "";
      var stale = fixtureState === "stale" || target.getAttribute("data-eg-retention-stale") === "true" || (option && option.getAttribute("data-eg-preview") === "stale");
      var notAvailable = fixtureState === "unavailable" || target.getAttribute("data-eg-retention-unavailable") === "true" || (option && option.getAttribute("data-eg-preview") === "unavailable");
      var same = !!current && select.value === current;
      var mode = notAvailable ? "unavailable" : stale ? "stale" : same ? "same" : "fresh";
      var blocked = mode !== "fresh";
      var suffix = state.locale === "vi" ? "-vi" : "-en";
      var template = mode === "fresh" ? target.getAttribute("data" + suffix + "-template") : target.getAttribute("data" + suffix + "-template-" + mode);
      target.textContent = (template || "").replace("{days}", select.value);
      var noChange = mode === "same";
      if (metrics) metrics.hidden = mode === "unavailable" || mode === "stale";
      metricValues.forEach(function (node) {
        node.textContent = noChange ? "0" : node.getAttribute("data-eg-metric-value");
      });
      if (unavailable) unavailable.hidden = !blocked || noChange;
      if (blocked && !noChange && unavailable) {
        var titleEn = mode === "unavailable" ? "No deletion preview available" : "Deletion preview is stale";
        var titleVi = mode === "unavailable" ? "Không có bản xem trước xoá" : "Bản xem trước đã cũ";
        var descriptionEn = mode === "unavailable"
          ? "The server returned no preview for this fixture. Nothing is deleted and no count is invented; confirmation stays blocked."
          : "The preview is based on an older retention read. Refresh before confirming; nothing is deleted and no count is invented.";
        var descriptionVi = mode === "unavailable"
          ? "Máy chủ không trả về bản xem trước cho mẫu này. Không xoá gì và không tự đặt số lượng; xác nhận vẫn bị chặn."
          : "Bản xem trước dựa trên lần đọc thời hạn cũ hơn. Phải làm mới trước khi xác nhận; không xoá gì và không tự đặt số lượng.";
        if (title) {
          title.textContent = state.locale === "vi" ? titleVi : titleEn;
          title.setAttribute("data-en", titleEn);
          title.setAttribute("data-vi", titleVi);
        }
        if (description) {
          description.textContent = state.locale === "vi" ? descriptionVi : descriptionEn;
          description.setAttribute("data-en", descriptionEn);
          description.setAttribute("data-vi", descriptionVi);
        }
      }
      if (dialog) dialog.setAttribute("data-eg-retention-state", mode);
      if (confirm) {
        var allowed = !blocked && !!ack && ack.checked;
        confirm.disabled = !allowed;
        confirm.setAttribute("aria-disabled", allowed ? "false" : "true");
      }
    }
    if (ack) ack.addEventListener("change", update);
    if (dialog) {
      dialog.querySelectorAll("[data-eg-retention-fixture]").forEach(function (input) {
        input.addEventListener("change", function () {
          if (ack) ack.checked = false;
          update();
        });
      });
    }
    select.addEventListener("change", function () {
      if (ack) ack.checked = false;
      update();
      select.focus();
    });
    document.addEventListener("eg:locale", update);
    update();
  }

  function bindReadonlySummaries() {
    document.querySelectorAll("[data-eg-summary-scope]").forEach(function (host) {
      var scope = document.querySelector(host.getAttribute("data-eg-summary-scope"));
      if (!scope) return;
      function update() {
        host.querySelectorAll("[data-eg-summary-value]").forEach(function (slot) {
          var field = document.getElementById(slot.getAttribute("data-eg-summary-value"));
          if (field) slot.textContent = fixtureFieldValue(field);
        });
      }
      scope.addEventListener("input", update);
      scope.addEventListener("change", update);
      update();
    });
  }

  function bindLocalPanels() {
    document.querySelectorAll("input[data-eg-local-panel-toggle]").forEach(function (input) {
      input.addEventListener("change", function () {
        var name = input.name;
        var value = input.value;
        document.querySelectorAll("[data-eg-local-panel]").forEach(function (panel) {
          if (panel.getAttribute("data-eg-local-panel-name") !== name) return;
          panel.hidden = panel.getAttribute("data-eg-local-panel") !== value;
        });
      });
    });
  }

  function bindViewSelects() {
    document.querySelectorAll("[data-eg-view-select]").forEach(function (select) {
      var scopeSelector = select.getAttribute("data-eg-view-scope");
      var scope = scopeSelector ? document.querySelector(scopeSelector) : document;
      if (!scope) scope = document;
      function panels() {
        return Array.prototype.filter.call(scope.querySelectorAll("[data-eg-view-panel]"), function (panel) {
          return panel.parentElement === scope;
        });
      }
      function update() {
        var value = select.value;
        var matched = false;
        var ownPanels = panels();
        ownPanels.forEach(function (panel) {
          var active = panel.getAttribute("data-eg-view-panel") === value;
          if (active) matched = true;
          panel.hidden = !active;
        });
        if (!matched && ownPanels.length) ownPanels[0].hidden = false;
      }
      select.addEventListener("change", update);
      update();
    });
  }

  function bindStepViewers() {
    document.querySelectorAll("[data-eg-step-viewer]").forEach(function (viewer) {
      var buttons = viewer.querySelectorAll("[data-eg-step-target]");
      var panels = viewer.querySelectorAll("[data-eg-step-panel]");
      if (!buttons.length || !panels.length) return;
      function select(button) {
        var target = button.getAttribute("data-eg-step-target");
        buttons.forEach(function (item) {
          var active = item === button;
          item.setAttribute("aria-pressed", active ? "true" : "false");
          if (item.hasAttribute("aria-controls")) {
            var controlled = document.getElementById(item.getAttribute("aria-controls"));
            if (controlled) controlled.hidden = !active;
          }
        });
        panels.forEach(function (panel) {
          var active = panel.getAttribute("data-eg-step-panel") === target;
          panel.hidden = !active;
        });
      }
      buttons.forEach(function (button) {
        button.addEventListener("click", function () { select(button); });
      });
    });
    document.querySelectorAll("[data-eg-zoom-stage]").forEach(function (stage) {
      var scope = stage.closest("[data-eg-step-viewer]") || document;
      var level = scope.querySelector("[data-eg-zoom-level]");
      var min = Number(stage.getAttribute("data-eg-zoom-min") || 75);
      var max = Number(stage.getAttribute("data-eg-zoom-max") || 150);
      var current = 100;
      function apply(value) {
        current = Math.min(max, Math.max(min, value));
        stage.style.setProperty("--eg-zoom", String(current / 100));
        if (level) level.textContent = current + "%";
      }
      scope.querySelectorAll("[data-eg-zoom]").forEach(function (button) {
        button.addEventListener("click", function () {
          var raw = button.getAttribute("data-eg-zoom");
          apply(raw === "reset" ? 100 : current + Number(raw || 0));
        });
      });
      apply(100);
    });
  }

  function scopeSlotValue(radio, formatLabel, key) {
    if (!radio) return "";
    if (key === "label") return state.locale === "vi" ? (radio.getAttribute("data-eg-scope-vi") || "") : (radio.getAttribute("data-eg-scope-en") || "");
    if (key === "count") return radio.getAttribute("data-eg-scope-count") || "";
    if (key === "ids") return state.locale === "vi"
      ? (radio.getAttribute("data-eg-scope-ids-vi") || radio.getAttribute("data-eg-scope-ids") || "")
      : (radio.getAttribute("data-eg-scope-ids-en") || radio.getAttribute("data-eg-scope-ids") || "");
    if (key === "version") return radio.getAttribute("data-eg-scope-version") || "av-12";
    if (key === "prefix") return radio.getAttribute("data-eg-scope-prefix") || "SHOP";
    if (key === "format") return formatLabel;
    return "";
  }

  function bindScopeSummary() {
    document.querySelectorAll("[data-eg-scope-summary]").forEach(function (host) {
      var source = document.querySelector(host.getAttribute("data-eg-scope-source"));
      if (!source) return;
      function update() {
        var radio = source.querySelector('input[type="radio"][name="export-scope"]:checked');
        var format = source.querySelector("[data-eg-format-select]");
        var formatLabel = format && format.options[format.selectedIndex] ? format.options[format.selectedIndex].text : "";
        host.querySelectorAll("[data-eg-scope-slot]").forEach(function (slot) {
          slot.textContent = scopeSlotValue(radio, formatLabel, slot.getAttribute("data-eg-scope-slot"));
        });
        var unavailable = radio && radio.getAttribute("data-eg-scope-unavailable") === "true";
        var notice = host.querySelector("[data-eg-scope-unavailable]");
        if (notice) notice.hidden = !unavailable;
        var ready = host.querySelector("[data-eg-scope-ready]");
        if (ready) ready.hidden = unavailable || !radio;
        document.querySelectorAll("[data-eg-scope-download]").forEach(function (button) {
          if (!button.hasAttribute("disabled")) button.setAttribute("aria-disabled", unavailable || !radio ? "true" : "false");
        });
      }
      source.addEventListener("change", update);
      source.addEventListener("input", update);
      document.addEventListener("eg:locale", update);
      update();
    });
    document.addEventListener("click", function (event) {
      var button = event.target.closest("[data-eg-scope-download]");
      if (!button || button.getAttribute("aria-disabled") !== "true") return;
      event.preventDefault();
      event.stopImmediatePropagation();
      announce(loc("Download is unavailable for this supplied scope", "Tải xuống không khả dụng cho phạm vi được cung cấp này"));
    }, true);
  }

  function bindPermissionGroups() {
    document.querySelectorAll("[data-eg-min-checked]").forEach(function (group) {
      var form = group.closest("form");
      var preview = form ? form.querySelector("[data-eg-permission-preview]") : null;
      if (!preview) return;
      var prefix = preview.getAttribute("data-eg-preview-prefix") || "";
      function update() {
        var list = Array.prototype.map.call(group.querySelectorAll('input[type="checkbox"]:checked'), function (box) {
          return box.value;
        });
        var text = list.length ? list.join(", ") : loc("no permissions selected", "chưa chọn quyền");
        preview.textContent = prefix ? prefix + " · " + text : text;
        if (list.length) preview.removeAttribute("data-state");
        else preview.setAttribute("data-state", "warning");
      }
      group.addEventListener("change", update);
      document.addEventListener("eg:locale", update);
      update();
    });
  }

  function bindMasterDetail() {
    document.querySelectorAll("[data-eg-master-detail]").forEach(function (host) {
      var rows = Array.prototype.slice.call(host.querySelectorAll("[data-eg-detail-row]"));
      var panels = Array.prototype.slice.call(host.querySelectorAll("[data-eg-detail-panel]"));
      var empty = host.querySelector("[data-eg-detail-empty]");
      if (!rows.length || !panels.length) return;
      function rowId(row) {
        return row.getAttribute("data-eg-detail-row");
      }
      function isVisible(row) {
        if (row.hidden) return false;
        for (var node = row; node; node = node.parentElement) {
          if (node.hidden) return false;
        }
        return true;
      }
      function visibleRows() {
        return rows.filter(isVisible);
      }
      function selectedRow() {
        return rows.filter(function (row) {
          return row.getAttribute("aria-selected") === "true";
        })[0] || null;
      }
      function select(id, options) {
        options = options || {};
        var current = null;
        rows.forEach(function (row) {
          var selected = id !== null && rowId(row) === id;
          row.setAttribute("aria-selected", selected ? "true" : "false");
          row.tabIndex = -1;
          if (selected) current = row;
        });
        panels.forEach(function (panel) {
          var active = id !== null && panel.getAttribute("data-eg-detail-panel") === id;
          panel.hidden = !active;
          if (active) panel.removeAttribute("aria-hidden");
          else panel.setAttribute("aria-hidden", "true");
        });
        if (empty) empty.hidden = visibleRows().length !== 0;
        var focusable = current && isVisible(current) ? current : visibleRows()[0];
        if (focusable) focusable.tabIndex = 0;
        if (options.focusRow && current) {
          current.focus();
        } else if (options.focusPanel) {
          var activePanel = panels.filter(function (panel) {
            return panel.getAttribute("data-eg-detail-panel") === id;
          })[0];
          var target = activePanel ? activePanel.querySelector("h2, h3, a, button") : null;
          if (target) target.focus();
        }
      }
      function reconcile() {
        var current = selectedRow();
        if (current && isVisible(current)) {
          select(rowId(current), {});
          return;
        }
        var first = visibleRows()[0];
        if (first) select(rowId(first), {});
        else select(null, {});
      }
      rows.forEach(function (row) {
        if (!row.hasAttribute("role")) row.setAttribute("role", "row");
        row.tabIndex = row.getAttribute("aria-selected") === "true" ? 0 : -1;
        row.addEventListener("click", function (event) {
          if (event.target.closest("a, button, input, select, textarea")) return;
          select(rowId(row), {});
        });
        row.addEventListener("keydown", function (event) {
          if (event.target.closest("a, button, input, select, textarea")) return;
          if (event.key === "Enter" || event.key === " ") {
            event.preventDefault();
            select(rowId(row), { focusPanel: true });
            return;
          }
          if (event.key !== "ArrowDown" && event.key !== "ArrowUp" && event.key !== "Home" && event.key !== "End") return;
          var list = visibleRows();
          if (!list.length) return;
          event.preventDefault();
          var index = list.indexOf(row);
          var next = list[0];
          if (event.key === "ArrowDown") next = list[Math.min(index + 1, list.length - 1)];
          else if (event.key === "ArrowUp") next = list[Math.max(index - 1, 0)];
          else if (event.key === "End") next = list[list.length - 1];
          if (next) select(rowId(next), { focusRow: true });
        });
      });
      var initial = selectedRow();
      if (!initial || !isVisible(initial)) initial = visibleRows()[0] || null;
      if (initial) select(rowId(initial), {});
      else select(null, {});
      var table = host.querySelector("table.eg-table") || host.querySelector("table");
      if (table) table._egSyncDetail = reconcile;
      var listHost = rows[0].parentElement;
      if (listHost) listHost._egSyncDetail = reconcile;
      host._egReconcile = reconcile;
    });
  }

  function bindDriftScope() {
    document.querySelectorAll("[data-eg-drift-scope]").forEach(function (host) {
      var labelControl = document.getElementById(host.getAttribute("data-eg-drift-scope"));
      var driftControl = host.querySelector("[data-eg-drift-filter]");
      if (!labelControl || !driftControl) return;
      var previousLabel = null;
      driftControl.addEventListener("change", function () {
        if (driftControl.value) {
          if (labelControl.value !== "test_drift") {
            previousLabel = labelControl.value;
            labelControl.value = "test_drift";
          }
        } else if (previousLabel !== null && labelControl.value === "test_drift") {
          labelControl.value = previousLabel;
          previousLabel = null;
        }
        labelControl.dispatchEvent(new Event("change", { bubbles: true }));
      });
    });
  }

  function bindScreenshotDemo() {
    document.querySelectorAll("[data-eg-screenshot-demo]").forEach(function (host) {
      var control = host.querySelector("[data-eg-screenshot-toggle]");
      if (!control) return;
      var panels = Array.prototype.slice.call(host.querySelectorAll("[data-eg-screenshot-state]"));
      function apply() {
        panels.forEach(function (panel) {
          panel.hidden = panel.getAttribute("data-eg-screenshot-state") !== control.value;
        });
      }
      control.addEventListener("change", apply);
      apply();
    });
  }

  function applyEntryState() {
    var entry = null;
    try {
      entry = new URLSearchParams(window.location.search).get("state");
    } catch (error) {
      entry = null;
    }
    if (!entry) return;
    if (reviewStates().indexOf(entry) < 0) return;
    setScenario(entry, false);
    if (entry === "empty" || entry === "new-case") {
      var title = document.querySelector(".eg-page-header__title");
      var crumb = document.querySelector('.eg-breadcrumbs__item[aria-current="page"]');
      var headerDescription = document.querySelector(".eg-page-header__description");
      var headerMeta = document.querySelector(".eg-page-header__meta");
      var originalHeader = {
        title: title ? title.innerHTML : null,
        crumb: crumb ? crumb.innerHTML : null,
        description: headerDescription ? headerDescription.innerHTML : null,
        meta: headerMeta ? headerMeta.innerHTML : null
      };
      function restoreHeader() {
        if (title && originalHeader.title !== null) title.innerHTML = originalHeader.title;
        if (crumb && originalHeader.crumb !== null) crumb.innerHTML = originalHeader.crumb;
        if (headerDescription && originalHeader.description !== null) headerDescription.innerHTML = originalHeader.description;
        if (headerMeta && originalHeader.meta !== null) headerMeta.innerHTML = originalHeader.meta;
      }
      var stateSelect = document.querySelector("[data-eg-review-state]");
      if (stateSelect && !stateSelect._egEntryRestore) {
        stateSelect._egEntryRestore = true;
        stateSelect.addEventListener("change", function () {
          if (stateSelect.value !== "empty" && stateSelect.value !== "new-case") restoreHeader();
        });
      }
      if (title) {
        title.textContent = loc("New manual case", "Ca thủ công mới");
        title.setAttribute("data-en", "New manual case");
        title.setAttribute("data-vi", "Ca thủ công mới");
      }
      if (crumb) {
        crumb.textContent = loc("New manual case", "Ca thủ công mới");
        crumb.setAttribute("data-en", "New manual case");
        crumb.setAttribute("data-vi", "Ca thủ công mới");
      }
      var description = document.querySelector(".eg-page-header__description");
      if (description) {
        description.textContent = loc("This draft has no identity or version until the first save. Title, preconditions and at least one paired step are required.", "Bản nháp này chưa có mã hay phiên bản cho đến lần lưu đầu tiên. Cần có tiêu đề, điều kiện tiên quyết và ít nhất một cặp bước.");
        description.setAttribute("data-en", "This draft has no identity or version until the first save. Title, preconditions and at least one paired step are required.");
        description.setAttribute("data-vi", "Bản nháp này chưa có mã hay phiên bản cho đến lần lưu đầu tiên. Cần có tiêu đề, điều kiện tiên quyết và ít nhất một cặp bước.");
      }
      var meta = document.querySelector(".eg-page-header__meta");
      if (meta) {
        meta.textContent = loc("SHOP-CL-0017 · Priority not set · Unassigned version · Source manual · Linh Nguyễn · ais-3", "SHOP-CL-0017 · Chưa đặt ưu tiên · Phiên bản chưa gán · Nguồn thủ công · Linh Nguyễn · ais-3");
        meta.setAttribute("data-en", "SHOP-CL-0017 · Priority not set · Unassigned version · Source manual · Linh Nguyễn · ais-3");
        meta.setAttribute("data-vi", "SHOP-CL-0017 · Chưa đặt ưu tiên · Phiên bản chưa gán · Nguồn thủ công · Linh Nguyễn · ais-3");
      }
    }
  }

  function init() {
    injectSprite();
    buildReviewControls();
    applyTheme();
    applyLocale();
    applyPhase();
    setScenario("default", false);
    bindNavToggle();
    bindNavSearch();
    bindLocaleThemePhase();
    bindMenus();
    bindDialogs();
    bindTabs();
    bindTables();
    bindCounters();
    bindLocalizedValues();
    bindFixtureForms();
    bindTemplateApply();
    bindBoundaryFill();
    bindBaseReload();
    bindRetentionPreview();
    bindFilters();
    bindDriftScope();
    bindMasterDetail();
    bindScreenshotDemo();
    bindDisclosures();
    bindReadonlySummaries();
    bindLocalPanels();
    bindViewSelects();
    bindStepViewers();
    bindScopeSummary();
    bindPermissionGroups();
    bindDelegatedActions();
    document.addEventListener("input", syncDraftComparisons);
    document.addEventListener("change", syncDraftComparisons);
    applyEntryState();
    setNavMode(state.navMode, false);
    window.addEventListener("resize", function () {
      syncSkeletonMatches();
      var natural = window.innerWidth >= 1280 ? "expanded" : window.innerWidth >= 768 ? "rail" : "drawer";
      if (!state.navUserSet) setNavMode(natural, false);
      if (window.innerWidth >= 768 && root.getAttribute("data-nav-open") === "true") closeDrawer();
      var toggle = document.querySelector("[data-eg-nav-toggle]");
      if (toggle && window.innerWidth >= 768) toggle.setAttribute("aria-expanded", state.navMode === "expanded" ? "true" : "false");
      setNavInert();
    });
    var live = document.createElement("div");
    live.setAttribute("data-eg-live", "");
    live.setAttribute("role", "status");
    live.setAttribute("aria-live", "polite");
    live.setAttribute("aria-atomic", "true");
    live.className = "eg-visually-hidden";
    document.body.appendChild(live);
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();
})();
