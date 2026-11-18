Status: proposed, awaiting owner approval

# Authoring generation

Flow id: `authoring-generation` · CR-D-2 phase: **G1**.

Feature tasks: `13.4.1-T1.1`, `13.4.1-T1.2`, `13.4.1-T4`; pipeline owner `13.3.1-T1–T4`; role-profile selector delta for `13.2.2-T6`; credit owner `08.5.5-T2`.

Legend: ✅ verified source with a link; ⚠️ unverified runtime/visual behavior; `[INFERENCE]` recommended design interpretation; `[DECISION]` recorded owner decision only; `[OPEN]` unresolved shape or evidence with a recommended default. This document proposes behavior, not approval. No browser, screen-reader, provider, or API run is claimed.

Sources: [authoring plan §5–6 and T4](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md#5-api-shapes-commands-queries-endpoints), [generation pipeline §4–6](../../../plans/features/13-product-phases/13.3.1-generation-pipeline.md#4-data-model-and-migrations), [role profiles §5 and T6](../../../plans/features/13-product-phases/13.2.2-role-profiles-and-prompt-library.md#5-api-shapes-commands-queries-endpoints), [generation brief](../web-ui/prompts-crd2/B2-43-generation-flow.md), [OpenAPI](../../../contracts/openapi.yaml), [UI rules and A15 catalogue](../../engineering/ui-rules.md#shared-component-catalogue-a15).

**Contract gap that governs this file:** the generation, intake and role-profile routes of `13.3.1 §5` routes 1–8 and `13.2.2 §5` are **not yet in `contracts/openapi.yaml`**. A grep of the contract for `/intakes`, `/generations`, `role-profiles` and `role_profile_ref` returns no route or schema. Their shapes below are therefore traced to the owning plan and recorded `[OPEN]` against the contract, never presented as shipped OpenAPI. Fields that already exist in another shipped schema (checklist item, manual case, AI settings) are traced to that schema. Paths omit `/v1` as the server prefix; implementation calls generated SDK operations and never composes route strings.

| Data field | openapi schema path | state (traced / [OPEN]) |
| --- | --- | --- |
| Requirement text, attachment kind, media type, bytes | `[OPEN]` — `POST authoring/intakes` body is `13.3.1 §5` route 1, absent from OpenAPI | Recommended: use the plan's `{kind, feature_node_id?, artifact_id?, media_type?, bytes?, text?, target_language?}`; render caps only from the response |
| Intake state, caps, `failure_code`, extracted hash/count | `[OPEN]` — `GET authoring/intakes/{i}` route 2 absent from OpenAPI | Recommended: use route 2's exact response; never infer caps or failure text |
| Estimate `credit_cost_micro`, `credits_available_micro`, `cache_hit`, `cap_blocked`, `opt_in`, `model_tier`, `output_kind`, `output_schema_version` | `[OPEN]` — `POST authoring/generations/estimate` route 3 absent from OpenAPI | Recommended: render the eight returned fields as-is; unavailable balance/cost is Not available, never 0 |
| 202 receipt `generation_id`, `state`, `model_tier`, `cache_hit`, `credit_cost_micro` | `[OPEN]` — `POST authoring/generations` route 4 absent from OpenAPI | Recommended: keep the confirmed estimate beside the receipt; route to the detail read for truth |
| Generation state, `progress.stage`, `progress.eta_class`, `outcome_code`, `error`, `cost_micro` | `[OPEN]` — `GET authoring/generations/{g}` route 6 absent from OpenAPI | Recommended: use route 6; `outcome_code` rendered as-is, `error.code` opaque escaped text |
| Generation provenance `model`, `model_tier`, `ai_settings_ref`, `role_profile_ref`, `prompt_version`, `output_schema_version`, `input_hash` | `[OPEN]` — route 6 absent from OpenAPI | Recommended: render returned values verbatim; never derive a model or profile from the request |
| Draft references `output_refs[]` (`entity_kind`, `entity_id`, `version_no`, `state`) | `[OPEN]` — route 6 absent from OpenAPI | Recommended: name exactly these drafts before accept; no other entity is claimed created |
| Accept token `version`/ETag and `accepted_refs[]` | `[OPEN]` — routes 6/8 absent from OpenAPI | Recommended: replay the route-6 `version` as `If-Match`; 412 accepts nothing |
| Run timestamps `created_at`, `started_at`, `finished_at` | `[OPEN]` — route 6 absent from OpenAPI | Recommended: absolute UTC from the row; null before transition, never the client clock |
| Role-profile selection `profile_id`, `version_no`, `source` and compatible output kind | `[OPEN]` — `GET projects/{p}/role-profiles` and `13.2.2 §5` absent from OpenAPI | Recommended: required explicit selection; no implicit role from RBAC/locale and no client-supplied hash |
| Role-profile name/code, seed version, retired flag, allowed output kinds | `[OPEN]` — profile list/detail schemas absent from OpenAPI | Recommended: use the authorized list read; retired/kind-incompatible entries are explained, not silently substituted |
| Run note (optional, single run) | `[OPEN]` — part of the generation request, absent from OpenAPI | Recommended: NFC, ≤500 code points, secret-shaped refusal; never reused by a later run |
| Settings version shown in the page header | `contracts/openapi.yaml#/components/schemas/AiSettingsView.version_no` (with `.source`, `.settings_id`) | traced for the version number; the fixture label `ais-3` is not a schema field, so display a labelled version number, `[OPEN]` for any friendly alias |
| Checklist item title, description, priority, state, version | `contracts/openapi.yaml#/components/schemas/ChecklistItemDto.title`, `.description`, `.priority`, `.state`, `.version` | traced via an authorized checklist read; generation detail itself returns only `output_refs` |
| Generated case title, precondition, steps action/expected, priority, state, version | `contracts/openapi.yaml#/components/schemas/ManualCaseDetailDto.title`, `.precondition`, `.steps` → `ManualCaseStepDto.action`/`.expected`, `.priority`, `.state`, `.version` | traced via the authorized case read after accept; previews before accept are not case rows |
| Case code `SHOP-TC-*` | `contracts/openapi.yaml#/components/schemas/ManualCaseDetailDto.code` | traced only after the entity exists; a draft reference has an id, not a guaranteed code |
| Partial/refunded per-item credit outcome | `[OPEN]` — no generation response field supplies per-item refunds | Recommended: show refund only when supplied; otherwise Not available and no retry of successful work |
| `outcome_code` and extraction `failure_code` sentences | `[OPEN]` — closed vocabularies live in plans, not contract schemas | Recommended: render values as-is; copy in [authoring state and copy sheet](authoring-states.md) |

## v2 pages

| Page | Role | Phase | Valid `?state=` values |
| --- | --- | --- | --- |
| [43 — generation](../web-ui/output/43-generation/evidgate-generation.html) | Primary: input → estimate → progress → checklist review → test-case review | G1 | `default`, `loading`, `empty`, `error`, `403`, `404`, `401`, `429`, `412`, `submitting` (HTML line 85) |
| [42 — pending changes](../web-ui/output/42-pending-changes/evidgate-pending-changes.html) | Human checklist-review boundary; nothing generated is applied here | G1 | Related destination; its queue states are not generation query states |
| [41 — editor](../web-ui/output/41-test-case-editor/evidgate-test-case-editor.html) | Review a generated case | G1 | Related destination |
| [44 — AI settings](../web-ui/output/44-ai-settings/evidgate-ai-settings.html) | Settings version and profile-editor neighbour | G1 | Related destination; the profile editor is a 13.2.2-T6 delta beside it, not on this page |

✅ `empty` means “no requirement supplied”; it is not “no generations exist”. The mockup's attachment/extraction/AI failure specimens and its partial/all-success case panels are **local fixtures inside `default`** (lines 331–409), not query states. ⚠️ No `?state=` exists for any of the fifteen §6 states below except where mapped; do not invent URLs.

## Regions

Reading order follows the reference's stage tabs.

| Order | Region | Proposed behavior following the reference |
| --- | --- | --- |
| 1 | Skip link, shell, project scope, breadcrumbs | Registry context, current location, safe Back navigation that retains requirement text |
| 2 | Narrow read-only notice, page title, settings-version link (HTML 59–83) | Show project and the real settings version; never claim an alias the contract does not return |
| 3 | Request feedback (87–140) | Loading/error/403/404/401/429/412/submitting; retain input and generated results where safe |
| 4 | Unreviewed-output banner and five-stage navigator (143–158) | One visible statement that AI output stays unreviewed until a human acts; stage navigator is in-page tabs, not route navigation |
| 5 | Input stage: requirement text, attachment, supplied scan/extraction facts, scope rail (177–235) | Editable at desktop; supported types from the server; no invented caps; image is human reference only |
| 6 | Estimate stage: credit meter, opt-in, grant, settings version, Generate checklist (238–263) | Estimate **before** the call, explicit confirmation, blocked reasons, role-profile selector |
| 7 | Progress stage: state strip, four-step stage strip, cancel (265–285) | Named stages and state transitions only; no percentage/ETA; cancel only while queued/running |
| 8 | Checklist review stage: unreviewed suggestions, provenance, queued operations, link to page 42 (287–318) | AI text escaped; review boundary explicit; nothing applied or approved here |
| 9 | Test-case stage: preflight again, all-success/partial panels, case previews, rejected output (320–392) | Available only after the reviewed checklist is applied; each case opens page 41 |
| 10 | Dialogs: preflight ×2, cancel, queue change (431–476) | Consent/confirmation with named scope; no provider call in the static reference |

Reuse A15: `app-page-header`, `app-section-header`, `app-request-state`, `app-loading-pattern`, `app-empty-pattern`, `app-error-pattern`, `app-forbidden-pattern`, `app-not-found-pattern`, `app-inline-feedback`, `app-status-pill`, `app-run-state-pill`, `app-revision-chip`, `app-hash-chip`, `app-copyable-identifier`, `app-number-value`, `app-data-table`, `app-data-table-value`, `app-conflict-dialog`, `app-retry-after`, `app-desktop-required-notice`, `[appDesktopMutation]`, `app-field-feedback`, `app-form-error-summary`, `app-absolute-time`; owned Helm button/select/input/textarea/radio/tabs/dialog/alertdialog/alert/skeleton families. Proposed NEW names only: `shared/ui/authoring/credit-estimate`, `shared/ui/authoring/generation-progress`, `shared/ui/authoring/role-profile-select`, `shared/ui/authoring/generated-result`. The later handover map owns selectors, ids and inputs; shared components stay presentational.

## States

Coverage: **15/15 §6 Generation states** plus the progress vocabulary. “Region behaviour” is the proposed requirement; only the mapped query state is demonstrated by the mockup.

| # | State | Region behaviour and recovery | Mockup `?state=` or [OPEN] |
| --- | --- | --- | --- |
| 1 | Empty attachment/requirement | Input stage explains what to supply; estimate/Generate disabled; manual authoring link retained | `empty` (copy says “No requirement supplied”) |
| 2 | Oversized attachment | Reject before upload with the server cap and a linked field error; retain other input; no estimate | [OPEN] no query state; the mockup instead says caps are unavailable (line 401), so oversize is not demonstrated |
| 3 | Unsupported attachment | Name accepted types and the supplied file's type; reject before extraction; retain text | [OPEN] static card line 400 under `default` |
| 4 | Scanning attachment | Attachment region busy with a text status; extraction/Generate wait; no result claimed | [OPEN] static card line 402 |
| 5 | Rejected attachment | Scanner refusal explained; nothing extracted; safe text retained; remove/replace only | [OPEN] static card line 403 |
| 6 | Extraction failure | Show intake `failure_code` as-is plus its sentence from [authoring-states.md](authoring-states.md); no checklist, no credit claim | [OPEN] static card line 404; no failure-code specimen |
| 7 | AI opt-in off | Explain, link to the authorized admin entry, keep manual authoring; no fake result, no bypass | [OPEN] static card line 405; estimate `opt_in` is the source of truth |
| 8 | Credits exhausted | Show supplied balance/cap state, block the AI call, link usage; no purchase flow | [OPEN] static card line 406; 0 must be a confirmed zero, unavailable is separate |
| 9 | Estimate unavailable | Block the AI call, state it is not zero credits, offer explicit retry; manual authoring unaffected | [OPEN] static card line 407 |
| 10 | Cache hit | Before the call show the hit and cost `0` **with the reuse reason**; the 202 echoes `cache_hit`; progress ends `succeeded` with `outcome_code = cache_hit` and `cost_micro = 0`; name the drafts the hit yields before accept; accept path identical to a miss | [OPEN] **no cache-hit state exists in the mockup**; it is a contract/plan requirement, not demonstrated |
| 11 | Gateway unavailable | Typed unavailable pattern, `Retry-After` where supplied, explicit retry after a fresh preflight; no fallback model | [OPEN] static card line 408 |
| 12 | Schema invalid | No version and no draft claim; refund only when the response supplies it; retain safe input; fresh preflight for any retry | [OPEN] static card line 409 and rejected panel 384–389 |
| 13 | Partial test-case generation | Separate validated results, failed items and rejected output; retain successful work; never retry it automatically; per-item refund only when supplied | `default` local panel `partial` (331–391); not a query state |
| 14 | Review pending | Checklist suggestions marked AI/unreviewed with provenance; operations queue to page 42; nothing applied/approved here | `default` stage 4 (287–318) |
| 15 | Cases ready for review | Preconditions, paired action/expected, priority and provenance inspectable; each opens page 41; no script/run/release action | `default` stage 5 (320–352) |

**Generation progress** (`authoring_generation.state`): `queued → running → succeeded | failed | blocked | canceled`; only `queued → canceled` and `running → canceled` are cancel edges, terminal rows answer `changed: false`. `blocked` is an in-job pre-send refusal; a request-time opt-in refusal is an HTTP preflight error with **no row**, never a state. `[INFERENCE]` The mockup's numbered 1–5 stage navigator is a workflow index, not a completion percentage; the runtime progress strip below is the honest progress surface.

| Progress fact | Rendering rule |
| --- | --- |
| `progress.stage` | Closed monotonic `queued → redact → generate → validate`; render only the stage on the row |
| Four-step strip | Composed from the linked intake row's `state` (`received`/`extracting`/`ready`; `rejected` is terminal) as the first step, then the generation stages; derive no stage that is not on a row |
| `progress.eta_class` | `short`/`long` only where present; never converted into seconds, a percentage or a queue position |
| Polling | `GET authoring/generations/{g}`; honour `Retry-After`; pause while the tab is hidden; stop on route leave; bounded ceiling (plan: 15 minutes) then stop with an explicit still-running message |
| Announcements | One polite live region announces **state transitions** only (queued → running → terminal), not each poll or a ticking timer |
| Terminal | Stop polling at `succeeded|failed|blocked|canceled`; `succeeded` reveals `output_refs[]` and enables accept; a `cache_hit` success shows `0` with the reuse reason |
| `outcome_code` | Render the closed vocabulary as-is; an unknown gateway status is shown verbatim, never translated or folded. Copy per code lives in [authoring-states.md](authoring-states.md) |
| `error.code` | Opaque escaped display text, never branched on, never a client response code, never deduplicated against `outcome_code`; both lines may legitimately match |
| Timestamps | `created_at`/`started_at`/`finished_at` are absolute UTC from the row; null before the transition; never estimated from elapsed time |

**Role-profile selector (13.2.2-T6 delta):** required in the estimate/preflight area before any AI action, with explicit `profile_id`, `version_no` and `source`; filter to profiles whose `allowed_output_kinds` contains the requested kind; retired or wrong-kind selections are refused with the supplied reason and **no substitute**; selecting needs only `authoring:generate` (no save permission); optional single-run note ≤500 code points. The request sends `{profile_id, version_no, source}` only — never a client hash. Server 422 reasons are `required`, `profile_not_found`, `profile_retired`, `output_kind_not_allowed`, each rendered as an explained field error. Selector states: loading, empty catalogue, error, refused, disabled (opt-in off/missing grant), success with the pinned version shown in provenance after the run. The profile **editor** is a separate 13.2.2-T6 delta beside page 44, not this screen.

Focus order: skip → shell → page actions → stage tabs (one roving tab stop; arrows/Home/End) → active stage fields → estimate/preflight trigger → progress cancel → result links → dialogs. Preflight initially focuses Cancel, traps Tab, Escape cancels without a call, and returns focus to its opener. Confirmation enables the AI action only after estimate, balance, opt-in and grant are all known-good. Stage changes preserve requirement text and draft selections; a blocked stage moves focus to its heading/explanation rather than dropping focus. The progress live region never steals focus. Accept/conflict dialogs follow the shared conflict pattern; a 412 accepts nothing and offers explicit reload. Errors are linked from the summary to the field; secret-shaped run-note refusal is announced once, not per keystroke.

## Copy length (vi vs en)

Measured with a throwaway read-only Python `HTMLParser` pass over the primary mockup: decoded `data-vi`/`data-en` pairs in headings, buttons, table headers, badges, descriptions and static state panels, deduplicated, Unicode code points, not pixels. 222 unique pairs; the longest vi/en specimens are shown. Ratios below 1.0 mean the Vietnamese is shorter; this is text length, not rendered width.

| Longest vi string | vi chars | Matching en string | en chars | vi/en |
| --- | --- | --- | --- | --- |
| “Mọi hành động AI đều xác nhận ước tính, số dư, chấp thuận của tổ chức và authoring:generate trước khi chạy. Thay đổi danh mục đi qua thay đổi chờ áp dụng; ca được tạo mở trong trình soạn ca thủ công để người dùng duyệt.” | 219 | “Every AI action confirms estimate, balance, tenant opt-in and authoring:generate before running. Checklist changes route through pending changes; generated cases open in the manual-case editor for human review.” | 210 | 1.043 |
| “Dữ liệu minh hoạ · storefront-web · SHOP · ais-3 · kỳ 2026-10 · số dư 1 240 trên 2 000 · ước tính danh mục 12 …” | 201 | “Illustrative fixture · storefront-web · SHOP · ais-3 · period 2026-10 · balance 1 240 of 2 000 · checklist estimate 12 …” | 219 | 0.918 |
| “Đưa yêu cầu qua quá trình tạo danh mục kiểm tra có tính tín dụng, người duyệt theo lô, rồi tạo ca thủ công. Mọi hành động AI đều có kiểm tra trước rõ ràng; lỗi được hiển thị trung thực.” | 185 | “Take a requirement through credit-aware checklist generation, human batch review and then manual-test-case generation. Every AI action has an explicit preflight; failures stay honest.” | 183 | 1.011 |
| “Loại được hỗ trợ: DOCX, PDF, PNG, JPEG. Giới hạn kích thước, số lượng và loại tệp phải lấy từ cấu hình được cung cấp; ở đây chưa có và được ghi là hợp đồng mở, không đoán.” | 171 | “Supported types: DOCX, PDF, PNG, JPEG. File-size, count and type caps must come from supplied configuration; they are unavailable here and are recorded as an open contract, never guessed.” | 187 | 0.914 |
| “Việc tạo dựa trên av-12 và phiên bản soạn hiện tại là av-13. Không ca kiểm thử nào được tạo. Tải lại bản gốc rõ ràng; không tự hợp nhất, tự thử lại hay phiên bản một phần.” | 171 | “Generation was based on av-12 and the current authoring version is av-13. No test case was created. Reload the base explicitly; there is no auto-merge, automatic retry or partial version.” | 187 | 0.914 |
| “Mẫu thay thế. Hai mục đã được xác thực và được giữ; SHOP-TC-0051 không phải kết quả được tạo trong trạng thái này. Công việc thành công không bao giờ được tự động thử lại.” | 171 | “Alternate fixture. Two items were validated and kept; SHOP-TC-0051 is not a created result in this state. Successful work is never retried automatically.” | 153 | 1.118 |

Proposed strings the mockup does not yet contain (cache hit, role-profile refusal) must be added in both locales and re-measured; allow at least 40% expansion in labels, selects and buttons, with wrapping rather than shrinking. Code-by-code `outcome_code`/`failure_code` sentences belong to [authoring-states.md](authoring-states.md), not this table.

## Responsive notes

- **1280px:** expanded 224px shell, 24px rhythm, capped content. The input stage keeps its 320px context rail beside the form; the estimate/result stages use the balanced two-column pane only where both sides remain readable. Stage tabs stay horizontal; long bilingual labels wrap or scroll locally inside the tab strip, never page-wide. Tables keep intrinsic id/time columns and flexible title/reason columns; results above 200 loaded rows use the shared virtualized table.
- **360px:** drawer shell, desktop-required read-only notice, 16px gutters (12px at ≤420px), 44px touch targets. Editing (requirement text, attachment choice, run note, profile selection), preflight confirmation, generation start, cancel and accept are **not available**; the screen remains inspectable: supplied input, estimate, opt-in/grant reasons, progress stages, provenance, results and links to manual authoring. Before/after and estimate/result panes stack; stage tabs become the labelled select while retaining the same panel semantics. If a hidden mutation held focus, move focus to the equivalent inspection heading after render. Re-check the viewport mutation policy in handlers, not only CSS. No page-level horizontal scroll.
- Both sizes × light/dark × vi/en: WCAG 2.2 AA — 2.5.8 targets ≥24×24px (44×44 coarse pointer); 2.4.11 sticky header/tabs/toasts never obscure the focused control; 2.5.7 no drag-only task; 1.4.3 text ≥4.5:1 and 1.4.11 necessary boundaries/focus ≥3:1 using [tokens](tokens.md); 2.4.7 visible unclipped focus; 1.4.10 reflow and 200% text; 4.1.3 one polite progress announcement. See the manual per-criterion checklist in [authoring-states.md](authoring-states.md). ⚠️ No viewport, contrast, keyboard or screen-reader run was performed here.

## Invariants

- **Estimate before the call, always.** `POST generations/estimate` uses the exact same request body as `POST generations`; the screen shows `credit_cost_micro` and `credits_available_micro` **before** the call and requires explicit confirmation. The AI call — never manual authoring — is blocked when `cap_blocked` is true, the estimate cannot be read, `authoring:generate` is missing, or the server refuses with `ai_credit_exhausted` (HTTP class per 08.5.5, `[OPEN]`), `403 healing_provider_not_approved`, `503 healing_unavailable` or `422` (role-profile/input refusal). Each reason is its own state; no fabricated result and no per-item number the estimate did not return.
- **The 202 is a receipt, not a reservation or a result.** It returns `{generation_id, state, model_tier, cache_hit, credit_cost_micro}`; keep the confirmed estimate beside it and treat the detail read as the only later source of truth. Never re-derive cost from a fixture or the client clock.
- **A cache hit is reuse, not a free bypass.** Estimate `cache_hit: true` shows cost `0` **with the reason** before the call; the row ends `succeeded` / `outcome_code = cache_hit` / `cost_micro = 0`; drafts come from the cached validated payload and are named before accept; accept uses the same `refs[]`, route-6 `version`/ETag, partial-accept and 412 rules. Never claim the hit spent credit, skipped review, or produced content without provenance.
- **Null is not zero.** An absent balance, unmetered cost, unavailable estimate, unknown total or missing refund renders Not available with its reason, never `0`. A confirmed zero balance/zero cost is different and is labelled as such. The credit meter's fill is a ratio of supplied balance to supplied cap only — it is never generation progress, and an unavailable balance/cap renders no fill and no guessed percentage.
- **No derived progress.** `progress.stage` is a closed monotonic vocabulary and `eta_class` is `{short, long}`; no percentage, no ETA seconds, no queue position, no stage inferred from elapsed time. The strip uses only the intake `state` and the generation `stage` present on the rows.
- **Terminal means stop.** Polling stops at `succeeded|failed|blocked|canceled`, on route leave and after the bounded ceiling; `Retry-After` is honoured and hidden tabs pause. Cancel is offered only for queued/running and never claims a version.
- **Provenance is server-pinned and read-only.** `model`, `model_tier`, `ai_settings_ref`, `role_profile_ref`, `prompt_version`, `output_schema_version`, `cost_micro` and UTC `created_at`/`started_at`/`finished_at` come from the detail read. A `cache_hit` row snapshots its own refs; it does not inherit the producer's display identity.
- **Accept is explicit and atomic per request.** Send `refs[]` plus the route-6 `version` as `If-Match` and one `Idempotency-Key`; a 412 accepts nothing and offers explicit reload; partial accept names exactly the accepted drafts. Generation never flips `draft → approved/active`; accept is the single flip owner, and apply on page 42 never flips a state.
- **AI and user text is untrusted.** All requirement text, suggestions, case previews, reasons and provenance render as escaped plain text, marked AI-generated where applicable; nothing reaches `innerHTML`. No script, run, release verdict or repair action is offered here; an image is stored and displayed as a human reference only, and an image-only generation is refused with `422 image_input_not_supported` — no image byte ever reaches a provider.
- **The role profile grants nothing.** A profile named “admin” or text requesting powers changes no permission; selection needs only `authoring:generate`; the request carries no schema, base prompt, gateway option or client hash; unknown/retired/wrong-kind selections fail before dispatch with no substitute and no credit spend.
- **No automatic retry of successful work.** A retry after failure/partial outcome is a fresh preflight and a new explicit intent; successful items are never regenerated implicitly.

## Open points

| [OPEN] | Recommended default before implementation |
| --- | --- |
| Generation, intake and role-profile routes/schemas are absent from `contracts/openapi.yaml` | Land the `13.3.1 §5` routes 1–8 and `13.2.2 §5` profile reads in the contract before UI build; until then implement against the plan shapes only after contract review, never invent a second DTO |
| No `?state=` fixture for any of the 15 §6 states except `empty`; attachment/extraction/AI failures and partial results are local cards | Keep the declared query states; independent design review must exercise each named failure through the real state mechanism, not accept static cards as coverage |
| Cache hit has no mockup specimen at all | Add the pre-call `cache_hit` estimate, 202 echo, `outcome_code = cache_hit` terminal row and “drafts named before accept” states in both locales; re-measure copy |
| Upload caps are explicitly unavailable in the mockup and plan | Render only server-supplied `caps`; unsupported/oversized/scanning/rejected states must show the supplied limit or say unavailable, never a guessed number |
| No mockup field for the role-profile selector, run note, profile refusal reasons or pinned provenance | Add the selector + optional run note in estimate/preflight, show pinned `profile_id/version_no/source` in provenance, and the four 422 reasons as field errors; profile editor stays beside page 44 |
| Per-item refund/credit status in partial and schema-invalid states is supplied only by fixture text | Show a refund only when the response supplies it; otherwise Not available with the reason and no retry of successful work |
| `outcome_code`/`failure_code` copy and the as-is rule for unknown codes are owned by the sibling state sheet | Link [authoring-states.md](authoring-states.md); do not duplicate or locally translate the closed vocabularies; a missing sibling file blocks T1.2 completeness, not this screen's structure |
| Mockup 404 says “Settings version not found” while the plan's foreign-id rule is about generation/settings reads | Use not-found for an unknown/foreign settings or generation id; never 403 or an empty workspace for another tenant's id |
| Mockup 412 is a whole-page state and says “no test case was created”; accept conflicts are per-request | Use the shared conflict dialog scoped to the accept/preflight action, retain draft and input, and never replace the workspace with a page-level conflict |
| Review and owner approval not yet recorded | Keep this proposed status; require independent design review, visual/a11y evidence, then owner approval before implementation |
