# Release view

Owner approval: pending (B2 / 09.1.1-T11.1)

Flow id: `release-view`

CR-D-2 phase: **G4** ([NAV]:32; [phase plan]:79). The route is hidden until the phase switch reaches G4. A directly opened earlier-phase reference carries the **“G4 reference · hidden in G1–G3”** banner, while its `[data-eg-phase="G4"]` body remains hidden; at G4 the body is available and the earlier-phase notice is hidden ([R]:2,37–40; [JS]:466–486). The footer still identifies it as a G4 reference ([R]:125). This is not a release approval action.

Feature task: **09.3.1-T10**, including T10.1 release route/store, T10.2 component matrix, T10.3 freshness/late-window warnings and T10.4 accessibility/i18n/budget acceptance ([release plan]:206–213). Related decision/waiver page 06 is T5/T9/T11, also G4; quality overview is 09.9.1, not an extra release-view implementation task ([phase plan]:77–79; [CR-D-2 screen map]:23,34–35).

The v2 mockups are the wireframes; this document ports their structure and records their gaps without inventing new visual designs. Source citations use the linked file label plus its source line numbers. Images below export **page 21**, 1280×800, with phase G4 so every requested state is reachable. Owner approval remains pending under [AC9 and T11.1]:31,196 and the [B2 gate]:79.

## v2 pages

Phases are from [NAV]:17,32–33. Each state list is copied from the corresponding `data-eg-states` attribute, not inferred from generic labels in JavaScript.

| Page | Role | Phase | Valid `?state=` values |
| --- | --- | --- | --- |
| [21 · Release view][R] | Primary: stored release decision, component/digest/bound-run/decision matrix, evidence and pinned-policy context | G4 | `default, loading, empty, no-match, error, 403, 404, 401, 429, 412, stale, missing-evidence, evidence-restricted, pending, partial` (line 38) |
| [06 · Decision and waivers][decision] | Linked run decision, immutable evidence identity, separate assessment fields and case-scoped exceptions; not this release's aggregate | G4 | `default, loading, empty, no-match, error, 403, 404, 401, 429, 412, stale, missing-only, evaluation-pending, waiver-requested, waiver-expired, waiver-revoked` (line 38) |
| [22 · Quality overview][quality] | Historical completeness/outcome measures; links back to Releases without fabricating a current verdict | G4 | `default, loading, empty, no-match, error, 401, 403, 404, 429, 412, partial, stale, unavailable, long` (line 39) |

## Regions

Reading order of page 21 ([R]):

1. **Shell and route identity** — skip link to `#main`, `.eg-nav[data-eg-nav]`, `.eg-topbar`, project scope/search/account/shortcuts, then `.eg-breadcrumbs` naming Storefront 2.18 (12–35).
2. **Phase gate and review controls** — `.eg-banner--phase[data-eg-phase-notice]`, `[data-eg-review][data-eg-states]`, then the G4 content wrapper (37–40). Review controls are a design fixture, not product authorization.
3. **Narrow-view notice and release header** — `.eg-banner--desktop` says read-only on mobile; `.eg-page-header` gives release id, project/org, evaluation timestamp, pinned revision/hash and the secondary “Open decision record” link (42–52). The linked record is explicitly a separate run decision (113), not a fabricated release approval.
4. **Scenario notices and request states** — warning banners for stale/missing/restricted evidence, neutral partial banner (54–57), loading skeleton with live status (59), then `.eg-state` empty/no-match/error/403/404/401/429/412 panels (61–70).
5. **Stored release decision** — `.eg-card[aria-labelledby="release-result-title"]`: supplied `INCONCLUSIVE`, release `rel-2026.10.13`, revision `rd-14`, evaluation UTC time, freshness 24 h / late window 15 min, pinned `pr-12` and hash; footer separates freshness/late evidence from historical evaluation (74–83).
6. **Component decision matrix** — `#matrix-title`, `.eg-toolbar--filters[data-eg-filter-table="#release-table"]`, filter input/reset, local no-match slot and `.eg-table--compact#release-table` (85–97). Seven columns: component, recorded digest, bound run/evidence, freshness, collection completeness, test outcome, stored run decision. Supplied rows are web (fresh, complete, all_passed, PASS), api (stale digest, complete, all_passed, pinned PASS) and worker (no bound run/revision, not evaluated, completeness/outcome “Not available,” neutral “Pending evaluation”). Abbreviated digests remain verbatim (89–94).
7. **Evidence and policy context** — `.eg-split-pane--balanced`: `#evidence-title` explains stale API binding and missing worker evidence; `#policy-title` provides pinned revision/hash and separate run-decision link, with no approval/waiver mutation action (99–117).
8. **Source boundary and footer** — subtle card identifies missing full digests/detail contracts and no real requests; footer labels illustrative values, G4 reference; `#shortcuts-dialog` sits after the shell (119–135).

