Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict.

**Page:** `08-failure-inbox/evidgate-failure-inbox.html`; edit in place.
**Phase:** G3. **Nav item:** `failures` (set `data-nav` on `<html>`).
**Plan ids:** 13.8.1-T1, 13.8.1-T2, 13.8.1-T3, 13.8.2-T2, 08.2.2, 09.4.1-T1.

## Goal

Separate the five failure labels from the two test-drift classes and show which incidents have a repair or test-case-review outcome.

## Change

- In `incident-filters`, retain the existing `.label-filter` choices: `product_defect`, `test_drift`, `environment`, `data_issue`, `unknown`. Do not introduce locator drift as a sixth failure label.
- Add an independently labelled drift-class filter: all, locator drift, UI change. Selecting a drift class scopes to `test_drift`; clearing it restores the normal label filters and resets the cursor.
- In `.incidents` / `incident-table-panel`, retain and explicitly label the existing failure-label column and source badge; add drift class and outcome columns without combining them with test outcome.
- For `test_drift`, locator drift links to an available candidate at `../13-healing-shadow-detail/evidgate-candidate-detail.html`; UI change links to `../47-ui-change-proposal/evidgate-ui-change-proposal.html` and never a script-repair candidate.
- For every non-`test_drift` label, show not-applicable drift class and "No repair" with no candidate link/count. Remove the current `inc-91` product-defect candidate implication from both row and selected preview.
- Update `selected-incident-detail` with the same label/class/outcome facts and source attribution. Rule/model suggestions remain distinct from human-confirmed classification; missing comparison evidence is not locator drift.
- Remove release-decision copy from selected-preview descriptions; preserve original failed result, evidence revision and context readiness. No release verdict in any G3 variant.

## States (in addition to the shared switcher)

- All five labels; both drift classes; comparison pending/ambiguous with no repair; candidate unavailable despite locator drift; no incidents versus no class-filter matches.
- A changed row selection keeps its outcome link aligned with the selected incident; stale triage uses the shared conflict specimen, never a fabricated read response.

## Fixture (illustrative, labelled as such)

- Use the delta brief's canonical fixtures verbatim: `inc-91` `product_defect` (HTTP 500 on Place order): no repair; `inc-92` `test_drift` → locator drift: candidate `cand-73`; `inc-93` `test_drift` → UI change: test case review proposal `ucp-12` for `SHOP-TC-0051`.
- `SHOP-TC-0051` "Apply a discount code / Áp dụng mã giảm giá" v3; any environment/data-issue/unknown review-state specimen has no repair and must not overwrite the canonical `inc-91/92/93` identities.

## Copy EN / VI

- "Failure label / Nhãn lỗi"; "Drift class / Loại thay đổi kiểm thử"; "Locator drift / Thay đổi bộ định vị"; "UI change / Thay đổi giao diện".
- "No repair / Không có đề xuất sửa"; "Review test case / Xem xét ca kiểm thử"; "Not applicable / Không áp dụng".

## Keep unchanged

- Incident search, status/owner/severity/flaky filters, cursor pager, original status and evidence links, keyboard row selection, shell, tokens and restricted-context behavior.

## Done when

- Changes are baked into the static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- Both drift classes filter correctly; each non-test-drift label has no repair link and UI change never routes to script repair.
