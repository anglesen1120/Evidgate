# Settings

Owner approval: pending (B2 / 09.1.1-T11.1)

Flow id: `settings`

CR-D-2 phase: **G1 shell**, with destinations available only at or before the current review phase. Secret references enter at G2; policies enter at G4. Later-phase tabs are hidden, not disabled teasers ([NAV]:38–43; [S]:154–174; [JS]:466–486).

Feature tasks: **09.7.1-T1–T11**, specifically T1 shell, T2 members, T3 secret references, T4 policy, T5 quota/retention, T6 audit, T7 agents, T8 integrations, T9 security events, T10 support access, T11 quota badge ([settings plan] §10). The phase map places T3/T5/T9/T10/T11 in G2 and T4/T7/T8 in G4; the G1 AI-credit region is separate from G2 quota/retention ([phase plan]:74–79,164). Project AI guidance on page 44 belongs to 13.4.1-T5 / 13.2.1, not a new Settings task ([CR-D-2 screen map]:56).

This is a specification of the existing v2 mockups, not a new wireframe or proof of application behavior. Source citations use the linked file label followed by its source line numbers. The six image rows reference exports of **page 27**, at 1280×800, phase G4; they do not substitute related-page screenshots. Owner approval remains pending under [AC9 and T11.1]:31,196 and the [B2 gate]:79.

## v2 pages

Phases are from [NAV]:38–43,55; states are the literal `data-eg-states` values in each source.

| Page | Role | Phase | Valid `?state=` values |
| --- | --- | --- | --- |
| [27 · Settings shell][S] | Primary: scope, destination tabs, organization AI consent context | G1 | `default, loading, empty, no-project, no-tabs, error, 403, 404, 401, 412, 429` (line 74) |
| [28 · Members and roles][members] | Directory, project role matrix, invite/edit/remove and last-admin dialogs | G1 | `default, loading, empty, error, 403, 404, 401, 429, 412` (line 77) |
| [31 · Usage and AI credits][usage] | Tenant credits; G2 resource quotas and deletion-impact review | G1; resource region G2 | `default, loading, empty, no-match, table-error, error, 403, 404, 401, 429, 412` (line 78) |
| [44 · Project AI settings][ai] | Versioned guidance, template preview, save/history | G1 | `default, loading, empty, error, 403, 404, 401, 412, 429, submitting, saved, validation-error` (line 77) |
| [29 · Secret references][secrets] | Binding/reference metadata, lifecycle, source-side rotation specimens; no credential values | G2 | `default, loading, empty, no-match, unavailable, error, 403, 404, 401, 429, 412, submitting, success, success-storefront, success-e2e, copy-fail, expires-soon, expired, long` (line 51) |
| [32 · Audit log][audit] | Attributable rows, support attribution, bounded export and chain specimens | G1 | `default, loading, empty, no-match, partial, error, 403, 404, 401, 429, 412, exporting, exported, long, chain-verified, chain-running, chain-failed` (line 52) |

## Regions

Reading order of page 27 ([S]):

1. **Shell and navigation** — `.eg-skip-link`, `.eg-shell`, `[data-eg-nav]`, `.eg-topbar`: project scope, navigation search, shortcuts and account menu (lines 12–47). Breadcrumbs precede all page content (49–55).
2. **Narrow-view notice and identity** — `.eg-banner--desktop` announces read-only changes on narrow screens (57–63); `.eg-page-header` gives Settings, scope and permission-fixture attribution (66–73).
3. **Review controls and request-state slot** — `[data-eg-review][data-eg-states]`, loading `.eg-skeleton` cards and live status, followed by `.eg-state` empty/error/permission/not-found/conflict patterns (74–123). This slot is not real request handling.
4. **Scope card** — `.eg-card[aria-labelledby="scope-title"]`, `.eg-form__grid`, `#settings-scope`, `#settings-project`; explanatory copy separates tenant opt-in from project guidance (126–148). Scope-change reset is described by the hint at line 139; a corresponding page-specific change handler is not established by the generic tab binding.
5. **Destinations card** — `.eg-tabs--vertical[data-eg-tabs]`, `.eg-tabs__list[role="tablist"]`, `.eg-tabs__mobile[data-eg-tab-select]`, associated `role="tabpanel"` panels (150–236). AI settings is selected initially. Project metadata is readonly and output language disabled (181–187); Members links to page 28; Audit shows only a supplied event; AI shows version `ais-3`, output languages and tenant consent; Usage links to page 31. G2/G4 tabs use `[data-eg-phase]` (173–174), with descriptive placeholder panels rather than new invented edit screens (227–231).
6. **Organization AI opt-in card** — `#consent-title`, warning banner and on/off/unavailable specimens; tenant-wide, read-only, no consent toggle or fabricated administration URL (238–253).
7. **Fixture footer and shortcuts dialog** — illustrative-data disclaimer, `#shortcuts-dialog.eg-dialog--wide` (257–274).

