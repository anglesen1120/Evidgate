Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict. Follow its Deliverable and links section and use relevant Canonical fixtures verbatim.

**Page:** `40-authoring-tree/evidgate-authoring-tree.html`. Create a new page that reuses the shell and `assets/` exactly like `03-run-list/evidgate-runs.html`.
**Phase:** G1. **Nav item:** `authoring` (set `data-nav` on `<html>`).
**Plan ids:** 13.4.1-T2, 13.1.1-T2, 13.1.1-T4.

## Goal

Browse a project's authoring hierarchy and find manual test cases by priority, review status, source, coverage and owner.

## Change

- Use a project header and two-pane authoring workspace: hierarchy at left, dense manual-case table at right. The single primary action creates a manual test case in `../41-test-case-editor/evidgate-test-case-editor.html`; generation and import/export are secondary links to `../43-generation/evidgate-generation.html` and `../45-import-export/evidgate-import-export.html`.
- Show module → feature → category → checklist item → manual test case; retain parent breadcrumbs and a visible selected node. Use nested lists and disclosure buttons with expanded state, not an incomplete ARIA tree; each case opens page 41.
- Add labelled table filters: priority (critical/high/medium/low), supplied authoring status, source (manual/ai/import), coverage (covered/uncovered) and owner, with clear/reset controls. Do not equate authoring status with execution PASS/FAIL.
- Table columns: project-prefixed case id, title, checklist parent, priority, status, source, covered/uncovered, owner and version. AI sources have a plain-text provenance summary; long titles remain inspectable.
- Coverage means links to zero or more automated `test_case` identities; zero links means uncovered, not a failed test. Show link metadata without exposing G2 run/script routes in G1.
- Add labelled per-row selection, a mixed-state select-visible checkbox and a selected-count toolbar with export-selection link to `../45-import-export/evidgate-import-export.html`. State whether selection covers only visible rows; do not invent bulk approval, implicit cross-page selection or direct destructive edits.
- Link checklist review/pending-change counts to `../42-pending-changes/evidgate-pending-changes.html`; queued edits are not rendered as already-applied versions. Preserve selected node and safe filters during local state changes.

## States (in addition to the shared switcher)

- Empty project; category without cases; no filter matches; tree loading/error independently of table; no owner; mixed coverage; permission-restricted creation/selection; long Vietnamese hierarchy and table overflow. Mobile keeps a readable hierarchy/table summary without mutation controls.

## Fixture (illustrative, labelled as such)

- storefront-web / `SHOP` / `ais-3`: “Checkout / Thanh toán” → “Order review / Xem lại đơn hàng” → “Validation / Kiểm tra tính hợp lệ” → `SHOP-CL-0017` “Show the order total / Hiển thị tổng đơn hàng”, priority high → `SHOP-TC-0042` “Confirm order shows the total / Xác nhận đơn hàng hiển thị tổng tiền”, v5 (v4 history). Also show `SHOP-TC-0043` “Required delivery address / Địa chỉ giao hàng bắt buộc”, v2, and `SHOP-TC-0051` “Apply a discount code / Áp dụng mã giảm giá”, v3. Use these same identities in coverage specimens; owner Linh Nguyễn.

## Copy EN / VI

- “Feature tree / Cây chức năng”; “New test case / Ca kiểm thử mới”; “Priority / Mức ưu tiên”; “Status / Trạng thái”; “Source / Nguồn”; “Coverage / Mức tự động hóa”; “Owner / Người phụ trách”.
- “Covered / Đã tự động hóa”; “Uncovered / Chưa tự động hóa”; “Export selected / Xuất mục đã chọn”; “Select visible rows / Chọn các dòng đang hiển thị”.

## Keep unchanged

- Shared shell, phase-gated nav, tokens, compact table/pagination patterns, theme/locale/state controls, read-only mobile policy and permission-driven actions; no new design system or release verdict.

## Done when

- Changes are baked into static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- All five filters, mixed-state bulk selection, hierarchy disclosures and links to pages 41–45 work on local fixtures; uncovered stays distinct from execution failure.
