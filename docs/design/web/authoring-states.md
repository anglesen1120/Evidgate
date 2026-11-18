Status: proposed, awaiting owner approval

# Authoring state matrix and bilingual copy sheet

Review record: [authoring-review.md](authoring-review.md)

Flow id: `authoring-states`

Feature tasks: `13.4.1-T1.2`; screen specifications `13.4.1-T1.1`; independent design review `13.4.1-T1.3`.

Legend: ✅ verified against a linked source · ⚠️ not exercised · `[INFERENCE]` design interpretation, not observed behaviour · `[DECISION]` existing project policy · `[OPEN]` missing contract or specimen, with a recommended default. This proposed sheet is not an approval or an accessibility test result.

✅ Coverage source: [authoring plan §6](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md#6-states-and-transitions), including its shared request-state paragraph and generation-progress paragraph. Compound states are expanded below: **tree/table 12, editor 15, pending changes 14, generation 15, timeline 6, AI settings 22, import/export 12, usage credits 7 = 103 screen-state rows across eight surfaces**. Generation lifecycle/stage rows and shared request rows are additional, not substitutes. The code-copy tables contain **10 outcome codes and 10 extraction failure codes**, exactly [generation plan §4](../../../plans/features/13-product-phases/13.3.1-generation-pipeline.md#4-data-model-and-migrations) and [§8](../../../plans/features/13-product-phases/13.3.1-generation-pipeline.md#8-security-privacy-and-performance).

**Data trace register.** Paths below are literal schema references in [OpenAPI](../../../contracts/openapi.yaml); a dot after a schema name denotes its field. Grouped fields each have their own listed path. Local UI labels, request state and draft counters are presentation state, not extra API members. This sheet introduces no data display beyond this register; the sibling screen specifications govern their fuller field inventories.

| Data field | openapi schema path | state (traced / [OPEN]) |
| --- | --- | --- |
| Node identity, parent, kind, title | `contracts/openapi.yaml#/components/schemas/NodeDto.node_id`, `NodeDto.parent_id`, `NodeDto.kind`, `NodeDto.title` | traced; subsequent shorthand paths in this table retain the same `contracts/openapi.yaml#/components/schemas/` prefix |
| Node order, revision, child and item counts | `contracts/openapi.yaml#/components/schemas/NodeDto.position`, `NodeDto.version`, `NodeDto.child_count`, `NodeDto.item_count` | traced |
| Ancestor title and node id; tree cursor | `contracts/openapi.yaml#/components/schemas/AuthoringNodePathEntry.title`, `AuthoringNodePathEntry.node_id`, `AuthoringTreeResponse.next_cursor` | traced |
| Case code, title, priority, status, revision, parent item/node | `contracts/openapi.yaml#/components/schemas/ManualCaseListItemDto.code`, `ManualCaseListItemDto.title`, `ManualCaseListItemDto.priority`, `ManualCaseListItemDto.state`, `ManualCaseListItemDto.version`, `ManualCaseListItemDto.checklist_item_id`, `ManualCaseListItemDto.node_id` | traced |
| Case selection id, coverage count/links; list cursor | `contracts/openapi.yaml#/components/schemas/ManualCaseListItemDto.case_id`, `ManualCaseListItemDto.linked_live_count`, `ManualCaseListItemDto.linked_case_ids`, `ManualCaseListResponse.next_cursor` | traced; selected ids/count are local visible-row state |
| Description, precondition; step number/action/expected/data | `contracts/openapi.yaml#/components/schemas/ManualCaseDetailDto.description`, `ManualCaseDetailDto.precondition`, `ManualCaseDetailDto.steps`, `ManualCaseStepDto.no`, `ManualCaseStepDto.action`, `ManualCaseStepDto.expected`, `ManualCaseStepDto.data` | traced |
| Case creation/update UTC times | `contracts/openapi.yaml#/components/schemas/ManualCaseDetailDto.created_at`, `ManualCaseDetailDto.updated_at` | traced |
| Queue operation, target, actor id, source, reason, time, stale flag | `contracts/openapi.yaml#/components/schemas/AuthoringChangeListItem.change_type`, `AuthoringChangeListItem.target_id`, `AuthoringChangeListItem.author_membership_id`, `AuthoringChangeListItem.source`, `AuthoringChangeListItem.reason`, `AuthoringChangeListItem.created_at`, `AuthoringChangeListItem.stale` | traced; do not replace membership id with an invented display name |
| Queue id, payload, state, base revision, applied batch | `contracts/openapi.yaml#/components/schemas/AuthoringChangeListItem.change_id`, `AuthoringChangeListItem.payload`, `AuthoringChangeListItem.state`, `AuthoringChangeListItem.base_version`, `AuthoringChangeListItem.applied_batch_id` | traced; before/after must be backed by fetched content, not an invented preview endpoint |
| Timeline version id/no, source, actor id, reason, UTC time, digest | `contracts/openapi.yaml#/components/schemas/AuthoringVersionListItemDto.version_id`, `AuthoringVersionListItemDto.version_no`, `AuthoringVersionListItemDto.source`, `AuthoringVersionListItemDto.author`, `AuthoringVersionListItemDto.reason`, `AuthoringVersionListItemDto.created_at`, `AuthoringVersionListItemDto.content_hash` | traced |
| Timeline snapshot, page cursor and restored version | `contracts/openapi.yaml#/components/schemas/AuthoringVersionDetailDto.content`, `AuthoringVersionListResponse.next_cursor`, `AuthoringRestoreResponse.version` | traced; diff only two fetched snapshots |
| Settings version/source/id, document, template, actor, reason, UTC time, hash | `contracts/openapi.yaml#/components/schemas/AiSettingsView.version_no`, `AiSettingsView.source`, `AiSettingsView.settings_id`, `AiSettingsView.settings`, `AiSettingsView.template`, `AiSettingsView.author`, `AiSettingsView.reason`, `AiSettingsView.created_at`, `AiSettingsView.content_hash` | traced; virtual default is version 0, null id/time, not a saved version |
| Settings description, environment URL, languages, writing style, coverage, four criteria, exclusion, step detail | `contracts/openapi.yaml#/components/schemas/AiSettingsDocument` → `contracts/schemas/ai-settings.v1.json#/properties/{description,environment_url,languages,writing_style,coverage_strategy,priority_criteria,exclusion_rules,step_detail}` | traced through OpenAPI external schema; criteria children are `critical`, `high`, `medium`, `low`; counters are NFC code-point counts of these draft fields |
| Import batch, created/updated/restored/unchanged counts, row index/key/kind/outcome/code | `contracts/openapi.yaml#/components/schemas/ImportResponse.batch_id`, `ImportResponse.counts`, `ImportCountsDto.created`, `ImportCountsDto.updated`, `ImportCountsDto.restored`, `ImportCountsDto.unchanged`, `ImportRowDto.index`, `ImportRowDto.key`, `ImportRowDto.kind`, `ImportRowDto.outcome`, `ImportRowDto.code` | traced; dry-run batch id is null; report contains no cell text |
| Intake kind, input text, attachment name/type/bytes, caps, scan/sensitivity status, intake state and extraction failure_code | No authoring-intake schema in current OpenAPI | [OPEN D1] Follow generation plan §4/§5; local filename is local file metadata, remote scan/sensitivity must come from authorized artifact metadata; render unavailable until supplied, never “scan clean” by inference |
| Estimate total credit_cost_micro, credits_available_micro, cap_blocked, opt_in, cache_hit, model_tier; 202 generation_id/state/receipt | No generation estimate/receipt schema in current OpenAPI | [OPEN D2] Follow generation plan §5 routes 3–4; block an AI call when preflight cannot be read; never replace null with zero or derive per-item prices |
| Generation state, progress.stage/eta_class, outcome_code, error.code, cost_micro, output_refs, accepted_refs, version/ETag, created_at/started_at/finished_at | No generation detail schema in current OpenAPI | [OPEN D3] Follow generation plan §4/§5 route 6; render only returned values; do not derive percentage, completion time, per-item failures or refund |
| Generated text/steps/priority, model, model_tier, ai_settings_ref, role_profile_ref, prompt_version, output_schema_version and run_note | No generation/provenance schema in current OpenAPI | [OPEN D4] Use the pinned references and escaped output defined in generation plan §4/§5; no invented prompt hash, fallback model, actor or timestamp |
| Credit balance/cap, period start/end, as_of, usage action/credits/calls, unavailable_reason | No AI-credit response schema in current OpenAPI | [OPEN D5] Adopt the recommended `balance_micro`, `period_cap_micro`, `period:{start,end}`, `as_of`, `usage:[{action,credits_micro,calls?}]`, `unavailable_reason` shape in authoring plan §5.3; unavailable is not zero; no invented total from visible rows |
| Case source/owner and their server filters; pending-change badges | Absent from `ManualCaseListItemDto` / `NodeDto` | [OPEN D6] Use authoring plan §5.3 recommendations; absent count is “—”, absent owner/source is unavailable, never claim an unsupported filter ran on the server |
| Import preview cell text, hierarchy path, validation cell pointers; export selected ids, exact count, source version and project prefix | Import report has no cell text; export selection/read metadata are not schemas here | [OPEN D7] Keep local parsed cells labelled preview; bind errors only to returned pointers; invalid/mixed rows block import; disable unsupported selected export with its scope explanation, never widen scope or guess prefix/count |
| Human display names and fixture labels `SHOP-TC-*`, `ais-3`, `av-12`; illustrative credit tariffs/refunds | Not equivalent to UUID/version fields above | [OPEN D8] Keep fixtures illustrative only; show authorized identifiers and actual version numbers; omit names/tariffs/refunds that the response did not supply |

## v2 pages

The page declarations are reference state selectors, not API simulations. A `[OPEN]` mapping below means **no dedicated query-state specimen**, not missing product behaviour: the row already specifies copy, region behaviour and accessibility. Do not invent query values. `default` may contain a static example but does not prove the full transition.

| Surface | Reference | Declared `?state=` values |
| --- | --- | --- |
| Tree + table | [40](../web-ui/output/40-authoring-tree/evidgate-authoring-tree.html), declaration line 89; [screen](authoring-tree.md) | `default,loading,empty,no-match,error,403,404,401,429,412` |
| Editor | [41](../web-ui/output/41-test-case-editor/evidgate-test-case-editor.html), line 88; [screen](authoring-editor.md) | `default,loading,empty,new-case,error,403,404,401,429,412,submitting,saved,validation-error,long` |
| Timeline | Same page 41, Timeline tab; [screen](authoring-editor.md#states) | No separate query state; the six timeline states are specified under States below |
| Pending changes | [42](../web-ui/output/42-pending-changes/evidgate-pending-changes.html), line 86; [screen](authoring-pending-changes.md) | `default,loading,empty,error,403,401,429,412,submitting,saved` |
| Generation | [43](../web-ui/output/43-generation/evidgate-generation.html), line 85; [screen](authoring-generation.md) | `default,loading,empty,error,403,404,401,429,412,submitting` |
| AI settings | [44](../web-ui/output/44-ai-settings/evidgate-ai-settings.html), line 77; [screen](authoring-ai-settings.md) | `default,loading,empty,error,403,404,401,412,429,submitting,saved,validation-error` |
| Import/export | [45](../web-ui/output/45-import-export/evidgate-import-export.html), line 89; [screen](authoring-import-export.md) | `default,loading,no-file,malformed,unsupported,validation,duplicate,mixed,denied,error,403,401,429,success,export-empty,export-unavailable` |
| Usage credits | [31](../web-ui/output/31-quota-retention/evidgate-usage-retention.html), line 78; [screen](authoring-usage-credits.md) | `default,loading,empty,no-match,table-error,error,403,404,401,429,412`; generic selectors alone do not establish credit-specific balance states |

Read-only visual inputs: [generation brief](../web-ui/prompts-crd2/B2-43-generation-flow.md), [tree](../web-ui/prompts-crd2/B2-40-feature-tree.md), [editor](../web-ui/prompts-crd2/B2-41-test-case-editor.md), [pending](../web-ui/prompts-crd2/B2-42-pending-changes.md), [settings](../web-ui/prompts-crd2/B2-44-ai-settings.md), [import/export](../web-ui/prompts-crd2/B2-45-import-export.md), [usage](../web-ui/prompts-crd2/B1-04-usage-credits.md), [delta brief](../web-ui/prompts-crd2/00-delta-brief.md). Current contracts and plan invariants override illustrative per-item prices, prompt hashes, caps or version labels in those artifacts.

## Regions

### Reading and focus order

Shared start: skip link → scope/navigation controls → breadcrumb → one h1 → active page controls in the order below. A route transition focuses its h1; a background refresh does not. Modal reading order: named heading/consequences → fields → footer Cancel then explicit confirmation. **Reference delta AR-04:** explicitly target Cancel for initial focus in restore (41), Apply (42), AI preflight (43), template replacement (44) and export confirmation (45), including non-destructive confirmations. Use the owned Helm/Brain initial-focus lifecycle, never a first-button query that selects Close. Tab/Shift+Tab stay contained; Escape cancels the unsubmitted intent without a request and close returns to the visible opener or nearest surviving heading. Static prose need not be a tab stop; simple inspection-only dialogs with no Cancel action may focus Close instead.

**Reference delta AR-07:** visible and accessible names use the same active interface locale through `@angular/localize`, including `aria-label`, the text referenced by `aria-labelledby`/`aria-describedby`, icon Close names and checkbox interpolation. Document `lang` changes with that locale; names/IDs supplied by users or servers are not translated. No Vietnamese-only fallback is acceptable on an English control.

| Accessible control | vi name | en name |
| --- | --- | --- |
| Dialog Close icon | Đóng | Close |
| Confirmation Cancel | Hủy | Cancel |
| Select visible rows | Chọn các dòng đang hiển thị | Select visible rows |
| Select a case | Chọn ca {code} | Select case {code} |

Keep `{code}` as the actual supplied identifier. Include expanded/collapsed disclosures, copy/full-value actions, mobile panel selects, dialog titles and error associations in the bilingual accessibility-tree/AT check, not just visible headings. The frozen references' untranslated names and initial-Close focus are recorded discrepancies, not alternate approved labels/focus behaviour.

| Screen | Main reading order | Focus order after shared start |
| --- | --- | --- |
| Tree + table | Header/actions → tree → filters → results/selection → paging → selected detail | Header links → nested disclosure buttons and node links → five filters/search/reset → visible-selection control → case links/row controls → pager → detail links; independent loading retains controls in the other pane |
| Editor | Identity/context → tabs → fields/steps → version reason → Save/Cancel → checklist/provenance | Tabs → title → precondition → priority → each step action/expected/data and step buttons → reason → Save → Cancel → contextual links; validation summary links target exact invalid fields |
| Pending changes | Queue → selected plain-text preview → batch summary/reason → confirmation | Queue operation selectors → preview disclosures/links → reason → Apply → Cancel in confirmation; no per-row Apply |
| Generation | Input → estimate/confirmation → progress → checklist review → generated cases/provenance | Stage tabs → input fields/file control → role-profile selector → estimate action → consent dialog Cancel/Confirm → result links → explicit accept selection/action; progress is not a focus trap |
| Timeline | Version list/newest first → fetched snapshot comparison → restore reason/confirmation | Version links/selectors → comparison disclosure → pager → restore opener → dialog Cancel → reason → Restore; completed restore focuses the new-version heading, not an old row |
| AI settings | Current version/opt-in notice → form → template preview → Save/Cancel → history/diff | Tabs → description → environment URL → languages → writing style → coverage → critical/high/medium/low criteria → exclusions → step detail → template selector/preview → reason → Save/Cancel → history controls |
| Import/export | Tabs → file/format/scope → preview/error summary → confirmation → result | Tabs → file or export format/scope → Preview → linked errors → inspectable cells → reason → confirmation Cancel/Import or Download; errors remain next to cells |
| Usage credits | Scope/as-of → balance → period → usage table → independent request feedback | Period controls → refresh → action/detail disclosures → pager; read-only facts are not extra tab stops; forbidden route contains no partial usage region |

A15 reuse: `app-page-header`, `app-section-header`, `app-data-table`, `app-data-table-records`, `app-data-table-value`, `app-request-state`, `app-inline-feedback`, `app-conflict-dialog`, `app-retry-after`, `app-status-pill`, `app-revision-chip`, `app-copyable-identifier`, `app-number-value`, `app-field-feedback`, `app-form-error-summary`, `app-absolute-time`, `[appDesktopMutation]`, `app-desktop-required-notice`, and owned Helm controls/dialogs. Source: [shared component catalogue](../../engineering/ui-rules.md#shared-component-catalogue-a15). Proposed new names only: `shared/ui/authoring/feature-hierarchy`, `shared/ui/authoring/change-preview`, `shared/ui/authoring/generation-progress`, `shared/ui/authoring/provenance`, `shared/ui/authoring/credit-preflight`. The component/id map belongs to the separate handover, not this sheet.

### Tree keyboard summary

The authoritative authoring model is [authoring-tree.md, States](authoring-tree.md#states). Use native nested lists, separate disclosure buttons with `aria-expanded`/`aria-controls`, and links or buttons for node selection; selected location is explicit. Tab/Shift+Tab follows visible controls; Enter activates a link, Enter/Space a disclosure button. Collapsing a branch containing focus first returns focus to its disclosure; hidden descendants leave the tab order. The sibling's hierarchy-scoped directional key conveniences (Up/Down/Home/End/Left/Right) apply only while focus is inside the hierarchy and are not an ARIA `tree` requirement; no incomplete `role=tree`, drag-only ordering, or automatic mutation on selection. Keep the full ancestor path accessible at depth ≥6. This summary defers any finer key behaviour to that sibling, not to the static shell script.

## States

### Shared request and interaction states

These apply independently to each data region on all eight screens. `R` in later notes means this shared contract. Button hover/active/focus-visible use shared tokens without side effects; disabled actions carry an adjacent persistent reason; inspection-only facts never gain a disabled-button role.

| State | Region behaviour / vi | en | A11y note | Mockup query |
| --- | --- | --- | --- | --- |
| idle | Chọn một mục để xem nội dung. | Choose an item to inspect. | No busy/live announcement before a request; preserve navigation. | [OPEN]; use neutral instruction, not an empty success |
| loading | Đang tải nội dung. | Loading content. | Scope `aria-busy=true`; skeletons hidden from AT; one polite status; keep independent regions usable. | `?state=loading` |
| ready | Đã tải nội dung. | Content loaded. | Clear busy; announce only meaningful count/change, no focus theft. | `?state=default` |
| empty | Chưa có dữ liệu trong phạm vi này. | There is no data in this scope. | Associated heading and explicit scope; not failure or PASS. | `?state=empty` where declared; import uses `no-file`/`export-empty` |
| generic error | Không thể tải; hãy thử lại. | Could not load; retry explicitly. | Persistent inline recovery; retry disabled while in flight; preserve prior data/draft. | `?state=error` |
| forbidden (403) | Bạn không có quyền xem nội dung này. | You do not have permission to read this content. | Route heading focus; no protected metadata or partial page. | `?state=403` |
| not_found (404) | Không tìm thấy nội dung. | Content not found. | Same unknown/foreign-id pattern; focus route heading, no ownership disclosure. | `?state=404` only where declared; otherwise [OPEN] |
| rate_limited (429) | Quá nhiều yêu cầu; chờ theo phản hồi máy chủ. | Too many requests; wait as directed by the server. | Expose `Retry-After` once, not a per-second live countdown; no mutation auto-retry. | `?state=429` |
| unavailable (503 / offline-ish) | Tạm thời không khả dụng; bản nháp được giữ trong trang. | Temporarily unavailable; the draft is retained on this page. | Do not promise offline persistence, queued writes or a service worker; reason stays readable. | [OPEN]; use shared unavailable pattern |
| stale after refresh failure | Dữ liệu có thể đã cũ; lần làm mới không thành công. | Data may be stale; refresh failed. | Keep readable last result with a timestamp only if supplied; polite warning, no refocus. | [OPEN]; retain data with inline feedback |
| 412 mutation conflict | Bản nháp được giữ; hãy xem thay đổi trước khi tải lại. | Draft retained; inspect changes before reloading. | Named conflict dialog; keep draft, explicit Reload only; no auto-merge/resubmit or false Saved. | `?state=412` where declared; never infer a 412 GET lifecycle |

### Tree + table — 12/12 expanded states

| State | Region behaviour and vi string | en string | A11y note | Mockup query |
| --- | --- | --- | --- | --- |
| Tree loading | Cây chức năng đang tải; bảng vẫn dùng được. | Feature tree loading; the table remains usable. | Busy tree only; keep table focus. | [OPEN]; whole-page `loading` is not independent loading |
| Table loading | Bảng ca kiểm thử đang tải; cây vẫn dùng được. | Case table loading; the tree remains usable. | Busy table only; retain selected tree node. | [OPEN]; require independent region specimen |
| Empty project | Dự án chưa có nội dung soạn kiểm thử. | This project has no authoring content yet. | Neutral empty heading; permitted create path explained. | `?state=empty` |
| Category without cases | Danh mục này chưa có ca kiểm thử. | This category has no test cases yet. | Keep category breadcrumb and tree; do not clear filters silently. | [OPEN]; distinct category empty panel |
| No filter matches | Không có ca kiểm thử khớp bộ lọc. | No test cases match these filters. | Keep filters and keyboard-reachable Reset. | `?state=no-match` |
| Tree error, table usable | Không tải được cây chức năng; bảng vẫn dùng được. | Could not load the feature tree; the table remains usable. | Inline tree Retry, no focus jump out of table. | [OPEN]; `error` is not proof of partial failure |
| 403 entry | Bạn không có quyền đọc nội dung soạn kiểm thử. | You do not have permission to read authoring content. | R forbidden; do not show case identities. | `?state=403` |
| Stale page | Không thể làm mới; đang hiển thị dữ liệu đã tải trước đó. | Refresh failed; previously loaded data is still shown. | R stale; readable results retained. | [OPEN]; stale banner plus data |
| Deep hierarchy | Đang xem đường dẫn phân cấp từ sáu cấp trở lên. | Viewing a hierarchy path at least six levels deep. | Full ancestor path; list relationships remain programmatic, no drag. | [OPEN]; depth ≥6 specimen |
| Longest-vi overflow | Mở toàn bộ tên mục hoặc ca kiểm thử để xem đầy đủ. | Open the full node or case title to inspect it. | Full-value disclosure works by keyboard/touch; not hover-only. | [OPEN]; long-title/deep-path specimen |
| Restricted creation | Bạn có thể xem nhưng không có quyền tạo ca kiểm thử. | You can read but do not have permission to create a test case. | Explained disabled creation, reason outside disabled control. | [OPEN]; mutation denial is not whole-page 403 |
| Restricted selection | Bạn có thể xem nhưng không được chọn dòng cho thao tác này. | You can inspect rows but cannot select them for this action. | Preserve case links; selection denial explained; no bulk approval. | [OPEN]; selection-specific reason |

### Editor — 15/15 expanded states

| State | Region behaviour and vi string | en string | A11y note | Mockup query |
| --- | --- | --- | --- | --- |
| New case | Ca kiểm thử mới chưa được lưu. | This new test case has not been saved. | Label required fields; no fake allocated code/version. | `?state=new-case` |
| Missing required field | Điền trường bắt buộc trước khi lưu. | Complete the required field before saving. | Focus linked summary after submit; field invalid and described. | `?state=validation-error` |
| Unsaved draft | Bạn có thay đổi chưa lưu. | You have unsaved changes. | Persistent draft notice, no repeated keystroke live announcements. | [OPEN]; dirty-draft specimen |
| Save in progress | Đang lưu phiên bản mới. | Saving a new version. | Save region busy; suppress duplicate intent, retain draft. | `?state=submitting` |
| Save success | Đã lưu phiên bản mới. | A new version was saved. | Polite confirmation after actual response; no release verdict. | `?state=saved` |
| Save failure | Không thể lưu; bản nháp được giữ lại. | Could not save; your draft is retained. | Inline error and explicit retry; no clearing fields. | `?state=error` as general failure reference |
| 412 with draft preserved | Phiên bản đã thay đổi; toàn bộ bản nháp của bạn được giữ lại. | The version changed; your entire draft is retained. | R conflict; current revision is not a fabricated snapshot. | `?state=412` |
| Read-only history | Phiên bản lịch sử chỉ để đọc và không bị ghi đè. | Historical versions are read-only and are never overwritten. | Readable/copyable values, not disabled form fields. | [OPEN]; timeline tab in `default` is a partial reference |
| Restore confirmation | Khôi phục sẽ thêm phiên bản mới; hãy nhập lý do và xác nhận. | Restore appends a new version; enter a reason and confirm. | Named dialog; Cancel initial focus; required reason. | [OPEN]; restore dialog, no dedicated query state |
| Restore success | Đã thêm phiên bản khôi phục; các phiên bản cũ không đổi. | A restored version was appended; earlier versions are unchanged. | Focus new-version heading after explicit action; announce once. | [OPEN]; static restore specimen, not `saved` proof |
| Queued checklist edit | Thay đổi danh sách kiểm tra đang chờ áp dụng. | The checklist change is pending application. | Queue link named; never announce applied or approved. | [OPEN]; pending marker specimen |
| AI denied: opt-in off | AI chưa được cho phép; bạn vẫn có thể soạn thủ công. | AI is not opted in; manual authoring remains available. | Disabled AI action described by persistent notice/admin link. | [OPEN]; separate preflight refusal |
| AI denied: credits exhausted | Đã hết tín dụng AI; bạn vẫn có thể soạn thủ công. | AI credits are exhausted; manual authoring remains available. | Readable reason/usage link; do not disable manual Save. | [OPEN]; separate credit refusal |
| AI denied: gateway unavailable | AI tạm thời không khả dụng; bản nháp không thay đổi. | AI is temporarily unavailable; the draft is unchanged. | Explicit retry only after fresh preflight; R unavailable. | [OPEN]; separate gateway refusal |
| AI denied: schema-invalid output | Kết quả AI không hợp lệ; không tạo phiên bản mới. | AI output was invalid; no new version was created. | Error text/code escaped; refund only if supplied. | [OPEN]; separate invalid-output specimen |

### Pending changes — 14/14 expanded states

| State | Region behaviour and vi string | en string | A11y note | Mockup query |
| --- | --- | --- | --- | --- |
| Empty queue | Không có thay đổi chờ áp dụng. | There are no pending changes. | Neutral heading; Apply unavailable with reason. | `?state=empty` |
| Comment operation | Nhận xét đang chờ áp dụng cho mục đã chọn. | A comment is pending for the selected item. | Operation text and target associated; preview plain text. | `?state=default` composite fixture |
| Merge operation | Thao tác gộp đang chờ xem xét. | The merge operation is awaiting review. | Name source/target lineage; no drag required. | `?state=default` composite fixture |
| Split operation | Thao tác tách đang chờ xem xét. | The split operation is awaiting review. | Ordered targets and affected links readable. | `?state=default` composite fixture |
| Delete operation | Thao tác xóa đang chờ xác nhận trong lô. | The delete operation is awaiting batch confirmation. | Name target and supplied consequences; no per-row Apply. | `?state=default` composite fixture |
| Long comment | Mở nhận xét để đọc đầy đủ trước khi áp dụng. | Expand the comment to read it fully before applying. | Keyboard/touch expansion; reason never essential hover-only text. | [OPEN]; full-length comment specimen |
| Invalid merge preview | Không thể gộp; hãy sửa lỗi được nêu trước khi áp dụng. | Cannot merge; resolve the stated problem before applying. | Apply blocked, reason linked to preview/error summary. | [OPEN]; merge-specific invalid preview |
| Invalid split preview | Không thể tách; hãy sửa lỗi được nêu trước khi áp dụng. | Cannot split; resolve the stated problem before applying. | Apply blocked; retain readable preview and queue. | [OPEN]; split-specific invalid preview |
| Applying | Đang áp dụng tất cả thay đổi trong một lô nguyên tử. | Applying all changes as one atomic batch. | Busy Apply region; no repeated confirmation/live spray. No global version is promised; a comment-only batch creates zero entity versions. | `?state=submitting`; reference single-version copy is superseded |
| Success | Đã áp dụng lô được xác nhận. | The confirmed batch was applied. | Polite status; clear only confirmed applied items. | `?state=saved` |
| Error | Không áp dụng được lô; hàng đợi vẫn được giữ. | Could not apply the batch; the queue is retained. | Inline recovery; preserve reason; no partial-success claim. | `?state=error` |
| Duplicate intent | Đang hiển thị kết quả của cùng yêu cầu áp dụng. | Showing the result of the same apply intent. | Reuse intent key; announce original result, not a second version. | [OPEN]; duplicate receipt specimen |
| 412 stale batch | Lô đã cũ; giữ nguyên hàng đợi và chưa áp dụng thay đổi nào. | The batch is stale; the queue is retained and nothing was applied. | R conflict; explicit Reload, no auto-resubmit. | `?state=412` |
| Permission revoked | Quyền áp dụng đã bị thu hồi; thay đổi chờ vẫn có thể xem khi được phép. | Apply permission was revoked; pending changes remain readable when authorized. | Remove mutation eligibility, announce once; if read also revoked use R forbidden without metadata. | [OPEN]; mutation-only denial differs from `403` |

### Generation — 15/15 screen states

| State | Region behaviour and vi string | en string | A11y note | Mockup query |
| --- | --- | --- | --- | --- |
| Empty attachment/input | Thêm yêu cầu hoặc tệp được hỗ trợ trước khi ước tính. | Add a requirement or supported attachment before estimating. | Persistent label/hint; no AI request. | `?state=empty` |
| Oversized attachment | Tệp vượt giới hạn được máy chủ cung cấp. | The attachment exceeds the server-supplied limit. | File error described and in submit summary; never invent cap. | [OPEN]; size-specific field state |
| Unsupported attachment | Loại tệp này không được hỗ trợ. | This attachment type is not supported. | Keep file name as text; request-time validation is not extraction failure. | [OPEN]; type-specific field state |
| Scanning attachment | Đang kiểm tra tệp; chưa thể dùng cho AI. | The attachment is being scanned and cannot yet be used for AI. | Polite transition; no scan-complete inference from elapsed time. | [OPEN]; authorized artifact scan metadata required |
| Rejected attachment | Tệp bị từ chối; hãy xem lý do và chọn tệp khác. | The attachment was rejected; inspect the reason and choose another file. | Persistent reason and reachable replacement control; no provider send. | [OPEN]; rejection-specific state |
| Extraction failure | Không trích xuất được văn bản; xem mã lý do bên dưới. | Text extraction failed; inspect the reason code below. | Read intake `rejected` + failure code; not a fictional later HTTP 422. | [OPEN]; use the ten-code copy table |
| Opt-in off | AI chưa được cho phép; soạn thủ công vẫn khả dụng. | AI is not opted in; manual authoring remains available. | Preflight refusal reason/admin link; no generation row invented. | [OPEN]; `403` generic denial is not opt-in-specific |
| Credits exhausted | Đã hết tín dụng AI; không gửi yêu cầu tạo. | AI credits are exhausted; no generation request is sent. | Usage link and manual path reachable; real zero distinct from missing balance. | [OPEN]; credit-specific refusal |
| Estimate unavailable | Không đọc được ước tính; chưa thể xác nhận dùng AI. | The estimate cannot be read; AI confirmation is unavailable. | Block Confirm with reason; explicit estimate retry, retain input. | [OPEN]; do not show zero estimate |
| Cache hit | Sẽ dùng lại kết quả đã xác thực với chi phí 0 tín dụng AI. | Validated output will be reused at a cost of 0 AI credits. | Announce reuse before confirmation; same review/accept path, not automatic approval. | [OPEN]; cache-specific preflight and success |
| Gateway unavailable | AI tạm thời không khả dụng; không tự thử lại. | AI is temporarily unavailable; no automatic retry occurs. | R unavailable; manual path and fresh explicit preflight. | [OPEN]; gateway-specific reason |
| Schema invalid | Kết quả không đúng cấu trúc; không tạo phiên bản mới. | Output did not match the schema; no new version was created. | Escaped outcome/internal code; refund line absent unless supplied. | [OPEN]; schema-specific failure |
| Partial test-case generation | Một số mục chưa tạo được; hãy xem riêng kết quả hợp lệ và mục lỗi. | Some items could not be generated; inspect valid results and failed items separately. | Separate named result/error lists from returned data only; never invent per-item receipts. | [OPEN]; partial-result read shape must identify actual results |
| Review pending | Kết quả AI đang chờ người dùng xem xét. | AI output is awaiting human review. | Neutral pending label, result links; never a PASS/approved announcement. | [OPEN]; checklist review tab is a partial reference |
| Cases ready for review | Các ca kiểm thử đã sẵn sàng để xem xét, chưa được duyệt tự động. | Test cases are ready for review and have not been automatically approved. | Named case links and provenance; explicit accept selection only. | [OPEN]; cases tab is a partial reference |

### Timeline — 6/6 states

| State | Region behaviour and vi string | en string | A11y note | Mockup query |
| --- | --- | --- | --- | --- |
| First page | Đang hiển thị các phiên bản mới nhất. | Showing the newest versions. | Ordered list with UTC times; no invented total/page number. | `?state=default` + timeline tab |
| Paging | Đang tải trang lịch sử tiếp theo. | Loading the next history page. | Busy list/pager; retain rows and focus; repeated cursor is R error. | [OPEN]; paging-specific specimen |
| Empty new-case history | Ca mới chưa có lịch sử đã lưu. | This new case has no saved history. | Neutral empty state, not a missing case. | [OPEN]; `new-case` is editor reference only |
| Restore confirmation | Khôi phục nội dung này thành phiên bản mới với lý do đã nhập? | Restore this content as a new version with the entered reason? | Named confirmation dialog; Cancel initially focused. | [OPEN]; restore dialog in page 41 |
| Restored | Đã thêm phiên bản khôi phục; lịch sử trước đó được giữ nguyên. | A restored version was appended; prior history is unchanged. | Focus new-version heading following explicit action; announce once. | [OPEN]; restored fixture, no dedicated query |
| Read-only | Bạn có thể xem và so sánh nhưng không khôi phục phiên bản này. | You can inspect and compare but cannot restore this version. | Explain permission/mobile policy; readable snapshot stays selectable. | [OPEN]; historical row in `default` is a partial reference |

### AI settings — 22/22 expanded states

**Reference deltas AR-05/06:** counters measure the NFC-normalized, end-trimmed projection of the retained draft in Unicode code points, never UTF-16 `value.length` or grapheme count; actual server limits win. Do not apply native `maxlength` at a code-point cap or truncate typing, paste or IME input. Preserve the full draft, validate after composition commits and on blur/correction/submit, and show associated over-limit feedback with Save blocked. Below/at/above mean `limit−1`, `limit`, `limit+1`; include supplementary characters, decomposed Vietnamese accents, paste/IME and correction in each cap's evidence.

Visual counters remain ordinary non-live descriptions associated by `aria-describedby`, outside live/status ancestors. One separate polite region announces entering at-limit/over-limit states, returning within the limit and meaningful submit outcomes only; updates within the same state, IME intermediate input and background refresh are silent. Do not duplicate a focused error-summary announcement. The frozen counter's native UTF-16 truncation and per-keystroke `aria-live` are reference defects, not boundary or speech evidence; actual AT output remains unverified.

| State | Region behaviour and vi string | en string | A11y note | Mockup query |
| --- | --- | --- | --- | --- |
| No settings yet | Đang dùng mẫu mặc định, phiên bản 0; chưa có bản lưu. | Using default template version 0; no saved settings exist yet. | Neutral notice; ETag `"0"`, null time/id not fabricated. | `?state=empty` as first-use reference |
| Dirty draft | Cài đặt có thay đổi chưa lưu. | Settings have unsaved changes. | Persistent notice; preview must not overwrite silently. | [OPEN]; dirty form specimen |
| Writing style below limit | Phong cách viết: 249 / 250 ký tự. | Writing style: 249 / 250 characters. | Counter associated with writing-style field, valid. | [OPEN]; boundary specimen |
| Writing style at limit | Phong cách viết: 250 / 250 ký tự; đã đạt giới hạn. | Writing style: 250 / 250 characters; limit reached. | Valid, no warning conveyed by colour alone. | [OPEN]; boundary specimen |
| Writing style above limit | Phong cách viết: 251 / 250 ký tự; giảm nội dung trước khi lưu. | Writing style: 251 / 250 characters; shorten it before saving. | Invalid field and linked summary; Save blocked, text retained. | `?state=validation-error` generic reference; exact boundary [OPEN] |
| Coverage below limit | Chiến lược bao phủ: 1499 / 1500 ký tự. | Coverage strategy: 1499 / 1500 characters. | Associated counter, valid. | [OPEN]; boundary specimen |
| Coverage at limit | Chiến lược bao phủ: 1500 / 1500 ký tự; đã đạt giới hạn. | Coverage strategy: 1500 / 1500 characters; limit reached. | Valid and fully readable. | [OPEN]; boundary specimen |
| Coverage above limit | Chiến lược bao phủ: 1501 / 1500 ký tự; giảm nội dung trước khi lưu. | Coverage strategy: 1501 / 1500 characters; shorten it before saving. | Invalid, Save blocked; no truncation. | `?state=validation-error` generic reference; exact boundary [OPEN] |
| Exclusions below limit | Quy tắc loại trừ: 1499 / 1500 ký tự. | Exclusion rules: 1499 / 1500 characters. | Associated counter, valid. | [OPEN]; boundary specimen |
| Exclusions at limit | Quy tắc loại trừ: 1500 / 1500 ký tự; đã đạt giới hạn. | Exclusion rules: 1500 / 1500 characters; limit reached. | Valid, text remains editable on desktop. | [OPEN]; boundary specimen |
| Exclusions above limit | Quy tắc loại trừ: 1501 / 1500 ký tự; giảm nội dung trước khi lưu. | Exclusion rules: 1501 / 1500 characters; shorten it before saving. | Invalid, Save blocked; retained draft. | `?state=validation-error` generic reference; exact boundary [OPEN] |
| Step detail below limit | Mức chi tiết của bước: 199 / 200 ký tự. | Step detail: 199 / 200 characters. | Associated counter, valid. | [OPEN]; boundary specimen |
| Step detail at limit | Mức chi tiết của bước: 200 / 200 ký tự; đã đạt giới hạn. | Step detail: 200 / 200 characters; limit reached. | Valid; no false error at the cap. | [OPEN]; boundary specimen |
| Step detail above limit | Mức chi tiết của bước: 201 / 200 ký tự; giảm nội dung trước khi lưu. | Step detail: 201 / 200 characters; shorten it before saving. | Invalid, Save blocked; summary link to field. | `?state=validation-error` generic reference; exact boundary [OPEN] |
| Template preview | Đang xem trước mẫu; bản nháp và lịch sử chưa thay đổi. | Previewing a template; the draft and history are unchanged. | Named preview dialog; full field diff, Cancel retains draft. | [OPEN]; preview dialog, no dedicated query |
| Cancelled replacement | Đã hủy thay mẫu; bản nháp của bạn không đổi. | Template replacement canceled; your draft is unchanged. | Return focus to preview opener; polite status once. | [OPEN]; cancellation specimen |
| Save loading | Đang lưu phiên bản cài đặt mới. | Saving a new settings version. | Busy Save region, duplicate submit blocked. | `?state=submitting` |
| Save error | Không lưu được; bản nháp cài đặt được giữ lại. | Could not save; the settings draft is retained. | Persistent error and explicit retry; no fake version. | `?state=error` |
| Save success | Đã lưu phiên bản cài đặt mới. | A new settings version was saved. | Polite response-backed confirmation with actual version. | `?state=saved` |
| 412 | Cài đặt đã thay đổi; bản nháp của bạn được giữ lại. | Settings changed; your draft is retained. | R conflict, current/base version and fetched comparison only. | `?state=412` |
| Read-only version | Phiên bản cài đặt này chỉ để đọc. | This settings version is read-only. | Readable values and diff; no disabled-field substitute. | [OPEN]; history-version specimen |
| Opt-in off | AI chưa được cho phép; lưu cài đặt không thay đổi chấp thuận. | AI is not opted in; saving settings does not change consent. | Persistent read-only notice/admin link; manual authoring remains. | [OPEN]; estimate-backed notice, never a new consent control |

### Import/export — 12/12 expanded states

| State | Region behaviour and vi string | en string | A11y note | Mockup query |
| --- | --- | --- | --- | --- |
| No file | Chưa chọn tệp để nhập. | No file has been selected for import. | Labelled file control; Preview unavailable with reason. | `?state=no-file` |
| Unsupported | Định dạng tệp không được hỗ trợ. | The file format is not supported. | Inline file error; available formats explicit. | `?state=unsupported` |
| Malformed | Không đọc được cấu trúc tệp; chưa nhập dữ liệu. | The file structure could not be read; nothing was imported. | Linked summary and file field error, preserve inspection context. | `?state=malformed` |
| Preview loading | Đang kiểm tra trước các dòng; chưa tạo phiên bản. | Previewing rows; no version has been created. | Busy preview only; no commit request. | `?state=loading` |
| Row errors | Sửa lỗi tại các ô được đánh dấu trước khi nhập. | Fix errors in the identified cells before importing. | Keyboard summary links to cell/field; error text not colour alone. | `?state=validation` |
| Duplicate ids | Có mã trùng; hãy xử lý trước khi nhập. | Duplicate ids were found; resolve them before importing. | Identify each supplied row; no guessed resolution/upsert. | `?state=duplicate` |
| Mixed valid/invalid | Có lỗi được báo; các dòng không được báo chưa kiểm chứng. Chưa ghi dòng nào. | Errors were reported; unreported rows are unverified. No rows were written. | Keep every row inspectable; count reported problems (≤100 details), never infer valid/invalid totals from a 422. Confirmation blocked, no partial import; see [import/export](authoring-import-export.md#states). | `?state=mixed`; specimen split totals are illustrative only |
| Import denied | Bạn không có quyền nhập; không tạo phiên bản mới. | You do not have permission to import; no new version was created. | Explanation alongside read-only preview if reading is authorized. | `?state=denied` |
| Success | Đã nhập dữ liệu theo phạm vi được xác nhận. | Data was imported within the confirmed scope. | Polite status; use returned batch/counts, no fabricated actor/time. | `?state=success` |
| No selection | Chưa chọn ca kiểm thử để xuất. | No test cases are selected for export. | Selection scope stated; no automatic whole-project fallback. | [OPEN]; selection-specific export specimen |
| Empty export scope | Phạm vi xuất không có ca kiểm thử. | The export scope contains no test cases. | Keep scope/format readable; explicit change-scope control. | `?state=export-empty` |
| Export unavailable | Chưa thể xuất phạm vi này; hãy xem lý do. | This scope cannot currently be exported; inspect the reason. | Persistent failure and explicit recovery; never fake a download. | `?state=export-unavailable` |

### Usage credits — 7/7 expanded states

| State | Region behaviour and vi string | en string | A11y note | Mockup query |
| --- | --- | --- | --- | --- |
| Balance present | Số dư tín dụng AI thuộc tổ chức; kỳ sử dụng được ghi riêng. | The AI credit balance belongs to the organization; the usage period is shown separately. | Semantic facts, exact supplied value/cap/as-of; no currency. | `?state=default` illustrative only |
| Confirmed zero balance | Số dư được xác nhận là 0; soạn thủ công vẫn khả dụng. | The confirmed balance is 0; manual authoring remains available. | Say confirmed zero, not unavailable; no inferred release result. | [OPEN]; exhausted-balance specimen |
| Unavailable balance | Không có dữ liệu số dư tín dụng AI; không hiển thị là 0. | AI credit balance is unavailable and is not shown as 0. | Persistent unavailable reason; independently known usage remains. | [OPEN]; missing-balance specimen |
| No usage | Chưa có thao tác sử dụng tín dụng AI được ghi nhận. | No AI-credit usage actions have been recorded. | Neutral empty table, retain independently supplied balance. | [OPEN]; `empty` is generic page reference only |
| No matches for period | Không có thao tác khớp kỳ sử dụng đã chọn. | No actions match the selected usage period. | Keep labelled UTC period/reset; distinct from no usage ever. | `?state=no-match` generic reference; credit-specific specimen [OPEN] |
| Opt-in off | AI chưa được cho phép; dữ liệu sử dụng trước đây vẫn có thể xem. | AI is not opted in; historical usage remains inspectable. | Use estimate `opt_in` only when authorized/available; do not make a second opt-in route or trigger an estimate merely to render usage. | [OPEN]; omit notice when no authorized estimate context |
| No usage:read | Bạn không có quyền xem mức sử dụng. | You do not have permission to read usage. | Hide sidebar entry; typed URL renders `/forbidden` heading, no credit region or partial page. | `?state=403` generic reference; route guard still required |

### Generation lifecycle, stages and live regions

In the generation progress region use one persistent `role=status`, `aria-live=polite`, `aria-atomic=true` for **transitions**, not an `aria-valuenow` progress bar. Keep it outside the busy results subtree so a terminal announcement is not suppressed. Update only when intake state, generation state or stage changes; identical polls and elapsed time do not re-announce. A terminal announcement can combine state and outcome once. Visible internal codes are not a second noisy alert. No forced focus on background completion; the user reaches the new result heading/link in the normal order.

| Returned value | vi announcement | en announcement | A11y / rendering rule |
| --- | --- | --- | --- |
| Intake `received` | Đã nhận yêu cầu đầu vào. | Input received. | First progress step only from the linked intake row. |
| Intake `extracting` | Đang trích xuất văn bản. | Extracting text. | Busy intake region, stage label remains text. |
| Intake `ready` | Văn bản đầu vào đã sẵn sàng. | Input text is ready. | Does not imply generation succeeded. |
| Intake `rejected` | Đầu vào bị từ chối; xem mã lý do. | Input rejected; inspect the reason code. | Persistent failure_code sentence below, no false ready step. |
| Generation `queued` | Yêu cầu tạo đang chờ xử lý. | Generation is queued. | Neutral waiting, no numeric queue position. |
| Generation `running` | Yêu cầu tạo đang được xử lý. | Generation is running. | Announce once on transition, not each poll. |
| Generation `succeeded` | Đã tạo xong; kết quả đang chờ xem xét. | Generation completed; output awaits review. | Stop polling; expose returned output_refs/provenance and explicit accept. |
| Generation `failed` | Yêu cầu tạo không thành công; xem mã kết quả. | Generation failed; inspect the outcome code. | Stop polling; do not invent drafts or refund. |
| Generation `blocked` | Yêu cầu tạo bị chặn; xem mã lý do. | Generation was blocked; inspect the reason code. | Stop polling; display outcome_code as-is and its sentence. |
| Generation `canceled` | Đã hủy yêu cầu tạo. | Generation was canceled. | Stop polling; no automatic restart or approval. |
| Stage `queued` | Giai đoạn: chờ xử lý. | Stage: queued. | Reported stage, not a computed fraction. |
| Stage `redact` | Giai đoạn: che dữ liệu nhạy cảm. | Stage: redacting sensitive data. | Announce only returned transition. |
| Stage `generate` | Giai đoạn: tạo nội dung. | Stage: generating content. | No invented intermediate output or percentage. |
| Stage `validate` | Giai đoạn: kiểm tra cấu trúc kết quả. | Stage: validating output structure. | Not success until returned terminal state. |
| `eta_class=short` | Thời gian dự kiến: ngắn. | Expected wait: short. | Optional reported class only; not an exact countdown. The 60 s short/long boundary is `[INFERENCE]` in 13.3.1 §4. |
| `eta_class=long` | Thời gian dự kiến: dài. | Expected wait: long. | Optional reported class only; no ETA from client clock. |

Polling honours `Retry-After`, pauses when the tab is hidden, stops on route leave, and stops at all four terminal states. Announce the current state once when returning to the visible tab if it changed while hidden, not a replay of missed polls. The intake step and generation stages retain separate row provenance. Duration is only from supplied start/end instants; never derive progress from elapsed duration.

## Copy length (vi vs en)

### Longest review specimens

These are deliberately long **proposed review specimens**, not measurements claiming which current mockup string is widest. In each locale test this sentence as an unclamped description/reason, alongside the longest state-copy sentence in its table; at 1280/360 px allow natural word wrapping without font shrinkage. For user text additionally exercise schema maxima (node title 200; case title 500; step action/expected/data 2000 code points) and six-level paths. Machine ids remain inspectable in full. Do not clamp the only error or prerequisite explanation.

| Screen | vi longest stress specimen | en longest stress specimen |
| --- | --- | --- |
| Tree + table | Chọn các dòng đang hiển thị trong danh mục kiểm tra tính hợp lệ của địa chỉ giao hàng trước khi xuất các ca kiểm thử đã chọn. | Select only the visible rows in the delivery-address validation category before exporting the selected manual test cases. |
| Editor | Phiên bản trên máy chủ đã thay đổi; toàn bộ điều kiện tiên quyết, bước thực hiện và kết quả mong đợi chưa lưu của bạn được giữ lại để xem xét. | The server version changed; all your unsaved preconditions, action steps and expected results are retained for inspection. |
| Pending changes | Lô thay đổi đã cũ; toàn bộ nhận xét, thao tác gộp, tách, xóa và lý do chưa lưu được giữ nguyên, chưa áp dụng thay đổi nào. | The batch is stale; all comments, merges, splits, deletions and the unsaved reason are retained, and nothing has been applied. |
| Generation | Không đọc được ước tính tín dụng AI cho yêu cầu hiện tại; chưa thể xác nhận tạo, nhưng bạn vẫn có thể tiếp tục soạn thủ công. | The AI credit estimate for the current requirement cannot be read; generation cannot be confirmed, but manual authoring remains available. |
| Timeline | Khôi phục nội dung phiên bản đã chọn sẽ thêm một phiên bản mới với lý do của bạn và không ghi đè bất kỳ phiên bản lịch sử nào. | Restoring the selected version appends a new version with your reason and never overwrites any historical version. |
| AI settings | Chiến lược bao phủ vượt giới hạn 1500 ký tự; hãy giảm nội dung trước khi lưu phiên bản mới, bản nháp hiện tại được giữ nguyên. | Coverage strategy exceeds the 1500-character limit; shorten it before saving a new version, while the current draft remains intact. |
| Import/export | Tệp có dòng thiếu kết quả mong đợi, mã trùng hoặc tiền tố dự án không hợp lệ; hãy sửa toàn bộ lỗi trước khi xác nhận nhập. | The file contains missing expected results, duplicate ids or invalid project prefixes; resolve every error before confirming import. |
| Usage credits | Không có dữ liệu số dư tín dụng AI của tổ chức tại thời điểm được cung cấp; không suy ra số dư bằng cách cộng các dòng sử dụng đang hiển thị. | The organization AI credit balance is unavailable at the supplied as-of time; do not infer it by summing the visible usage rows. |

Shared mobile notice: **vi** “Chế độ này chỉ để đọc. Mở màn hình rộng hơn để chỉnh sửa hoặc áp dụng thay đổi.” / **en** “This view is read-only. Open a wider screen to edit or apply changes.” Shared unknown-value label: **vi** “Không có dữ liệu” / **en** “Unavailable”, followed by its supplied reason rather than a false zero.

### Outcome-code copy — exactly 10 rows

✅ Vocabulary is the generation plan §4 `outcome_code`: seven gateway statuses plus exactly three generation-only tokens. Show the raw token in a labelled code field **as-is**, unchanged and untranslated, alongside one localized sentence. A newly added gateway status remains visible as-is; never map it to the nearest known token. `ok` describes a gateway outcome, not human acceptance or a release PASS.

| outcome_code (as-is) | vi sentence | en sentence |
| --- | --- | --- |
| `ok` | Lần gọi AI hoàn tất thành công; kết quả vẫn cần được xem xét. | The AI call completed successfully; its output still requires review. |
| `timeout` | Lần gọi AI đã hết thời gian chờ. | The AI call timed out. |
| `schema_violation` | Kết quả AI không đúng cấu trúc bắt buộc. | AI output did not match the required schema. |
| `budget_exceeded` | Lần gọi AI bị chặn do vượt ngân sách được áp dụng. | The AI call was blocked by the applicable budget limit. |
| `error` | Lần gọi AI không thành công do lỗi được máy chủ ghi nhận. | The AI call failed with the error recorded by the server. |
| `blocked_no_optin` | Lần gọi AI bị chặn vì chưa có chấp thuận sử dụng AI. | The AI call was blocked because AI opt-in was absent. |
| `blocked_leak` | Lần gọi AI bị chặn do kiểm tra dữ liệu nhạy cảm. | The AI call was blocked by the sensitive-data check. |
| `cache_hit` | Kết quả đã xác thực được dùng lại với chi phí 0 tín dụng AI và vẫn cần xem xét. | Validated output was reused at a cost of 0 AI credits and still requires review. |
| `blocked_kill_switch` | Yêu cầu tạo bị chặn vì dịch vụ AI đã bị tắt trước lần gọi. | Generation was blocked because AI service was disabled before the call. |
| `canceled` | Yêu cầu tạo đã bị hủy và không tự khởi động lại. | Generation was canceled and will not restart automatically. |

### Extraction-failure copy — exactly 10 rows

✅ These tokens are post-intake job failures from generation plan §8: show them beside intake `state=rejected`. Request-time `media_type_not_allowed`, `empty_input` and `size_limit_exceeded` are separate validation reasons, not extra failure_code rows. In particular, do not introduce `unsupported_media_type` as an eleventh extraction code.

| failure_code (as-is) | vi sentence | en sentence |
| --- | --- | --- |
| `container_mismatch` | Cấu trúc thực tế của tệp không khớp loại tệp đã khai báo. | The actual file container does not match the declared type. |
| `macro_present` | Tệp chứa macro nên không được trích xuất. | The file contains macros and cannot be extracted. |
| `encryption_unsupported` | Tệp được mã hóa theo cách không được hỗ trợ. | The file uses unsupported encryption. |
| `external_reference_present` | Tệp chứa tham chiếu bên ngoài nên không được trích xuất. | The file contains external references and cannot be extracted. |
| `archive_limit_exceeded` | Tệp vượt giới hạn giải nén được áp dụng. | The file exceeds the applicable archive extraction limit. |
| `page_limit_exceeded` | Tệp vượt giới hạn số trang được áp dụng. | The file exceeds the applicable page limit. |
| `char_limit_exceeded` | Văn bản trích xuất vượt giới hạn ký tự được áp dụng. | Extracted text exceeds the applicable character limit. |
| `no_extractable_text` | Không tìm thấy văn bản có thể trích xuất trong tệp. | No extractable text was found in the file. |
| `extraction_timeout` | Quá trình trích xuất văn bản đã hết thời gian chờ. | Text extraction timed out. |
| `extraction_failed` | Quá trình trích xuất văn bản không thành công. | Text extraction failed. |

For an **unknown code** in either vocabulary retain its escaped raw token and show exactly this generic reason: **vi** “Máy chủ trả về mã chưa được nhận diện; hãy giữ mã này khi liên hệ hỗ trợ.” / **en** “The server returned an unrecognized code; retain this code when contacting support.” Unknown is not success, zero cost, permission denial or a retryable error by inference. A null code is “Unavailable / Không có dữ liệu”, not the unknown-code reason. Internal `error.code` is separately labelled opaque escaped text: never branch on it, translate it, turn it into a client response code, or deduplicate it against `outcome_code`; both may legitimately show `budget_exceeded`.

## Responsive notes

### 1280 px and 360 px behaviour

[Layout](layout.md#breakpoints-and-navigation), [shared state sheet](state-sheet.md#shared-accessibility-and-themes), and [UI rules](../../engineering/ui-rules.md#theme-i18n-and-accessibility) remain binding. At 1280 px expanded navigation and the referenced desktop split/stack layouts apply; table overflow belongs to a named local scroller, not the page. At 360 px drawer navigation and stacked labelled records retain full readable content; six-level hierarchy indentation must not consume the text width, and comparisons stack before/after. Retain normal Vietnamese word wrapping and a long-token escape hatch; never hide page overflow to mask clipped content.

Below 768 px authoring is **read-only for editing/apply**, not a blanket ban on navigation or inspection. Keep hierarchy disclosure, filters, reset, paging, locale/theme, history/diff inspection and permitted links. Do not allow case/settings Save, restore, queued writes/discard/apply, generation/cancel/accept or import commit; the authoring selection/export workflow must not bypass the desktop mutation policy. Export availability follows the sibling screen's explicitly authorized read-only scope, never an inferred selected scope. Show the shared desktop-required notice and a readable snapshot instead of vanished field content. Re-check viewport policy inside handlers and confirmation submit after resize, not only in CSS. When a focused mutation control disappears on desktop→mobile resize, hand focus after render to the corresponding read-only region heading/notice; never leave it on a removed node. An open confirmation cancels without a request and returns to that surviving target.

**Reference delta AR-03:** drive control removal and focus handoff from the same viewport `NarrowMutationPolicy` state. On 768→767px transition, preserve draft/queue/reason/selection context and cancel every unsubmitted mutating confirmation through the owned dialog lifecycle, whether or not it contains a form; clear pending consent and release the focus trap/background inert state without a request. After the read-only projection renders, focus the corresponding visible summary heading/desktop-required notice (`tabindex="-1"`), including when an editor field or the dialog held focus. Never focus BODY, a removed opener or a hidden confirmation button. Do not steal focus from a surviving inspection control. Widening restores the retained draft but neither reopens nor submits consent. This does not claim to cancel a request already dispatched; handler/submit checks still forbid any new narrow mutation.

**Reference delta AR-01/02:** target sizing is the real non-overlapping activation box, not the glyph: ≥24×24px fine pointer, ≥44×44px coarse pointer with existing control/touch tokens and 8px gaps where feasible. This includes tree disclosures, checkbox labels/hit areas and Pending/Import implementation-metadata disclosures; no minimum-size exception is proposed for them. Metadata summaries grow beyond the reference's 20px text line. Machine IDs keep their intrinsic column budget and shared non-wrapping copyable/full-value treatment, not forced word fragments; the named approved local table scroller remains. Measure rectangles, full-value recovery and focus-ring clearance rather than inferring a pass from no page overflow.

### Manual WCAG 2.2 AA review checklist

⚠️ **All runs below are required, not performed or passed in this docs-only assignment.** Every row explicitly lists its screens and its run matrix: **1280 px + 360 px, light + dark, vi + en**. Also perform the 320-CSS-px reflow equivalent where named; 360 px alone does not prove 1.4.10. Each run records state, focused selector, screenshot/AT observation and pass/fail in the independent review record. Abbreviations name screens, not optional subsets: **Tree = Tree + table; Pending = Pending changes; Settings = AI settings; Import = Import/export; Usage = Usage credits**.

| Criterion | Screens | Required run | Manual procedure and expected result | Evidence status |
| --- | --- | --- | --- | --- |
| 1.1.1 Non-text content | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Decorative icons/skeletons hidden; meaningful icon buttons have localized names; text describes state. | ⚠️ Not run |
| 1.3.1 Info and relationships | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Inspect headings, nested lists, labelled fields, table headers and stacked record associations; no CSS-only field labels. | ⚠️ Not run |
| 1.3.2 Meaningful sequence | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Read DOM/AT order against Regions, including stacked diffs and mobile summaries. | ⚠️ Not run |
| 1.3.3 Sensory characteristics | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Instructions name field/action, not only colour, icon position or “right-hand panel”. | ⚠️ Not run |
| 1.3.4 Orientation | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Rotate narrow viewport without an orientation lock; readable information/navigation persists. | ⚠️ Not run |
| 1.4.1 Use of colour | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Distinguish selection, dirty, stale, invalid and blocked with text/semantics as well as colour. | ⚠️ Not run |
| 1.4.3 Contrast (minimum) | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Measure actual composite foreground/background: normal text ≥4.5:1, large text ≥3:1, including muted reasons. | ⚠️ Not run; token table is precedent only |
| 1.4.4 Resize text | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Set 200% text; labels, dialogs, counters and full reasons remain readable/reachable without lost function. | ⚠️ Not run |
| 1.4.5 Images of text | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | All authored instructions/states remain real text; mockup images are not production text. | ⚠️ Not run |
| 1.4.10 Reflow | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en; additionally 320 CSS px | Zoom/reflow to 320 CSS px; no page-level two-dimensional scroll, lost controls or clipped reasons; any genuine comparison exception stays local and justified. | ⚠️ Not run |
| 1.4.11 Non-text contrast | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Measure required control boundaries, selected indicators and focus against adjacent colours ≥3:1. | ⚠️ Not run |
| 1.4.12 Text spacing | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Apply line 1.5×, paragraph 2×, letter 0.12em and word 0.16em together; no clipped labels/cells/errors. | ⚠️ Not run |
| 1.4.13 Content on hover or focus | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Full-value/tooltip disclosures are dismissible, hoverable and persistent; keyboard/touch can recover full text. | ⚠️ Not run |
| 2.1.1 Keyboard | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Complete permitted inspection and desktop authoring without pointer; disclosure/selection are separate; mobile cannot mutate. | ⚠️ Not run |
| 2.1.2 No keyboard trap | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Tab through scrollers; dialogs/drawer trap only while open and Escape/Cancel restores focus. | ⚠️ Not run |
| 2.1.4 Character-key shortcuts | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | No global unmodified-character shortcut fires while typing; any future shortcut is remappable/off/focus-scoped. | ⚠️ Not run |
| 2.2.1 Timing adjustable | Editor, Pending, Generation, Settings, Import | 1280/360 px · light/dark · vi/en | No UI deadline discards draft/input; session expiry is explained and no mutation is resubmitted; distinguish external service timeout. | ⚠️ Not run |
| 2.2.2 Pause, stop, hide | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Reduced motion removes nonessential animation; generation polling pauses while hidden and does not auto-scroll reading content. | ⚠️ Not run |
| 2.3.1 Three flashes or below threshold | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Loading/errors introduce no flashing indicator. | ⚠️ Not run |
| 2.4.1 Bypass blocks | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | First skip link reaches main content without traversing every shell item. | ⚠️ Not run |
| 2.4.2 Page titled | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Localized document title identifies the screen and safe project context; state selector does not rewrite it to an unrelated page. | ⚠️ Not run |
| 2.4.3 Focus order | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Follow Regions order, validation-summary links and modal focus return; refresh never steals focus. | ⚠️ Not run |
| 2.4.4 Link purpose (in context) | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Case/version/usage links identify destination in context, not repeated ambiguous “Open”. | ⚠️ Not run |
| 2.4.5 Multiple ways | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Standalone pages reachable through shell plus contextual breadcrumb/link; sequential generation steps retain Back and context. | ⚠️ Not run |
| 2.4.6 Headings and labels | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Unique h1, named regions, persistent field labels and explicit scope/units. | ⚠️ Not run |
| 2.4.7 Focus visible | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Keyboard focus shows shared 2px ring/2px offset, not clipped by cards/scrollers. | ⚠️ Not run |
| 2.4.11 Focus not obscured (minimum) | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Tab forward/back near sticky topbar/table headers, toast and loaded/virtual row boundary; focused control never entirely covered; no centred-on-focus hiding under chrome. | ⚠️ Not run |
| 2.5.1 Pointer gestures | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Single-pointer controls perform all actions; no multipoint/path gesture is required. | ⚠️ Not run |
| 2.5.2 Pointer cancellation | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Activation on up/click; pointer-down then moving away does not commit; destructive actions still confirm. | ⚠️ Not run |
| 2.5.3 Label in name | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Accessible name includes visible localized label, especially Save/Apply/Restore and disclosure controls. | ⚠️ Not run |
| 2.5.4 Motion actuation | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | No device-motion-only operation or accidental motion-triggered mutation. | ⚠️ Not run |
| 2.5.7 Dragging movements | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | No action requires dragging: use tree buttons, URL sort/filter, step buttons and native file picker; any future drag has single-pointer/keyboard alternative. | ⚠️ Not run |
| 2.5.8 Target size (minimum) | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Measure all interactive targets ≥24×24 CSS px, including disclosures, chips, checkboxes, icon buttons and dialog close; project coarse-pointer policy is 44×44. | ⚠️ Not run |
| 3.1.1 Language of page | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | The document `lang` follows the UI locale on every authoring route and survives locale switch; no route retains a stale attribute. | ⚠️ Not run |
| 3.1.2 Language of parts | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Supplied alternate-language content (vi inside en UI and vice versa) is marked when its language is known; raw machine codes are never wrapped in a wrong-language span or mistranslated. | ⚠️ Not run |
| 3.2.1 On focus | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Moving focus by Tab or programmatically never navigates, mutates, expands or commits; only explicit activation changes state. | ⚠️ Not run |
| 3.2.2 On input | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Changing a field, select or filter value never silently commits a mutation or moves focus; template selection previews and filters stay reversible until explicit Save/Apply. | ⚠️ Not run |
| 3.2.3 Consistent navigation | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Group labels and journey order stay identical across authoring screens and visits; phase-hidden entries are absent, not disabled teasers; focus order does not change between visits. | ⚠️ Not run |
| 3.2.4 Consistent identification | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | The same function carries the same localized label and permission explanation on every authoring screen (Save, Apply, Restore, Export, disclosure controls). | ⚠️ Not run |
| 3.2.6 Consistent help | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | If shell help is present, its relative order stays consistent; do not add a new help mechanism solely for this sheet. | ⚠️ Not run |
| 3.3.1 Error identification | Editor, Pending, Generation, Settings, Import; Tree and Usage filters | 1280/360 px · light/dark · vi/en | Every error is textual, associated with its field or region and retained until corrected; never colour-only, toast-only or a vanished panel. | ⚠️ Not run |
| 3.3.2 Labels or instructions | Editor, Pending, Generation, Settings, Import; Tree and Usage filters | 1280/360 px · light/dark · vi/en | Every input carries a persistent visible label with the required/optional convention and hints; no placeholder-only labelling, and caps come only from supplied configuration. | ⚠️ Not run |
| 3.3.3 Error suggestion | Editor, Pending, Generation, Settings, Import; Tree and Usage filters | 1280/360 px · light/dark · vi/en | Where a safe correction is known, suggest it in text (shorten over-limit content, choose a supported format, reload on 412); never guess a server rule or invent a cap. | ⚠️ Not run |
| 3.3.4 Error prevention (data) | Editor, Pending, Generation, Timeline, Settings, Import | 1280/360 px · light/dark · vi/en | Review/confirm destructive batch, restore, dirty replacement, credit spend and import; retain drafts and immutable history; no automatic resubmit. | ⚠️ Not run |
| 3.3.7 Redundant entry | Editor, Pending, Generation, Settings, Import | 1280/360 px · light/dark · vi/en | Stage changes, retry and validation do not require retyping already supplied safe data; preserve fields across 412 without auto-merging. | ⚠️ Not run |
| 4.1.2 Name, role, value | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | Check disclosure expanded state, selected tab, mixed select-visible checkbox, invalid/busy and named dialogs; no fake ARIA tree. | ⚠️ Not run |
| 4.1.3 Status messages | Tree, Editor, Pending, Generation, Timeline, Settings, Import, Usage | 1280/360 px · light/dark · vi/en | One polite meaningful transition/count; generation lifecycle/stage updates without focus theft, per-poll announcements or timer spam. | ⚠️ Not run |

No media playback, prerecorded/live audio/video, authored authentication challenge or sensory-input verification is introduced by these surfaces; media-specific criteria, 1.4.2 audio control and 3.3.8 authentication are outside this design change. Shared sign-in remains subject to its own accessibility specification. If one is added here, expand this checklist before approval. Each criterion owns exactly one row, so a pass can never be inherited from a sibling criterion; a row is signed off only by its own named screens, run and procedure.

## Invariants

- ✅ [Authoring plan §5.2](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md#52-shapes-the-screens-rely-on): null/unavailable is never zero; only confirmed zero is rendered as 0. Credits are not money, quota, a release verdict or a client-derived balance.
- ✅ Conditional writes use the owning read's tag: case `ETag`; selected-change batch `ETag`; settings quoted `version_no`; role-profile quoted version; generation detail `version`/`ETag` on accept. Missing tag is the server's 422, not a client-invented 428. Restore uses its documented intent key and appends; do not invent an If-Match requirement the route lacks.
- A 412 preserves every unsaved field/queue/reason, applies nothing for the stale atomic batch/accept intent, and offers explicit Reload only. Error `details[]` pointers are not current snapshots; fetch authorized current content for comparison and state unavailable fields honestly. No auto-merge, overwrite, re-submit or Saved message on conflict.
- Use one `Idempotency-Key` per mutation intent; retry the same intent with the same key, never derive another version from a duplicate receipt. Accept is explicit `refs[]`, including partial accept; stale accept accepts nothing. A cache hit uses the identical accept/concurrency path.
- Generation progress is stage/lifecycle only — never a percentage, ETA, fabricated queue position or completion from elapsed time. No generation row is invented for request-time opt-in, credit, gateway or input refusal. No refund or failed-item version is inferred.
- Cached output is validated reuse: estimate shows `cache_hit=true` and cost 0 with reason before confirmation; terminal detail is `succeeded`/`cache_hit`/cost 0 with materialized draft refs and pinned provenance. It is not a free bypass of gates, review or human accept; no new provider call is inferred.
- All user/AI/import strings, raw outcome/failure/internal codes and snapshots are escaped plain text, never `innerHTML`. Machine identifiers use shared safe rendering/bidi isolation and copy policy; no silent stripping of format controls. UI locale changes labels, not supplied codes, identifiers, version identity or output language.
- Lists are keyset-paged, not “load all”; repeated cursor is a visible contract error, never a shorter successful list. Selection is visible rows only, never implicit cross-page selection, bulk approval or silently widened export.
- History is immutable; restore appends. Counters validate normalized code points without truncating input. Templates preview/change a draft only, never rewrite history. The four mandatory settings limits are 250/1500/1500/200; the current external schema also supplies description 1000, environment URL 2048 and each priority criterion 250, overriding the older brief's missing-limit statement.
- Permission denial and responsive read-only are distinct. Missing `usage:read` hides its nav entry and typed URL shows only forbidden; no credit fetch/partial region. Other mutation denials remain explained without hiding authorized readable data. Below 768 px handler-time viewport policy also blocks side effects after a resize.
- ⚠️ Source comparison is not rendered a11y evidence. [Tokens](tokens.md#contrast-table) and [state sheet](state-sheet.md#shared-accessibility-and-themes) provide shared recipes, not a pass for this proposed design's composites. No build, browser, AT, linter or formatter was run for this file.

## Open points

| Open item | Recommended default / required resolution |
| --- | --- |
| [OPEN D1] Intake/attachment fields absent from OpenAPI | Publish owning generation/intake schemas; show authorized metadata only, unavailable scan state/caps otherwise; no invented “clean” or extraction success. |
| [OPEN D2] Estimate/202 receipt absent from OpenAPI | Adopt owning plan route 3/4 shapes before integration; unreadable estimate blocks AI confirmation, never manual authoring. |
| [OPEN D3] Generation detail/progress/accept fields absent from OpenAPI | Adopt owning route 6/8 shapes; no percentage, guessed per-item results/refund or invented accept tag; partial-result UI waits for explicit returned refs/reasons. |
| [OPEN D4] Generated output/provenance fields absent from OpenAPI | Render owning plan's pinned refs, real model and server UTC timestamps; omit invented prompt hash and client-clock provenance. |
| [OPEN D5] Credit balance/usage response absent from OpenAPI | Use authoring plan §5.3 recommended shape and explicit org scope; retain unavailable vs zero, independent summaries and period boundaries. |
| [OPEN D6] Source/owner filters and pending counts absent | Use plan recommendations for optional server filters and `pending_change_count`; absent count “—”, absent metadata unavailable; no false server-filter success. |
| [OPEN D7] Preview/error/export scope metadata incomplete | Use local file preview explicitly, authoritative error pointers when supplied, whole invalid/mixed import blocked, exact supported export scope only; disable selected export rather than widen it. |
| [OPEN D8] Fixture-only identities/names/tariffs/refunds | Keep labelled illustration in reference artifacts; production shows actual authorized ids/version fields and response-backed cost/refund only. |
| [OPEN] Dedicated state specimens marked `[OPEN]` in matrix | Add each named state to implementation fixtures/review coverage, preserving its specified copy, region and a11y behaviour; do not invent undeclared mockup query values or defer the behaviour until build. |
| [OPEN] Static timeline and partial generation reference gaps | Keep immutable fetched snapshots and returned result refs as truth; add independent paging/restored/partial-result fixtures, no fictional timeline query or per-item creation. |
| [OPEN] Usage opt-in notice without estimate context | Show only a previously authorized applicable estimate's `opt_in`; otherwise omit the notice as unavailable, never add a second consent route or require `authoring:generate` to read usage. |
| [OPEN] Manual checklist and owner gate | Independent observations and this docs-only correction disposition are in [authoring-review.md](authoring-review.md). The frozen references are unchanged; complete criterion/run evidence and owner approval remain open. Resolve those gates before implementation; this file does not record an accessibility pass or approval. |

Source-discovery evidence: graph `get_file_outline`, `search_graph`, `get_code_snippet` and `check_index_coverage` used before source reads. The shared `RequestStateComponent.handoffTarget` precedent resolves route handoff to the main heading; this sheet requires that heading to be programmatically focusable. Index evidence is best-effort for the main checkout, not this worktree: `ui-rules.md` and this new file had missing freshness, so the live worktree rules and source ranges were read directly. All quoted states/codes come from those live files. Source relevance batch: `typesafe/jev-1.14.0`, 2/2 done, 0 failed, cost $0.000026586; it scored the explicit vocabulary source below threshold, overridden by the assignment's direct requirement and the exact §4/§8 vocabulary reads, not used to omit it.