Reuse the [00-components gallery]: `#shell`, `#nav`, `#topbar`, `#tabs`, `#state`, `#banner`, and its `eg-btn`, `eg-badge`, `eg-card`, `eg-field`/`eg-select`, `eg-kpi`, `eg-skeleton`, `eg-dialog` families. Related pages additionally use `eg-table`, filters and drawers; they are not duplicated inside the primary shell. The gallery's state matrix identifies reusable families (gallery lines 60–95), not proof of this flow's backend integration.

Tabs use roving `tabindex`, `aria-selected` and `aria-controls`; arrow keys, Home and End skip hidden/disabled tabs and activate the corresponding panel ([JS]:869–936). Preserve the skip link, associated field labels, loading `role="status"`, visible focus and dialog return-focus behavior; application WCAG/i18n acceptance is W8/W9 ([overview]:221–222), not established by a static spec.

## States

`?state=` is validated against the declared list, then `setScenario` applies show/hide attributes, read-only flags and mutation disabling ([JS]:530–572,2250–2259). It does not perform HTTP requests. Shared state styling is `.eg-state`: surface/border/text plus neutral empty, danger error/permission/not-found, warning conflict icon roles ([CSS]:3063–3139). These are error-state tones, not evaluation verdicts.

| State | Verified mockup presentation | Available actions | HTTP/problem meaning | Exports: light-vi · dark-vi · light-en · dark-en |
| --- | --- | --- | --- | --- |
| default | Scope, AI-selected destination panel and separate organization opt-in card; normal body visible ([S]:124–253). | Select scope/project; switch destination tabs; open AI settings, members or usage; inspect account/shortcuts. Scope-reset copy is descriptive, not verified functional switching. | Loaded fixture; not a claimed HTTP response. The application reads server-declared permissions ([settings plan]:42,54). | ![settings default light vi](images/settings/default-light-vi.png) ![settings default dark vi](images/settings/default-dark-vi.png) ![settings default light en](images/settings/default-light-en.png) ![settings default dark en](images/settings/default-dark-en.png) |
| loading | Skeleton card plus three-card grid, `aria-busy="true"`, hidden decorative skeletons and live loading status; normal body hidden ([S]:74–85,124). | Shell/review controls remain; destination actions unavailable with the hidden body. | Read in progress, not permission denial or an empty resource. | ![settings loading light vi](images/settings/loading-light-vi.png) ![settings loading dark vi](images/settings/loading-dark-vi.png) ![settings loading light en](images/settings/loading-light-en.png) ![settings loading dark en](images/settings/loading-dark-en.png) |
| empty | “No settings destinations”; explanatory copy asks an administrator without revealing hidden destinations; body hidden ([S]:87–91,124). | No in-panel CTA; ask an administrator as instructed; shell/review controls remain. | Empty accessible-destination set; not a fabricated secret, zero quota or success. No specific status code is assigned. | ![settings empty light vi](images/settings/empty-light-vi.png) ![settings empty dark vi](images/settings/empty-dark-vi.png) ![settings empty light en](images/settings/empty-light-en.png) ![settings empty dark en](images/settings/empty-dark-en.png) |
| error | “The page could not be loaded”; Retry preserves scope, no draft merging/resubmission; body hidden ([S]:92–97,124). | Retry sets the local review state to default (`data-eg-set-state`); no network request is implied. | Generic load error, exact HTTP code unspecified; a real service 503 carries `Retry-After` ([overview]:336). | ![settings error light vi](images/settings/error-light-vi.png) ![settings error dark vi](images/settings/error-dark-vi.png) ![settings error light en](images/settings/error-light-en.png) ![settings error dark en](images/settings/error-dark-en.png) |
| 403 | Lock-icon “You do not have access”; ask the project owner; normal body hidden, not a disabled-but-visible tab strip ([S]:98–102,124). | No in-panel CTA; request access outside the fixture. Related mutation triggers are disabled by JS; read-only-state CSS also hides `[data-eg-mutate]` ([JS]:506–525; [CSS]:3715–3718). | Forbidden: permission missing. Foreign-tenant identifiers use the **404** pattern, not this screen ([overview]:330–336). Permission-hidden routes/tabs are an additional plan requirement, not implemented by HTML `hidden`. | ![settings 403 light vi](images/settings/403-light-vi.png) ![settings 403 dark vi](images/settings/403-dark-vi.png) ![settings 403 light en](images/settings/403-light-en.png) ![settings 403 dark en](images/settings/403-dark-en.png) |
| 412 | “Review conflict”; copy says draft retained, explicit reload, no auto-merge; body hidden. **There is no reload button or revision diff on the primary page** ([S]:113–117,124). | Shell/review controls only; no primary-page save/reload action. Page 44 has a separate draft-conflict specimen ([ai]:116–128). | A save with stale ETag/`If-Match` fails its precondition; current revision is returned in `error.details` ([overview]:325,336). Here this is a shared demonstration, not a failed shell save; the shell is readonly. | ![settings 412 light vi](images/settings/412-light-vi.png) ![settings 412 dark vi](images/settings/412-dark-vi.png) ![settings 412 light en](images/settings/412-light-en.png) ![settings 412 dark en](images/settings/412-dark-en.png) |

