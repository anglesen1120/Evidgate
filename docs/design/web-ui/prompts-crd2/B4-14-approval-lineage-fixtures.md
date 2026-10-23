Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict.

**Page:** `14-healing-approval/evidgate-candidate-detail.html` and `15-knowledge-revocation/evidgate-knowledge-lineage.html`; edit fixture references in place using shared `assets/`.
**Phase:** G3. **Nav item:** page 14 `repairs` (replace old `healing` in `data-nav` on `<html>`); page 15 preserves `knowledge`.
**Plan ids:** 09.5.2, 13.8.1-T1, 13.8.1-T2, 13.8.2-T2, 13.4.1-T7.

## Goal

Keep approval and knowledge lineage consistent with the canonical incident classification: a product defect never becomes a locator-repair candidate.

## Change

- On page 14, fix `candidate-provenance`, `original-failure-panel`, `candidate-lineage-panel` and their scenario-reset values: `cand-73` comes from locator-drift `inc-92`, never product-defect `inc-91`.
- Replace the HTTP 500 / Place order source message attached to `cand-73` with a locator-failure specimen consistent with `SHOP-TC-0043` required-delivery-address intent. Keep original raw/normalized failure, attempt and evidence references visible and internally aligned.
- Align text-only locator diff, unchanged assertion and validation explanations in `proposal-diff-panel` / `validation-evidence-panel` to that same required-address intent, without weakening any assertion or safety check.
- On page 15, correct `knowledge-record` / `#sourceFailure` and `lineage-history`: the source chain is `inc-92` test_drift → locator drift → `cand-73` → existing `kr-22`. Remove the current inc-91 human-test_drift claim and HTTP 500 association.
- Preserve `inc-91` as product_defect with no candidate/knowledge repair if it appears in another scenario; `inc-93` UI change leads only to `ucp-12` authoring review, not this locator lineage.
- Update all default, translated, loading/error/history/reset fixture strings that would restore the old source relationship. Do not alter approval/revocation authorization, concurrency, negative-control or independent-review behavior.
- Body links use sibling-relative paths: incident `../09-failure-detail-triage/evidgate-incident-detail.html`, run `../04-run-detail/evidgate-run-detail.html`, manual case `../41-test-case-editor/evidgate-test-case-editor.html`, UI-change proposal `../47-ui-change-proposal/evidgate-ui-change-proposal.html`.

## States (in addition to the shared switcher)

- Existing approval/rejection, historical and revoked states retain original failure and immutable lineage; no release verdict or Apply action is introduced.
- Reloading or switching locale/scenario never brings back an inc-91 locator-repair association. Unavailable metadata remains unavailable, not fabricated provenance.

## Fixture (illustrative, labelled as such)

- Use the delta brief's canonical fixtures verbatim: `inc-91` `product_defect` (HTTP 500 on Place order): no repair; `inc-92` `test_drift` → locator drift: candidate `cand-73`; `inc-93` `test_drift` → UI change: test case review proposal `ucp-12` for `SHOP-TC-0051`.
- `SHOP-TC-0043` "Required delivery address / Địa chỉ giao hàng bắt buộc" v2; `SHOP-TC-0051` "Apply a discount code / Áp dụng mã giảm giá" v3. Runs are `run-1842` from CI (GitHub Actions), `run-1843` local (`evidgate run`, laptop of Linh); keep any displayed source consistent.

## Copy EN / VI

- "Original failure / Lỗi gốc"; "Locator drift / Thay đổi bộ định vị"; "No repair / Không có đề xuất sửa".
- "Source incident / Sự cố nguồn"; "Knowledge lineage / Nguồn gốc tri thức".

## Keep unchanged

- Both layouts, typography/tokens, approval and revocation dialogs, permissions, hash binding, sample metrics, history/negative-control semantics, dependent-candidate and effect-preview panels. No restyle or new workflow; only fixture consistency and page 14 nav identity change.

## Done when

- Corrections are baked into static HTML; scenario/locale handlers preserve them. Both pages continue using the nav array in `assets/evidgate.js` and shared CSS.
- Both pages work at 1280 px and 360 px, light/dark and vi/en; keyboard focus remains visible and nothing overflows.
- `cand-73` and its knowledge lineage consistently belong to inc-92; inc-91's HTTP 500 remains a product defect with no repair, never a candidate source.