Reuse the [00-components gallery] families `eg-shell`, `eg-nav`, `eg-topbar`, `eg-breadcrumbs`, `eg-card`, `eg-btn`, `eg-badge`, `eg-toolbar`, `eg-field`/`eg-input`, `eg-table`, `eg-state`, `eg-banner`, `eg-skeleton`, `eg-dialog`, `eg-grid` and `eg-split-pane`. The primary uses neutral badges for pending; the gallery's shared `eg-chip--decision[data-state="pending"]` is an additional palette reference, not its actual cell markup. Useful gallery anchors: `#shell`, `#nav`, `#topbar`, `#table`, `#state`, `#banner` (gallery lines 60–95 list the state families). This port introduces no component or token.

Keep the native table/caption/column headers, visible search label, skip link, loading `role="status"`, and keyboard-operable links/reset/reload. The read-only screen has **no primary mutation CTA**; promoting navigation into “approve release” would change the approved design and scope ([R]:51,109–114). WCAG, locale correctness and lazy-chunk budget remain T10.4 acceptance, not claims established by screenshots.

## States

`applyEntryState` accepts only declared `?state=` values; `setScenario` toggles authored state attributes without calculating verdicts or sending requests ([JS]:530–572,2250–2259). Required exports below are all the primary page. Shared state CSS uses a surface/border/text layout with neutral empty, danger load/permission/not-found icons and warning conflict/no-match icons ([CSS]:3063–3139). Pending evaluation uses the separate neutral palette, not any error tone ([CSS]:1134–1137).

| State | Verified mockup presentation | Available actions | HTTP/problem meaning | Exports: light-vi · dark-vi · light-en · dark-en |
| --- | --- | --- | --- | --- |
| default | Supplied stored INCONCLUSIVE, three-component matrix, api stale-binding context and worker neutral pending; no client aggregate ([R]:74–114). | Open linked run decision, bound runs, policy context; filter components and clear the filter; inspect shortcuts. | Loaded fixture; application data comes from release and release-decision reads ([release plan]:210). A stored verdict is not recomputed from the visible rows. | ![release-view default light vi](images/release-view/default-light-vi.png) ![release-view default dark vi](images/release-view/default-dark-vi.png) ![release-view default light en](images/release-view/default-light-en.png) ![release-view default dark en](images/release-view/default-dark-en.png) |
| loading | Card skeleton, `aria-busy="true"`, decorative content hidden from assistive technology and live “Loading release record”; main data body hidden ([R]:59,72). | Shell/review controls and authored header navigation remain; matrix/filter are unavailable in the hidden data body. | Read in progress; not an empty release and not a pending evaluation result. | ![release-view loading light vi](images/release-view/loading-light-vi.png) ![release-view loading dark vi](images/release-view/loading-dark-vi.png) ![release-view loading light en](images/release-view/loading-light-en.png) ![release-view loading dark en](images/release-view/loading-dark-en.png) |
| empty | “No component records”; copy explicitly says an empty matrix is not a release PASS; main data body hidden ([R]:62,72). | No empty-panel CTA; header “Open decision record” and shell/review controls remain. | No supplied component records; no specific error code assigned. Never fill the release with another component's PASS. | ![release-view empty light vi](images/release-view/empty-light-vi.png) ![release-view empty dark vi](images/release-view/empty-dark-vi.png) ![release-view empty light en](images/release-view/empty-light-en.png) ![release-view empty dark en](images/release-view/empty-dark-en.png) |
| error | “Release record could not be loaded”; no guessed verdict; main data body hidden ([R]:64,72). | Retry explicitly sets local scenario to default; no request is sent by the specimen. Header/shell remain. | Generic load failure; mockup does not assign a concrete status. A real 503 has `Retry-After` ([overview]:336). | ![release-view error light vi](images/release-view/error-light-vi.png) ![release-view error dark vi](images/release-view/error-dark-vi.png) ![release-view error light en](images/release-view/error-light-en.png) ![release-view error dark en](images/release-view/error-dark-en.png) |
| 403 | Lock-icon “Release view is not authorized”; main decision/matrix body hidden; header remains ([R]:65,72). Copy says `release:read`, which conflicts with the plan permissions; see Open points. | No inline permission CTA or release mutation. Header link remains authored navigation; do not infer its destination permission from the role label. | Forbidden: permission missing. Other-tenant ids must use **404**, never this existence-disclosing state ([overview]:336). Generic JS disables mutations, but CSS also hides mutation triggers in read-only states; this primary has no mutation controls ([JS]:506–525; [CSS]:3715–3718). | ![release-view 403 light vi](images/release-view/403-light-vi.png) ![release-view 403 dark vi](images/release-view/403-dark-vi.png) ![release-view 403 light en](images/release-view/403-light-en.png) ![release-view 403 dark en](images/release-view/403-dark-en.png) |
| 412 | Warning conflict panel compares pinned `pr-12` / hash with current `pr-13`; no automatic merge/recompute. Unlike load errors, the stored data body stays visible ([R]:69,72). | “Reload current view” sets the local state to default; linked run/policy navigation and filter remain. It does not publish or approve anything. | Stale ETag/`If-Match` precondition on a **save** ([overview]:325,336). This read-only release page is a **linked-edit demonstration**, not a claim its GET or a nonexistent release-save operation returns 412. | ![release-view 412 light vi](images/release-view/412-light-vi.png) ![release-view 412 dark vi](images/release-view/412-dark-vi.png) ![release-view 412 light en](images/release-view/412-light-en.png) ![release-view 412 dark en](images/release-view/412-dark-en.png) |

