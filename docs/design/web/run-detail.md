# Run detail

Owner approval: pending (B2 / 09.1.1-T11.1)

Flow id: `run-detail`  
CR-D-2 phase: **G2** ([NAV.md:14–18,22][nav]).  
Feature tasks: `09.3.1-T1` (related run list), `T2` (detail identity/context/timeline), `T3` (evidence), `T4` (attempts), `T6` (producers), `T7` (actions); `09.2.1-T4` (related original-report comparison). `09.3.1-T8` healed labels are G3; `09.3.1-T5`, `T9`, `T10`, `T11` (decision, exceptions, releases) are G4, not implied by this G2 page. Task ids are verified in [09.3.1 §10][feature], [09.2.1:157–164][onboarding-plan] and the [CR-D-2 task exceptions, 13-product-phases.md:79][phases].

The approved visual direction is the supplied v2 HTML, not a newly drawn wireframe. The exports document the primary page at 1280×800, phase G4 so every route is available; that capture setting does not add a release-decision panel to the G2 mockup or constitute owner sign-off.

## v2 pages

| Page | Role | Phase | Valid `?state=` values from the page |
| --- | --- | --- | --- |
| [04 · Run detail][primary] | Primary run truth surface: attempts, bound-step metadata, evidence workbench, context and history | G2 | `default,loading,empty,no-match,error,403,404,401,429,412` (`evidgate-run-detail.html:41`) |
| [05 · Evidence panel][evidence] | Frozen revision, producer slots, missing reasons, artifact availability and profile metadata | G2 | `default,loading,empty,no-match,error,403,404,401,429,412` (`05-evidence-panel/evidgate-run-detail.html:41`) |
| [07 · Report comparison][comparison] | Original versus normalized counts, identities, statuses, digests and mapping discrepancies | G2 | `default,loading,empty,no-match,error,403,404,401,429,412` (`evidgate-report-compare.html:44`) |
| [11 · Artifact viewer][viewer] | Read-only ready screenshot, trace-step specimens, metadata and content restrictions | G2 | `default,loading,empty,no-match,error,403,404,401,429,412` (`evidgate-artifact-viewer.html:45`) |
| [03 · Run list][list] | Entry point: source, state, completeness and rerun-scope filtering; per-run expanded context | G2 | `default,loading,empty,no-match,error,403,404,401,429,412` (`evidgate-runs.html:45`) |

Phases come from [NAV.md:14–18,22][nav]. The states are review controls, not network simulations: [assets/evidgate.js:378–411,530–572][js] reads the page's declaration, toggles `[data-eg-show-state]` / `[data-eg-hide-state]`, updates read-only flags and announces changes. A query state is accepted only if declared (`:2250–2259`). Theme and locale persist independently (`:415–429`).

## Regions

Reading order follows [04-run-detail/evidgate-run-detail.html][primary].

