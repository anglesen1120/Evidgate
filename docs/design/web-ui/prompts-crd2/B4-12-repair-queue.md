Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict.

**Page:** `12-healing-queue/evidgate-healing-review.html`; edit in place.
**Phase:** G3. **Nav item:** `repairs` (replace old `healing` in `data-nav` on `<html>`).
**Plan ids:** 13.8.1-T2, 13.8.1-T3, 13.8.1-T4, 13.8.2-T2, 09.5.1-T1.

## Goal

Browse locator-drift repair candidates and UI-change test-case proposals as distinct outcomes, with shadow-window and PR-delivery context.

## Change

- Rename `page-heading` and `#queueTitle` from Healing Review / Shadow proposals to Repair proposals; retain the proposal-only safety message in `shadow-status`.
- Extend `#queueSection .filters` with proposal kind: locator drift or UI change. Preserve status/availability filters and cursor pagination; filter changes reset cursor.
- In `#candidateRows`, add kind, linked manual case/version, shadow-window status and PR state/link. Keep failure label/source and supplied validation/sample data separate; absence of PR data is not an open PR.
- Locator drift rows link to `../13-healing-shadow-detail/evidgate-candidate-detail.html`; UI-change rows link to `../47-ui-change-proposal/evidgate-ui-change-proposal.html` and show test-case review, never locator repair validation.
- In `candidate-detail` / `#detailData`, show the selected proposal's classification reason, source incident, case/version, kind and appropriate next destination. Update selected-row summaries consistently.
- Extend `shadow-status` with supplied per-project window state and metrics (start/end when supplied, observed proposal count); while the window is active, show observing/not offered and no PR-offer action. Do not invent a duration, threshold or automatic end condition.
- Keep `availability-scope` distinct from the shadow window: most-restrictive availability still wins even when the window is complete. Completion alone does not imply validation or reviewer approval.
- Correct old fixtures that imply candidates for `unknown` or `product_defect`: active proposals require `test_drift`. Historical records remain read-only and clearly non-actionable; no apply or release-verdict action in any state.
- The queue is read-only navigation. PR state is applicable to locator-repair delivery; UI-change proposals show pending-authoring-review status and PR not applicable, rather than pretending acceptance changes a script.

## States (in addition to the shared switcher)

- Locator candidate and UI-change proposal; shadow window observing/completed/unavailable; PR not offered/open/merged/closed; availability disabled/unavailable; missing metrics; empty queue versus no kind-filter matches.
- Missing validation does not imply passed; historical applied remains a history label only, never a control or evidence that runtime auto-apply is enabled.

## Fixture (illustrative, labelled as such)

- Use the delta brief's canonical fixtures verbatim: `inc-91` `product_defect` (HTTP 500 on Place order): no repair; `inc-92` `test_drift` → locator drift: candidate `cand-73`; `inc-93` `test_drift` → UI change: test case review proposal `ucp-12` for `SHOP-TC-0051`.
- `cand-73` uses `SHOP-TC-0043` "Required delivery address / Địa chỉ giao hàng bắt buộc" v2; `ucp-12` uses `SHOP-TC-0051` "Apply a discount code / Áp dụng mã giảm giá" v3. Supplied 24 samples are not a safety probability.

## Copy EN / VI

- "Repair proposals / Đề xuất sửa kiểm thử"; "Proposal kind / Loại đề xuất"; "Shadow window / Giai đoạn quan sát mô phỏng".
- "Observing · PR not offered / Đang quan sát · chưa đề nghị tạo yêu cầu hợp nhất"; "PR state / Trạng thái yêu cầu hợp nhất".
- "Test case review / Xem xét ca kiểm thử"; "Not applicable / Không áp dụng"; "Proposal only / Chỉ là đề xuất".

## Keep unchanged

- Existing split queue/detail layout, dependency banner, availability resolution, safe historical metadata, page-size limits, shell and tokens; no approval workflow added to the queue.

## Done when

- Changes are baked into the static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- Both kinds open the correct detail; active shadow windows offer no PR delivery and non-test-drift incidents have no active repair proposal.
