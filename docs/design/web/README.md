# Web UX design foundation (09.1.1-T11)

Design foundation for `apps/web`: flow specifications, design tokens, themes, layout and breakpoints. It documents the owner-approved Web UI v2 mockups in [`../web-ui/output/`](../web-ui/output/NAV.md); it does not replace them. Source task: [09.1.1-T11](../../../plans/features/09-web-ui/09.1.1-app-scaffold-and-build-acceptance-npm.md) (AC9, sub-tasks T11.1–T11.3). Owner approval of the flow screens is gate B2 ([`prompts-crd2/00-README.md`](../web-ui/prompts-crd2/00-README.md)); it is recorded in each flow file header and is still pending.

| Sub-task | File | Content |
| --- | --- | --- |
| T11.1 | [`onboarding.md`](onboarding.md) | Onboarding flow: v2 pages, regions, six states, copy lengths |
| T11.1 | [`run-detail.md`](run-detail.md) | Run detail flow |
| T11.1 | [`failure-inbox.md`](failure-inbox.md) | Failure inbox flow |
| T11.1 | [`healing-shadow-review.md`](healing-shadow-review.md) | Healing shadow review flow |
| T11.1 | [`settings.md`](settings.md) | Settings flow |
| T11.1 | [`release-view.md`](release-view.md) | Release view flow (phase G4) |
| T11.2 | [`tokens.md`](tokens.md), [`../../../apps/web/src/styles/tokens.css`](../../../apps/web/src/styles/tokens.css) | Token inventory, themes, contrast table; `contrast.py` recomputes the table |
| T11.3 | [`layout.md`](layout.md) | Grid, breakpoints (≥ 1280 / 768–1279 / < 768), dense-table density rules |

## Screenshots

`images/<flow>/<state>-<theme>-<locale>.png` for the six flows: state `default`, `loading`, `empty`, `error`, `403`, `412`; theme `light`, `dark`; locale `vi`, `en`. `images/layout/failure-inbox-<width>.png` for [`layout.md`](layout.md).

How they were exported (re-run after a v2 mockup change):

- Playwright with the system Chrome (`chromium.launch({ channel: 'chrome' })`), viewport 1280×800, `reducedMotion: 'reduce'`, viewport shot (not full page).
- Pages are opened from `docs/design/web-ui/output/<page>.html` over `file://`. Before load, set `localStorage` keys `evidgate-phase` = `G4` (so the G4 release view renders), `evidgate-theme` = `light`|`dark` and `evidgate-locale` = `vi`|`en` (`assets/evidgate.js`, `store.read`/`store.write`).
- The state is chosen with the review `<select data-eg-review-state>` (select + `change` event), not with `?state=`: `applyEntryState` in `assets/evidgate.js` rewrites the page header to "New manual case" for `?state=empty` on every page (recorded as `[OPEN]` in each flow file).
- The capture script is not kept in the repository: it would need its own lint and type configuration, and the mockups are a frozen reference. This recipe is the record.

## Not in this folder

- The component state sheet and the `style-tokens` lint rule (AC9 lint part): 09.1.1-T11.4.
- The CDK table demo restyled with the tokens (T11.3 "done when" part): needs the T2 spike and the T1 app; handed to 09.1.1-T11.4 (see `STATUS.md` §6 and the T11 Notes in the task file).
