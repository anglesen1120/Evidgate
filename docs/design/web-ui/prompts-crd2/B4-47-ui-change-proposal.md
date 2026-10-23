Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict.

**Page:** create `47-ui-change-proposal/evidgate-ui-change-proposal.html`, reusing the shell and `assets/` exactly like `03-run-list/evidgate-runs.html`.
**Phase:** G3. **Nav item:** `repairs` (set `data-nav` on `<html>`; highlight parent page 12).
**Plan ids:** 13.8.1-T1, 13.8.1-T3, 13.8.2-T2, 13.1.2-T1, 13.4.1-T3.

## Goal

Review a UI-change proposal against the pinned manual-case version and queue an accepted change for authoring review, never for automatic script repair.

## Change

- Build a review header with proposal id/status, `test_drift` / UI-change classification and rule reason. State explicitly that meaning changed and no script repair is proposed.
- Place the original failed result, attempt and evidence link first and persistently visible beside the proposal; on narrow layouts it precedes the proposed change, never hidden behind a tab.
- Show manual-case code, approved/base authoring version, current version, author/source/reason and timeline links at `../41-test-case-editor/evidgate-test-case-editor.html`. Immutable history is never rewritten.
- Use a bounded read-only comparison for current and proposed manual-case steps: action, expected result and data fields. Highlight semantic changes for human review, not as safe locator healing; redact sensitive content.
- Include source run/step, profile-reference hashes/spec version and structural comparison reason. AI suggestions, if present, are untrusted plain text marked AI-generated with supplied model, settings version and time; no fabricated model names or safety scores.
- Primary action "Queue for review" accepts into pending changes at `../42-pending-changes/evidgate-pending-changes.html`, not directly into a version. After a supplied success fixture, show queued status and a link to that pending batch; retries cannot duplicate the same proposal.
- Secondary "Reject proposal" opens a labelled reason dialog; require a nonblank reason, show submitting/error/success, preserve text on error, trap/return focus and support Escape.
- Queueing preserves the base version and uses the shared stale-data/412 pattern when applicable. Do not auto-merge; show the new current version and preserve the review draft for explicit reload.
- Only the separate page 42 batched-authoring flow can apply pending changes as one immutable version. This proposal page has no Apply, script repair, PR creation, merge or execution action.
- Known ungranted authoring-write permission makes review read-only with a reason; mobile remains read-only. Do not infer approval rights from a role name or invent route contracts.

## States (in addition to the shared switcher)

- Pending review; queueing; queued with pending-batch link; rejected with reason; duplicate acceptance already queued; stale base version; unavailable comparison/evidence; permission revoked.
- Missing required rejection reason; long bilingual step text; source failure remains visible after queue/reject and no release verdict appears.

## Fixture (illustrative, labelled as such)

- Use the delta brief's canonical fixtures verbatim: `inc-91` `product_defect` (HTTP 500 on Place order): no repair; `inc-92` `test_drift` → locator drift: candidate `cand-73`; `inc-93` `test_drift` → UI change: test case review proposal `ucp-12` for `SHOP-TC-0051` "Apply a discount code / Áp dụng mã giảm giá" v3. A stale-base specimen reviews historical v2 against current v3, never changes the canonical current version.
- Proposed wording is an illustrative human-review suggestion, not confirmed product intent; no new authoring version exists until the separate pending batch is applied.
- Match page 42's accepted specimen exactly: action "Enter a code / Nhập mã" → "Open the discount field, then enter a code / Mở trường mã giảm giá rồi nhập mã"; expected "Discount field is visible / Trường mã giảm giá hiển thị" → "Discount field opens on request and accepts the code / Trường mã giảm giá mở khi được yêu cầu và nhận mã"; reason "Review the changed discount interaction / Xem xét tương tác mã giảm giá đã thay đổi".

## Copy EN / VI

- "UI-change proposal / Đề xuất do giao diện thay đổi"; "No script repair proposed / Không đề xuất sửa kịch bản".
- "Queue for review / Đưa vào danh sách chờ xem xét"; "Queued in pending changes / Đã đưa vào danh sách thay đổi chờ áp dụng".
- "Reject proposal / Từ chối đề xuất"; "Reason required / Cần nhập lý do"; "Open pending changes / Mở thay đổi chờ áp dụng".

## Keep unchanged

- Reuse authoring comparison/version patterns, shared confirmation/conflict behavior and shell tokens; do not change page 42's apply contract or the original failure data.

## Done when

- Changes are baked into the static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- Acceptance only queues into page 42, rejection requires a reason, stale review changes nothing, and the failure remains visible with no script repair/apply action.
