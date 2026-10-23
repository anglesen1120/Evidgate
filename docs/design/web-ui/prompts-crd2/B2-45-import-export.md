Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict. Follow its Deliverable and links section and use relevant Canonical fixtures verbatim.

**Page:** `45-import-export/evidgate-import-export.html`. Create a new page that reuses the shell and `assets/` exactly like `03-run-list/evidgate-runs.html`.
**Phase:** G1. **Nav item:** `authoring` (set `data-nav` on `<html>`; parent is page 40).
**Plan ids:** 13.4.1-T5, 13.1.1-T5.

## Goal

Preview authoring imports before committing them and export an explicitly scoped set of manual cases with project-prefixed identities.

## Change

- Use Import and Export tabs with project name/code visible and a back link to `../40-authoring-tree/evidgate-authoring-tree.html`. Each active tab has one primary action; no execution, billing or release workflow.
- Import accepts CSV/XLSX/JSON and shows filename, format, target project and source `import`. Show any supplied size/type limits without inventing numeric caps; keep file intake state separate from validation success.
- Preview a dense row table: source row number, project-prefixed case id, hierarchy path, title, preconditions, priority, step/expected summary and validation result. Retain long values through safe inspection; all imported strings are untrusted plain text.
- Put row errors next to the corresponding cells plus a keyboard-reachable summary linking to the first problem. Include malformed format, missing required data, wrong project prefix and duplicate id specimens; never silently drop rows or overwrite cases.
- Confirmation shows the exact validated scope and a reason; a supplied successful import appends an immutable authoring version with source `import`, actor/time and resulting ids. Do not invent upsert, duplicate-resolution or partial-import policy; unresolved validation blocks confirmation and leaves preview inspectable.
- Export lets the user choose CSV/XLSX/JSON and scope: project, selected hierarchy node, current filtered cases or explicit selection from page 40. Show the exact supplied count, project code prefix and source version; selection is not silently expanded to the whole project.
- Preview export fields sufficient to preserve hierarchy, identities, preconditions, priority and action/expected steps for the documented round trip. Do not invent column schemas, premium formats or download of scan-pending/sensitive artifacts; explain unavailable states.

## States (in addition to the shared switcher)

- No file; unsupported/malformed file; preview loading; validation errors; duplicate ids; mixed valid/invalid rows; import denied; import failure/success; no selected rows; empty export scope; export unavailable. 412 is a linked conditional-edit specimen only if supplied, not a fabricated import response.

## Fixture (illustrative, labelled as such)

- storefront-web / `SHOP` / `ais-3`; row 2 `SHOP-TC-0042` “Confirm order shows the total / Xác nhận đơn hàng hiển thị tổng tiền”, v5 (v4 history), valid; row 3 `SHOP-TC-0043` “Required delivery address / Địa chỉ giao hàng bắt buộc”, v2, missing expected result; row 4 deliberately corrupts the prefix as `OTHER-TC-0051` for “Apply a discount code / Áp dụng mã giảm giá”, v3 (canonical id `SHOP-TC-0051`), wrong project prefix. A separate valid fixture imports as `av-13`; no partial-success claim for the invalid fixture.

## Copy EN / VI

- “Import / Nhập”; “Export / Xuất”; “Project code prefix / Tiền tố mã dự án”; “Preview rows / Xem trước các dòng”; “Row errors / Lỗi theo dòng”; “Export scope / Phạm vi xuất”.
- “Import new version / Nhập thành phiên bản mới”; “Selected cases / Ca kiểm thử đã chọn”; “Fix errors before importing / Sửa lỗi trước khi nhập”; “No cases selected / Chưa chọn ca kiểm thử”; “Download export / Tải tệp xuất”.

## Keep unchanged

- Shared shell/tokens, parent `authoring` nav, table/pagination/overflow patterns, locale/theme/state controls, scoped permissions and mobile-read-only rules. No format paywall, hidden cross-project scope or automatic AI call.

## Done when

- Changes are baked into static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- All three formats and row-error previews are inspectable; confirmation/export name the exact scope, preserve the project prefix and never claim an invalid import created a version.
