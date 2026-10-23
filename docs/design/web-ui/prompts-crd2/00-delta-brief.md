# CR-D-2 delta brief for OpenDesign (read with `00-shared-brief.md`)

Upload this file to the OpenDesign project root as `crd2-delta-brief.md`. Every CR-D-2 prompt tells the run to read it first. Where it conflicts with `00-shared-brief.md`, this file wins. All other shared rules still apply.

## What changed

The product changed under owner decision CR-D-2 (`docs/decisions/CR-D-2-direction-a.md`).

- **Old product:** test evidence first, then a release decision.
- **New product:** authoring first. The flow runs requirements → checklist → manual test cases → Playwright scripts bound to real elements → element memory per project → drift classification → repair proposals.
- A repair proposal is a pull request reviewed by a human. It is never applied automatically.

Work ships in four phases:

| Phase | Name | What the user gets |
|---|---|---|
| G1 | Author | Feature tree, checklist and manual test cases, AI generation with credits, versions, batched review, import/export |
| G2 | Run | Generated Playwright scripts (draft / active / flaky), `evidgate run` on a laptop or in CI, runs and evidence |
| G3 | Learn & repair | Failure inbox, drift classification, repair proposals with before/after evidence, shadow healing |
| G4 | Expand | Release decisions, waivers, policies, deploy targets, agent pools, suites, Test Studio V2, quality overview, mobile |

## Navigation rule

- Each sidebar item carries a `phase` (G1–G4).
- An item whose phase is later than the workspace's current phase is **hidden**: not disabled and not teased.
- The design-review control bar gets a "Phase G1 / G2 / G3 / G4" switch (`Giai đoạn`). Its default is G1.
- Group order follows the user's journey: Authoring → Scripts & runs → Learn & repair → Release (G4) → Settings.
- The sidebar stays generated from one nav array in `assets/evidgate.js`. Do not hand-write sidebar markup per page.

## Authoring vocabulary (plan 13, epics 13.1–13.4)

- **Tree:** module → feature → category → checklist item → manual test case. A project code prefixes exported ids, for example `SHOP-TC-0042`.
- **Version:**
  - Every change (manual, AI or import) creates an immutable version with author, source (`manual` / `ai` / `import`) and reason.
  - Restore creates a new version and never rewrites history.
- **Batched review:**
  - Comments, merges, splits and deletes on checklist items queue as **pending changes**.
  - "Apply" writes them as one version with `If-Match`.
  - A stale apply changes nothing and shows the shared 412 pattern.
- **Generation:**
  - Inputs are requirement text, DOCX/PDF (text extracted) and images, within type and size caps.
  - The flow produces a prioritised checklist first and test cases second.
  - Per-case actions: rebuild, translate (vi ↔ en).
  - Before any AI call the screen shows the **estimated credit cost** and the remaining balance.
  - Per-tenant AI opt-in must be on. When it is off, show why and give an admin link; never show a fake result.
  - AI output is untrusted plain text, marked "AI-generated / Do AI tạo", with provenance: model, settings version, time.
- **Project AI settings** (versioned), with length limits shown as live counters:
  - description;
  - environment URL;
  - languages;
  - writing style (≤ 250 chars);
  - coverage strategy (≤ 1 500);
  - four priority criteria;
  - exclusion rules (≤ 1 500);
  - step detail (≤ 200).
- **Coverage:** a manual test case links to zero or more automated `test_case` identities. With no link, it shows as **uncovered / chưa tự động hoá**.
- **Permissions** (role mapping is still an inference): `authoring:read`, `authoring:write`, `authoring:generate`, `script:generate`. Hide or disable actions the server does not grant, and explain why.

## Scripts, memory and repair (plan 13, epics 13.5–13.8)

- **Generated script:**
  - Status is `draft` → `active` (3 of 3 green on one build) or `flaky` (green only on retry).
  - It reaches the repository only as a pull request; show the PR link and state.
  - A **testability score** is shown per project.
  - Unbound steps keep the script `draft` and are listed by step.
