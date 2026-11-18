Status: proposed, awaiting owner approval

# Authoring pending changes

Review record: [authoring-review.md](authoring-review.md)

Flow id: `authoring-pending-changes` · CR-D-2 phase: **G1**; the manual-case proposal specimen is **G3 only**.

Feature tasks: `13.4.1-T1.1`, `13.4.1-T1.2`, `13.4.1-T3`; contract owner `13.1.2-T1/T2`; later proposal handoff `13.8.1-T3`.

Legend: ✅ verified source with a link; ⚠️ unverified runtime/visual behavior; `[INFERENCE]` recommended design interpretation; `[DECISION]` recorded owner decision only; `[OPEN]` unresolved shape or evidence with a recommended default. This document proposes behavior, not approval. Independent review and owner approval remain required; no review or browser pass is claimed.

Sources: [authoring plan §5–6](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md#5-api-shapes-commands-queries-endpoints), [pending brief](../web-ui/prompts-crd2/B2-42-pending-changes.md), [OpenAPI](../../../contracts/openapi.yaml) lines 7292–7553 and 13650–13802, [UI rules and A15 catalogue](../../engineering/ui-rules.md#shared-component-catalogue-a15). OpenAPI paths omit `/v1` because it is the server prefix; implementation uses generated SDK operations, not composed route strings.

Data provenance below covers product facts shown by the reference; fixture values are not API evidence. A dotted schema field names a property inside that schema, not a JSON Pointer literal. `[OPEN]` rows cannot be treated as traced simply because a plan describes the field.

| Data field | openapi schema path | state (traced / [OPEN]) |
| --- | --- | --- |
| Change identity, operation, target kind/id | `contracts/openapi.yaml#/components/schemas/AuthoringChangeListItem.change_id`, `.change_type`, `.target_kind`, `.target_id` | traced; `target_kind` is currently only `checklist_item` |
| Queue lifecycle and source | `contracts/openapi.yaml#/components/schemas/AuthoringChangeListItem.state`, `.source` | traced; pending/applied/discarded and manual/ai/import are separate facts |
| Author membership, per-change reason, queued UTC instant | `contracts/openapi.yaml#/components/schemas/AuthoringChangeListItem.author_membership_id`, `.reason`, `.created_at` | traced; null reason means Not set |
| Target base version, selection staleness | `contracts/openapi.yaml#/components/schemas/AuthoringChangeListItem.base_version`, `.stale` | traced; stale is supplied only on an `ids` selection read |
| Operation body and selected change revision | `contracts/openapi.yaml#/components/schemas/AuthoringChangeDetailResponse.payload`, `.version` | traced container only; payload details use the route's referenced `contracts/schemas/authoring-change.v1.json`, not arbitrary HTML |
| Target title/description/priority | `contracts/openapi.yaml#/components/schemas/ChecklistItemDto.title`, `.description`, `.priority` | traced via a separate authorized target read; payload is not a complete before snapshot |
| Loaded changes, cursor, count displayed in confirmation | `contracts/openapi.yaml#/components/schemas/AuthoringChangePageResponse.items`, `.next_cursor`; `contracts/openapi.yaml#/components/schemas/ApplyAuthoringChangesRequest.change_ids` | traced; count exact selected ids, not a project-wide total |
| Batch receipt, exactly applied ids, committed UTC instant | `contracts/openapi.yaml#/components/schemas/ApplyAuthoringChangesResponse.batch_id`, `.applied_change_ids`, `.committed_at` | traced |
| Versions actually written and resulting lineage | `contracts/openapi.yaml#/components/schemas/ApplyAuthoringChangesResponse.versions`, `.lineage`; `contracts/openapi.yaml#/components/schemas/AppliedAuthoringVersionDto.entity_kind`, `.entity_id`, `.version_no`; `contracts/openapi.yaml#/components/schemas/AppliedAuthoringLineageDto.from_item_id`, `.to_item_id`, `.relation` | traced; versions can be empty for comment-only apply |
| Discard receipt and instant | `contracts/openapi.yaml#/components/schemas/DiscardedAuthoringChangeResponse.change_id`, `.state`, `.discarded_at` | traced |
| Timeline version identity, batch, author, source/reason/time | `contracts/openapi.yaml#/components/schemas/AuthoringVersionListItemDto.version_id`, `.version_no`, `.batch_id`, `.author`, `.source`, `.reason`, `.created_at` | traced via authorized timeline read; source `authoring` in the mockup is not a schema value |
| Project display name/code and unrelated `ais-3` header chip | [OPEN] no pending-response schema field | Recommended: use existing authorized project context for name/code; omit settings chip unless a real linked settings read establishes relevance |
| Friendly `SHOP-CL-*` codes and author display name | [OPEN] list supplies UUIDs/membership id, not these labels | Recommended: show safe ids until an authorized display projection is available; never fabricate codes/names |
| Global pending total and single `av-12`/`av-13` project revision | [OPEN] no total or global revision in change page | Recommended: label loaded/selected count; display per-target base versions and opaque batch ETag, not invented global versions |
| Complete before/after snapshot, affected-case links/count and delete consequences | [OPEN] payload is an untyped OpenAPI container; response supplies no complete impact preview | Recommended: compare only fetched target facts and validated operation payload; unavailable impact is explicit and blocks a misleading destructive confirmation |
| Batch-level required version reason | [OPEN] `ApplyAuthoringChangesRequest` permits only `change_ids` | Recommended: retain the local reason draft but never send an unknown property or claim it was persisted; resolve with contract owner before making it a required submitted field |
| Current/base comparison and changed-field snapshots after 412 | [OPEN] error details are pointers/reasons, not before/after snapshots | Recommended: retain draft and supplied pointers; fetch authorized current targets only on explicit Reload; do not invent changed text |
| G3 `ucp-12`, incident, manual-case review operation and proposed step/expected changes | [OPEN] current change enum/target kind cannot encode this specimen | Recommended: hide at G1–G2; keep G3 handoff unavailable until its owning contract extends the supported operation, never coerce it to a checklist comment |

## v2 pages

| Page | Role | Phase | Valid `?state=` values |
| --- | --- | --- | --- |
| [42 — pending changes](../web-ui/output/42-pending-changes/evidgate-pending-changes.html) | Primary queue → preview → atomic confirmation | G1; proposal row G3 | `default`, `loading`, `empty`, `error`, `403`, `401`, `429`, `412`, `submitting`, `saved` (HTML line 86) |
| [41 — editor](../web-ui/output/41-test-case-editor/evidgate-test-case-editor.html) | Inspect affected case and immutable timeline | G1 | Related destination; its states are not pending-page query states |
| [43 — generation](../web-ui/output/43-generation/evidgate-generation.html) | Human review handoff | G1 | Related destination; generation success does not apply this queue |

✅ The reference has static long-comment, invalid merge/split and duplicate-intent specimens at lines 305–312, not distinct query-state values. Its `saved` label is illustrative, not proof of a supplied response. Do not add invented `?state=invalid-merge` links.

## Regions

| Order | Region | Proposed behavior following the reference |
| --- | --- | --- |
| 1 | Skip link, shell, project scope, breadcrumbs | Retain registry context, current location and safe Back to feature tree navigation |
| 2 | Narrow read-only notice, page title and context (HTML 61–84) | Distinguish loaded queue count from global total; expose a real batch token only as non-editable metadata |
| 3 | Request/conflict feedback (88–145) | Scoped loading/error; retain queue after mutation failure; distinguish read denial from write revocation |
| 4 | Atomic-scope banner and queued-operation table (148–177) | Operation, target, author, source, reason and supplied consequences in reading order; no per-row Apply |
| 5 | Selected plain-text preview (179–255) | Comment, merge, split, delete; source/survivor or temporary-output lineage explicit; full long comment available; G3 review specimen gated |
| 6 | Batch summary, local reason, confirmation and collapsed metadata (257–293) | Confirm exact reviewed ids and consequences; Apply is the only primary mutation; discard is a separate confirmed per-change action, never partial Apply |
| 7 | Receipt/timeline and illustrative state specimens (295–315) | Show only response-confirmed applied ids and actual versions; keep any newer/unconfirmed queued draft |

Reuse A15: `app-page-header`, `app-section-header`, `app-data-table`, `app-data-table-value`, `app-copyable-identifier`, `app-revision-chip`, `app-status-pill`, `app-request-state`, `app-inline-feedback`, `app-conflict-dialog`, `app-retry-after`, `app-desktop-required-notice`, `[appDesktopMutation]`, `app-form-error-summary`, `app-field-feedback`, `app-absolute-time`; owned Helm tabs/dialog/alertdialog/button families. The catalogue and `shared/ui/` directory were inspected read-only. Proposed NEW names only: `shared/ui/authoring/change-preview`, `shared/ui/authoring/batch-summary`. Their later handover map owns selectors/ids; shared components remain presentational.

## States

Coverage: **8/8 plan items**, rendered as **10 rows** (applying, success and error counted separately; invalid merge and split as two variants of one item). Every mapping is a real declared query state or explicitly `[OPEN]`.

| State | Region behavior and recovery | Mockup `?state=` or [OPEN] |
| --- | --- | --- |
| Empty queue | Empty message; Apply unavailable with explanation; no empty version; navigation retained | `empty` |
| One of each operation | Four checklist operation previews; inspect without mutation; actual queue must obey structural conflict guards | `default`; preview tabs |
| Long comment | Full escaped text wraps/expands; no lost reason or hover-only recovery | [OPEN] no query state; static specimen at line 309 under `default` |
| Invalid merge/split preview | Both variants retain every row, identify invalid source/target/shape, disable batch Apply and link error to preview | [OPEN] no query state; two static specimens at 310–311; illustrative split/merge reasons are not authoritative validation rules |
| Applying | Mark summary busy; prevent duplicate submit/discard; keep queue/reason visible; one polite status | `submitting` (reference replaces the body; retained-body behavior is the proposed correction) |
| Success | Receipt names exact applied ids, batch/time and returned versions; comments may produce no entity version; clear only those ids | `saved`; reference's unconditional “exactly one av-13” is a contract gap |
| Error | Retain queue, selected preview and local reason; explicit retry with same intent/key after uncertain delivery; no optimistic success | `error`; distinguish initial read error from apply error |
| Duplicate intent | While busy suppress resubmit; after replay show stored result once, no second version; conflicting key/body is a typed 409, not success | [OPEN] no query state; static line 312 |
| 412 stale batch | Nothing applied; preserve entire queue/reason; shared conflict dialog with supplied revision pointers and explicit Reload; no auto-merge/resubmit | `412`; full retained queue is specified, not demonstrated by hidden reference data stack |
| Permission revoked | If read remains, queue/preview stay readable; explain-disabled Apply/discard; if read revoked, forbidden pattern with no metadata leak | `403`; reference hides its body despite readable-queue copy, so read-only mutation denial needs correction |

Shared request coverage: `idle → loading → ready(empty or rows) | error(code)`; `loading`, `401`, `429` are declared. A foreign/unknown detail uses not-found, not empty queue or 403 ([OPEN] no primary `404` specimen). On 429/503 honor `Retry-After`; retry disabled while waiting/in flight. Failed refresh retains prior rows with a stale label. Unavailable/offline-ish preserves drafts without promising offline writes or a service worker.

Focus order: skip → shell controls → Back → request recovery when present → queue row preview controls → selected preview tabs/content links → reason (desktop) → Apply → metadata disclosure → timeline. Native table semantics and caption remain; do not make whole rows generic clickable divs. Tabs have one roving tab stop, arrows/Home/End; Tab enters selected panel. Confirmation/alertdialog initially focuses Cancel, traps Tab, Escape cancels and returns to Apply. Conflict opens on the failed explicit mutation, retains draft, and returns to its opener; Reload is a deliberate read, never submit. A focused linked error summary follows invalid confirmation, not each keystroke. One polite live region announces applying/applied/conflict transition once; queue refreshes do not steal focus or announce every cell. Discard confirms the named change and does not claim to write an item/version.

**Reference delta AR-04/07:** set the Apply confirmation's initial focus explicitly to localized Cancel, never the leading Close icon. Its vi/en Close/Cancel names follow the shared inventory in [authoring-states.md](authoring-states.md#reading-and-focus-order). A resize cancels unsubmitted consent and hands focus to the surviving inspection heading under that sheet's lifecycle; retaining a dialog with a hidden Apply button is not an acceptable narrow state.

## Copy length (vi vs en)

Measured with a throwaway read-only Python `HTMLParser` pass over the primary mockup: decoded `data-vi`/`data-en` pairs in headings, buttons, table headers, badges, descriptions and static state panels, deduplicated, Unicode code points, not pixels. 155 unique pairs; the longest vi/en specimens are shown. Ratios below 1.0 mean the Vietnamese is shorter; this is text length, not rendered width.

| Longest vi string | vi chars | Matching en string | en chars | vi/en |
| --- | --- | --- | --- | --- |
| “Lô dựa trên av-12 và phiên bản soạn hiện tại là av-13. Mọi thao tác đang chờ và lý do chưa lưu được giữ. Tải lại là thao tác rõ ràng; không tự hợp nhất, không thử lại ngầm và không có phiên bản một phần.” | 203 | “The batch is based on av-12 and the current authoring version is av-13. Every queued operation and the unsaved reason are retained. Reload is explicit; there is no auto-merge, silent retry or partial version.” | 208 | 0.976 |
| “Xem xét các thay đổi danh mục kiểm tra và ca thủ công đang chờ, sau đó áp dụng toàn bộ lô thành một phiên bản soạn bất biến. Không áp dụng từng dòng và không có thành công một phần.” | 181 | “Review queued checklist and manual-case changes, then apply the whole batch as one immutable authoring version. There is no per-row apply and no partial success.” | 161 | 1.124 |
| “Phản hồi minh hoạ được cung cấp: toàn bộ lô tạo đúng một phiên bản soạn bất biến với nguồn soạn và lý do được cung cấp. Chỉ lô đã xác nhận được xoá; không kịch bản nào thay đổi.” | 177 | “Supplied fixture response: the whole batch produced one immutable authoring version with source authoring and the supplied reason. Only the confirmed batch cleared; no script changed.” | 183 | 0.967 |
| “Cùng ý định áp dụng dùng lại một khoá idempotency. Đây là siêu dữ liệu thu gọn, không phải trường yêu cầu sửa được; bản xem trước này không gọi mạng.” | 149 | “The same apply intent reuses one idempotency key. These values are collapsed metadata, not editable request fields; this preview makes no network call.” | 151 | 0.987 |
| “Việc tách sẽ để mục danh mục nguồn không còn cặp bước. Áp dụng bị chặn và lý do được cung cấp hiển thị; cả nguồn lẫn đích tạm đều không được ghi.” | 145 | “The split would leave the source checklist item with no paired step. Apply is blocked and the supplied reason is shown; neither the source nor the temporary target is written.” | 175 | 0.829 |

Proposed strings the mockup does not yet contain (batch reason persistence, exact-scope statement, write-revoked explanation) must be added in both locales and re-measured; allow at least 40% expansion in labels, buttons and dialogs, with wrapping rather than shrinking. Code-by-code `outcome_code`/`failure_code` copy belongs to [authoring-states.md](authoring-states.md), not this table.

**Product-copy delta:** the measured “one immutable authoring version” strings above are frozen-reference text, not the apply contract. Product loading/confirmation copy says vi “Áp dụng tất cả thay đổi trong một lô nguyên tử” / en “Apply all changes as one atomic batch”; the receipt lists only returned entity versions, including zero for a comment-only batch. Do not promise a global version or reuse the fixture's single-version success wording.

## Responsive notes

- **1280px:** expanded 224px shell; 24px content rhythm; preserve table → preview → summary stacking, not an invented permanent preview rail. Operation/source/id/time are intrinsic, target/reason flexible. Long text has full-value disclosure; no unbounded page width. More than 200 loaded rows uses shared virtualization, but one apply is at most 200 exact ids.
- **360px:** drawer shell and desktop-required notice; queue becomes labelled records, before/after panes stack, long ids use safe full-value disclosure. Reason remains selectable read-only text. No edit, Apply, discard or mutating confirmation; preview choice, navigation, metadata and copy remain available. Recheck viewport policy in handlers/submits after resize, not only CSS. If a hidden desktop control held focus, transfer it to the equivalent inspection heading/control after render.
- Both sizes × light/dark × vi/en must meet WCAG 2.2 AA: 2.5.8 targets ≥24×24px (44×44 coarse-pointer); 2.4.11 no header/dialog/toast obscures focus; 2.5.7 no drag-only task; 1.4.3 text ≥4.5:1 and 1.4.11 necessary controls/focus ≥3:1 using [tokens](tokens.md); 2.4.7 visible unclipped 2px focus; 1.4.10 reflow without page scrolling and 200% text; 4.1.3 one contextual status announcement. See the per-screen manual run checklist in [authoring-states.md](authoring-states.md). ⚠️ These are acceptance requirements; no viewport, contrast, keyboard or screen-reader run was performed in this docs-only slice.
- **Reference delta AR-01:** the implementation-metadata disclosure is a real full-row activation target, at least 24px high/wide on fine pointers and 44px high/wide on coarse pointers, using existing control/touch tokens. Keep its text and chevron within that target, allow wrapped text to grow it, and retain visible keyboard focus. A 20px `<summary>` line is not the activation-area requirement. Measure it in open/closed states, both viewport sizes and pointer modes; no target-size exception is proposed.

## Invariants

- ✅ `GET changes?ids=…` returns the batch ETag over all affected items; use that exact opaque value as `If-Match`, never an item's version, `av-*` fixture or UI-computed hash. `ids` cannot combine with filters; selection is 1–200. Do not silently omit unloaded rows while claiming “whole project queue.”
- One explicit apply intent owns one `Idempotency-Key`, reused for a replay with the same body. Changed scope is a newly reviewed intent with a new key. Missing If-Match is server 422, not invented 428. A stale 412 writes nothing, keeps every queued operation and local reason, and never triggers automatic retry/merge.
- ✅ Apply is one atomic batch, not necessarily one entity-version row: the response returns one entry per written entity and **zero** for a comment-only batch. Group the receipt by batch; never invent an extra global version. Apply does not publish/approve drafts; generation accept owns that boundary.
- ✅ Discard has **no If-Match**; state pending is its precondition and it requires idempotency. A second discard may answer 409 `change_not_pending`; do not label it a new successful mutation.
- The reference's four structural examples target the same item and its delete detaches live cases. These are alternative visual specimens, not a valid executable combined queue: contract guards reject overlapping pending structural changes and reject delete/merge/split targets with live cases. Never copy invented cascade/detach consequences into product behavior.
- Null reason/unknown total/unavailable impact is not empty text, zero or successful validation. Render Not set/Not available with context; confirmed empty queue may truthfully show zero.
- All payload/reason/title text is escaped plain text; identifiers preserve safe originals with shared bidi/format-character handling. No `innerHTML`, script repair, release verdict, inferred permission or automatic approval. UTC instants come from server reads/receipts, never client time.

## Open points

| [OPEN] | Recommended default before implementation |
| --- | --- |
| Project/name/code/settings chip; human target codes and author labels lack a pending-specific projection | Use authorized existing scope and safe ids, omit unrelated settings metadata; never create local lookup truth |
| Missing global count/global `av-*` revision | Show loaded/selected scope and actual batch token/per-target versions; do not claim project completeness |
| Before/after, impact links/count and full 412 snapshots absent | Read authorized entity facts; show unavailable impact and supplied pointer reasons honestly; require explicit Reload; never guess delete consequences |
| Batch reason not accepted by apply request | Preserve draft but do not send it or promise persisted provenance until the contract owner supplies a supported shape |
| G3 manual-case proposal unsupported by current enum | Gate by phase and contract; no checklist-operation coercion, no script mutation |
| Missing query fixtures for long comment, invalid previews and duplicate intent; submitting/conflict/revoked states hide readable content | Retain required state behavior described above; independent design review must check retained queue and disabled actions, not certify static cards as runnable states |
| Unconditional single-version success, `source=authoring`, invalid merge/split/deletion examples conflict with schema | Use actual receipt/entity versions, closed source values and server validation reasons; keep examples labelled illustrative |
| Review and owner approval | Independent observations and docs-only dispositions are recorded in [authoring-review.md](authoring-review.md). Corrected reference/runtime behaviour, remaining visual/AT evidence and owner approval are still open; keep proposed status and do not start implementation. |
