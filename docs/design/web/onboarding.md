# Onboarding

Owner approval: pending (B2 / 09.1.1-T11.1)

Flow id: `onboarding`

CR-D-2 phase: **G1**, with a separately gated G2 continuation ([NAV.md:34–35,55][nav]).

Feature tasks: `09.2.1-T1` (wizard shell and resource-derived resume); `09.2.1-T2`–`T5` (connection, sample run, comparison and enablement), `T7` (connection health/CLI). The G1 authoring destinations are `13.4.1-T3`–`T5` and phase navigation is `13.4.1-T7`. These are existing task ids, not new scope: [09.2.1 §10][feature] and [13-product-phases.md:79,154–164][phases]. `09.2.1-T2`–`T8` are G2; the older feature-plan milestone does not override that phase map.

This specification documents the supplied Web UI v2 mockups; it introduces no replacement wireframe or visual system. Exported images are the primary page at 1280×800, phase G4 so its G2 continuation is visible. Their inclusion is not owner approval or evidence of a working Angular feature.

## v2 pages

| Page | Role | Phase | Valid `?state=` values from the page |
| --- | --- | --- | --- |
| [23 · Onboarding wizard][primary] | Primary four-step G1 setup and separate G2 continuation | G1; G2 region | `default,loading,empty,error,403,404,401,429,412` (`evidgate-onboarding.html:77`) |
| [24 · Authoring home][home] | Project coverage, resource-derived resume, pending changes, credits; secondary CI health | G1; G2 region | `default,loading,empty,no-match,error,403,404,401,429,412` (`evidgate-project-overview.html:78`) |
| [44 · Project AI settings][ai] | Versioned guidance, template preview, save and history; not tenant consent | G1 | `default,loading,empty,error,403,404,401,412,429,submitting,saved,validation-error` (`evidgate-ai-settings.html:77`) |

Phase source: [NAV.md:34–35,55][nav]. Review states are local specimens, not HTTP requests: [assets/evidgate.js:378–411,530–572,2250–2259][js] reads the declaration, changes visibility, updates read-only/mutation flags and accepts only declared query states. Theme and locale use the shared review controls, with persisted `evidgate-theme` and `evidgate-locale` ([assets/evidgate.js:415–429][js]).

## Regions

Reading order follows [23-onboarding-wizard/evidgate-onboarding.html][primary], not a new drawing.

| Order | Region and source | Existing implementation / gallery components |
| --- | --- | --- |
| 1 | Skip link, project navigation and top bar (`:12–47`) | `.eg-skip-link`, `.eg-shell`, `[data-eg-nav]`, `.eg-topbar`, scope badge, search, account menu and shortcuts dialog. Shared shell/navigation/topbar specimens in [00-components][components]. |
| 2 | Breadcrumbs, narrow-screen read-only notice, page identity and return action (`:48–76`) | `.eg-breadcrumbs`, `.eg-banner--desktop`, `.eg-page-header`; title “Create your first reviewed checklist”; back-to-authoring-home secondary link. |
| 3 | Review controls and mutually selected state surface (`:77–127`) | `[data-eg-review]`, `[data-eg-show-state]`, `[data-eg-hide-state]`; `.eg-skeleton`, `.eg-state`, `.eg-btn`. These review controls are explicitly not product navigation (`assets/evidgate.js:403–410`). |
| 4 | Setup progress (`:129–146`) | `.eg-card`, `.eg-stepper`, `.eg-progress`; project complete, AI settings current, requirement/checklist upcoming; current step `2 / 4`, completed progress `1 / 4`. Viewing a tab does not complete a step. |
| 5 | Step details (`:148–259`) | `.eg-tabs--vertical`, `[data-eg-tabs]`, `[data-eg-tab-select]`, four `role="tabpanel"` bodies. Project fields and requirement entry use `.eg-form`, `.eg-field`, `.eg-input`, `.eg-select`, `.eg-textarea`, `[data-eg-mutate]` and `.eg-readonly-summary`. AI settings show saved `ais-3`, draft open and consent off. Checklist shows a manual four-item table plus Open review, not automatic approval. |
| 6 | Created resources (`:261–283`) | `.eg-card`, `.eg-table--compact`, badges and resume links: project exists, saved settings plus unsaved draft, requirement not saved, checklist pending review. |
| 7 | Continue without CI (`:285–306`) | Three `.eg-kpi` blocks for estimate, tenant balance and opt-in; links to AI settings, authoring tree and generation. No CI prerequisite for G1. |
| 8 | G2 continuation (`:308–332`) | `.eg-card[data-eg-phase="G2"]`, repository/default-branch fields, binding/sample-run/comparison summaries and project-overview link. Hidden before G2, not an unfinished G1 step. |
| 9 | Fixture footer and shortcuts overlay (`:336–353`) | Illustrative-data notice and native `.eg-dialog` with close controls. |