| Order | Region and source | Existing implementation / gallery components |
| --- | --- | --- |
| 1 | Shell and navigation (`:12–27`) | Skip link, `.eg-shell`, `[data-eg-nav]`, `.eg-topbar`, project scope, search, account menu and breadcrumbs to Runs → run-1842. |
| 2 | Read-only mobile notice and run identity (`:28–40`) | `.eg-banner--desktop`, `.eg-page-header`; G2 badge, run/project, CI source, timestamp; `.eg-page-header__actions[data-eg-mutate]` groups Evidence panel, Compare report and Rerun run. |
| 3 | Review/state surface (`:41–59`) | `[data-eg-review]`, busy skeleton split pane, `.eg-state` empty/no-match/error/permission/not-found/conflict blocks. |
| 4 | Independent run facts and missing-shard explanation (`:60–69`) | Three `.eg-card` / `.eg-badge` summaries: collection incomplete, `has_failures` outcome, frozen `ev-3`; `.eg-banner--warning` names shard-3 and links to evidence. No release decision. |
| 5 | Attempts by execution (`:70–83`) | `.eg-table--compact.eg-table--workbench`; execution/attempt, raw status, normalized status, manual case, script, duration and assertion detail. Separate failed original, successful retry and blocked dependant; flaky remains a script label. |
| 6 | Bound-step metadata (`:84–97`) | `.eg-table--compact`: step/action, binding, profile, green-source run, case revision, profile hash, spec/captured UTC. Only structural metadata, not element contents or input values. |
| 7 | Evidence workbench (`:98–151`) | `#artifacts`, `.eg-toolbar--filters#workbench-filters`, `[data-eg-filter-table="#artifact-table"]`, revision/view selects, `ev-3`/Pinned chips, missing reasons, producer slots and artifact table. Other revisions are disabled because their payloads are not supplied. |
| 8 | Timeline and execution context (`:152–169`) | `.eg-split-pane--balanced`, `.eg-timeline` with source UTC times; environment, source, manifest, suite/policy provenance, component digest, evidence revision and related links. |
| 9 | Profile/link state specimens (`:170–181`) | Nested `.eg-card` grid distinguishes no prior green profile, capture unavailable, missing links, historical case version, collecting without a frozen revision, and older revision pinned. These are specimens in default, not additional query-state names. |
| 10 | Footer and dialogs (`:183–196`) | Explicit illustrative-data notice; `.eg-dialog` shortcuts and rerun confirmation. Rerun copy preserves the original attempt/evidence and does not start hosted execution. |

Reuse the [00-components][components] visual vocabulary: shell/nav/topbar/page header (`:151–224`), buttons/badges/chips (`:253–323`), toolbar, card/table (`:354–508`), state/skeleton (`:487–529`), banner/timeline (`:577–660`) and dialogs (`:778` onward). These are mockup class references, not claims that Angular components have been delivered. The future shared-component names in [09.3.1:48–50][feature] include `AttemptsTable`, `MissingReasonsList`, `ProducerStatusTable`, `EvidenceBadge` and `DecisionChip`; the last is not visible on this G2 primary page.

Related-page continuity:

- [05-evidence-panel/evidgate-run-detail.html:65–85,114–141][evidence] pins ev-3, exposes the hash/provenance and producer reasons, and withholds content actions for restricted, scan-pending and non-ready artifacts.
- [07-report-comparison/evidgate-report-compare.html:65–91,124–140][comparison] preserves original/normalized representations side by side; equal row counts do not prove equal content. Restricted original-report access is an explicitly **disabled** button (`:87`); mapping discrepancies link to mapping resolution. No release verdict is present.
- [11-artifact-viewer/evidgate-artifact-viewer.html:69–83,108–137][viewer] shows a ready screenshot with local step/zoom controls; other artifact states are metadata only. Its isolated-origin notice describes a presentation boundary, not a deployed sandbox (`:71`).
- [03-run-list/evidgate-runs.html:65–81,138–145,157–170][list] offers local filters, preserves original failure beside retries, and labels create/rerun/cancel dialogs as no-request fixtures. It explicitly supplies no alternate cursor.

## States

Each image group is ordered **light-vi, dark-vi, light-en, dark-en** and always captures page 04, not the related page that happens to show a more complete state.

