Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict. Follow its Deliverable and links section and use relevant Canonical fixtures verbatim.

**Page:** `24-project-connection/evidgate-project-overview.html`. Edit in place.
**Phase:** G1 (CI regions from G2). **Nav item:** `overview` (set `data-nav` on `<html>`).
**Plan ids:** 13.4.1-T2, 13.4.1-T4, 13.4.1-T5, 13.4.1-T7, 09.2.1-T6, 09.2.1-T7.

## Goal

Turn the project overview into an authoring home: see coverage, recent versions and pending changes, then continue the next authoring task.

## Change

- Rewrite `page-heading`/`.title-block` and the `topbar` breadcrumb as the project authoring home; keep `project-select` and its scope behaviour. Use one primary “Create requirement” link to `../43-generation/evidgate-generation.html`.
- Replace `project-summary` connection/catalog/policy summary with labelled authoring counts and coverage (linked automated identities versus uncovered manual cases), never a release verdict or inferred quality score.
- Rework `.section.split` around a recent immutable versions list (author, source manual/ai/import, reason, UTC time; links to `../41-test-case-editor/evidgate-test-case-editor.html` Timeline) and a pending-changes summary linking to `../42-pending-changes/evidgate-pending-changes.html`.
- Add an AI credit balance summary linked to `../31-quota-retention/evidgate-usage-retention.html`; default balance 1 240 of 2 000 credits for period 2026-10, explicitly tenant-scoped. Distinguish unavailable from zero in a separate state. Add secondary entry to `../40-authoring-tree/evidgate-authoring-tree.html`.
- Keep `connection-health`, `latest-run` and `quickstart` as secondary G2+ regions, hidden entirely in G1. Remove `latest-run` release-decision row for all G1–G3 and remove the `project-summary` policy link until G4; collection and outcome stay separate when runs are visible.
- Change `onboarding-progress` and its `.steps` to project → AI settings → first requirement → first checklist; link Resume to `../23-onboarding-wizard/evidgate-onboarding.html`. Replace the obsolete local `onboarding-dialog` CI wizard rather than leaving a second conflicting G1 path.
- Rewrite `.no-projects` copy for authoring and provide an empty-project path to first requirement. Do not require a repository/CI connection to begin G1 manual work; no fake generated checklist if AI is off.

## States (in addition to the shared switcher)

- No projects; project without authored items; no pending changes; unavailable credit summary; AI opt-in off with admin settings link; G2 no CI binding. Existing 412 remains a linked edit demonstration, not a home read response.

## Fixture (illustrative, labelled as such)

- storefront-web / `SHOP` / `ais-3`; “Checkout / Thanh toán”; `SHOP-TC-0042` “Confirm order shows the total / Xác nhận đơn hàng hiển thị tổng tiền”, current v5 and v4 history; source `manual`, Linh Nguyễn; reason “Clarify validation / Làm rõ kiểm tra tính hợp lệ”; pending comment on `SHOP-CL-0017` “Show the order total / Hiển thị tổng đơn hàng”, priority high. Credit balance 1 240 of 2 000, period 2026-10.

## Copy EN / VI

- “Authoring home / Trang chủ soạn kiểm thử”; “Create requirement / Tạo yêu cầu”; “Recent versions / Phiên bản gần đây”; “Pending changes / Thay đổi chờ áp dụng”.
- “Uncovered / Chưa tự động hóa”; “AI credit balance / Số dư tín dụng AI”; “Continue setup / Tiếp tục thiết lập”.

## Keep unchanged

- Project picker, shared sidebar, theme/locale controls, read-only mobile notice, safe 403/404 distinction, and G2 connection metadata/secret-free snippet/copy feedback; no connection or retention redesign.

## Done when

- Changes are baked into static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- G1 contains no CI-health panel, policy link or release verdict; its authoring links and Resume target agree with pages 23 and 40–44.
