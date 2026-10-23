Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict.

**Page:** `03-run-list/evidgate-runs.html`; edit in place.
**Phase:** G2. **Nav item:** `runs` (set `data-nav` on `<html>`).
**Plan ids:** 13.5.2-T1, 13.5.1-T4, 13.1.1-T4, 09.3.1-T1.

## Goal

Identify local and CI executions and trace their results back to generated scripts and manual cases without confusing script health with test outcome.

## Change

- In `runs-heading`, replace the release-evaluation-led intro with script/run provenance copy; do not imply that the browser hosts execution.
- In `run-filters`, replace `decisionFilter` with a run-source filter: all, local `evidgate run`, CI. Preserve execution-state, completeness and rerun-scope filters; changes reset the cursor.
- In the Run list table (`#runsBody`), remove the release-decision column for G2/G3; add source and script-status columns. Status chips use `draft`, `active`, `flaky` with localized explanations, never a combined run/script verdict.
- In each `.detail-row .detail-grid`, add linked generated scripts at `../46-generated-scripts/evidgate-generated-scripts.html` and linked manual cases at `../41-test-case-editor/evidgate-test-case-editor.html`. Show supplied ids; multiple scripts retain individual statuses, never an inferred aggregate.
- Remove release-decision source/value copy from expanded rows and all review-state variants. Preserve the failed test outcome and missing-evidence explanation as test data, not a release verdict.
- Keep create/rerun/cancel interactions as existing supplied fixtures, not a new hosted-run capability; source metadata describes where execution actually happened.

## States (in addition to the shared switcher)

- Local and CI sources; source unavailable; no generated script/manual link; mixed script statuses; empty project versus no source-filter matches.
- Collecting run has pending outcome even if its linked script was previously active; a flaky script never hides the original failed attempt.

## Fixture (illustrative, labelled as such)

- Use the delta brief's canonical fixtures verbatim: `run-1842` from CI (GitHub Actions); `run-1843` local (`evidgate run`, laptop of Linh). Preserve their supplied execution/completeness/outcome independently of script status.
- `SHOP-TC-0042` "Confirm order shows the total / Xác nhận đơn hàng hiển thị tổng tiền" v5; `SHOP-TC-0043` "Required delivery address / Địa chỉ giao hàng bắt buộc" v2; `SHOP-TC-0051` "Apply a discount code / Áp dụng mã giảm giá" v3.
- `gs-0042`: active, 3/3 green on build `b-318`, PR #214 merged; `gs-0043`: draft, 2 unbound steps, PR not opened; `gs-0051`: flaky (green on retry in 1 of 3), PR #219 open.

## Copy EN / VI

- "Run source / Nguồn lượt chạy"; "Local · evidgate run / Cục bộ · evidgate run"; "CI / CI".
- "Generated script / Kịch bản được tạo"; "Manual test case / Ca kiểm thử thủ công"; "Script status / Trạng thái kịch bản".
- "Draft / Bản nháp"; "Active / Đang hoạt động"; "Flaky / Chập chờn"; "Link unavailable / Chưa có liên kết".

## Keep unchanged

- Run identity links, evidence-revision and reprocessing semantics, timestamps, cursor pager, expanded-row keyboard behavior, existing tokens and shell assets.

## Done when

- Changes are baked into the static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- Source filtering and script/manual-case links work with local fixtures; no release verdict is visible in any G2/G3 scenario.
