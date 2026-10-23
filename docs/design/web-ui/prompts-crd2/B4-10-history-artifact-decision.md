Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict.

**Page:** `10-investigation-history/evidgate-case-history.html` and `11-artifact-viewer/evidgate-artifact-viewer.html`; edit in place using shared `assets/`, not separate self-contained artifacts.
**Phase:** page 10 G3; page 11 G2. **Nav item:** page 10 `failures`, page 11 `runs` (preserve each `data-nav` on `<html>`).
**Plan ids:** 09.4.1, 13.4.1-T7, 13.8.2-T2.

## Goal

Inspect case history and artifacts without displaying stored release decisions before G4, while retaining separate completeness and test-outcome facts.

## Change

- On page 10, remove only the release-decision metric and PENDING chip from `stored-evaluation-fields` / `.metrics`. Preserve collection completeness and the failed test outcome, including its original-failure explanation. Set page 10 `.metrics` to two columns (`repeat(2, …)`) and keep its single-column mobile rule.
- On page 11, remove only the Release decision row and Pending evaluation value from `#decisionFields` / `separate-evaluation-fields`; keep completeness and test outcome rows. Replace "Three independent stored results" with wording that describes the two remaining facts.
- Keep the existing `#decisionFields` id if scenario handlers depend on it; removing the decision row must not break safe hiding/showing of the retained completeness/outcome context.
- Remove matching release-decision/verdict copy from both pages' review scenarios, reset/locale strings and explanatory notes. Do not remove FAIL/PASS when they explicitly describe test outcomes or raw attempts, not release verdicts.
- Preserve page 10's `original-failure-context`, `revision-comparison`, `revision-timeline`, `case-rename-history`, `incident-attempts` and `rerun-comparison` without restyling or rewriting stored test outcomes.
- Preserve page 11's `evidence-viewer`, `artifact-metadata`, `related-artifacts`, isolated-viewer boundary and availability/permission checks; never enable fetching restricted, scan-pending or otherwise non-ready artifacts.
- Keep body links sibling-relative: `../04-run-detail/evidgate-run-detail.html` for runs and `../25-test-catalog/evidgate-test-catalog.html` for catalog context. No new route or decision action.

## States (in addition to the shared switcher)

- Existing loading/empty/error/permission/conflict and historical-revision states remain; none restores a release-decision field in G2/G3.
- Restricted/scan-pending artifacts retain metadata-only access; original failed attempts remain visible beside retry outcomes; incomplete evidence does not imply a passed test.

## Fixture (illustrative, labelled as such)

- Use the delta brief's canonical fixtures verbatim: project `storefront-web`; `run-1842` from CI (GitHub Actions), `run-1843` local (`evidgate run`, laptop of Linh).
- Any manual-case context uses `SHOP-TC-0042` "Confirm order shows the total / Xác nhận đơn hàng hiển thị tổng tiền" v5 (v4 is history), `SHOP-TC-0043` "Required delivery address / Địa chỉ giao hàng bắt buộc" v2 or `SHOP-TC-0051` "Apply a discount code / Áp dụng mã giảm giá" v3; do not invent alternative current titles.
- If incident context appears, `inc-91` is product_defect with no repair, `cand-73` belongs to locator-drift `inc-92`, and UI-change `inc-93` leads only to `ucp-12` for `SHOP-TC-0051`.

## Copy EN / VI

- "Collection completeness / Mức độ đầy đủ của bằng chứng"; "Test outcome / Kết quả kiểm thử".
- "Two independent stored results / Hai kết quả được lưu độc lập"; "Original failure remains unchanged / Lỗi gốc vẫn được giữ nguyên".

## Keep unchanged

- Both layouts, typography/tokens, history/revision selection, raw and normalized attempts, artifact controls/security, keyboard behavior and shell. Only release-verdict presentation and related copy change; no restyle apart from that column count.

## Done when

- Changes are baked into static HTML, not injected by page-specific JS; both pages continue using shared CSS and the nav array in `assets/evidgate.js`.
- Both pages work at 1280 px and 360 px, light/dark and vi/en; keyboard focus remains visible and nothing overflows.
- No default or scenario exposes a G2/G3 release verdict; completeness, outcome, immutable history and artifact restrictions remain intact.