| State | What the mockup shows and evidence | Available actions | HTTP / problem meaning | Exported screenshots |
| --- | --- | --- | --- | --- |
| `default` | Incomplete shard-3 collection, stored `has_failures`, frozen ev-3 with newer ev-4 known, original failure/retry/blocked attempts and evidence workbench; [evidgate-run-detail.html:60–181][primary]. | Evidence panel, Compare report, rerun dialog, case/script links, artifact-state filter and the ready screenshot viewer link. Non-ready artifacts have no open/download action; unavailable revisions cannot be selected. | Normal supplied run data; incompleteness is not a transport error and neither a passing nor failing release verdict is inferred. | ![run-detail default light vi](images/run-detail/default-light-vi.png) ![run-detail default dark vi](images/run-detail/default-dark-vi.png) ![run-detail default light en](images/run-detail/default-light-en.png) ![run-detail default dark en](images/run-detail/default-dark-en.png) |
| `loading` | Busy two-card skeleton split pane and an announced run/attempt/evidence/profile loading status; body hidden; [evidgate-run-detail.html:42–48,59][primary]. | Back-to-Runs breadcrumbs/shared navigation remain. Header action group is marked `aria-disabled` by `syncMutationTriggers`; default body/filter controls are absent. | Fetch unresolved, not zero attempts and never PASS. No concrete HTTP status is assigned to this review state. | ![run-detail loading light vi](images/run-detail/loading-light-vi.png) ![run-detail loading dark vi](images/run-detail/loading-dark-vi.png) ![run-detail loading light en](images/run-detail/loading-light-en.png) ![run-detail loading dark en](images/run-detail/loading-dark-en.png) |
| `empty` | “No attempts in this run”; explicitly distinct from passed, unavailable or filtered out; [evidgate-run-detail.html:50,59][primary]. Direct `?state=empty` also overwrites the header with “New manual case” due to shared JS ([assets/evidgate.js:2250–2304][js]). | State body has no CTA. Breadcrumb navigation remains; at desktop the header links/rerun fixture are not state-blocked merely by `empty` (`assets/evidgate.js:506–525`). | Successful empty attempt collection, not a foreign-run 404 or a successful test result. Header contamination is not intended run-detail meaning. | ![run-detail empty light vi](images/run-detail/empty-light-vi.png) ![run-detail empty dark vi](images/run-detail/empty-dark-vi.png) ![run-detail empty light en](images/run-detail/empty-light-en.png) ![run-detail empty dark en](images/run-detail/empty-dark-en.png) |
| `error` | “Run detail could not be loaded”; Retry keeps run/revision identity and recreates no attempts/artifacts; [evidgate-run-detail.html:52,59][primary]. | Retry sets the local scenario to default. Breadcrumbs/shared navigation remain. Header actions are not explicitly state-blocked by `error`; do not read this as production authorization. | Failed read with no specific HTTP status assigned by this HTML. Production must render the server error envelope, not fabricate evidence. | ![run-detail error light vi](images/run-detail/error-light-vi.png) ![run-detail error dark vi](images/run-detail/error-dark-vi.png) ![run-detail error light en](images/run-detail/error-light-en.png) ![run-detail error dark en](images/run-detail/error-dark-en.png) |
| `403` | Lock icon and missing `run:read`; attempts, profiles and artifact metadata undisclosed; [evidgate-run-detail.html:53,59][primary]. JS sets read-only and the header `[data-eg-mutate]` group is hidden by CSS, not left as disabled visible controls ([assets/evidgate.js:562–567][js]; [assets/evidgate.css:3715–3717][css]). | Breadcrumbs, shared navigation and review controls remain. No access-request action or run mutation is shown in the state body. | Forbidden: permission missing. A foreign-tenant run id must instead use `404` / Run not found. Disabled-not-hidden permission behavior in the feature plan remains an open mismatch. | ![run-detail 403 light vi](images/run-detail/403-light-vi.png) ![run-detail 403 dark vi](images/run-detail/403-dark-vi.png) ![run-detail 403 light en](images/run-detail/403-light-en.png) ![run-detail 403 dark en](images/run-detail/403-dark-en.png) |
| `412` | “The run changed while you were working”; linked-edit conflict example names base rv-12, current rv-13, retained draft and explicit reload; [evidgate-run-detail.html:57,59][primary]. | Reload current read resets the local scenario to default; no automatic merge or edit retry. Header actions are not blocked solely by `412` in the shared JS. | Stale `ETag` / `If-Match` precondition on a linked edit, not an error produced by ordinary GET. Preserve user draft/current revision and require explicit reload in the real edit flow. | ![run-detail 412 light vi](images/run-detail/412-light-vi.png) ![run-detail 412 dark vi](images/run-detail/412-dark-vi.png) ![run-detail 412 light en](images/run-detail/412-light-en.png) ![run-detail 412 dark en](images/run-detail/412-dark-en.png) |

