Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict. Follow its Deliverable and links section and use relevant Canonical fixtures verbatim.

**Page:** `01-app-shell/evidgate-workspace-shell.html`. Edit in place, plus the shared nav array and renderer in `assets/evidgate.js`.
**Phase:** G1. **Nav item:** `overview` (set `data-nav` on `<html>`).
**Plan ids:** 13.4.1-T7, 09.1.1-T4, 09.7.1-T1.

## Goal

Make the shared shell lead with test authoring and reveal later navigation only at its workspace phase.

## Change

- In `assets/evidgate.js`, extend every item in `groups` with a named `phase` field (G1–G4); adapt `render()` to filter before building groups, omit empty groups and retain the single nav array. Do not hand-write page sidebars or edit `NAV.md` in this run.
- Group order: Test design → Scripts & runs → Learn & repair → Release (G4) → Settings. Put `overview` (24) and `onboarding` (23) inside the first group, not a preceding Overview group.
- First group G1 items: `authoring` → `40-authoring-tree/evidgate-authoring-tree.html`, `generation` → `43-generation/evidgate-generation.html`, `pending-changes` → `42-pending-changes/evidgate-pending-changes.html`. Page 41 editor and page 45 import/export use parent `data-nav="authoring"`; do not add duplicate sidebar entries.
- Settings gains G1 `ai-settings` → `44-ai-settings/evidgate-ai-settings.html`; keep `settings`, `members`, `audit`, `quota` at G1. `secrets`, `security`, `support` start at G2; `tokens`, `integrations`, `agents` start at G4.
- Scripts & runs G2: `scripts` → `46-generated-scripts/evidgate-generated-scripts.html`, existing `catalog`, `mapping`, `runs`. Learn & repair G3: `failures`, `repairs` → `12-healing-queue/evidgate-healing-review.html` (rename old `healing` id), `knowledge`, `availability`; pages 13 and 47 use parent `repairs`.
- Keep every page functional if its current nav item is hidden or removed: never throw, fabricate an active item or reveal a filtered destination; retain the readable body and safe navigation. Page 14's `healing` → `repairs` migration is handled separately. Do not rename any other existing ids beyond the README's required changes.
- Keep existing `studio`, `suites`, `quality`, `releases`, `policies`, `deploy` at G4; keep their existing targets. Pages 06, 17–22, 30, 34, 35, 37, 38 must have no sidebar entry/teaser at G1–G3.
- Add a labelled G1/G2/G3/G4 select, default G1, to `.review-dock[data-od-id="design-review-controls"]`; use the shared renderer for this review-only phase control on pages, preserving theme/locale/state. Later-phase items are absent, not disabled; phase does not grant permissions.
- In `#main-content`, replace the run-heading, `.statusline`, evaluation-fields and run-tests-panel default specimen with a compact authoring workspace specimen and link to page 40; update the topbar breadcrumb. Remove release verdict/waiver specimens from component-state-sheet and scenarioArea at G1–G3; retain only G4 review specimens at G4.
- Page 02 `02-login/evidgate-sign-in.html` (footer `data-t="footerLeft"` and its en and vi dictionary entries): replace only the tagline "Evidence-first release decisions" with "From requirements to tests that repair themselves, under review / Từ yêu cầu đến kiểm thử tự sửa, luôn có người duyệt"; no other change to the sign-in page.

## States (in addition to the shared switcher)

- Each phase, collapsed menu, mobile drawer and permission-restricted nav; phase changes preserve focus on the control and never leave an invisible item focused or active.

## Fixture (illustrative, labelled as such)

- Lotus Commerce / storefront-web; module “Checkout / Thanh toán”; the phase selector is a local review fixture, not a production workspace upgrade control.

## Copy EN / VI

- “Test design / Thiết kế kiểm thử”; “Scripts & runs / Kịch bản và lượt chạy”; “Learn & repair / Học và đề xuất sửa”; “Release / Phát hành”; “Settings / Cài đặt”; “Phase / Giai đoạn”.
- “Feature tree / Cây chức năng”; “Generate tests / Tạo kiểm thử”; “Pending changes / Thay đổi chờ áp dụng”; “AI settings / Cài đặt AI”.

## Keep unchanged

- `workspace-header` org/project search, account menu, skip link, shared CSS tokens, support banner safety, menu toggle/tooltips and drawer Escape/focus-return behaviour. Preserve existing destinations/icons for unchanged nav items.

## Done when

- Changes are baked into static HTML except the shared sidebar, which still comes from the nav array in `assets/evidgate.js`; minimal JS only switches local review states.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. Every new control has visible keyboard focus; mobile remains read-only and nothing overflows.
- G1 shows authoring first; G2 adds scripts/runs; G3 adds repair; G4 reveals expansion items. No release verdict appears in G1–G3, including review specimens.
