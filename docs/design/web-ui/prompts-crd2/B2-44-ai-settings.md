Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict. Follow its Deliverable and links section and use relevant Canonical fixtures verbatim.

**Page:** `44-ai-settings/evidgate-ai-settings.html`. Create a new page that reuses the shell and `assets/` exactly like `03-run-list/evidgate-runs.html`.
**Phase:** G1. **Nav item:** `ai-settings` (set `data-nav` on `<html>`; Settings group).
**Plan ids:** 13.4.1-T5, 13.2.1-T1, 13.2.1-T2.

## Goal

Configure reusable project AI guidance in vi/en, inspect version history and know which settings version future generation will reference.

## Change

- Header names project storefront-web, current settings version and last author/time, with a back link to `../27-settings-shell/evidgate-settings.html`. Use Settings and Version history tabs; “Save new version” is the settings tab's single primary action.
- Form fields: description, environment URL, languages, writing style, coverage strategy, four separate priority criteria (critical/high/medium/low), exclusion rules and step detail. Explain each field without adding provider/model selectors or tenant consent controls.
- Live associated character counters and limits: writing style ≤250, coverage strategy ≤1,500, exclusion rules ≤1,500, step detail ≤200. Reflect invalid/over-limit content with text and error association; block save until valid rather than silently truncating.
- For description, environment URL and priority-criterion text show length counters without an invented maximum; languages is a labelled vi/en selection. No additional field limits are defined in the delta brief: record the remaining server-contract question, not a fabricated cap.
- Provide vi and en template choices with a preview of changed fields; explicitly confirm replacing a dirty draft. Template selection changes the draft only, not saved history or existing generated cases; selected output language is separate from interface locale.
- Save appends an immutable settings version and shows supplied author/time/reason metadata; version history lists versions with read-only field differences. Explain that generation provenance and cache identity capture the version used at call time, not whatever is latest now.
- Show tenant AI opt-in as a read-only on/off/unavailable notice linked back to `../27-settings-shell/evidgate-settings.html`; off does not prevent manual authoring or claim consent changed when project settings are saved. Respect supplied edit grants without inventing a role mapping.
- On a supported conditional-save conflict show the shared 412 pattern with current/base settings versions and changed fields; retain the draft and require explicit Reload, never auto-merge. Do not invent an endpoint or claim an unconfirmed wire contract.

## States (in addition to the shared switcher)

- No settings yet; unsaved draft; counter just below/at/above each stated limit; vi/en template preview and canceled replacement; save loading/error/success; read-only version; AI opt-in off; supported 412 specimen; long environment URL with no credentials shown.

## Fixture (illustrative, labelled as such)

- storefront-web; settings `ais-3` by Linh Nguyễn, 2026-10-23T09:00:00Z; writing style “Use short, observable steps / Dùng các bước ngắn, có thể quan sát”; exclusion “Exclude destructive production actions / Loại trừ thao tác phá hủy trên môi trường thực tế”; `ais-2` as history, not a mutable overwrite target.

## Copy EN / VI

- “Project AI settings / Cài đặt AI của dự án”; “Writing style / Phong cách viết”; “Coverage strategy / Chiến lược bao phủ”; “Priority criteria / Tiêu chí ưu tiên”; “Exclusion rules / Quy tắc loại trừ”; “Step detail / Mức chi tiết của bước”.
- “Save new version / Lưu phiên bản mới”; “Preview template / Xem trước mẫu”; “Version history / Lịch sử phiên bản”; “Character limit exceeded / Vượt giới hạn ký tự”; “Settings version used / Phiên bản cài đặt đã dùng”.

## Keep unchanged

- Shared shell/tokens, Settings group placement, phase/locale/theme/state controls, conflict-dialog semantics, read-only mobile and permission patterns. No credit pricing, model decisions or generated-content auto-apply.

## Done when

- Changes are baked into static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- Each specified numeric limit has working local boundary states, both templates preserve review before replacement, and saving appends a version without rewriting history/provenance.