HTTP authority is [00-overview.md:325,330–336][overview]: stale optimistic preconditions return 412 with the current revision id in `error.details`; 403 means forbidden, while 404 also covers other-tenant ids. Error fields are `code`, `message`, `request_id`, `retryable` and `details`. These semantics do not turn the review switch into an API implementation. The CSS uses neutral empty, danger permission/error/not-found icons and warning conflict/no-match icons ([assets/evidgate.css:3063–3139][css]); these feedback tones are not release verdicts. Mutation-group marking is from [assets/evidgate.js:506–527][js]; `aria-disabled` on a container is not equivalent to native disabled descendants.

Additional declared primary-page states:

- `no-match` — “No attempts match these filters”; copy says the frozen revision and filters stay visible (`evidgate-run-detail.html:51`), but the containing workbench is actually hidden (`:59`); see the gap below.
- `404` — “Run not found”; existence details undisclosed (`:54`). Use this for a foreign-tenant id, never the 403 explanation.
- `401` — expired session, no automatic submission of the pinned revision/filters (`:55`); re-auth is described, not wired by this specimen.
- `429` — wait, no automatic retry and no revision change (`:56`); production honors `Retry-After` per 00 §9.2.

## Copy length (vi vs en)

Measured from primary HTML with a throwaway read-only Python `HTMLParser` pass. Localized `data-vi` / `data-en` pairs in `h1`–`h6`, buttons / `.eg-btn` links, table headers and badges are decoded, stripped of tags, whitespace-collapsed and deduplicated, including default-page specimens and dialogs. Counts are Unicode code points without normalization, not pixels or grapheme clusters. Paragraphs, field labels, select options and JS-injected review controls are not in this measurement. Ratio = vi count / en count.

| Source line | Longest Vietnamese strings (quoted mockup copy) | vi chars | Matching English | en chars | vi / en |
| --- | --- | --- | --- | --- | --- |
| 177 | “Đang thu thập · chưa chốt phiên bản” | 35 | Collecting · no frozen revision | 31 | 1.129 |
| 178 | “Chốt phiên bản cũ · đã có bản mới” | 33 | Old revision pinned · newer exists | 34 | 0.971 |
| 88 | “Đặc tả / thời điểm ghi nhận UTC” | 31 | Spec / captured UTC | 19 | 1.632 |
| 85 | “Hồ sơ từ lần chạy đạt gần nhất” | 30 | Last green profile | 18 | 1.667 |
| 85 | “Siêu dữ liệu bước đã liên kết” | 29 | Bound-step metadata | 19 | 1.526 |

The mockup's source rules allow headings and badge labels to wrap anywhere (`assets/evidgate.css:163,613–618,1066–1069,1204`); table data cells wrap (`:2934–2936`). Header cells remain nowrap (`:2893–2899`), so the long captured-UTC header contributes to local table width. Button labels remain a single line (`:992–996`) and action groups wrap whole controls (`:640–645`). No automatic digest truncation is demonstrated by the displayed `mf-7d2c…91ab` / `sha256:…` strings: these are already abbreviated HTML values (`evidgate-run-detail.html:162–165`), not full values clipped with a tooltip. Account/scope ellipsis is separate shared chrome (`assets/evidgate.css:498–506,586–589`). This is source analysis, not a rendered overflow or translation-quality pass.

## Responsive notes

- **≥1280:** expanded navigation by default (`assets/evidgate.js:281,2348`). Run facts use three columns, missing reasons/producer slots use two, and timeline/context use a balanced split pane (`evidgate-run-detail.html:60,110,152`). Workbench tables can scroll within their wrappers; `.eg-table--workbench` has a 900 px minimum (`assets/evidgate.css:2849–2872`).
- **768–1279:** icon rail by default. Content padding changes and timeline/context split pane collapses to one column (`assets/evidgate.css:4029–4040`). KPI/metadata grids retain their authored column count until <768. Table wrappers contain local overflow; no claim is made that all seven attempt columns fit without scrolling at 768.
- **<768:** drawer navigation and stacked header (`assets/evidgate.css:4062–4082,4167–4182`). The page's desktop-required banner explains read-only metadata/status/link inspection (`evidgate-run-detail.html:28–30`). All marked header actions are hidden, including the grouped Evidence/Compare links, because they share `[data-eg-mutate]` (`:39`; `assets/evidgate.css:4200–4202`); evidence links elsewhere in the body remain. Grids become one column, tables become block cards with localized `data-label` text, header rows disappear and wrapper horizontal scrolling is removed (`:4266–4335,4357–4365`). Controls use touch-height tokens (`:4215–4235`).
- **360 px requirement:** the product forbids horizontal page scroll ([apps/web/README.md:20][web-readme]). The source supplies wrapping/card conversion, but the nowrap header issue described below must not be mislabeled as verified compliance. Visual and viewport acceptance is not claimed: the screenshots are static captures of the v2 mockup; visual, keyboard and screen-reader acceptance of the flow remains the owner action recorded in the header, and [layout.md](layout.md) owns shared layout evidence.