Gallery reuse is visual, not a claim that Angular components already exist: shell/page header, buttons/badges/chips, tabs/table, state/skeleton, banner/dialog, stepper/progress and forms are represented in [00-components:151–224,253–323,381–530,577–637,825,1133][components]. Tabs use selected-state semantics, roving tabindex, arrow keys and Home/End ([assets/evidgate.js:884–937][js]).

Related-page continuity: [24:149–168,207–224,267–325][home] keeps authoring coverage separate from release quality, resumes at AI settings, and only at G2 shows prefix-only token metadata and a secret-free CLI snippet. [44:116–150,218–253][ai] retains a conflicting draft, offers explicit Reload base values, validates capped fields and appends versions; it never turns saved project guidance into tenant AI consent.

## States

Each row contains one image group, ordered **light-vi, dark-vi, light-en, dark-en**. All six exports use the primary page, including its unchanged reference defects noted under Open points.

| State | What the mockup shows and evidence | Available actions | HTTP / problem meaning | Exported screenshots |
| --- | --- | --- | --- | --- |
| `default` | Four-step setup, AI-settings draft selected, resource ledger and separate G2 continuation; [evidgate-onboarding.html:129–332][primary]. | Inspect steps; back to authoring home; open project, AI settings, manual checklist review, authoring tree or generation. Tab changes are local inspection, not a save. | Normal resource view; no HTTP success or completed wizard is implied by choosing this specimen. | ![onboarding default light vi](images/onboarding/default-light-vi.png) ![onboarding default dark vi](images/onboarding/default-dark-vi.png) ![onboarding default light en](images/onboarding/default-light-en.png) ![onboarding default dark en](images/onboarding/default-dark-en.png) |
| `loading` | Busy skeleton card plus three skeleton tiles; live-status text, with default content hidden; [evidgate-onboarding.html:77–88,127][primary]. | Shared navigation and Back to authoring home remain; no step-body mutation. | Resource fetch unresolved, not an empty project, saved settings or successful outcome. | ![onboarding loading light vi](images/onboarding/loading-light-vi.png) ![onboarding loading dark vi](images/onboarding/loading-dark-vi.png) ![onboarding loading light en](images/onboarding/loading-light-en.png) ![onboarding loading dark en](images/onboarding/loading-dark-en.png) |
| `empty` | “No setup started”; create a project and defer repository/CI to G2; [evidgate-onboarding.html:90–94][primary]. Direct `?state=empty` also rewrites the header to “New manual case” through the shared JS defect ([assets/evidgate.js:2250–2304][js]). | No create-project CTA is present inside this state. Back to authoring home and shared navigation remain. | Successful absence of setup resources, not a 404 or a finished wizard. Header rewrite is not the intended onboarding meaning. | ![onboarding empty light vi](images/onboarding/empty-light-vi.png) ![onboarding empty dark vi](images/onboarding/empty-dark-vi.png) ![onboarding empty light en](images/onboarding/empty-light-en.png) ![onboarding empty dark en](images/onboarding/empty-dark-en.png) |
| `error` | “The page could not be loaded”; current scope retained, no draft merged or resubmitted; [evidgate-onboarding.html:95–100][primary]. | Retry (`data-eg-set-state="default"`) and back/navigation. Retry changes the local specimen; it is not an API retry implementation. | Failed load; the mockup does not assign a particular HTTP status or show a typed error envelope. | ![onboarding error light vi](images/onboarding/error-light-vi.png) ![onboarding error dark vi](images/onboarding/error-dark-vi.png) ![onboarding error light en](images/onboarding/error-light-en.png) ![onboarding error dark en](images/onboarding/error-dark-en.png) |
| `403` | Lock icon, “You do not have access”, ask a project owner; default body hidden; [evidgate-onboarding.html:101–105,127][primary]. JS marks read-only; CSS hides `[data-eg-mutate]` rather than leaving its controls visible ([assets/evidgate.js:562–567][js]; [assets/evidgate.css:3715–3717][css]). | Back/navigation and review controls remain. No access-request button or visible disabled step controls are provided in this specimen. | Forbidden: required permission missing. Do not use this for foreign-tenant ids: those use the declared `404` pattern. Feature-plan disabled-not-hidden behavior remains an open mismatch. | ![onboarding 403 light vi](images/onboarding/403-light-vi.png) ![onboarding 403 dark vi](images/onboarding/403-dark-vi.png) ![onboarding 403 light en](images/onboarding/403-light-en.png) ![onboarding 403 dark en](images/onboarding/403-dark-en.png) |
| `412` | “Review conflict”; changed edit target, retained draft, explicit Reload described, no auto-merge; [evidgate-onboarding.html:116–120,127][primary]. The primary state itself has no Reload button. | Back/navigation and review controls only in the conflict body; the related AI-settings page has Reload base values ([44:127][ai]). | Failed stale `ETag`/`If-Match` precondition. Preserve draft, expose current revision and require explicit reload; the primary specimen only explains this behavior. | ![onboarding 412 light vi](images/onboarding/412-light-vi.png) ![onboarding 412 dark vi](images/onboarding/412-dark-vi.png) ![onboarding 412 light en](images/onboarding/412-light-en.png) ![onboarding 412 dark en](images/onboarding/412-dark-en.png) |

