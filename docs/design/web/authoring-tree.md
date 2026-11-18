Status: proposed, awaiting owner approval

# Authoring tree and manual-case table

Flow id: `authoring-tree`. CR-D-2 phase: **G1**.

Feature tasks: `13.4.1-T1.1`, `13.4.1-T1.2`, `13.4.1-T2`; related `13.1.1-T2`, `13.1.1-T4`, `13.1.1-T5`.

Legend: ✅ verified source with a link · ⚠️ unverified runtime/visual evidence · `[INFERENCE]` deduction · `[DECISION]` accepted policy · `[OPEN]` unresolved, with recommended default. This is a proposed interpretation of the supplied visual references, not implementation, a review record or owner approval. The design gate remains closed until independent design review and subsequent owner approval ([feature §2 AC1 and §10](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md)).

## v2 pages

| Page | Role | Phase | Declared `?state=` values |
| --- | --- | --- | --- |
| [40 · Feature tree](../web-ui/output/40-authoring-tree/evidgate-authoring-tree.html) | Primary hierarchy, manual-case table, filters and visible-row selection | G1 | `default`, `loading`, `empty`, `no-match`, `error`, `403`, `404`, `401`, `429`, `412` (HTML:89) |
| [41 · Editor](../web-ui/output/41-test-case-editor/evidgate-test-case-editor.html) | Case detail and Timeline; specified in [authoring-editor.md](authoring-editor.md) | G1 | See that specification; history is not a second tree panel. |
| [25 · Catalogue brief](../web-ui/prompts-crd2/B3-25-test-catalog.md) | G2 continuity for automated identities and uncovered manual cases | G2 | Related-page delta, not a G1 route entitlement. |