Additional primary-page states ([R]:38), without extra screenshot requirements:

- `no-match` — a dedicated filter-empty state advises clearing the filter; the matrix wrapper is hidden but stored release decision/context remains; Clear filter is still available ([R]:63,72,87–88). It is not an empty release.
- `stale` — warning that newer evidence exists; preserve the recorded digest binding and historical evaluation, never silently rewrite it ([R]:54,103).
- `missing-evidence` — warns that worker has no eligible run/evidence revision and must remain pending, not inherit another component's result ([R]:55,94,104).
- `evidence-restricted` — metadata-only warning; no preview/open/download artifact link or secret exposure ([R]:56). This is a contextual banner, not a demonstrated restricted-artifact row transition.
- `pending` — declared, but **not implemented as a distinct evaluation specimen**: no primary state binding hides INCONCLUSIVE or replaces the evaluated timestamp/PASS component rows; generic switching leaves default data ([R]:38,72–94; [JS]:530–572). [OPEN] Do not equate this state with the neutral worker pending cell or claim it satisfies missing-evaluation acceptance.
- `partial` — neutral banner describes unavailable full digest/timestamps; the existing abbreviated matrix remains unchanged rather than receiving distinct partial-row bindings ([R]:57,89–94). [OPEN] Row-level partial metadata presentation is not demonstrated.
- `404` — “Release not found”; same non-enumerating response for unknown and foreign-tenant ids ([R]:66; [overview]:336).
- `401` — session-expired copy preserves the safe return-route intent; no inline sign-in CTA or release-result regeneration ([R]:67).
- `429` — server-supplied `Retry-After: 30 seconds` specimen; stored decision unchanged; do not extrapolate that fixed value to real responses ([R]:68; [overview]:336).

Related page 06 demonstrates the **actual separate** `evaluation-pending` treatment: new identity, neutral pending/no-result fields, no default run FAIL, reasons or exceptions ([decision]:53–58,132–137). It is supporting reference, not evidence that page 21's `pending` works. Page 22 keeps collection completeness, test outcome and release decision as distinct dimensions and links to the stored release screen ([quality]:90–94).

## Copy length (vi vs en)

Computed from primary HTML with a throwaway read-only Python HTMLParser pass (no application execution). Scope: `data-vi`/`data-en` within `h1`–`h6`, buttons/`.eg-btn` links, table headers and `.eg-badge` elements, including hidden states/dialogs. Strip tags, decode entities, collapse whitespace, deduplicate Vietnamese text at its first occurrence, count Unicode code points using `len`, stable-sort by descending Vietnamese length. Ratio = vi/en. Descriptions, state-title spans, KPI labels and JS-injected shell copy are excluded from this explicit scope; locale application uses [JS]:430–432.