## Invariants

- Collection completeness, test outcome and release decision are independent. Incomplete collection and `has_failures` are separate cards; the G2 page explicitly shows no release verdict ([evidgate-run-detail.html:36,61–67][primary]). Missing data never implies pass.
- Original failure persists beside a successful retry; `flaky` is a script label, and a blocked dependant is not passed (`:71–81`). A successful retry does not overwrite stored `has_failures`.
- Revision identity is immutable for this view: ev-3 remains pinned and newer ev-4 is announced, not silently selected (`:63,99–109,139–147`). State review changes visibility, not resource identity.
- Bound-step metadata is not captured sensitive payload: no DOM element contents, input values or profile expansion (`:85–95`). Missing profile, missing links and unavailable capture remain distinct (`:171–178`).
- Non-ready/restricted/scan-pending artifacts stay metadata-only, with no open or download actions (`:131–137`). The related viewer's presentation boundary is not proof of a secure deployed origin ([11:69–71,118–137][viewer]).
- A missing evaluation must render neutral, verdict-free `pending`, never PASS/FAIL, per [09.1.1 AC8/T11.2][foundation]. This G2 primary page has no evaluation chip at all; the absence is not a demonstrated `pending` decision state. Warning-colored collection or `pending_upload` badges belong to evidence availability, not evaluation verdicts.
- Rerun would create a new run rather than overwrite attempts/evidence; the dialog starts no hosted execution (`:192–195`). No healing-apply control is present; this flow neither delivers nor applies a repair. Any eventual healing surface must preserve the PR-only / original-failure invariants rather than infer an official pass from a shadow result.

## Open points