Additional primary-page states ([S]:74):

- `no-project` — advertised only; there is no dedicated show/hide panel or scope transition, so generic switching leaves the default content visible ([S]:74,87–124; [JS]:530–572). [OPEN] No-project behavior is not specified by a working specimen.
- `no-tabs` — advertised only; likewise leaves default content, unlike the explicitly authored `empty` panel. [OPEN] No-tabs behavior needs a distinct specimen or an explicit relationship to `empty`.
- `404` — “Not found,” with no existence details; same pattern for unknown/foreign-tenant ids ([S]:103–107; [overview]:336).
- `401` — expired session, sign-in-again copy; retained drafts are not automatically submitted; no inline sign-in button ([S]:108–112).
- `429` — wait as requested by server; no value changes or automatic retry; no duration is invented ([S]:118–122; [overview]:336).

## Copy length (vi vs en)

Measured with a throwaway read-only Python HTMLParser pass over the primary HTML only. Scope: bilingual `data-vi`/`data-en` text within actual `h1`–`h6`, buttons (including `.eg-btn` links), table headers and `.eg-badge` elements, including hidden panels/dialogs. Strip tags, decode entities, collapse whitespace, deduplicate by Vietnamese string (first source occurrence), count Unicode code points with `len`, sort descending; ratio = vi/en. State-title spans, descriptions, hints and injected shared-shell controls are not silently counted as headings. Both attribute values drive locale text through [JS]:430–432.

| Vietnamese string | vi chars | Matching English | en chars | vi/en | Primary source |
| --- | ---: | --- | ---: | ---: | --- |
| “Bật · chính sách src-7 được cung cấp” | 36 | “On · supplied policy src-7” | 26 | 1.38 | [S]:212 |
| “Chấp thuận sử dụng AI của tổ chức” | 33 | “Organization AI opt-in” | 22 | 1.50 | [S]:241 |
| “Mở mức sử dụng và tín dụng AI” | 29 | “Open usage and AI credits” | 25 | 1.16 | [S]:223 |
| “Không có dữ liệu trạng thái” | 27 | “Status unavailable” | 18 | 1.50 | [S]:212 |
| “Mở thành viên và vai trò” | 24 | “Open members and roles” | 22 | 1.09 | [S]:193 |

