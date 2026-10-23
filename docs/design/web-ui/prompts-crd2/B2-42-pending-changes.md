Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict. Follow its Deliverable and links section and use relevant Canonical fixtures verbatim.

**Page:** `42-pending-changes/evidgate-pending-changes.html`. Create a new page that reuses the shell and `assets/` exactly like `03-run-list/evidgate-runs.html`.
**Phase:** G1. **Nav item:** `pending-changes` (set `data-nav` on `<html>`).
**Plan ids:** 13.4.1-T3, 13.1.2-T1, 13.1.2-T2; G3 proposal handoff: 13.8.1-T3.

## Goal

Review queued checklist changes and, from G3, accepted manual-case review proposals; apply the entire batch as one immutable authoring version, or keep it pending when stale.

## Change

- Header: project, pending-count and base authoring version, with a back link to `../40-authoring-tree/evidgate-authoring-tree.html`. Make “Apply as one version” the single primary action, unavailable for an empty queue or missing write permission.
- Build a compact queued-change list/table with operation (comment/merge/split/delete), target checklist id/title, author, source and reason. Include checklist results routed from page 43, clearly pending rather than already saved/approved.
- Selecting an operation reveals a plain-text before/after preview. Merge/split show source/target lineage and affected case links; delete names the affected checklist item and supplied consequences, never guesses cascading deletion.
- From G3, include accepted UI-change proposal `ucp-12` from `../47-ui-change-proposal/evidgate-ui-change-proposal.html` as a distinct manual-case review item for `SHOP-TC-0051` v3: before/after step and expected-result differences, source proposal, incident `inc-93` and human review reason. It joins the same one-version apply with `If-Match`; a stale case/batch base changes nothing. Never modify a script or offer repair Apply; hide this specimen at G1–G2.
- Show a batch summary and version reason before confirmation; warn that the whole queue is one atomic operation. Do not offer per-row Apply, partial apply or automatic approval of generated cases.
- Apply binds to the displayed base revision with `If-Match`; reuse one Idempotency-Key for the same apply intent. Keep implementation metadata in a collapsed details area, not as editable request fields; make no network calls from the mockup.
- Success requires a supplied response and shows one new version with actor, source/reason and UTC time, linked to `../41-test-case-editor/evidgate-test-case-editor.html` Timeline. Clear only the confirmed applied batch, not an unconfirmed queued draft.
- A stale apply returns 412 and changes nothing: keep the full queue and unsaved reason visible, show current/base revisions and changed fields, offer explicit Reload. Do not auto-merge, silently retry or show a partially successful version.

## States (in addition to the shared switcher)

- Empty queue; one of each operation; long comment; invalid merge/split preview with apply blocked and reason; apply submitting/success/error; duplicate-intent response; 412 stale batch; permissions revoked. Untrusted AI/user text is plain text with supplied provenance.

## Fixture (illustrative, labelled as such)

- storefront-web / `SHOP` / `ais-3`; comment on `SHOP-CL-0017` “Show the order total / Hiển thị tổng đơn hàng”, priority high; merge/split/delete previews retain that canonical id and clearly label any temporary targets. G3 queue includes `ucp-12` for `SHOP-TC-0051` “Apply a discount code / Áp dụng mã giảm giá”, v3; reason “Review the changed discount interaction / Xem xét tương tác mã giảm giá đã thay đổi”; step “Enter a code / Nhập mã” → “Open the discount field, then enter a code / Mở trường mã giảm giá rồi nhập mã”; expected “Discount field is visible / Trường mã giảm giá hiển thị” → “Discount field opens on request and accepts the code / Trường mã giảm giá mở khi được yêu cầu và nhận mã”. Base batch `av-12`, separate conflict current `av-13`; successful alternative produces one version, never one per row.

## Copy EN / VI

- “Pending changes / Thay đổi chờ áp dụng”; “Apply as one version / Áp dụng thành một phiên bản”; “Comment / Nhận xét”; “Merge / Gộp”; “Split / Tách”; “Delete / Xóa”.
- “Nothing was applied / Chưa áp dụng thay đổi nào”; “Base version / Phiên bản gốc”; “Review changes / Xem xét thay đổi”; “No pending changes / Không có thay đổi chờ áp dụng”.

## Keep unchanged

- Shared shell, nav/tokens, 412 dialog, phase/locale/theme/state controls and mobile read-only policy. This is authoring batch Apply only; never reuse its action for repair proposals or release approval.

## Done when

- Changes are baked into static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- Success demonstrates exactly one appended version, including an accepted G3 manual-case review item without touching a script; the stale fixture preserves every operation and applies none, with explicit reload and no auto-resubmission.
