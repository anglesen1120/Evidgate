Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict. Follow its Deliverable and links section and use relevant Canonical fixtures verbatim.

**Page:** `27-settings-shell/evidgate-settings.html`. Edit in place.
**Phase:** G1. **Nav item:** `settings` (set `data-nav` on `<html>`).
**Plan ids:** 13.4.1-T5, 13.4.1-T7, 13.2.1-T1, 09.7.1-T1, 11.4.1-T3.

## Goal

Make project AI settings discoverable while clearly separating project configuration from tenant-wide AI consent.

## Change

- Add a G1 AI settings destination in `.settings-rail`/`#settingsNav` and `#mobileDestination`, under Project scope, linking to `../44-ai-settings/evidgate-ai-settings.html`; match shared nav id `ai-settings` without duplicating the sidebar.
- In `#tabContent`, provide a static AI-settings entry specimen: selected project, current settings version and one primary “Open AI settings” link. Do not embed a second full settings editor or invent a permission name.
- Add a clearly scoped tenant AI opt-in status next to the entry: on/off/unavailable and supplied policy source. Off explains that AI generation is blocked but manual authoring remains available; provide an admin settings link only when its destination and permission are supplied, otherwise explain who to contact without a dead/fabricated URL.
- Apply phase filtering before permissions to both settings destination lists: General/Members/Audit/AI settings and AI usage at G1; Secret references/Security events/Support and existing quota-retention controls at G2; Policies/Agents/Integrations/API tokens/Deploy targets at G4. Keep page 31's G1 credit entry available.
- Preserve first-accessible-destination selection after phase/scope changes; a hidden tab must not leave its content visible or a stale selection. Direct access never bypasses scope or permissions.
- Extend `design-review-controls` with opt-in on/off/unavailable specimens and the shared phase selector; do not make the design-review phase select a production setting or consent toggle.

## States (in addition to the shared switcher)

- Project settings readable but not editable; AI opt-in off; status unavailable; no project selected; no accessible tabs after filtering; phase lowered while a G4 tab is selected. Existing 412 stays a linked edit specimen, not a navigation mutation.

## Fixture (illustrative, labelled as such)

- Hà Lê / Lotus Commerce / storefront-web; project settings `ais-3`; tenant opt-in off and a separate on fixture. Role-to-authoring-permission mapping and the exact opt-in administration destination are not invented.

## Copy EN / VI

- “AI settings / Cài đặt AI”; “Open AI settings / Mở cài đặt AI”; “Project settings version / Phiên bản cài đặt dự án”; “Organization AI opt-in / Chấp thuận sử dụng AI của tổ chức”.
- “AI generation is off; manual authoring is available / Chưa bật tạo bằng AI; vẫn có thể soạn kiểm thử thủ công”; “Contact an administrator / Liên hệ quản trị viên”; “Status unavailable / Không có dữ liệu trạng thái”.

## Keep unchanged

- Organization/Project scope toggle, scope-context, `settings-title`, support/quota notice safety, member-list specimen, theme/locale, existing permission filtering and safe unavailable/403/404 patterns. Do not redesign unrelated destination pages.

## Done when

- New entry and states are baked into static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- AI settings is project-scoped, opt-in is tenant-scoped, and no G4 destination/content remains visible at G1–G3; no fabricated consent-management action is offered.
