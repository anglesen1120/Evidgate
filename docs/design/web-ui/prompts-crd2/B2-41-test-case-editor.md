Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict. Follow its Deliverable and links section and use relevant Canonical fixtures verbatim.

**Page:** `41-test-case-editor/evidgate-test-case-editor.html`. Create a new page that reuses the shell and `assets/` exactly like `03-run-list/evidgate-runs.html`.
**Phase:** G1. **Nav item:** `authoring` (set `data-nav` on `<html>`; parent is page 40).
**Plan ids:** 13.4.1-T3, 13.4.1-T5, 13.1.1-T3, 13.1.2-T1, 13.3.1-T3.

## Goal

Edit a manual test case, review its checklist context and inspect or restore immutable versions without losing an unsaved draft.

## Change

- Header: project-prefixed case id, hierarchy breadcrumb back to `../40-authoring-tree/evidgate-authoring-tree.html`, authoring status and current version. Provide Editor, Checklist review and Timeline tabs with one primary action appropriate to the active tab.
- Editor: labelled title, preconditions, priority (critical/high/medium/low), and ordered steps pairing action with expected result and test data where supplied. Keep expected results editable only as explicit human authoring, never automatic repair; include a version reason before saving.
- Show source manual/ai/import and author. AI text is escaped plain text marked AI-generated, with model (`model-small` / `model-large`), prompt hash, settings `ais-3` and UTC generation time from provenance; these model ids are illustrative placeholders.
- Rebuild and vi↔en Translate are explicit per-case actions. Each opens preflight with canonical estimate (test cases 4 per checklist item, translate 1 per case) and balance 1 240 of 2 000, period 2026-10; respect tenant opt-in and `authoring:generate`. Never overwrite the visible draft while generation is pending or silently treat output as approved.
- Checklist review shows parent item, priority and related cases. Comments, merge, split and delete queue changes for `../42-pending-changes/evidgate-pending-changes.html`; show a pending marker and lineage preview, not immediate deletion or a second apply action here.
- Timeline shows immutable version id, author, source, reason, UTC time and inspectable diff. Restore requires confirmation and reason and creates a new version; keep the original history rows unchanged.
- Save uses the draft's base revision; stale save shows the shared 412 dialog with current revision/changed fields, retaining unsaved fields visibly. Offer explicit Reload; never auto-merge, auto-resubmit or mark Saved after a conflict.

## States (in addition to the shared switcher)

- New manual case; missing required field; unsaved draft; save in progress/success/failure; 412 on save; read-only history; restore confirmation/success; queued checklist edit; AI opt-in off/credits exhausted/gateway unavailable and schema-invalid output (no version created, supplied refund status only).

## Fixture (illustrative, labelled as such)

- storefront-web / `SHOP` / `ais-3`; `SHOP-TC-0042` “Confirm order shows the total / Xác nhận đơn hàng hiển thị tổng tiền”, current v5 and v4 history; parent `SHOP-CL-0017` “Show the order total / Hiển thị tổng đơn hàng”, priority high. Precondition “Checkout is open / Đã mở trang thanh toán”; action “Confirm the order / Xác nhận đơn hàng”; expected “The order total is shown / Hiển thị tổng tiền đơn hàng”. In a separate conflict specimen the server is at v6, the draft is based on v5 and saving creates nothing; a separate successful restore specimen appends v6 from v4 without rewriting v5 or v4.

## Copy EN / VI

- “Preconditions / Điều kiện tiên quyết”; “Steps / Các bước”; “Expected result / Kết quả mong đợi”; “Save new version / Lưu phiên bản mới”; “Checklist review / Xem xét danh sách kiểm tra”.
- “AI-generated / Do AI tạo”; “Rebuild / Tạo lại”; “Translate / Dịch”; “Timeline / Dòng thời gian”; “Restore as new version / Khôi phục thành phiên bản mới”; “Your draft is retained / Bản nháp của bạn được giữ lại”.

## Keep unchanged

- Shared shell/tokens, parent `authoring` nav selection, conflict-dialog semantics, permission and mobile-read-only rules. No execution controls, release verdicts or repair apply button.

## Done when

- Changes are baked into static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- Local fixtures demonstrate a preserved draft on 412, a restore adding rather than replacing history, and credit preflight for both AI actions; checklist edits remain pending until page 42 applies them.