| Vietnamese string | vi chars | Matching English | en chars | vi/en | Primary source |
| --- | ---: | --- | ---: | ---: | --- |
| “Ma trận quyết định thành phần” | 29 | “Component decision matrix” | 25 | 1.16 | [R]:86 |
| “Tải lại chế độ xem hiện tại” | 27 | “Reload current view” | 19 | 1.42 | [R]:69 |
| “Quyết định phát hành đã lưu” | 27 | “Stored release decision” | 23 | 1.17 | [R]:75 |
| “Ngữ cảnh chính sách đã ghim” | 27 | “Pinned policy context” | 21 | 1.29 | [R]:109 |
| “G4 · Bản ghi phát hành” | 22 | “G4 · Release record” | 19 | 1.16 | [R]:46 |

Longest English in this same scope: “Component decision matrix” (25). The reload label expands 42% by character count; do not assume en is always wider. Page/card headings and badges wrap ([CSS]:613–618,1020–1069,1198–1204); button labels stay intact and header action groups wrap ([CSS]:640–647,883,995–996). Desktop table headers use `white-space: nowrap` inside the local scroll wrapper ([CSS]:2849–2853,2898–2899). Mobile rows become labeled cards rather than truncating verdicts; raw `data-label` localization gaps are recorded below. Abbreviated digest strings are supplied source data, **not a CSS truncation of known full hashes** ([R]:89,121). No tooltip can truthfully reveal an unavailable full digest. [INFERENCE] Actual rendered widths/360 px overflow still require the orchestrator's visual check; character counts are not pixel measurements.

## Responsive notes

- **≥1280:** expanded global sidebar ([JS]:281), four-column stored-record summary and two-column balanced evidence/policy context ([R]:76,99; [CSS]:736–749). Matrix is a compact seven-column table with local horizontal overflow if necessary ([CSS]:2849–2853); that is not page-level scrolling.
- **768–1279:** global icon rail, content padding `--eg-space-6`; evidence/policy split collapses to one column at 1279 px ([CSS]:4029–4036). The component matrix remains tabular; there is no invented tablet column-removal rule.
- **<768:** drawer navigation, page header stacked, desktop-required read-only banner visible, record summary/grid one-column ([CSS]:4062–4082,4167–4182,4357–4365). Table wrapper loses overflow, header is hidden, and each component row becomes a card with `td::before` labels from `data-label` ([CSS]:4266–4330). There are no release mutation controls at any width ([R]:42,109–114). Filter and navigation remain inspection actions.
- **≤420 / 360 px:** content/cards use `--eg-space-4`; header action button becomes full width; badges/identity text wrap rather than creating a page scrollbar ([CSS]:4416–4433,1066–1069). Keep the no-horizontal-page-scroll invariant from [app mapping]:20; observed source rules are not a browser-tested guarantee.

## Invariants

- **Collection completeness, test outcome and stored decision are separate fields**, never an averaged score or a client-derived release aggregate ([R]:90–94; [quality]:90–94; [release plan]:23–26,72).
- **Missing/pending never renders as PASS.** Worker has no bound run/evidence; its “Pending evaluation” badge uses `.eg-badge--neutral` and the neutral `--eg-neutral-bg` / `--eg-neutral-fg` palette, implying neither success nor danger ([R]:94; [CSS]:1057–1060). The shared decision-chip pending style uses the same neutral roles ([CSS]:1134–1137). The web/api PASS cells are only supplied evaluated component results, never defaults for worker.
- **A release verdict is shown only when evaluated.** Until a stored evaluation exists, show neutral pending/no result without an evaluated timestamp or borrowed release verdict ([release plan]:24,40,114). Page 21's declared `pending` does not currently demonstrate this invariant; the gap below must be resolved before claiming acceptance.
- **Historical values remain pinned.** Newer evidence and stale digests qualify recorded bindings rather than rewriting older evaluations; raw freshness/late-window metadata is not a browser-side expiry calculation ([R]:54,79–82,103–105).
- **No release mutation or client-side healing.** This screen only reads stored data and follows links; healing remains a proposed PR, never an action applied from the UI ([R]:109–114,121; [binding rulings]:123).
- **Restricted evidence is metadata-only**, with no artifact preview/download links or invented secret values ([R]:56). Other-tenant ids remain indistinguishable from unknown ids ([R]:66; [overview]:336).

## Open points