The longest English string in this same scope is “On · supplied policy src-7” (26). Vietnamese expansion reaches 50% in this sample; this is a character count, not a font-width measurement. Badges wrap (`flex-wrap`, `white-space: normal`, `overflow-wrap: anywhere`, [CSS]:1020–1069); card headings wrap ([CSS]:1198–1204). Button and vertical-tab labels remain unbroken ([CSS]:883,995–996,1413–1421), while action groups can wrap as units. The shared topbar ellipsis rule applies to `.eg-topbar__scope .eg-btn__label` ([CSS]:582–589), not the primary's plain project-name span ([S]:20–23); do not claim that name has tooltip truncation. [INFERENCE] Rendered overflow at 360 px still needs the orchestrator's visual check; no browser measurement is claimed here.

## Responsive notes

- **≥1280:** expanded global sidebar ([JS]:281). The scope form uses its authored grid; the destination card has a 200–260 px tab column and flexible panel ([CSS]:1397–1434). The AI summary uses its authored three-column grid ([S]:204–208).
- **768–1279:** global navigation is an icon rail and content padding becomes `--eg-space-6` ([JS]:281; [CSS]:4029–4036). Vertical tabs remain two-column until 767 px; this is not a new compact tab design.
- **<768:** drawer navigation; destination tab list disappears and its native select becomes visible; cards/banners wrap, forms and KPI grids become one column; the desktop-required read-only banner appears; controls use `--eg-control-touch` ([CSS]:4062–4096,4099–4182,4215–4219,4357–4365). Mutations on related pages are hidden, but navigation/inspection remains. At ≤420 px content/card padding is `--eg-space-4` ([CSS]:4416–4429).
- **360 px invariant:** no page-level horizontal scroll; preserve wrap/min-width behavior rather than shrinking labels. [OPEN] The mobile select contains only five G1 destinations ([S]:160–166), while G2/G4 desktop tabs exist; there are no mobile options for Secrets/Policies. Do not silently invent them or claim phase parity.

## Invariants

- **Secrets are references only, never shown on ongoing Settings surfaces.** Page 29 explicitly excludes fetching/copying/revealing credentials; prefix, binding, scope, rotation state and raw UTC metadata remain inspectable ([secrets]:43,53,135–145). No secret-value field belongs in a Settings store ([settings plan]:44,66).
- Rotation is a source-side change, not proof the downstream CI reference was replaced. Related success specimens say downstream CI must receive/verify the new reference ([secrets]:72–73). The plan delegates any one-time newly generated value to the shared reveal dialog, never to the list ([settings plan]:75,104).
- Organization AI opt-in and project settings are separate; saving project guidance cannot enable tenant consent; off/unavailable remains explicit and manual authoring stays available ([S]:207–212,238–250).
- The server is authoritative for permissions. Phase hiding is not authorization, and role labels confer no inferred privilege ([S]:146; [settings plan]:27,42,110–115).
- Retention warnings concern deletion timelines, not test verdicts. Missing metrics remain unavailable, never invented as zero ([settings plan]:32,113; [usage]:268,375–379).
- Healing, if linked from audit/context, is proposed through a PR, never applied by Settings; keep the no-client-merge boundary ([binding rulings]:123).

## Open points

These gaps are not approval or permission to redesign the read-only reference.

