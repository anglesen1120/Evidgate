Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict. Follow its Deliverable and links section and use relevant Canonical fixtures verbatim.

**Page:** `43-generation/evidgate-generation.html`. Create a new page that reuses the shell and `assets/` exactly like `03-run-list/evidgate-runs.html`.
**Phase:** G1. **Nav item:** `generation` (set `data-nav` on `<html>`).
**Plan ids:** 13.4.1-T4, 13.3.1-T1, 13.3.1-T2, 13.3.1-T3, 08.5.5-T2.

## Goal

Take a requirement through credit-aware AI checklist generation, human batch review and then manual-test-case generation, with honest failure states.

## Change

- Provide static, locally switchable stages: Input → Credit estimate → Progress → Checklist review → Test cases. One primary action per stage; show project and settings version with a link to `../44-ai-settings/evidgate-ai-settings.html`, and safe back navigation without losing requirement text.
- Input accepts requirement text, DOCX/PDF for text extraction and images. Show supported types, file-size/count/type caps from supplied configuration, scan/sensitivity state and field errors. Numeric caps are not specified by plan 13: record that open contract in design notes, never invent limits or treat unknown validation as passed.
- Before each AI action, including checklist and subsequent test-case generation, show canonical estimated credit cost and tenant balance (checklist 12, test cases 4 per checklist item; balance 1 240 of 2 000, period 2026-10), opt-in and permission status, then require explicit confirmation. Unavailable estimate/balance is a separate failure state that blocks the AI call, not manual authoring; default is populated.
- Progress distinguishes intake/extraction, redaction, generation and schema validation using supplied states; announce status without a false completion percentage or fabricated output while loading. Requests in this artifact remain local fixtures, not live provider calls.
- Checklist result shows priority critical/high/medium/low and plain-text suggestions with model, prompt hash, settings version and UTC time. Route comments/merge/split/delete and the reviewed result into `../42-pending-changes/evidgate-pending-changes.html`; do not silently save an approved version.
- Make test-case generation available only for the reviewed/applied checklist state. Render generated preconditions, paired action/expected steps, priority and provenance as an inspectable result; link each case to `../41-test-case-editor/evidgate-test-case-editor.html` for human review, never a script/run or release decision.
- Partial-failure view separates validated results, failed items and rejected output. Retain safe inputs/results, show supplied per-action credit/refund status, and require a fresh preflight before any explicit retry; never claim a failed item created a version or retry the successful work automatically.
- AI opt-in off explains why and links to the authorized admin entry through `../27-settings-shell/evidgate-settings.html`; exhausted credits links to `../31-quota-retention/evidgate-usage-retention.html`, gateway unavailable offers an explicit retry and manual `../40-authoring-tree/evidgate-authoring-tree.html` path. No purchase flow or invented fallback model.

## States (in addition to the shared switcher)

- Empty/oversized/unsupported/scanning/rejected attachment; extraction failure; opt-in off; credits exhausted; estimate unavailable; gateway unavailable; schema invalid (no version, credit refunded per supplied response); partial test-case generation; review pending; test cases ready for review.

## Fixture (illustrative, labelled as such)

- storefront-web / `SHOP` / `ais-3`; “A customer reviews an order before confirming / Khách hàng xem lại đơn hàng trước khi xác nhận” + `checkout-requirement.pdf`; `SHOP-CL-0017` “Show the order total / Hiển thị tổng đơn hàng”, priority high; `SHOP-TC-0042` “Confirm order shows the total / Xác nhận đơn hàng hiển thị tổng tiền”, v5 (v4 history); `SHOP-TC-0043` “Required delivery address / Địa chỉ giao hàng bắt buộc”, v2; `SHOP-TC-0051` “Apply a discount code / Áp dụng mã giảm giá”, v3. Models `model-small` / `model-large` are placeholder provenance ids.
- Period 2026-10: balance 1 240 of 2 000 credits. Estimates: checklist generation 12 · test cases 4 per checklist item · translate 1 per case · script step mapping 2 per unbound step · locate fallback 3 per incident. Usage this period: checklist 312 · test cases 284 · translate 41 · script mapping 96 · locate fallback 27. Use these exact illustrative values, never money; show only G1 actions here, leaving later actions to their own phase screens.

## Copy EN / VI

- “Requirement / Yêu cầu”; “Estimated credits / Tín dụng AI ước tính”; “Remaining balance / Số dư tín dụng AI còn lại”; “Generate checklist / Tạo danh sách kiểm tra”; “Generate test cases / Tạo ca kiểm thử”.
- “AI-generated / Do AI tạo”; “Review pending changes / Xem xét thay đổi chờ áp dụng”; “Some items could not be generated / Không thể tạo một số mục”; “AI generation unavailable / Tính năng tạo bằng AI không khả dụng”.

## Keep unchanged

- Shared shell/tokens, `generation` nav selection, permissions and conflict patterns, theme/locale/state switchers, read-only mobile and untrusted-input/output rules. Do not add scripts, mobile execution or credit prices.

## Done when

- Changes are baked into static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- Local states show both AI preflights, the pending-changes review boundary and each named failure; no generation while opt-in is off, no auto-apply, no guessed caps or price.