- **Element profile:** per bound step it stores structure (role, accessible name, test id, neighbours, position), never user content or input values. The UI shows "last green profile" metadata only.
- **Drift classification:**
  - Failure labels are `product_defect`, `test_drift`, `environment`, `data_issue`, `unknown`.
  - Inside `test_drift`, the class is **locator drift** (same role and accessible name, so a candidate locator plus a matching test case edit) or **UI change** (meaning changed, so a test case review proposal; no script repair).
  - Every other label yields no repair.
- **Repair proposal:**
  - Before/after screenshots appear only when the project opted in.
  - It always shows an accessibility/DOM diff and the 08.6.1 review fields.
  - It is delivered as a PR. There is never an "apply" button.
  - The original failure stays visible.
- **Credits:** AI usage is metered in credits. Usage & quota shows balance, usage by action (checklist, test case, translate, script mapping, locate fallback) and period.

## Deliverable and links (overrides `00-shared-brief.md`)

- Edit or create the named nested HTML file inside the existing project structure, and reuse `assets/evidgate.css` and `assets/evidgate.js`.
- Do not produce a separate self-contained artifact, and do not inline a copy of the shared CSS. This replaces the shared brief's self-contained / embedded-CSS deliverable rule.
- Paths in a prompt's **Page** line and in the nav array are project-root identifiers. A body link between numbered folders uses `../<folder>/<file>.html`.
- Page phase: task phases follow the Milestone cells of the plans. A screen can be hidden later than its tasks where CR-D-2 suppresses it. Example: page 06 stays G4 although `09.3.1-T5` is G3, because no release verdict is shown in G1–G3.

## Canonical fixtures (illustrative; use these exact values on every page)

| Fixture | Value |
|---|---|
| Project | `storefront-web`, code prefix `SHOP`, AI settings version `ais-3` |
| Manual test cases | `SHOP-TC-0042` "Confirm order shows the total / Xác nhận đơn hàng hiển thị tổng tiền" (current version v5; v4 is history) · `SHOP-TC-0043` "Required delivery address / Địa chỉ giao hàng bắt buộc" (v2) · `SHOP-TC-0051` "Apply a discount code / Áp dụng mã giảm giá" (v3) |
| Checklist item | `SHOP-CL-0017` "Show the order total / Hiển thị tổng đơn hàng", priority high |
| Requirement input | text "A customer reviews an order before confirming / Khách hàng xem lại đơn hàng trước khi xác nhận" + `checkout-requirement.pdf` |
| Models (provenance) | `model-small` and `model-large` (placeholder ids, no vendor names) |
| Credits | Period 2026-10: balance **1 240** of **2 000** credits. Estimates: checklist generation 12 · test cases 4 per checklist item · translate 1 per case · script step mapping 2 per unbound step · locate fallback 3 per incident. Usage this period: checklist 312 · test cases 284 · translate 41 · script mapping 96 · locate fallback 27. Credits only, never money. |
| Generated scripts | `gs-0042` for `SHOP-TC-0042`: `active`, 3/3 green on build `b-318`, PR #214 merged · `gs-0043` for `SHOP-TC-0043`: `draft`, 2 unbound steps, PR not opened · `gs-0051` for `SHOP-TC-0051`: `flaky` (green on retry in 1 of 3), PR #219 open |
| Testability score | `storefront-web` 72 / 100 (illustrative) |
| Runs | `run-1842` from CI (GitHub Actions) · `run-1843` local (`evidgate run`, laptop of Linh) |
| Incidents | `inc-91` `product_defect` (HTTP 500 on Place order): no repair · `inc-92` `test_drift` → locator drift: candidate `cand-73` · `inc-93` `test_drift` → UI change: test case review proposal `ucp-12` for `SHOP-TC-0051` |

## Do not

- Do not invent endpoints, prices, or credit amounts beyond the illustrative fixture table above. Plan 13 Q1 (model pair) and the Q2 credit amounts are still `[OPEN]`.
- Do not show a release verdict on G1–G3 screens.
- Do not show a mobile UI: mobile is a G4 decision after the spike.