| Plan requirement | Verified reference coverage and unresolved gap |
| --- | --- |
| [OPEN] AC1 / T1: permission-filtered tabs absent from DOM, route resolves not found | Primary tabs have static markup; phase tabs use `hidden`, and there is no server-permission fixture selector or route guard implementation here ([S]:158–174; [JS]:466–486,884–936). The descriptive scope-reset hint (line 139) also lacks a page-specific handler. Implement guarded destinations and scope reset without treating phase visibility as permission. |
| [OPEN] AC2 / T2: last-admin refusal verbatim | Page 28 has a last-admin row and guard dialog ([members]:174,291–292), but it is illustrative rather than a demonstrated server refusal. Exact last-admin error passthrough remains application work. |
| [OPEN] AC3 / T3: metadata-only references and one-time reveal delegation | Page 29 supplies metadata (135–145) and explicitly never produces a credential (71–73). This preserves the no-plaintext list, but does not demonstrate the separate shared one-time reveal path required by the plan (75,104). |
| [OPEN] AC4–AC5 / T4: policy publish 412 diff/reload and critical-case 422 | Primary Policies is descriptive only ([S]:230–231), and its 412 panel has neither diff nor reload (113–117). Policy revision editing/critical-case refusal lives outside this shell; the required publish race and exact `critical_case_not_quarantinable` remain feature acceptance. |
| [OPEN] AC6 / T5: deletion impact count and age band | Page 31 supplies a deletion-impact dialog, count, eligible storage and earliest-affected date, plus fresh/stale/unavailable preview choices and an initially disabled Save control ([usage]:354–384). A complete affected age band is not explicit (line 372 gives only the earliest date); real response binding and confirmation remain application acceptance rather than demonstrated backend behavior. |
| [OPEN] AC7 / T6: capped export banner on unnarrowed export | Page 32 shows a 10,000-row cap and narrowing guidance ([audit]:71,120,219–232). It does not establish truncation of a real unnarrowed result or the exact capped-result copy. **Authority gap:** feature plan line 103 uses client cursor fetches capped at 10,000; [overview]:375 specifies a server export endpoint with max 100,000 and bounded range. Higher authority wins; settle this before wiring export, without inventing an endpoint here. |
| [OPEN] AC8 / T6: support chip plus tooltip with grant id/expiry | Page 32 has the support chip and detail drawer ([audit]:144,206–208), but the chip is not a grant-id/expiry tooltip. Preserve accessible attribution and resolve the tooltip requirement during implementation. |
| [OPEN] AC9–AC11 / T9/T8/T10: security events, webhook settings, support grants/banner | These are not primary destination tabs ([S]:158–174); the audited support row is not a support-grant administration screen or persistent response-header banner. Map their phase/permission-aware entry points before those feature tasks; do not add speculative tabs to this port. |
| [OPEN] AC12 / T11: shell quota badge from warned/blocked timestamps | Page 31 has resource-state specimens ([usage]:235–295), but the primary topbar has no summary-driven warn/block quota link ([S]:20–29). Missing-summary suppression and response-driven shell state remain application acceptance. |
| [OPEN] Primary state completeness | `no-project` and `no-tabs` are declared without distinct specimens; 412 has copy but no actionable reload. The G2/G4 mobile destinations are also absent from the select. These source gaps must not be described as implemented behavior. |
| [OPEN] Approval and export evidence | This file does not record owner approval or replace the future W8/W9 accessibility/i18n checks. Visual, keyboard and screen-reader acceptance is not claimed: the screenshots are static captures of the v2 mockup; approval of the flow is the owner action recorded in the header. Locale routing is `/vi/` and `/en/` with profile preference ([binding rulings]:97), not the mockup's attribute/localStorage implementation. |

[S]: ../web-ui/output/27-settings-shell/evidgate-settings.html
[members]: ../web-ui/output/28-members-roles/evidgate-members-roles.html
[usage]: ../web-ui/output/31-quota-retention/evidgate-usage-retention.html
[ai]: ../web-ui/output/44-ai-settings/evidgate-ai-settings.html
[secrets]: ../web-ui/output/29-secret-references/evidgate-secret-references.html
[audit]: ../web-ui/output/32-audit-log/evidgate-audit-log.html
[NAV]: ../web-ui/output/NAV.md
[JS]: ../web-ui/output/assets/evidgate.js
[CSS]: ../web-ui/output/assets/evidgate.css
[00-components gallery]: ../web-ui/output/00-components/evidgate-components.html
[overview]: ../../../plans/00-overview.md
[settings plan]: ../../../plans/features/09-web-ui/09.7.1-administer-without-seeing-secrets-acceptance-an.md
[phase plan]: ../../../plans/13-product-phases.md
[binding rulings]: ../../../plans/features/README.md
[AC9 and T11.1]: ../../../plans/features/09-web-ui/09.1.1-app-scaffold-and-build-acceptance-npm.md
[B2 gate]: ../web-ui/prompts-crd2/00-README.md
[CR-D-2 screen map]: ../web-ui/prompts-crd2/00-README.md