HTTP authority: [00-overview.md:325,330–336][overview] defines stale preconditions and the error envelope (`code`, `message`, `request_id`, `retryable`, `details`), 403 forbidden and 404 also for other-tenant ids. This is the app contract, not evidence that a static state sends a request. State styling is the existing `.eg-state` surface: neutral empty, danger error/permission/not-found icons and warning conflict icon ([assets/evidgate.css:3063–3139][css]); an error tone is not a test verdict.

Additional declared primary-page states:

- `404` — not found / not accessible with no existence disclosure (`evidgate-onboarding.html:106–110`); use this for a foreign-tenant id, never the 403 copy.
- `401` — session expired; sign in again, retained drafts do not auto-submit (`:111–115`); copy only, no sign-in action in the state body.
- `429` — server requests waiting; unchanged values and no automatic retry (`:121–125`); the production client must honor the server's `Retry-After` per 00 §9.2.

Related AI-settings-only states, not aliases for primary-page exports: `submitting` is a local no-request save specimen (`44:228–230`); `saved` is the declared form-success state (`assets/evidgate.js:1555–1558`); `validation-error` explicitly blocks an over-limit save (`44:232–238`). Authoring home additionally declares `no-match` (`24:78`).

## Copy length (vi vs en)

Measured from the primary HTML with a throwaway read-only Python `HTMLParser` pass. Scope: localized `data-vi` / `data-en` strings in headings (`h1`–`h6`), buttons / `.eg-btn` links, table headers and badges, including inactive tab bodies. HTML is decoded, tags stripped, whitespace collapsed and duplicate translation pairs removed; counts are Unicode code points without normalization, not pixels or grapheme clusters. Injected review-control labels, paragraphs, field hints and select options are outside this specific measurement. Ratio = vi count / matching en count.