✅ Source: [tree brief](../web-ui/prompts-crd2/B2-40-feature-tree.md), [delta vocabulary](../web-ui/prompts-crd2/00-delta-brief.md#authoring-vocabulary-plan-13-epics-131134), [feature §5–6](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md). HTML query states are review specimens, not HTTP simulations. Independent-state cards at HTML:360–399 are static specimens inside `default`, not additional query values. Product chrome must not ship the review state/phase switcher.

Data trace inventory follows. Each schema path is relative to this file; comma-separated properties name individual members of that schema. `[OPEN]` means the field is absent, not that a mockup supplies a live value.

| Data field | openapi schema path | state (traced / [OPEN]) |
| --- | --- | --- |
| Project name, slug, code prefix and organization display | `../../../contracts/openapi.yaml#/components/schemas/ProjectDto.name`, `ProjectDto.slug`, `ProjectDto.case_prefix`; `OrganizationDto.name` | traced; use the server scope, never derive a prefix or organization from the mockup; shell identity/profile beyond these values is consumed from the existing shell, not redefined here |
| Node identity, parent, kind, title, sibling order and revision | `../../../contracts/openapi.yaml#/components/schemas/NodeDto.node_id`, `NodeDto.parent_id`, `NodeDto.kind`, `NodeDto.title`, `NodeDto.position`, `NodeDto.version` | traced; includes `sub_category`, which the illustrative five-level tree omits |
| Direct child/checklist counts | `../../../contracts/openapi.yaml#/components/schemas/NodeDto.child_count`, `NodeDto.item_count` | traced; these are not subtree manual-case totals |
| Root-first breadcrumb identity, kind and title; addressed node; next tree cursor | `../../../contracts/openapi.yaml#/components/schemas/AuthoringNodePathEntry.node_id`, `AuthoringNodePathEntry.kind`, `AuthoringNodePathEntry.title`; `AuthoringTreeResponse.node`, `AuthoringTreeResponse.path`, `AuthoringTreeResponse.next_cursor` | traced |
| Checklist identity, owning node, title, description, priority, lifecycle, source, order and revision | `../../../contracts/openapi.yaml#/components/schemas/ChecklistItemDto.item_id`, `ChecklistItemDto.node_id`, `ChecklistItemDto.title`, `ChecklistItemDto.description`, `ChecklistItemDto.priority`, `ChecklistItemDto.state`, `ChecklistItemDto.source`, `ChecklistItemDto.position`, `ChecklistItemDto.version` | traced; compose checklist children from their own paged read, not from invented `NodeDto` children |
| Case identity/code, title, parent ids, priority, lifecycle and revision | `../../../contracts/openapi.yaml#/components/schemas/ManualCaseListItemDto.case_id`, `ManualCaseListItemDto.code`, `ManualCaseListItemDto.title`, `ManualCaseListItemDto.checklist_item_id`, `ManualCaseListItemDto.node_id`, `ManualCaseListItemDto.priority`, `ManualCaseListItemDto.state`, `ManualCaseListItemDto.version` | traced; parent title comes from the checklist read |
| Coverage label/count and linked automated identities | `../../../contracts/openapi.yaml#/components/schemas/ManualCaseListItemDto.linked_live_count`, `ManualCaseListItemDto.linked_case_ids` | traced; positive count = covered, confirmed zero = uncovered; no execution result is implied |
| Next case/checklist cursors | `../../../contracts/openapi.yaml#/components/schemas/ManualCaseListResponse.next_cursor`, `ChecklistItemListResponse.next_cursor` | traced; no total count or page-number jump is returned |
| Current selected ids, mixed checkbox and selected/loaded count | `../../../contracts/openapi.yaml#/components/schemas/ManualCaseListItemDto.case_id`; `ManualCaseListResponse.items` | traced inputs; selection and loaded count are local presentation state, not server totals |
| Priority/status/coverage/free-text query values | Row enums above; `../../../contracts/openapi.yaml#/components/parameters/AuthoringPriorityFilter`, `AuthoringCaseStateFilter`, `AuthoringCoveredFilter`, `AuthoringQuery` | traced query parameters; add free-text control even though the fixture shows only five selects |
| Source filter and owner filter | No matching manual-case query parameters | [OPEN] OI-13-501: optional `source` and `owner_membership_id`; explain unsupported filtering until the owner ships them, never filter only one fetched page as if global |
| Row source, owner membership/display name, “Unassigned” and AI model/provenance | No members on `ManualCaseListItemDto` | [OPEN] recommend explicit source/owner/provenance projection; until then show unavailable, not “manual” or “unassigned”; membership display-name resolution also needs an authorized source |
| Pending-change badge on tree/table rows or header | No `pending_change_count` on `NodeDto` or `ManualCaseListItemDto` | [OPEN] OI-13-503: add server count projection; show `—` with “Pending count unavailable”, not zero or a count of one loaded queue page |
| Project-wide “12 checklist items”, “38 manual cases”, per-checklist case totals and `ais-3` header | No authoring aggregate/settings-reference member on the tree/list shapes | [OPEN] recommend omit decorative totals/settings badge until an explicit aggregate/reference read exists; loaded rows may be counted only as loaded |
| `SHOP-CL-0017` checklist human code; mockup `SHOP-TC-0042` code shape | Checklist has no code; manual code schema is `<PREFIX>-<n>`, not `<PREFIX>-TC-<n>` | [OPEN] recommend server case code verbatim and checklist title plus UUID; never manufacture CL/TC segments |
| “In review” status | `ManualCaseListItemDto.state` allows only `draft`, `approved`, `retired` | [OPEN] recommend use those three values; queue/review activity is separate metadata, not a fabricated lifecycle enum |
| `tc-77`/`tc-91` coverage aliases versus canonical catalogue ids | `ManualCaseListItemDto.linked_case_ids` returns UUIDs only | [OPEN] recommend show supplied UUIDs; human catalogue labels require a scoped catalogue read; retain canonical fixture identity consistency in future mockup corrections |
| Export selected scope | No selected-id parameter on export | [OPEN] OI-13-502: recommend `ids` 1–200, mutually exclusive with scope filters; until then explain selection export unavailable and never silently export the category |
| Error code/message, request id, retryability and details | `../../../contracts/openapi.yaml#/components/schemas/ErrorEnvelope.error.code`, `ErrorEnvelope.error.message`, `ErrorEnvelope.error.request_id`, `ErrorEnvelope.error.retryable`, `ErrorEnvelope.error.details` | traced; use safe localized failure copy and escaped server codes; `Retry-After` is a response header, not a guessed timer |

## Regions

Reading order preserves page 40's hierarchy/table split rather than introducing a dashboard.

| Order | Region | Presentation and action |
| --- | --- | --- |
| 1 | Skip link, shell, project scope and breadcrumbs | Reuse shipped shell; current navigation is Authoring under the existing Test design group. Scope identifies every request. |
| 2 | Desktop-required notice, h1, context and header actions (HTML:59–87) | One primary “New test case”; Generate and Import/export secondary. Permission-disabled creation explains its prerequisite. New-case parent selection must resolve a real checklist before creation. |
| 3 | Scoped request/stale feedback | Tree and table own independent busy/error state; retain successful sibling data. Route-level denial is separate from write denial. |
| 4 | Root-first selected-node breadcrumb (HTML:153–165) | Preserve parent context and selected node when filters change. No inferred parent strings. |
| 5 | Left hierarchy (HTML:168–264) | Module → feature → category/subcategory → checklist → case, with native nested lists, disclosure buttons, visible current scope and case links. Load child pages on explicit expansion; no fetch-all recursion. |
| 6 | Right scope, filters, table and selection toolbar (HTML:266–345) | Priority, lifecycle, source, coverage, owner plus free text/reset. Table cells: checkbox, code, title, checklist parent, priority, lifecycle, source/provenance, coverage, owner, version. Unsupported fields remain explicit per trace table. |
| 7 | Cursor navigation and pending-change link (HTML:347–355) | Honest loaded count; Next only for a new non-null cursor, Previous only for retained navigation context. No “of total” or arbitrary page jump. Pending count is unavailable until projected. |

A15 reuse: `app-page-header`, `app-section-header`, `app-data-table` and its cell/records/value companions, `app-request-state` and state patterns, `app-inline-feedback`, `app-retry-after`, `app-status-pill`, `app-revision-chip`, `app-copyable-identifier`, `app-desktop-required-notice`, `[appDesktopMutation]`; owned Helm button/input/select/checkbox/card/tooltip families. Catalogue source: [UI rules A15](../../engineering/ui-rules.md#shared-component-catalogue-a15). Proposed NEW names only: `shared/ui/authoring/feature-tree`, `shared/ui/authoring/case-filters`, `shared/ui/authoring/coverage-summary`, `shared/ui/authoring/visible-selection`. These remain presentational, with input/output bindings and no injected feature store or SDK.

CR-D-2 related-page delta ([catalogue brief](../web-ui/prompts-crd2/B3-25-test-catalog.md), [page index](../web-ui/prompts-crd2/00-README.md#1-review-what-each-existing-page-needs)):

- **Page 25, G2:** retain stable automated identity/revision/hash semantics and existing rename/history/owner scope. Add accessible “Automated identities” versus “Uncovered manual cases” views; switching views/filters resets the cursor. Uncovered view searches manual title/code, never imaginary platform/revision fields. Show manual code/title/version links beside each automated identity, including many-to-many links. Zero live automated links means uncovered; unavailable link metadata means unavailable, not zero. No manual link on an automated identity is a different state. Empty catalogue, no uncovered cases and no filter matches remain distinct. Never reveal a revoked authoring-read case through a preview. Link back to tree/editor without auto-linking, bulk linking or automatic script generation.
- **Page 24:** its authoring-home links land on this selected tree or the editor Timeline, pending changes and generation, rather than CI-first content in G1. Recent versions must come from the version read; aggregate counts remain subject to the same provenance rule.
- **Page 23:** the G1 onboarding route is project → AI settings → requirement → checklist → this tree; CI steps are G2. A truly empty project cannot create a case without a valid parent checklist.
- **Pages 01/27/31:** retain phase-gated navigation, AI-settings/consent destinations and permission-gated credit navigation; their detailed deltas live in the companion navigation/settings/usage specifications. This page does not add a production phase switch or expose G2 scripts/runs merely because coverage metadata exists.

## States

Coverage: **9/9 semicolon-delimited Tree + table entries** in feature §6, expanded to 11 rows below because independent loading and long-content conditions deserve separate behavior. Shared request state is `idle → loading → ready(empty allowed) | error`; stale is retained data after a failed refresh, not a 412 GET response.

| State | Region behaviour and recovery | Mockup `?state=` or [OPEN] |
| --- | --- | --- |
| Tree loading independently | Busy hierarchy/skeleton, table and its filters remain usable; tree-scoped polite “Loading hierarchy”. | [OPEN] independent query absent; `default` static Tree loading specimen HTML:370–371; `loading` hides both panes |
| Table loading independently | Preserve selected hierarchy; busy table, named caption and filters; no fabricated rows or temporary zero. | [OPEN] independent query absent; `default` static Table loading specimen HTML:384–385 |
| Empty project | Explain no authoring content; retain project scope. Generation/import or manual setup may create valid hierarchy; New case needs an existing checklist, never a phantom parent. | `empty`; fixture incorrectly suggests first case without demonstrating parent creation |
| Category without cases | Keep category and breadcrumb; table has scoped empty panel, not project-empty; zero shown only after confirmed empty response. | `default` static specimen HTML:379–381; [OPEN] no isolated query |
| No filter matches | Preserve tree, scope and all filters; Clear filters resets query/cursor/selection. | `no-match`; [OPEN] fixture outer hide gate HTML:152 also hides scope/filters, contradicting its copy |
| Tree error, table usable | Tree-only retry disabled in flight; retain table and safe filters; generic failure does not imply empty project. Table error is symmetric. | `default` static error cards HTML:374–390; [OPEN] `error` hides both panes |
| 403 entry | No project/case payload; shared forbidden page and access explanation. Write-denied readers instead retain the readable page. | `403`; [OPEN] its copy conflates missing write with denied read, so do not copy that authorization meaning |
| Stale page after failed refresh | Keep last good rows and hierarchy, label affected region stale, explicit Refresh; no success/empty claim. | [OPEN] no stale query/specimen; `412` is mutation conflict imagery, not this read failure |
| Deep hierarchy ≥6 levels | Keep all levels, bounded indentation, wrapped labels and selected-path access; expansion is never a drag. | [OPEN] default five-level fixture does not demonstrate ≥6 levels |
| Longest Vietnamese hierarchy/table content | Full value recoverable by keyboard/touch disclosure; no global break-all, clipped essential reason or body-wide horizontal scroll. | `default` long category HTML:234; [OPEN] no full long/deep stress state |
| Restricted creation/selection | Reader keeps filters, browse and links; unavailable create/selection controls have nearby explanation, not silently removed at desktop. Server remains authoritative. | `default` read-only grant specimen HTML:393–395; [OPEN] no isolated query |

Additional shared rows: `401` signs in with safe return URL and never submits; `404` is indistinguishable for foreign/unknown ids; `429` and 503 use real `Retry-After` via `app-retry-after`; unknown/repeated cursor becomes a visible fixed `unexpected` failure, never a shorter list or exposed thrown message. A 412 on a linked mutation uses [editor conflict rules](authoring-editor.md#states), not a tree lifecycle value.

Keyboard model: ✅ [WAI-ARIA APG tree interaction](https://www.w3.org/WAI/ARIA/apg/patterns/treeview/) is the reference for directional keys; [B2-40](../web-ui/prompts-crd2/B2-40-feature-tree.md) explicitly requires nested lists/disclosure buttons, not an incomplete ARIA tree. The proposal retains native list/button/link semantics and Tab access; it is **not** a `treegrid` (the case table is separate) and must not add isolated `treeitem` roles. Tree-style key conveniences below operate only when focus is within the hierarchy, never in search/select/input controls. A future role=`tree` conversion would require the complete APG tree/group/treeitem, roving-focus and loaded-node metadata model plus independent AT review; it is not implied here.

| Key/action | Defined hierarchy behavior |
| --- | --- |
| Tab / Shift+Tab | Reach visible disclosure buttons, scope-selection buttons and case links in reading order; collapsed descendants are hidden and not focusable. No keyboard trap. |
| Right | On closed parent: expand, retain focus; on open parent: focus first visible child control; on leaf: no-op. Fetching children shows scoped busy state, never fabricated nodes. |
| Left | On open parent: collapse; on closed parent/leaf: focus parent control; at root: no-op. Before collapse hides a focused descendant, return focus to the parent disclosure. |
| Down / Up | Move to next/previous visible logical node's primary control without selecting scope, fetching unopened branches or changing table checkbox selection. |
| Home / End | Focus first/last loaded visible node; never load all nodes or move to hidden descendants. |
| Type-ahead | Printable characters move focus to the next loaded visible title starting with the accumulated prefix; repeated character cycles matches. Match the active locale case-insensitively with Vietnamese text intact; ignore IME composition and modified shortcuts. No result filtering or network call. |
| Enter / Space | Native button activation expands/collapses or explicitly selects scope; Enter on a case link navigates, Space retains native link scrolling. Focus movement alone never changes scope or bulk selection. |
| Pointer/touch | Same disclosure/selection commands; expanded state on each disclosure uses `aria-expanded` and `aria-controls`; current scope is separately named and visually marked. No drag or double-click-only function. |

Focus order: skip link → shell/scope → breadcrumbs/header links → hierarchy controls → filters in displayed order → select-visible/row checkbox and case link → export/pager/pending link. Disabled controls have focus-reachable adjacent help. Result refresh keeps the initiating control focused; one polite region announces scope/result changes and selected visible count, not every row. Decorative skeletons/icons are hidden from AT. The select-visible checkbox uses native indeterminate/mixed state and its accessible name states visible rows only.

## Copy length (vi vs en)

Longest-risk specimens below are source/proposed text, not measured rendered-width evidence. Preserve complete user titles; authored compact status chips use the current shared vocabulary and full context outside the chip. Allow 40% copy expansion without reducing type.

| Use | vi specimen | en specimen | Treatment |
| --- | --- | --- | --- |
| Long hierarchy title (HTML:234) | Tuỳ chọn giao hàng và kiểm tra địa chỉ tại các thị trường bị hạn chế | Delivery options and address validation in restricted markets | Natural word wrap; long token escape only; preserve canonical case titles elsewhere |
| Case link (HTML:209) | Ca thủ công · SHOP-TC-0042 v5 · Xác nhận đơn hàng hiển thị tổng tiền | Manual case · SHOP-TC-0042 v5 · Confirm order shows the total | Fixture identity only; production code is server value; full accessible name |
| Scope explanation (HTML:270) | Phạm vi bảng là danh mục Kiểm tra tính hợp lệ bao ngoài; mục đã chọn có một ca. | The table covers the enclosing Validation category; the selected item has one case. | Wrap as body copy, replace counts with supplied data only |
| Selection | Chọn các dòng đang hiển thị · chỉ dòng hiển thị | Select visible rows · visible rows only | Full label/help outside compact chip |
| Missing pending count | Chưa có số lượng thay đổi chờ áp dụng | Pending change count unavailable | `—` plus accessible explanation, never bare zero |
| Unsupported filter | Bộ lọc người phụ trách chưa được hỗ trợ | Owner filtering is not available yet | Adjacent persistent explanation |
| Read-only | Cần máy tính để tạo ca hoặc thay đổi lựa chọn xuất | Use a desktop to create cases or change export selection | Notice stays visible at 360px; hierarchy scope browsing still works |
| Page 25 uncovered empty | Mọi ca kiểm thử thủ công đều đã được liên kết kiểm thử tự động | All manual cases have an automated identity link | Say only after a complete server-scoped result, not one loaded page |

## Responsive notes

- **1280px:** expanded 224px shell by default; left hierarchy and right dense table retain page 40's tree-rail split. Table overflow stays in a named local region with keyboard access; pager sits outside it. Use [layout.md](layout.md) and [UI column/long-content rules](../../engineering/ui-rules.md#data-tables), not a squeezed ten-column equal-width grid. Title flexes; identifiers/status/selection remain intrinsic. Above 200 loaded rows use the shared large-list behavior, subject to its documented UI22 owner decision.
- **768–1279px:** shell rail and stacked panes; same scope/filter semantics. A narrow container on a wide desktop does not itself revoke mutation controls.
- **360px / below 768px:** modal shell drawer; notice explains read-only editing/apply. Hierarchy, expansion, scope browsing, filters, reset, links and version inspection remain usable. Do not confuse selecting a browse scope with export-checkbox selection: export checkboxes/toolbar and mutation actions are removed by viewport policy. Show labeled stacked case records with semantic field/value associations, not CSS labels alone. Bound indentation (including ≥6 levels), stack the root-first breadcrumb, and keep full labels reachable without page-level horizontal scrolling or `overflow-x:hidden` masking.
- If a resize removes a focused mutation control, hand focus to the corresponding summary heading or notice after render; preserve draft/selection context without submitting. Touch targets are 44px under project policy, minimum 24×24 CSS px in every layout. Disclosure controls and focus rings must not be clipped by indentation or table scrollers.

Manual acceptance remains ⚠️ **not run**; apply each row to **tree/table at 1280 and 360px, light/dark, vi/en**:

| Criterion | Required inspection / expected outcome |
| --- | --- |
| 2.5.8 target size | Measure disclosure, checkbox, reset, chip disclosures and icon actions ≥24×24px; coarse-pointer controls 44px. |
| 2.4.11 focus not obscured | Tab through shell, deep tree and pager; sticky chrome/local headers/toasts never cover focused control; use nearest-edge scroll and header offset. |
| 2.5.7 dragging | Expand, browse, filter and select entirely by buttons/keyboard; no drag-only ordering. |
| 1.4.3 / 1.4.11 contrast | Text ≥4.5:1 (large ≥3:1), meaningful boundaries/icons ≥3:1 in actual composite states using [tokens.md](tokens.md); token presence alone is not a measurement. |
| 2.1.1 / 2.4.7 / 4.1.2 | All commands reachable, visible unclipped 2px/2px focus, accurate disclosure names/expanded state and mixed selection; no partial ARIA tree. |
| 1.4.10 / 1.4.12 / 4.1.3 | Reflow/spacing/long vi remains readable; independent loads/stale/result-count updates announce once, never erase usable sibling data. |

## Invariants

- ✅ [Feature §5.2](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md): generated SDK operations choose the shipped route group; UI never concatenates path prefixes or resolves the route-group open question itself.
- The tree read asks for `include=path,counts`; a missing projection is an explicit gap, never an empty path or an inferred count.
- Keyset pages only, opaque cursor, current scope/query mirrored in URL and restored by reload/back/forward. Any filter/scope change resets cursor and visible-row export selection; no hidden/cross-page selection survives invisibly. Repeat cursor is a contract failure. Do not fabricate totals or page jumps.
- Source/owner filters must be server-scoped; no fetch-all workaround. Owner absent is unavailable, not proven unassigned. Pending count absent is `—`, not `0`. Partial loaded data is not an aggregate.
- Coverage is live automated identity linkage, not PASS/FAIL, script readiness or a release decision. Confirmed zero is uncovered; unavailable link data is unknown. G1 may show metadata but cannot expose G2 run/script navigation.
- Browse reads do not need `If-Match`; editor saves replay the read case ETag. Missing tag is server 422, stale save 412, never a client-invented 428 or GET “stale lifecycle”. Queue/apply semantics belong to the pending-change surface.
- AI provenance and user content render as escaped plain text; no `innerHTML`, repair action or automatic approval. Any shared generation status renders supplied stage only, with no derived percentage/ETA. Null credit/cost/estimate is unavailable with reason, never zero; no illustrative per-item cost becomes product pricing.
- Permissions and phase are separate: later-phase destinations hidden; current-phase readers keep data with explained write-denied controls. No authoring content is persisted to local storage, telemetry or an offline cache.

## Open points

| Open item | Effect and recommended default |
| --- | --- |
| OI-13-501 | Source/owner parameters and indexes remain a backend-owner dependency. Retain their labeled disabled controls with unsupported reason until supplied; do not claim the five-filter acceptance check passes meanwhile. |
| OI-13-503 | Missing pending counts affect tree, table, header and page-24 badges. Recommend `pending_change_count` projection; render `—` plus reason, never “0 pending”. |
| OI-13-502 | Selection export cannot express ids. Recommend explicit 1–200 ids; explain unavailable export and preserve the visible-row selected ids without issuing a broader export. |
| List metadata, aggregate totals and identifiers | Row source/owner/provenance, checklist codes, settings badge, aggregate counts, catalogue aliases and “In review” are not supplied as shown. Use the safe defaults in the trace table; no inferred owner, CL/TC code, enum or total. |
| Empty-project parent prerequisite | Recommend generation/import or explicit checklist setup before case create; do not enable the fixture's parentless case invitation as a valid API call. |
| Partial states and keyboard coverage | Independent load/error, stale, long/deep, native-list tree-style keys and no-match retained controls need dedicated interactive review; static cards are not proof. Retain native semantics; complete APG conversion requires its own review. |
| Approval and evidence | Independent design review record and owner approval are absent; recommended default is keep proposed status and do not start implementation. No browser, screen-reader, visual, axe, build, test, lint or formatter pass is claimed. |
