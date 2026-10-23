Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict.

**Page:** `25-test-catalog/evidgate-test-catalog.html`; edit in place.
**Phase:** G2. **Nav item:** `catalog` (set `data-nav` on `<html>`).
**Plan ids:** 13.1.1-T4, 13.4.1-T2, 09.2.1-T6.

## Goal

Understand how stable automated `test_case` identities cover manual cases, including manual cases that have no automated link yet.

## Change

- In `catalog-heading`, explain the distinction between automated identity and authored manual case without renaming `evidgate.id` or changing immutable revision semantics.
- Above `catalog-results`, add two accessible views: automated identities and uncovered manual cases. This is a local view switch, not a new API contract.
- In `catalog-filters`, retain existing automated filters and add a clearly scoped coverage choice; the uncovered view uses manual-case title/code search rather than pretending platform/revision fields exist there. View/filter changes reset cursor.
- In `#caseRows`, add linked manual-case code/title/version beside each automated identity, using `../41-test-case-editor/evidgate-test-case-editor.html`. Show multiple supplied links without collapsing identities into one case.
- In the uncovered view, list manual code, title, authoring version and "uncovered" status. A manual case with zero linked automated identities belongs here; never fabricate an automated id or confuse this with an unlinked automated test.
- Provide a link from uncovered cases to `../40-authoring-tree/evidgate-authoring-tree.html`; do not introduce auto-linking, bulk linking, or automatic script generation.

## States (in addition to the shared switcher)

- One manual case linked to multiple automated identities; automated identity with no supplied manual link; empty automated catalog; no uncovered cases versus no filtered matches.
- Link metadata unavailable is explicitly unavailable, not zero coverage. Revoked authoring-read permission must not expose case content through a link preview.

## Fixture (illustrative, labelled as such)

- Use the delta brief's canonical fixtures verbatim: `SHOP-TC-0042` "Confirm order shows the total / Xác nhận đơn hàng hiển thị tổng tiền" v5 (v4 is history), linked to `case-checkout-17` and `case-checkout-firefox-17`; script `gs-0042` is active, 3/3 green on `b-318`, PR #214 merged.
- `SHOP-TC-0043` "Required delivery address / Địa chỉ giao hàng bắt buộc" v2 has draft `gs-0043`, 2 unbound steps and PR not opened. Its uncovered specimen has zero `manual_case_link` identities: an unfinished draft alone is not an automated identity link.
- `SHOP-TC-0051` "Apply a discount code / Áp dụng mã giảm giá" v3 has `gs-0051`, flaky (green on retry in 1 of 3), PR #219 open. Do not rename a canonical manual case to demonstrate long content.

## Copy EN / VI

- "Automated identities / Định danh kiểm thử tự động"; "Linked manual cases / Ca kiểm thử thủ công được liên kết".
- "Uncovered manual cases / Ca kiểm thử thủ công chưa tự động hóa"; "No automated link / Chưa liên kết kiểm thử tự động".
- "All manual cases are covered / Mọi ca kiểm thử thủ công đều đã được liên kết kiểm thử tự động"; "Open authoring / Mở phần soạn kiểm thử".

## Keep unchanged

- `identity-rename-panel`, `historyDialog`, stable id and revision/hash display, owner assignment and permission scope, existing catalog pager, shell and token styles.

## Done when

- Changes are baked into the static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- The uncovered fixture is reachable even with no automated identity; multiple automated links do not rewrite the authored case or its version.