| Source line | Longest Vietnamese strings (quoted mockup copy) | vi chars | Matching English | en chars | vi / en |
| --- | --- | --- | --- | --- | --- |
| 69 | “Tạo danh sách kiểm tra được duyệt đầu tiên” | 42 | Create your first reviewed checklist | 36 | 1.167 |
| 234 | “Dùng danh sách kiểm tra thủ công” | 32 | Use manual checklist | 20 | 1.600 |
| 302 | “Bước ca kiểm thử của tạo sinh” | 29 | Generation test-case stage | 26 | 1.115 |
| 170 | “Danh sách kiểm tra đầu tiên” | 27 | First checklist | 15 | 1.800 |
| 74 | “Về trang chủ soạn kiểm thử” | 26 | Back to authoring home | 22 | 1.182 |

Wrapping/truncation rules read from CSS, not a rendered overflow certification: title and stepper labels wrap anywhere (`assets/evidgate.css:613–618,1716–1730`); badges wrap (`:1066–1069`); `.eg-btn__label` stays on one line (`:992–996`) while the header action group wraps whole controls (`:640–645`). Table headers are nowrap (`:2893–2899`) until mobile replaces them with cell labels. No primary-page title/button ellipsis is authored; account text and topbar scope have their own ellipsis rules (`:498–506,586–589`). The 1.800× tab label and 1.600× action show why a generic 30–40% expansion allowance alone is insufficient.

## Responsive notes

- **≥1280:** expanded sidebar by default (`assets/evidgate.js:281,2348`); progress steps can share a row (`assets/evidgate.css:1659–1675`), step details use vertical tabs, and three-column KPI summaries remain grids. Header actions may wrap as complete controls.
- **768–1279:** default icon rail; content padding changes and split panes collapse (`assets/evidgate.css:4029–4040`). This primary page keeps its vertical step tabs and multi-column KPI grids until the mobile breakpoint. Tables can scroll inside `.eg-table__wrap` if needed (`:2849–2872`); this is not permission to scroll the entire page.
- **<768:** drawer navigation; project scope hidden and search moves to a full row (`:4062–4082,4148–4165`). The step tablist becomes the labelled select (`:4085–4097`), stepper becomes vertical (`:4393–4399`), grids/forms stack (`:4357–4365`), tables become labelled cards (`:4266–4335`), and controls use the touch-height token (`:4215–4235`). `[data-eg-mutate]` is hidden and `.eg-readonly-summary` appears (`:4200–4207`); the primary read-only banner explains the limitation (`evidgate-onboarding.html:57–63`).
- **360 px requirement:** no horizontal page scroll is permitted by the product contract ([apps/web/README.md:20][web-readme]). These are source rules, not a claim of a viewport test; the G2 inputs and unmarked step-navigation actions need the explicit review listed below. See [layout.md](layout.md) for shared shell rules.

## Invariants

- G1 authoring never requires CI: four G1 steps and the explicitly separate `[data-eg-phase="G2"]` region ([evidgate-onboarding.html:69–70,288–331][primary]).
- Step selection is not persisted completion: progress derives from supplied resources, not local completion flags (`:132–144,264–278`), matching the resource-derived principle of T63 ([features/README.md:122][rulings]). Static inspection does not prove server resume works.
- AI guidance, tenant consent and checklist approval stay separate: saved `ais-3` is not an unsaved draft, opt-in remains off, manual work remains available and Open review does not mean approved (`:200–208,232–253,305`).
- Missing sample run / incomplete comparison never becomes PASS (`:325–327`); completeness, test outcome and release decision remain separate in related home CI metadata ([24:314–318][home]). No release verdict is invented in G1.
- `pending` must be neutral and verdict-free, never success/danger, per [09.1.1 AC8/T11.2][foundation]. The primary page has no evaluation-pending decision chip; its warning-colored **checklist review** status is a different domain (`:240,278`), not evidence that an evaluation chip satisfies the contract.
- No healing is applied here; links navigate to authoring/review and no apply-healing action exists in the documented regions. Static links do not authorize an API mutation.

## Open points

