Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict.

**Page:** `09-failure-detail-triage/evidgate-incident-detail.html`; edit in place.
**Phase:** G3. **Nav item:** `failures` (set `data-nav` on `<html>`).
**Plan ids:** 13.8.1-T1, 13.8.1-T2, 13.8.1-T3, 13.8.2-T2, 08.2.2, 09.4.1-T2.

## Goal

Explain the rule-based classification and its outcome while preserving human triage and the original failure.

## Change

- Adjacent to `triage-panel`, add a classification panel with separate failure label, label source, drift class, comparison rule/reason and resulting proposal link or none.
- Compare the last-green profile with the failed-step profile using metadata: case revision, step, profile hashes, spec version and comparison time. Present reasons as bounded untrusted plain text, never raw profile/user/input content.
- For `test_drift`, explain the supplied rule: same role and accessible name → locator drift; missing/changed meaning → UI change. Show equality/change facts, not harvested content values. Models may suggest but never decide this class.
- Replace `shadow-proposal` and its disabled V2-only action with a conditional outcome region: locator drift → candidate link at `../13-healing-shadow-detail/evidgate-candidate-detail.html`; UI change → review proposal at `../47-ui-change-proposal/evidgate-ui-change-proposal.html`; other labels → no repair.
- Preserve `inc-91` as a product defect in the default fixture and remove its misleading `cand-73` relation. Use separate selected review fixtures for drift examples, with internally consistent title/run/step metadata.
- Missing profiles, incompatible spec versions or ambiguity show comparison unavailable/unknown and no repair; do not let a model-generated reason create eligibility.
- Preserve `triageForm` label/reason/assignee/close flow and server-confirmed human attribution; saving a label does not rewrite frozen evidence or erase the original failure.
- Remove the release-decision item from `decision-strip` and any release-verdict copy in scenarios. Keep collection completeness, test outcome, original failure and retry history distinct.

## States (in addition to the shared switcher)

- Product defect, environment, data issue and unknown → no repair; locator drift → candidate; UI change → manual-case review only.
- Profile missing/spec mismatch/ambiguous; valid comparison but proposal pending/unavailable; stale human triage with preserved reason draft; restricted evidence.

## Fixture (illustrative, labelled as such)

- Use the delta brief's canonical fixtures verbatim: `inc-91` `product_defect` (HTTP 500 on Place order): no repair; `inc-92` `test_drift` → locator drift: candidate `cand-73`; `inc-93` `test_drift` → UI change: test case review proposal `ucp-12` for `SHOP-TC-0051`.
- `SHOP-TC-0051` "Apply a discount code / Áp dụng mã giảm giá" v3; `SHOP-TC-0043` "Required delivery address / Địa chỉ giao hàng bắt buộc" v2. Other label states are separate review specimens, not relabelled canonical incidents.

## Copy EN / VI

- "Classification reason / Lý do phân loại"; "Profile comparison / So sánh hồ sơ phần tử"; "Same role and accessible name / Cùng vai trò và tên truy cập".
- "Locator drift / Thay đổi bộ định vị"; "UI change / Thay đổi giao diện"; "No repair / Không có đề xuất sửa".
- "Comparison unavailable / Chưa thể so sánh"; "Open test case review proposal / Mở đề xuất xem xét ca kiểm thử".

## Keep unchanged

- `original-failure-panel`, step-event chronology, bounded plain-text logs, artifact sensitivity controls, attempt history, human-triage permissions/conflict flow, shell and tokens.

## Done when

- Changes are baked into the static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- Every classification fixture has an explicit rule reason and consistent outcome; the original failure stays visible and no non-test-drift case offers repair.
