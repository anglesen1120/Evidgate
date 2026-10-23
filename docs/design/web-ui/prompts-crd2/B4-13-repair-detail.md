Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict.

**Page:** `13-healing-shadow-detail/evidgate-candidate-detail.html`; edit in place.
**Phase:** G3. **Nav item:** `repairs` (replace old `healing` in `data-nav` on `<html>`).
**Plan ids:** 13.8.1-T2, 13.8.1-T4, 13.8.2-T1, 13.8.2-T2, 08.6.1, 09.5.1-T2.

## Goal

Review a locator-drift proposal with its matching manual-case edit and validation evidence, then inspect PR delivery without applying changes or hiding the failure.

## Change

- Rename `candidate-heading` to repair proposal detail; retain `candidate-provenance` hashes, base commit, incident/run/evidence identity and rule-classification reason.
- Correct `original-failure-panel` fixture to a locator timeout for `inc-92` / `test_drift`, not HTTP 500 product failure. Keep it always visible, before proposed/shadow results on narrow layouts, including loading/error states with known context.
- Extend `proposal-diff-panel` with accessibility/DOM structural diff and before/after screenshots only when project screenshot opt-in and artifact permissions/readiness permit. Opt-out renders an explanation and no image request, preview, thumbnail or open/download control.
- Treat accessibility/DOM diff as redacted untrusted plain text; exclude input values and user content. Missing diff is unavailable, not empty/equal. Ready screenshot links use `../11-artifact-viewer/evidgate-artifact-viewer.html` on the isolated viewer origin, not raw URLs.
- Beside the locator diff, show the matching manual-case step edit linked to `../41-test-case-editor/evidgate-test-case-editor.html`, pinned authoring version and proposed version context. It changes locator wording only, not expected business behavior.
- Keep `validation-evidence-panel` and all 08.6.1 review fields: semantic identity, unique match, business region/post-condition, unchanged assertion hash, negative control, sample evidence and provenance. Equal hashes alone do not establish safety; a negative control that passes blocks delivery.
- Preserve `shadow-result-panel` as separate evidence, and add supplied shadow-window state. No PR offer while observing or availability is more restrictive; do not invent window thresholds.
- In `candidate-review-actions`, replace legacy V2 approval/rejection mutations and their dialog with a read-only delivery section: eligible delivery is PR-only, with supplied PR link/state or a reason it is not offered. No Apply, direct merge or assertion-changing repair control anywhere.
- Preserve independent-review requirements for sensitive cases in `risk-review-panel`; rebased/base-hash changes require revalidation. UI-change class links to `../47-ui-change-proposal/evidgate-ui-change-proposal.html`, never this locator-repair diff.

## States (in addition to the shared switcher)

- Screenshot opt-in off/on; ready/restricted/scan-pending screenshot; missing structural diff; insufficient validation; negative control unexpectedly passed; expired/rebased proposal; observing window; PR not offered/open/merged/closed.
- Original failure remains failed beside a green shadow run; unavailable evidence never enables delivery. No release verdict in any G3 state.

## Fixture (illustrative, labelled as such)

- Use the delta brief's canonical fixtures verbatim: `inc-91` `product_defect` (HTTP 500 on Place order): no repair; `inc-92` `test_drift` → locator drift: candidate `cand-73`; `inc-93` `test_drift` → UI change: proposal `ucp-12` for `SHOP-TC-0051` "Apply a discount code / Áp dụng mã giảm giá" v3.
- `cand-73` uses `SHOP-TC-0043` "Required delivery address / Địa chỉ giao hàng bắt buộc" v2. Runs are `run-1842` from CI (GitHub Actions), `run-1843` local (`evidgate run`, laptop of Linh); use only these canonical run identities.
- Align the existing place-order locator/assertion examples to required-delivery-address intent without weakening behavior. Keep role/name identity and expected result unchanged; default screenshots opted out, opted-in specimens synthetic and redacted.

## Copy EN / VI

- "Original failure / Lỗi gốc"; "Matching test case edit / Chỉnh sửa ca kiểm thử tương ứng"; "Accessibility / DOM diff / So sánh cây truy cập / DOM".
- "Screenshots are not enabled for this project / Dự án này chưa bật ảnh chụp màn hình"; "Deliver through a pull request / Chuyển mã qua yêu cầu hợp nhất".
- "Revalidation required / Cần kiểm chứng lại"; "Shadow result does not replace the failure / Kết quả mô phỏng không thay thế lỗi gốc".

## Keep unchanged

- Original/raw versus shadow separation, validation table structure, hash/lineage access, risk-review fields, artifact security rules, shared shell and tokens. Do not edit page 14's separate approval screen.

## Done when

- Changes are baked into the static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- Opt-out performs no screenshot fetch; diff/case edit/validation and original failure remain inspectable; PR is the only repository delivery and no apply control exists.