- [OPEN] **G1 design versus older five-step acceptance:** [09.2.1 AC1–AC5,AC7 and §6][feature] still require project → CI connection → sample run → comparison → audited advisory activation, while the primary v2 page has project → AI settings → requirement → checklist. CR-D-2 deliberately separates CI until G2 ([13-product-phases.md:79][phases]; [prompts-crd2/00-README.md:36][approval]). Port this v2 design without silently claiming the older ACs passed; the owner must reconcile the receiving task contracts before feature implementation.
- [OPEN] **Absent G2 completion surfaces:** the primary page's G2 block (`:308–332`) has no OIDC-versus-token creation flow, one-time secret copy, live sample-run progression, report-comparison step, advisory preview/confirm or enabled-and-collecting terminal state required by `09.2.1` AC2–AC5/AC7. Related home provides connection metadata/CLI only (`24:288–325`); it does not fill these gaps. AC6's no-evaluation `pending` decision specimen and AC8's mapping resolution are also absent from these three pages.
- [OPEN] **Resume/specimen consistency:** the ledger simultaneously shows an unsaved requirement and a checklist resource (`:277–278`), while step 3 remains upcoming; tab-forward buttons are enabled inspection controls, not resource-gated Next (`:234,253`). Do not interpret this fixture as proof of `09.2.1-T1.2`/`T1.3` server-derived progression.
- [OPEN] **Empty query header:** shared `applyEntryState` rewrites every `?state=empty` page to manual-case title, breadcrumbs, description and metadata (`assets/evidgate.js:2250–2304`). Keep exports faithful; a separate reference correction needs route scoping rather than pretending onboarding intentionally creates a manual case here.
- [OPEN] **403 visibility:** default bodies and marked mutations are hidden (`:127`; `assets/evidgate.css:3715–3717`), whereas `09.2.1` §8 requires corresponding forbidden actions disabled, not silently hidden. This page has no permission-specific disabled action examples. Related-page restrictions must not become a blanket assumption that all controls stay visible.
- [OPEN] **412 recovery:** primary conflict copy describes retained drafts and explicit Reload but has neither a reload control nor base/current revision detail (`:116–120`). Related AI settings supplies its own reload (`44:116–127`); that does not implement primary-page recovery or typed `error.details` rendering.
- [OPEN] **Narrow-screen read-only coverage:** G2 repository/branch inputs are not within `[data-eg-mutate]` (`:318–323`), and tab navigation/manual-checklist buttons remain unmarked (`:234,253`). [INFERENCE] CSS alone therefore does not establish that every visible editable input is read-only below 768 px. Do not claim the desktop-only mutation rule is fully implemented.
- [OPEN] **Fixture arithmetic:** the displayed estimate is 12 credits with “4 credits per checklist item” (`:295`) while the checklist has four items (`:246–249`); 4 × 4 = 16, not 12. Retain supplied values in exports and obtain a source correction; no pricing or credit rule is invented here.
- [OPEN] **Owner sign-off:** B2 / AC9 approval is not recorded by this document. The exact pending header remains until the owner acts; source and copy analysis do not constitute accessibility, responsive or behavioral acceptance.

[primary]: ../web-ui/output/23-onboarding-wizard/evidgate-onboarding.html
[home]: ../web-ui/output/24-project-connection/evidgate-project-overview.html
[ai]: ../web-ui/output/44-ai-settings/evidgate-ai-settings.html
[components]: ../web-ui/output/00-components/evidgate-components.html
[nav]: ../web-ui/output/NAV.md
[js]: ../web-ui/output/assets/evidgate.js
[css]: ../web-ui/output/assets/evidgate.css
[approval]: ../web-ui/prompts-crd2/00-README.md
[overview]: ../../../plans/00-overview.md
[phases]: ../../../plans/13-product-phases.md
[rulings]: ../../../plans/features/README.md
[feature]: ../../../plans/features/09-web-ui/09.2.1-onboarding-wizard-acceptance-a-new-tenant.md
[foundation]: ../../../plans/features/09-web-ui/09.1.1-app-scaffold-and-build-acceptance-npm.md
[web-readme]: ../../../apps/web/README.md