| Requirement | Verified reference coverage and unresolved gap |
| --- | --- |
| [OPEN] AC10 / T10.1: real SDK-fed release and decision reads | Page 21 explicitly has no requests and lacks full component/run-detail contracts ([R]:119–121); the plan already names `GET /v1/releases/{id}` and `/decision` ([release plan]:210). Bind the generated SDK response at implementation time; do not treat missing source fixtures as permission to invent an endpoint or DTO. |
| [OPEN] AC10 / T10.2: components → full digests → bound runs → decisions | Three illustrative rows show the matrix shape, but full digests/timestamps are intentionally unavailable ([R]:89–94,105,121). The UI must retain actual supplied values and distinguish missing binding from restricted access; production binding/data consistency is not demonstrated. |
| [OPEN] T10.3 + AC2: evaluated-only release verdict | `pending` is declared with no dedicated body; `INCONCLUSIVE`, evaluated identity and component PASS rows remain visible through generic switching ([R]:38,49,72–94; [JS]:530–572). A real unevaluated-release fixture must suppress the release verdict/evaluated facts and show neutral pending. Related page 06 supplies a useful specimen, not a substitute screenshot or permission to alter the v2 reference here. |
| [OPEN] T10.3: freshness/late-window warnings and aggregate never PASS with missing component | Default shows api stale digest, worker missing evidence and stored INCONCLUSIVE; `stale` / `missing-evidence` add explanatory banners ([R]:54–55,75,93–105). There is no independent expired late-window boundary specimen or component-level timestamps to demonstrate expiry. Keep stored aggregate and per-run decisions distinct; client code must never invent the missing-component aggregate. |
| [OPEN] T10.3: partial/restricted data | `partial` and `evidence-restricted` add banners only; the default rows remain ([R]:56–57,89–94). Neither proves row-level unavailable metadata substitution or transition to a restricted artifact item. Confirm actual response fields with the owning feature before binding them. |
| [OPEN] Authorization wording | The 403 copy says `release:read` ([R]:65), but the authoritative plan route uses `run:read` + `decision:read` and the API read requires `decision:read` ([release plan]:62,72; [overview]:359–361). Consume server-declared permissions; do not create `release:read` from mockup copy. The plan wins; copy alignment remains needed before implementation. |
| [OPEN] T10.4: full vi/en matrix | Desktop headers have bilingual attributes ([R]:90); mobile row `data-label` values are hardcoded (mostly Vietnamese, plus “Digest”) and lack `data-label-en`/`data-label-vi` ([R]:92–94). Since mobile labels come from `data-label`, English mobile retains Vietnamese field labels; shared locale code only translates paired label attributes ([JS]:442–444; [CSS]:4322–4330). Page 21 cannot establish complete mobile i18n acceptance as-is. |
| [OPEN] T10.4: a11y and budget | Source has caption/headers/labels and keyboard links, but axe, screen-reader handling of CSS-card tables, focus behavior, all viewport widths and ≤150 kB gzip lazy chunk remain application checks ([release plan]:213). Visual, keyboard and screen-reader acceptance is not claimed: the screenshots are static captures of the v2 mockup, and approval of the flow is the owner action recorded in the header. |
| [OPEN] Policy conflict state vs read-only release contract | The mandatory 412 export is authored as a linked-edit demonstration with local reload ([R]:69); it does not define a release-save request, revision-diff editor, or automatic recomputation. Confirm linked-edit integration in the owning settings/decision feature; keep release itself read-only. |
| [OPEN] Owner approval and evidence | Source phase gate follows the approved CR-D-2 direction, but this document does not record B2 approval or assert static fixtures implement guards/session recovery. Visual, keyboard and screen-reader acceptance is not claimed: the screenshots are static captures of the v2 mockup, and approval of the flow is the owner action recorded in the header. |

[R]: ../web-ui/output/21-release-view/evidgate-release-view.html
[decision]: ../web-ui/output/06-decision-waivers/evidgate-run-decision.html
[quality]: ../web-ui/output/22-quality-overview/evidgate-quality-overview.html
[NAV]: ../web-ui/output/NAV.md
[JS]: ../web-ui/output/assets/evidgate.js
[CSS]: ../web-ui/output/assets/evidgate.css
[00-components gallery]: ../web-ui/output/00-components/evidgate-components.html
[overview]: ../../../plans/00-overview.md
[release plan]: ../../../plans/features/09-web-ui/09.3.1-run-detail-is-the-truth-surface.md
[phase plan]: ../../../plans/13-product-phases.md
[binding rulings]: ../../../plans/features/README.md
[AC9 and T11.1]: ../../../plans/features/09-web-ui/09.1.1-app-scaffold-and-build-acceptance-npm.md
[B2 gate]: ../web-ui/prompts-crd2/00-README.md
[CR-D-2 screen map]: ../web-ui/prompts-crd2/00-README.md
[app mapping]: ../../../apps/web/README.md
