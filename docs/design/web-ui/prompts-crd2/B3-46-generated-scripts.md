Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict.

**Page:** create `46-generated-scripts/evidgate-generated-scripts.html`, reusing the shell and `assets/` exactly like `03-run-list/evidgate-runs.html`.
**Phase:** G2. **Nav item:** `scripts` (set `data-nav` on `<html>`).
**Plan ids:** 13.5.1-T1, 13.5.1-T2, 13.5.1-T4, 13.5.1-T5, 13.5.1-T6, 13.5.1-T7.

## Goal

Inspect generated Playwright scripts, binding gaps, stability and repository delivery without offering browser-hosted execution or direct application of code.

## Change

- Build a dense list with search/status filters, cursor pager and columns for script, manual-case link, automated `test_case`, status, stability/build and PR state/link. Opening a row reveals a detail drawer.
- Above the list show `storefront-web` testability 72 / 100 from the canonical fixture, supplied measurement time and an unavailable variant. Do not invent a formula, threshold or safety interpretation.
- Show `draft`, `active`, `flaky` distinctly: active requires 3/3 green stability runs on the same build; green only on retry is flaky. Mixed-build results never count as 3/3.
- The drawer links the approved manual case/version at `../41-test-case-editor/evidgate-test-case-editor.html`, automated catalog at `../25-test-catalog/evidgate-test-catalog.html` and run evidence at `../04-run-detail/evidgate-run-detail.html`; preserve ids and `evidgate.id` annotation in read-only provenance.
- List every step with bound/unbound state; for unbound steps show step number, action and supplied reason. Any unbound step keeps the script draft; never guess a binding or expose user content/input values.
- Add locator-policy lint results by step with rule, result and bounded plain-text explanation: role/label/test id → text → CSS priority; reject absolute XPath, `nth()`, `force: true`, skip/fixme and catch-and-ignore. Missing lint is unavailable, not passed.
- Show each of the three stability attempts, build identity, original status and retry separately; script status is supplied fixture data, not calculated by the browser.
- PR link/state is separate from stability/status. Repository delivery is PR-only; open/merged/closed/not-created fixtures do not imply runtime application. No Apply, direct commit, merge or hosted-run button.
- Use an accessible drawer with labelled heading, close button, Escape, focus containment/return; at narrow widths show a read-only stacked detail view. The primary contextual action is opening an existing PR, not generating code in the browser.
- Explain customer-side `evidgate generate` needs an approved case, environment URL and seed test. Missing seed halts generation; Q3 on who supplies a seed remains open, with no fabricated seed fallback.

## States (in addition to the shared switcher)

- Draft with unbound steps; active 3/3 same build; flaky green-on-retry; mixed-build samples; lint rejection/unavailable; missing seed; PR not created/open/merged/closed; score unavailable; no scripts versus no matches.

## Fixture (illustrative, labelled as such)

- Use the delta brief's canonical fixtures verbatim: `gs-0042` for `SHOP-TC-0042` "Confirm order shows the total / Xác nhận đơn hàng hiển thị tổng tiền" v5: active, 3/3 green on build `b-318`, PR #214 merged.
- `gs-0043` for `SHOP-TC-0043` "Required delivery address / Địa chỉ giao hàng bắt buộc" v2: draft, 2 unbound steps, PR not opened; `gs-0051` for `SHOP-TC-0051` "Apply a discount code / Áp dụng mã giảm giá" v3: flaky (green on retry in 1 of 3), PR #219 open.
- `storefront-web` testability 72 / 100; `run-1842` from CI (GitHub Actions), `run-1843` local (`evidgate run`, laptop of Linh). PR links use illustrative repository URLs ending `/pull/214` and `/pull/219`, never a different PR fixture.

## Copy EN / VI

- "Generated scripts / Kịch bản được tạo"; "Draft / Bản nháp"; "Active / Đang hoạt động"; "Flaky / Chập chờn".
- "3/3 green on one build / 3/3 lượt chạy đạt trên cùng một bản dựng"; "Unbound steps / Các bước chưa liên kết phần tử".
- "Locator-policy lint / Kiểm tra chính sách bộ định vị"; "Project testability score / Điểm khả năng kiểm thử của dự án"; "Delivered through a pull request only / Chỉ chuyển mã qua yêu cầu hợp nhất".

## Keep unchanged

- Shared navigation phase gate, typography, tokens, table/pager and drawer patterns; existing run/catalog pages. No new dependencies, credit prices or execution infrastructure.

## Done when

- Changes are baked into the static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- Drawer states demonstrate same-build stability, explicit unbound steps and lint/PR distinctions; no script can be applied without a PR and no release verdict appears.
