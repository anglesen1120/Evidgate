Status: approved 2026-11-19 by the coordinator on the owner's delegation (owner may override); [OPEN] defaults accepted as recorded in docs/status-carried.md

# Authoring design-to-code handover

Flow id: `authoring-handover`. CR-D-2 phase: **G1**.

This is the component, task and string-ownership map required by
[13.4.1 §10, T1.4](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md#10-tasks-sub-tasks-and-re-estimate).
Legend: ✅ linked source fact · ⚠️ unverified runtime/visual evidence ·
`[INFERENCE]` interpretation · `[DECISION]` recorded policy · `[OPEN]` unresolved
contract, naming or evidence gap. Approval accepts the recorded defaults; it does
not manufacture missing contracts or certify runtime accessibility. The
[review record](authoring-review.md) distinguishes written-design corrections
from implementation evidence. Carried defaults remain in
[status-carried.md](../../status-carried.md).

## 1. Design-to-task map

Task ids below are **13.4.1** sub-tasks from the
[feature plan §10](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md#10-tasks-sub-tasks-and-re-estimate).
T6.1 and T6.2 are verification receivers, not additional screen implementations.
State requirements come from
[plan §6](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md#6-states-and-transitions)
and each linked specification. Static reference cards are not evidence that those
states have run.

Routes below are application destinations, not API paths. The
[registry](../../../apps/web/src/app/shell/nav/nav-registry.ts) supplies scope and
segments: project `authoring`, `generation`, `pending-changes`, `overview`,
`ai-settings`; org `onboarding`, `settings`, `usage` (entry id `quota`).
The plan explicitly fixes `/orgs/:orgId/usage` as the credit destination.
`[OPEN]` child destinations below are not new registered routes; their exact
paths must not be inferred from mockup filenames. Generated SDK operations own
API path construction, as required by plan §5.2.

| Design source | Building tasks and verification receivers | Routes / placement | States covered | Open gaps and safe defaults |
| --- | --- | --- | --- | --- |
| [authoring-tree.md](authoring-tree.md) | 13.4.1-T2.1 data/store; 13.4.1-T2.2 hierarchy; 13.4.1-T2.3 table/selection; 13.4.1-T2.4 states/a11y/i18n; 13.4.1-T6.1–T6.2 | `/projects/:projectId/authoring`; selected node, filters and cursor in URL query | Independent tree/table loading and error; empty project/category; no matches; 403; stale refresh; deep/long content; restricted creation/selection; keyset/repeated-cursor failure | [OPEN] OI-13-501 source/owner filters: unavailable with explanation until server support; OI-13-502 selection export: never widen scope; OI-13-503 pending count: `—`, not zero. Source/owner/provenance, aggregate totals and some dedicated specimens remain gaps. |
| [authoring-editor.md](authoring-editor.md) | 13.4.1-T3.1 form; 13.4.1-T3.2 conflict integration; 13.4.1-T3.3 checklist review; 13.4.1-T3.4 a11y/i18n/adversarial; 13.4.1-T5.2 Timeline; 13.4.1-T6.1–T6.2 | Case/new-case destination within project authoring; Editor, Checklist review and Timeline tabs. [OPEN] exact child URL is not a registry segment | New/invalid/dirty; save pending/success/error/no-op; draft-preserving 412; queued changes; four AI refusals; immutable Timeline first page/paging/empty/restore confirmation/restored/read-only | [OPEN] OI-13-503 badge count; write/restore reason transport; parent/source/author/provenance projections; current-value read after 412; exact version-id presentation; per-case AI integration. Never invent a reason property, current snapshot or version. |
| [authoring-pending-changes.md](authoring-pending-changes.md) | 13.4.1-T3.3 queue/preview/apply; 13.4.1-T3.2 conflict reuse; 13.4.1-T3.4; 13.4.1-T6.1–T6.2 | `/projects/:projectId/pending-changes` | Empty; comment/merge/split/delete; long comment; invalid previews; applying/success/error; duplicate intent; retained-queue 412; revoked permission | [OPEN] Human labels/global totals; complete impact/before-after snapshots; unsupported batch reason; G3 proposal operation; retained-body specimens. Apply is one atomic batch, not necessarily one entity-version row; comment-only receipt may have none. |
| [authoring-generation.md](authoring-generation.md) | 13.4.1-T4.1 intake/preflight; 13.4.1-T4.2 progress/review/accept; 13.4.1-T6.1–T6.2. Role selector handoff: 13.2.2-T6 | `/projects/:projectId/generation`; Input/Estimate/Progress/Checklist/Test cases are in-page stages, not registry routes | Attachment empty/oversized/unsupported/scanning/rejected; extraction failure; opt-in off; exhausted/unavailable estimate; cache hit; gateway/schema failure; partial results; review pending/ready; supplied lifecycle/stages and selector refusals | [OPEN] Intake/estimate/detail/accept/profile contracts and pre-accept draft-read linkage; cache-hit/profile/partial-result specimens. OI-13-505/507/508 are plan handovers, not newly invented endpoints or progress values; see below. |
| [authoring-ai-settings.md](authoring-ai-settings.md) | 13.4.1-T5.1 guidance/history/template/conflict; 13.4.1-T6.1–T6.2. Adjacent profile editor is built by 13.2.2-T6, not this story | `/projects/:projectId/ai-settings`; Settings/Version history tabs. [OPEN] sibling role-profile editor URL is not registered here | Effective defaults v0; dirty draft; below/at/above limits; template preview/cancel; save pending/error/success/no-op; retained-draft 412; immutable read-only history; opt-in off; profile delta states | [OPEN] OI-13-509 grant alignment and evidence; display-name/consent/profile contracts; default/no-op/stale/readable-no-write specimens; complete conflict comparison. Current registry already says `authoring:read`; older design references to `project:read` are historical, not a new instruction to undo it. Save still needs `project:write`. |
| [authoring-import-export.md](authoring-import-export.md) | 13.4.1-T5.3 import preview/write and export; 13.4.1-T6.1–T6.2 | Linked from project authoring; Import/Export tabs. [OPEN] exact child URL is not a registry segment | No file; unsupported/malformed; preview loading; row errors; duplicate keys; wrong prefix; mixed invalid rows; denied; success/no-op; import failure; no selection; empty/unavailable export | [OPEN] OI-13-506: keep preview and write in T5.3; no silent T5.4. OI-13-502: selected/filtered export unavailable until expressible. Local parsed cells are unvalidated; invalid-file details are not exhaustive totals; exact export count/source version require real data. |
| [authoring-usage-credits.md](authoring-usage-credits.md) | 13.4.1-T7.2 credit region; 13.4.1-T6.2 grants/a11y/built navigation coverage | `/orgs/:orgId/usage`, org `quota` entry, `usage:read`; never the Settings `quota-retention` placeholder | Present/confirmed zero/unavailable balance; no usage/no period matches; estimate-backed opt-in off; hidden nav/typed forbidden without grant; scoped loading/error/stale/429/503 | [OPEN] OI-13-504 credit response/period/as-of/availability and precision; supported filtering and partial-response semantics; initial/zero/unavailable/opt-in/stale specimens. One balance, null-not-zero, G1 actions only, no money. |
| [authoring-nav-phase.md](authoring-nav-phase.md) | 13.4.1-T7.1 phase navigation; 13.4.1-T7.2 single credit placement; 13.4.1-T5.1 AI gate; 13.4.1-T6.2 | Shared shell across registry destinations; related deltas: org `/onboarding`, project `/overview`, org `/settings`, project `/ai-settings`, org `/usage`, each under its corresponding scope prefix | Identity loading/ready/empty/search-no-match/error/stale; permission filtering; G1–G4 hidden and unmatched destinations; rail/mobile drawer; safe focus after filtering | [OPEN] OI-13-503 pending badges; OI-13-504 credit payload; OI-13-509 historical gate mismatch/evidence. Five-step onboarding, home counts, hidden-entry search/identity source and dedicated shell specimens remain named gaps. Never ship a phase switch or duplicate the home balance. |
| [authoring-states.md](authoring-states.md) | 13.4.1-T2.4; 13.4.1-T3.4; 13.4.1-T4.1–T4.2; 13.4.1-T5.1–T5.3; 13.4.1-T6.1–T6.2; 13.4.1-T7.1–T7.2 | Cross-surface matrix for the destinations above; no separate page | All eight plan §6 surfaces; shared request machine; retained stale/offline-ish drafts; generation lifecycle; bilingual outcome/extraction copy; keyboard/focus/mobile and manual WCAG checklist | [OPEN] D1–D8 contract/provenance gaps; dedicated state/AT evidence; alternate proposed component names below. OI-13-501/503 correspond to D6, OI-13-502 to D7, OI-13-504 to D5; the sheet does not close those receiver contracts. |
| [authoring-review.md](authoring-review.md) | 13.4.1-T2.4; 13.4.1-T3.4; 13.4.1-T4.1–T4.2; 13.4.1-T5.1–T5.3; 13.4.1-T6.1–T6.2; 13.4.1-T7.1–T7.2 implement/verify recorded deltas | Review of all above surfaces and references; no runtime route | Eight-surface coverage audit and shared/nav rules; AR-01–AR-07 target sizing, identifiers, resize focus, Cancel focus, Unicode, live counters and localized names | ⚠️ Written-design corrections do not fix frozen HTML or prove production behavior. Fresh AT/focus/Unicode/long-content/grant/persistence evidence remains open, alongside each screen's contract gaps. Historical approval-pending prose does not override the dated first-line approval. |

### Open-item boundary

The numbered receiver register is
[feature-plan Notes](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md#notes-decisions-and-open-items).
The screen **Open points** tables directly name OI-13-501, OI-13-502,
OI-13-503, OI-13-504, OI-13-506 and OI-13-509; do not invent additional
screen open-item rows merely to fill the numbering sequence.

- **OI-13-501:** server source/owner filters; receiver 13.1.1-T2.
- **OI-13-502:** exact selected-id export; receiver 13.1.1-T5.
- **OI-13-503:** pending counts only, not missing per-case coverage; receiver
  13.1.1-T2 with 13.1.2 as data owner.
- **OI-13-504:** org AI-credit shape; receiver 08.5.5-T5.2.
- **OI-13-505:** handed to 13.3.1; estimate `opt_in` is authoritative.
  Generation/editor/usage must not add a second consent read.
- **OI-13-506:** retain import preview inside 13.4.1-T5.3 under the accepted
  default. A separately estimated T5.4 requires an explicit amendment.
- **OI-13-507 / OI-13-508:** former first-draft questions now described by the
  plan as generation route-shape facts: monotonic `queued → redact → generate →
  validate`, optional `eta_class`, server timestamps and detail `version`/`ETag`.
  They are not evidence that missing OpenAPI shapes have landed; generation and
  the state sheet retain that integration gap. No percentage or guessed ETA.
- **OI-13-509:** T5.1 owns entry/read `authoring:read` and Save `project:write`
  alignment. The current registry reflects the entry default; permission-state
  acceptance evidence is still required, not implied by that single row.

## 2. Component reuse per screen

✅ Existing names below come from the
[shared component catalogue (A15)](../../engineering/ui-rules.md#shared-component-catalogue-a15)
and the linked screen's **Regions** section. `app-data-table` family means
`app-data-table`, `app-data-table-cell`, `app-data-table-records` and
`app-data-table-value`, not a second table implementation. The existing virtual
viewport applies above 200 loaded rows; keyset fetching remains separate.
Owned Helm primitives are imported through `@evidgate/ui/<family>`; dialogs are
those owned families, not an invented `app-dialog`.

All screen proposals in the final column are under `shared/ui/authoring/`;
these names are **new presentational proposals**, not assertions that components
already exist. They accept inputs/events and contain no feature store, SDK
client or data service. Table cells may use the existing value/records helpers;
chips express supplied status/revision/hash, not fabricated outcomes.

| Screen / source | Existing A15 reuse | Proposed new names from that source |
| --- | --- | --- |
| [Tree/table](authoring-tree.md#regions) | `app-page-header`, `app-section-header`, `app-data-table` family, `app-request-state`, `app-inline-feedback`, `app-retry-after`, `app-status-pill`, `app-revision-chip`, `app-copyable-identifier`, `app-desktop-required-notice`, `[appDesktopMutation]`; owned Helm buttons/inputs/selects/checkboxes/cards/tooltips | `feature-tree`, `case-filters`, `coverage-summary`, `visible-selection` |
| [Editor/checklist/Timeline](authoring-editor.md#regions) | `app-page-header`, `app-section-header`, `app-request-state`, `app-inline-feedback`, `app-retry-after`, `app-conflict-dialog`, `app-status-pill`, `app-revision-chip`, `app-copyable-identifier`, `app-number-value`, `app-absolute-time`, `app-form-error-summary`, `app-field-feedback`, `app-field-requirement`, `app-desktop-required-notice`, `[appDesktopMutation]`; owned Helm tabs/form controls/buttons/dialogs | `case-editor-form`, `step-list`, `ai-preflight`, `version-timeline`, `version-diff`, `queued-changes` |
| [Pending changes](authoring-pending-changes.md#regions) | `app-page-header`, `app-section-header`, `app-data-table`, `app-data-table-value`, `app-copyable-identifier`, `app-revision-chip`, `app-status-pill`, `app-request-state`, `app-inline-feedback`, `app-conflict-dialog`, `app-retry-after`, `app-form-error-summary`, `app-field-feedback`, `app-absolute-time`, `app-desktop-required-notice`, `[appDesktopMutation]`; owned Helm tabs/dialogs/buttons | `change-preview`, `batch-summary` |
| [Generation](authoring-generation.md#regions) | `app-page-header`, `app-section-header`, `app-request-state`, `app-loading-pattern`, `app-empty-pattern`, `app-error-pattern`, `app-forbidden-pattern`, `app-not-found-pattern`, `app-inline-feedback`, `app-status-pill`, `app-run-state-pill`, `app-revision-chip`, `app-hash-chip`, `app-copyable-identifier`, `app-number-value`, `app-data-table`, `app-data-table-value`, `app-conflict-dialog`, `app-retry-after`, `app-field-feedback`, `app-form-error-summary`, `app-absolute-time`, `app-desktop-required-notice`, `[appDesktopMutation]`; owned Helm controls/tabs/dialogs/alerts | `credit-estimate`, `generation-progress`, `role-profile-select`, `generated-result` |
| [AI settings](authoring-ai-settings.md#regions) | `app-page-header`, `app-section-header`, `app-request-state`, `app-inline-feedback`, `app-conflict-dialog`, `app-retry-after`, `app-revision-chip`, `app-hash-chip`, `app-copyable-identifier`, `app-number-value`, `app-absolute-time`, `app-field-feedback`, `app-form-error-summary`, `app-data-table` family where needed, `app-desktop-required-notice`, `[appDesktopMutation]`; owned Helm fields/tabs/dialogs/buttons | `character-counter`, `version-diff`, `template-preview`, `role-profile-summary` (profile-editor receiver: 13.2.2-T6) |
| [Import/export](authoring-import-export.md#regions) | `app-page-header`, `app-section-header`, `app-request-state`, `app-inline-feedback`, `app-data-table` family, `app-number-value`, `app-copyable-identifier`, `app-absolute-time`, `app-field-feedback`, `app-form-error-summary`, `app-revision-chip`, `app-desktop-required-notice`, `[appDesktopMutation]`; owned Helm controls/tabs/dialogs | `import-row-table`, `scope-summary`, `row-error-summary` |
| [Usage credits](authoring-usage-credits.md#regions) | `app-page-header`, `app-section-header`, `app-request-state`, `app-inline-feedback`, `app-retry-after`, `app-number-value`, `app-data-table`, `app-data-table-records`, `app-data-table-value`, `app-absolute-time`, `app-desktop-required-notice`; owned Helm selects/buttons/cards/alerts | `credit-summary`, `credit-usage-table` |
| [Phase navigation](authoring-nav-phase.md#regions) | `app-page-header`, `[appPageHeaderActions]`, `app-section-header`, `[appSectionHeaderActions]`, `app-request-state`, `app-inline-feedback`, `app-forbidden-pattern`, `app-not-found-pattern`, `app-absolute-time`, `app-number-value`, `app-data-table` where applicable, `app-desktop-required-notice`; existing shell and owned Helm dropdown-menu/buttons/select | `nav-phase-group`, `credit-summary` (shared with usage; no duplicate numeric balance on home) |
| [Shared state sheet](authoring-states.md#regions) | The sheet reuses the existing request/table/conflict/form/chip/time/narrow helpers above; it is not a new runtime screen | Also proposes `feature-hierarchy`, `change-preview`, `generation-progress`, `provenance`, `credit-preflight`; naming overlap is explicit below |
| [Review record](authoring-review.md) | No runtime component: its AR-01–AR-07 requirements apply to the reused controls and screen proposals above | None; review evidence is not a component family |

`version-diff`, `credit-summary`, `change-preview` and `generation-progress` each
remain one proposed shared name where repeated. Do not fork copies per screen.

[OPEN] The shared state sheet uses `feature-hierarchy` where the tree screen
proposes `feature-tree`, and `credit-preflight` alongside the editor's
`ai-preflight` and generation's `credit-estimate`. It also proposes `provenance`
without assigning a separate component in each screen. This map preserves those
source names rather than inventing aliases or asserting interchangeable APIs.
T2.2 and T4.1 must raise a handover amendment if one proposed boundary replaces
another; they must not implement duplicate wrappers or silently settle the gap
inside feature code. Reuse the screen-specific proposals above for their named
regions; shared extracted boundaries require recorded resolution.

## 3. Explicit i18n id ownership

✅ [T1.4](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md#10-tasks-sub-tasks-and-re-estimate)
reserves `@@authoring.*`, `@@settings.ai.*` and `@@usage.credits.*`.
The registry already owns `@@shell.nav.*`. The following is namespace ownership,
not a claim that new ids/translations have been extracted. The designs do not
supply a complete per-string suffix list; no such list is invented here.

| Namespace | Screen owner and receiving task | Content owned / boundary |
| --- | --- | --- |
| `@@authoring.*` | Tree/table T2.4; editor/checklist/pending T3.4; generation T4.1–T4.2; Timeline T5.2; import/export T5.3 | Screen titles, filters, visible-only selection and accessible names, draft/save/conflict context, queue/preview/apply, intake/estimate/progress/accept, provenance labels, Timeline/restore, import rows/errors and exact export scope. Shared authoring state/copy follows the state sheet, not separate translations per page. |
| `@@settings.ai.*` | AI guidance T5.1; adjacent role-profile editor handoff to 13.2.2-T6 | Guidance fields, counters/limit transitions, defaults, template language/preview/replacement, immutable settings history/diff, opt-in explanation and settings-specific save/conflict context. Profile-editor delta stays beside settings; selector and single-run note remain generation content under `@@authoring.*`. |
| `@@usage.credits.*` | Org usage credit region T7.2 | Balance/cap/UTC period/as-of labels, unavailable versus confirmed zero, G1 action labels, supported-period controls, empty/no-match and scoped retry/opt-in explanations. No project balance copy or money vocabulary. |
| `@@shell.nav.*` | Shared navigation T7.1; AI entry alignment coordinated with T5.1 | Existing group/entry/short labels and navigation accessibility copy. Preserve shipped `@@shell.nav.group.design`, `.runs`, `.learn`, `.release`, `.settings`; do not retranslate them from screen headings. Existing entries include `@@shell.nav.authoring`, `.generation`, `.pending-changes`, `.ai-settings`, `.quota` and `.quota-short`. |

Existing shared-component ids stay with their current components; a screen does
not rename shared Cancel/Close, request-state or conflict primitives into its
own namespace. Screen-specific surrounding strings use the owning namespace.
Every new string needs an explicit id and Vietnamese catalogue entry under
[AC15](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md#2-user-stories-and-acceptance-criteria).
Visible and accessible names must follow the active vi/en interface locale.

The [state sheet's copy tables](authoring-states.md#copy-length-vi-vs-en) own the
outcome/extraction sentences and unknown-code fallback. Codes, supplied ids,
version references and server timestamps remain escaped data, not translated
ids. Interface language does not choose template language or generated-output
language. Credit zero needs a confirmed value; a cache-hit zero needs its reuse
reason, not a different tariff sentence invented by the screen.

## 4. Implementation reference rule and acceptance handoff

**13.4.1-T2, 13.4.1-T3, 13.4.1-T4, 13.4.1-T5, 13.4.1-T6 and 13.4.1-T7 must
reference this file instead of re-deciding a component or an id.** Follow the
linked screen for behavior, this map for reuse/ownership, A15 for existing
building blocks, and the owning contract for data. A source disagreement or
missing route/schema remains `[OPEN]`; it is not permission to create a fallback
DTO, broaden export scope, duplicate the sidebar or silently rename an id.

Acceptance receivers, using the
[feature AC list](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md#2-user-stories-and-acceptance-criteria):

- **AC1:** dated approval headers and the independent review are design inputs,
  not proof of any UI implementation acceptance.
- **AC2–AC4:** T2 owns deep native hierarchy, URL-restored server keyset filters,
  visible-row-only selection and honest unsupported export scope.
- **AC5–AC6:** T3 owns draft-preserving case conflict and atomic pending apply
  with the correct read/batch ETag and one idempotency intent.
- **AC7–AC9, AC17:** T4 owns confirmed estimate before either AI call, honest
  polling/lifecycle, escaped provenance, authorized draft reads before accept,
  exact partial accept, stale accept applying nothing and the identical
  cache-hit review/concurrency path.
- **AC10–AC12:** T5 owns immutable append-only Timeline/restore, Unicode-aware
  settings boundaries/history and truthful import/export scope/errors/counts.
- **AC13:** T6.1 owns fixtures/vi flows and en spot checks; T6.2 owns built-app
  keyboard, axe, hostile text, limited grants and phase hiding. Verification is
  not replaced by static design cards.
- **AC14:** T7 owns journey order, later phases hidden and unmatched, no phase
  switch, and one org credit region behind `usage:read`.
- **AC15–AC16:** all building tasks own explicit ids and WCAG 2.2 AA in vi/en,
  light/dark at 1280/360 px. Apply the state-sheet checklist and AR-01–AR-07
  deltas, including target size, unobscured focus, no drag-only action, resize
  cancellation/focus handoff, localized Cancel-first dialogs and non-spamming
  Unicode counters. The review's open runtime/AT evidence stays visible.

This handover does not add routes, task estimates, contract fields or acceptance
waivers. The role-profile editor remains 13.2.2-T6's implementation, and import
preview remains T5.3 under the recorded default.