- [OPEN] **G2 versus older decision ACs:** [09.3.1 AC1/AC2/AC4][feature] requires the stored FAIL/pending/decision provenance view, while the CR-D-2 phase map moves `09.3.1-T5` to G4 and the primary G2 HTML explicitly omits release verdicts. Do not add a verdict to these screenshots or claim those ACs passed. The receiving plan must reconcile phase-scoped acceptance and the missing neutral-pending evaluation specimen (`09.3.1-T3.3`).
- [OPEN] **Action semantics not designed completely here:** the primary only supplies a no-request rerun confirmation (`:192–195`), not full/subset scope, reprocess, idempotent create or cancel-202 handling required by AC5/AC6 and `09.3.1-T7`. The related list supplies create/cancel fixture dialogs but not live submission. They cannot demonstrate server state transitions or idempotency.
- [OPEN] **Later-phase acceptance is not covered:** AC7/AC8/AC10/AC11 waiver/quarantine, healed/shadow candidate-hash blocks and release aggregation do not appear on this primary page. They belong to the G3/G4 task surfaces identified in the header, not invented controls in this G2 reference. The retained flaky chain covers only AC3's visible retry distinction.
- [OPEN] **Producer/identity detail:** `09.3.1-T2.2`, `T2.4`, `T6` require declared producer metadata, `last_sequence`, stale epochs and `after_cancel`; the workbench shows only slot/state/expected-versus-accepted (`:116–125`). The context supplies one component digest but no bound-release component matrix or explicit unbound-release state (`:159–168`). The feature's `ref_name` bidi-isolation/unverified-ref hand-over (`09.3.1:224`) has no specimen here.
- [OPEN] **Immutable historical views are placeholders:** prior/newer revision options are disabled because their payloads are absent (`:102,139–147`), so `09.3.1-T3.2`'s working old-revision selection is not demonstrated. `T3.4`'s required `#attempt-<execution_key>-<n>` / `#producer-<shard>` anchors are not present on the attempt/producer rows (`:76–78,120–122`). The existing `#artifacts` anchor does not substitute for them.
- [OPEN] **No-match contradicts visibility:** `no-match` copy says filters/revision remain visible (`:51`), but the outer `[data-eg-hide-state]` hides the entire workbench (`:59`). It also calls the state “attempts” while the filter toolbar targets artifacts (`:101–105`). Keep that discrepancy explicit rather than describing a functioning attempt-filter view.
- [OPEN] **Empty query header contamination:** `assets/evidgate.js:2250–2304` applies the manual-case title/description/metadata rewrite to any `?state=empty` route, including this run. Export the reference faithfully; a separately approved source correction needs to scope this behavior to its intended page.
- [OPEN] **Permission visibility:** `09.3.1` §8 requires forbidden actions disabled, not hidden, but CSS hides the marked header group in 403 (`assets/evidgate.css:3715–3717`). The related comparison's restricted-report button is disabled (`07:87`), whereas restricted-artifact content actions are intentionally absent (`04:137`); these are different authored patterns, not one blanket rule. `aria-disabled` on a marked container in loading is not native disabled state for every descendant.
- [OPEN] **412 is illustrative, not an implemented concurrency edit:** the state names base/current rv ids and offers local Reload current read (`:57`), but there is no editable retained draft comparison, typed server error envelope or actual reload request. A normal read must not be documented as sending `If-Match` just to produce this specimen.
- [OPEN] **Dense-table overflow / tooltip evidence:** generic header `white-space: nowrap` (`assets/evidgate.css:2899`) is not reset for generated mobile `td::before` labels (`:4322–4330`); [INFERENCE] the long captured-UTC label can exceed a narrow card. Values abbreviated in the HTML are not full values with accessible tooltips. `09.1.1-T11.3`'s truncation-with-tooltip acceptance is not proven by this page, and AC9's URL filters/cursor pagination is not proven by the related local-filter list (`03:65–81`).
- [OPEN] **Malformed reference markup:** the hidden ev-4 description on `evidgate-run-detail.html:147` lacks a closing quote for `data-vi` and the intended description text/closing paragraph markup. [INFERENCE] browser error recovery may alter that hidden branch. Reference files are read-only in this task; no repair or invented payload is included.
- [OPEN] **Owner approval and runtime checks:** B2 / AC9 sign-off remains pending. Source reading and copy measurements do not establish end-to-end acceptance, keyboard/a11y conformance, security isolation or 360 px no-overflow behavior.

[primary]: ../web-ui/output/04-run-detail/evidgate-run-detail.html
[evidence]: ../web-ui/output/05-evidence-panel/evidgate-run-detail.html
[comparison]: ../web-ui/output/07-report-comparison/evidgate-report-compare.html
[viewer]: ../web-ui/output/11-artifact-viewer/evidgate-artifact-viewer.html
[list]: ../web-ui/output/03-run-list/evidgate-runs.html
[components]: ../web-ui/output/00-components/evidgate-components.html
[nav]: ../web-ui/output/NAV.md
[js]: ../web-ui/output/assets/evidgate.js
[css]: ../web-ui/output/assets/evidgate.css
[overview]: ../../../plans/00-overview.md
[phases]: ../../../plans/13-product-phases.md
[feature]: ../../../plans/features/09-web-ui/09.3.1-run-detail-is-the-truth-surface.md
[onboarding-plan]: ../../../plans/features/09-web-ui/09.2.1-onboarding-wizard-acceptance-a-new-tenant.md
[foundation]: ../../../plans/features/09-web-ui/09.1.1-app-scaffold-and-build-acceptance-npm.md
[web-readme]: ../../../apps/web/README.md
