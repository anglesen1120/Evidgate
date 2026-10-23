# CR-D-2 — Direction (a): requirements → test cases → scripts → self-learning repair

> Status: **owner decision, 2026-10-23 (chat).** The owner chose direction (a) and accepted the risk plan in §6. The owner accepted the recommendation to keep A5 (customer-operated execution) for phases G1–G3 when asking for this note. On the same day the owner asked for a separate master plan ([`plans/13-product-phases.md`](../../plans/13-product-phases.md)) plus the rewiring of the existing plans, and for a mobile spike in G2 with adoption of its techniques on web; the owner then chose mobile-mcp / mobilewright over Google ARTEMIS for iOS + Android coverage (§7). The edits listed in §8 are made in the same pull request as this file, except row 9.
> Legend: ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Every threshold below is a starting value for the owner to tune, not a measured fact.

## 1. Decision

Evidgate changes from an evidence and release-gate platform with healing as an add-on to a **test authoring and maintenance product that learns each project**:

1. A QA user turns requirements into a reviewed checklist and test cases.
2. Evidgate turns approved test cases into Playwright scripts bound to real UI elements.
3. Every run teaches Evidgate the project's UI; when an element or its id changes, Evidgate classifies the failure and proposes a repair, never hiding a product bug.

The evidence store, tenancy, identity and policy work already built stay as the foundation. What changes is the order of delivery and the product on top.

### 1.1 Why (evidence)

| Fact | Source |
|---|---|
| The owner's product goal: start from requirements and test cases written by QA, and keep the automated tests matching the application as its UI changes | Owner, chat 2026-10-23 `[DECISION]` |
| Playwright ships free Planner, Generator and Healer agents; the Healer may mark a test skipped when it judges the feature broken | ✅ [playwright.dev/docs/test-agents](https://playwright.dev/docs/test-agents), read 2026-10-23 |
| Healing in the old plan was shadow-only in V1 and PR-based in V2 (00 §4 M4), behind about 500 raw V1 person-days of platform work; `src/Modules/Evidgate.Modules.Healing` had no source files on `develop` `170becf` | ✅ `plans/00-overview.md` §4, §14; repository tree, 2026-10-23 |

`[INFERENCE]` Text-only test case generation is a commodity (general chat models and the Playwright Planner do it); the defensible value is execution plus per-project memory plus a repair loop that cannot hide bugs.

## 2. Product loop

1. **Project setup:** description, test environment URL, languages, and AI settings: writing style, coverage strategy, four priority criteria, exclusion rules, step detail level, each with templates.
2. **Author:** requirement text, documents or UI images → AI checklist → batched review (comment, merge, delete, then one "update") → test cases. Every AI update creates a version and a timeline entry.
3. **Script:** an approved test case becomes a Playwright script. Each step is bound to an element found on the live page. Scripts reach the repository only as a pull request (A2 unchanged).
4. **Run:** in customer CI or with `evidgate run` on a developer machine (A5 kept, §4). Results, traces and redacted DOM / accessibility snapshots are ingested.
5. **Learn:** every green run records an **element profile** per bound step: role, accessible name, test id, id, text, neighbours, position.
6. **Classify a failure** against the last green profile into the plan 08.2.2 label set (`product_defect`, `test_drift`, `environment`, `data_issue`, `unknown`; 00 §8.7). A human label overrides a rule or model label. The repair-relevant outcomes are:
   - `test_drift`, same element with a changed id / attributes (locator drift) → propose a new locator and the matching test case edit;
   - `test_drift`, element missing or its meaning changed (UI change) → propose a test case review, no script repair;
   - `product_defect`, element present and the assertion fails → report it, never repair;
   - `environment`, `data_issue` and `unknown` → never produce a repair proposal.
7. **Feedback:** accepted, rejected and reverted proposals re-rank later proposals for that project only. The ranker never authorises (08.7.1).

## 3. What stays, changes, or is deferred

| Area | State on `develop` (`STATUS.md` §2) | Direction (a) |
|---|---|---|
| Tenancy, RLS, permissions, audit (01, 02) | Partly built | Keep |
| Test identity, revisions, suites (03.1, 03.2) | Partly built (03.1.1, 03.1.3, 03.2.1) | Keep; add feature → category → checklist → manual test case with versions |
| CLI, `adapter-playwright`, ingestion (04) | Partly built | Keep; it is the data path of loop step 4 |
| Artifacts: traces, screenshots, DOM (06) | Partly built | Keep; required by loop steps 5–6 |
| Safe-healing rules (`DECISIONS.md` §5) | Locked text | Keep unchanged; they are the selling point |
| Healing: incidents, triage, candidates, validation (08) | Plan only | Move forward; becomes the product core |
| AI drafts (03.7, 3.5 person-days, V2) | Plan only | Grow into the authoring module (loop steps 2–3) |
| Policy and release decision (07) | Partly built | Keep the code; reporting only, not the lead feature |
| Agent mode, mobile / Appium, heavy operations and compliance (05 V2, 10, 11) | Plan | Defer, except the data-protection items needed before real customer data (DPA, transfer assessment, deletion, redaction; §6.6) and the G1 dependencies named in §8 row 4 |
| Web UI (09) | Shell only | Redesign around authoring screens and the repair review screen |

## 4. Locked-rule changes

| Rule | Before | After |
|---|---|---|
| `DECISIONS.md` §1 | Evidence + release gate; healing as an add-on | Test authoring and self-learning maintenance on an evidence foundation (§1 above) `[DECISION]` |
| A4 (timing only) | Healing in shadow / proposal mode, validated before a human applies it; delivery plan: shadow in V1, PR in V2 (00 §4 M4) | Same rule; proposals and approved pull requests are offered from G3, after the per-project shadow window (§6.1) `[DECISION]` |
| §5.7 | Runtime auto-apply off in V1 and V2; V3 may reuse approved mappings | Unchanged in substance: **runtime auto-apply stays off in G1–G3**; reuse of approved mappings is considered at G4 at the earliest, under O6 `[DECISION]` |
| A5 | Customer-operated execution, no hosted runner in V1–V2 | **Kept for G1–G3** (customer CI or local `evidgate run`); a hosted runner is reconsidered at G4 only on paying demand `[DECISION]` |
| A2 | Git holds approved test code | Unchanged |
| §5 rules 1–6, 8, 9 | Mechanics only, forbidden patterns, original failure kept, semantic checks, sensitive cases reviewed, hash-bound approval, rule of three, kill switches | Unchanged |

Mapping `[INFERENCE]`: G1–G2 replace the V1 build order, G3 takes the place of the V2 healing milestone (M4), G4 covers the remaining V2 and V3 scope. Edit 2 (§8) wrote the exact mapping into 00 §4.

## 5. Phases and exit gates

Phases are sequence plus exit gates, not dates (same rule as O8). A phase does not start before the previous gate passes.

| Phase | Scope | Exit gate (go) | Stop / pivot signal |
|---|---|---|---|
| G1 Author | Project + AI settings, requirement → checklist → test cases, batched updates, versions, timeline, export (csv / xlsx / json), vi + en; model gateway, credits and redaction (§6.2, §6.6) | At least 5 external teams use it for at least 3 weeks and export or use the test cases | Fewer than 3 teams return after week 1 |
| G2 Run | Test case → Playwright script bound to live elements with a pre-action target check (§7); CI / local run; ingest results and redacted snapshots; element profiles; mobile spike with mobile-mcp / mobilewright (13.7.1, §7) | At least 70 % of generated scripts pass 3 of 3 runs on the same build without manual edits; at least 3 teams agree to pay | Most scripts need manual repair |
| G3 Learn and repair | Failure classification, locator proposals with an accessibility + OCR + visual locate fallback (13.7.2, §7), validation, review screen, pull requests, per-project feedback ranking | Shadow window: **0 adjudicated false repairs** on the 08.8.1 corpus over at least 300 proposals, reported with the rule-of-three caveat (08.8.2-T2: true rate may still be up to ~1 %); the acceptance rate is reported but is not the gate; the severity-weighted threshold stays O6 `[OPEN]` | Any false repair whose cause is not understood and fixed |
| G4 Expand | Requirement-change impact analysis, policy / gate, team review, mobile (§7), hosted runner if paid for | Paying demand per item | — |

## 6. Risk register

### 6.1 A wrong repair hides a real bug

Five layers, all required before a proposal reaches a reviewer:

1. **Rules first, model second:** element-profile similarity decides between locator drift and UI change; locator drift needs the same role and accessible name. A model may suggest, never decide (08.2).
2. **Mechanics-only diff:** the AST allowlist rejects any added or removed `expect` or `skip` / `fixme`, `force: true`, catch-and-ignore, or changed expected value; mechanics are limited to locators and (proposal-only) wait conditions (08.4.1, §5.1, §5.2).
3. **Replay proof:** the repaired test passes on the failing build in the sandbox, every original assertion still executes (assertion hash), and the new locator matches exactly one element (08.4.2, 08.4.3).
4. **Negative control:** on a page variant with the target removed or renamed the repaired test must fail; if it passes the locator is too loose and the proposal is rejected (§5.4, 08.4.2).
5. **Human approval** via pull request with the 08.6.1 fields (diff, original failure, context hash, validation results, assertion hash, expiry) plus, as a new requirement for §8 row 4, before / after screenshots and a DOM diff. Payment, authorisation, deletion and security cases are proposal-only and need an approver who is not the author (§5.5, 08.6.1-T2).

Plus: a **per-project shadow window** of 2–4 weeks (proposals labelled, measured, not offered as pull requests) and the **kill switches** at system, tenant and project level, with healing becoming `unavailable` when the model or store fails (08.6.3, §5.9). Rule of three (§5.8): 0 failures in 300 samples still allows a true rate near 1 %, so the product never claims "100 % safe".

### 6.2 AI cost exceeds revenue

- Locator repair by profile similarity needs no model call in the common case; call a model only in the ambiguous band.
- Send the accessibility subtree around the element, not the full DOM or screenshots.
- Batch review actions into one update; cache by hash of (requirement, project settings).
- Two model sizes: small for classification and checklists, large for test case and script generation.
- Credits per AI action and a hard per-tenant / per-project budget through the gateway (08.5.1, 08.5.5); record the real cost per action.
- `[DECISION]` (owner, 2026-10-23) Price credits at no less than 3× the measured model cost; review monthly. G1 is a free trial (operator-granted credits) that measures the real cost per action; the credit amounts are set before the G2 offer (plan 12 §Price test, 12.4.1-T4).

### 6.3 Generated scripts are unstable

- Generate against the live app: the customer supplies the environment URL and a seed test (login, data); locators are written only after the element is found (same approach as the Playwright Generator).
- Locator order: `getByRole` / `getByLabel` / `getByTestId`, then text, then CSS; absolute XPath and `nth()` are forbidden.
- A new script becomes `active` only after 3 of 3 green runs on one build; otherwise it stays `draft`. Green-on-retry is `flaky`, never a repair.
- A testability score per project (share of bound elements with a test id or a clear accessible name) gives customers a concrete reason to add test ids.
- G2 scope: web, Playwright, Chromium only.

### 6.4 Free tools compete (Playwright agents, chat models)

- Use them, do not compete with them: the generator may call the Playwright agents internally.
- Differentiate on what they lack: per-project element memory across runs; one chain from test case text to script to element to result that a QA reader understands; governed repair that never skips and is audited.
- First market: Vietnamese QA teams, vi + en UI, local payment.

### 6.5 One developer, growing scope

- Phase gates (§5) are hard stops.
- One engine (Playwright), one CI (GitHub Actions), one model provider behind the gateway (08.5.1, 08.5.5).
- G1 reuses Tenancy, Identity and Catalog; G2 reuses the CLI, `adapter-playwright` and Evidence.
- Deferred work (§3) stays frozen until G4, except the data-protection items named in §3.

### 6.6 Personal data in requirements, DOM and screenshots (PDPL)

- Requirement text, documents and UI images sent to a model in G1 are covered by `DECISIONS.md` §7: opt-in per tenant, redaction at source, and the cross-border transfer assessment before any external team uses G1.
- FR-08-2 masks input values and tokens before storage. New requirements, to be added to 08.1.2-T2 and plan 11 in the follow-up: CLI-side redaction before upload, accessibility tree by default, screenshots opt-in.
- Short retention for DOM and screenshots; element profiles store attributes, not user content.
- A DPA is signed before a pilot sends real data (`DECISIONS.md` §7).

### 6.7 Nobody pays

- End of G1: 5–10 teams on a free trial; measure features created, test cases exported, weekly return.
- End of G2: a real price offer; at least 3 teams agree to pay before G3 starts.

## 7. Mobile: tool assessment (2026-10-23)

Owner decision `[DECISION]` (2026-10-23): the mobile candidate is **mobile-mcp with mobilewright** (Mobile Next), not Google ARTEMIS, because it covers both iOS and Android. A G2 spike tests it; the web techniques below are adopted now.

### 7.1 mobile-mcp and mobilewright

| Fact | Source |
|---|---|
| MCP server for iOS and Android: simulators, emulators and real devices; one tool set for both platforms (list elements, tap by element ref or coordinates, type, swipe, screenshots, app install / launch, device logs, crash reports) | ✅ [github.com/mobile-next/mobile-mcp](https://github.com/mobile-next/mobile-mcp) README |
| Accessibility-first: drives apps from the native accessibility tree without a vision model and falls back to screenshot + coordinates only when needed | ✅ same README, "Main Features" |
| Needs Xcode command line tools (iOS), Android platform tools (`adb`), Node.js 20+; an optional paid device cloud (Mobile Next Cloud) exists | ✅ same README, "Prerequisites", "Platform Support" |
| Apache-2.0, TypeScript, npm `@mobilenext/mobile-mcp` 1.0.12 (released 2026-10-18), created 2025-03-28, 9.9k stars | ✅ GitHub API and npm registry, read 2026-10-23 |
| Depends on `mobilewright` 0.0.67, "a mobile device automation framework inspired by Playwright" (Apache-2.0, TypeScript, created 2026-03-24, pre-1.0) | ✅ npm registry `mobilewright`; [github.com/mobile-next/mobilewright](https://github.com/mobile-next/mobilewright) |
| `mobilewright`'s driver `@mobilewright/driver-mobilecli` depends on `mobilecli` 1.0.21, licensed **FSL-1.1-ALv2** (Functional Source License; converts to Apache-2.0 later; restricts competing use) | ✅ npm registry `mobilecli@1.0.21`; [github.com/mobile-next/mobilecli](https://github.com/mobile-next/mobilecli) LICENSE |
| A model-driven run can be saved as a deterministic mobilewright script | ⚠️ not documented; the spike checks it |

Fit `[INFERENCE]`:

- **Better fit than ARTEMIS:** TypeScript and npm match the customer-side stack (`DECISIONS.md` §4.3) and ADR-0002 / ADR-0005, so no language exception or new package ecosystem is needed; one API covers iOS and Android; the accessibility-first approach matches element profiles (13.6); mobilewright gives a Playwright-style script that can be the deterministic regression runner, the same split as web (model explores, script runs).
- **Mobile product use: G4 only.** Mobile stays deferred (§3); G1–G3 are web.
- **Integration shape at G4:** an `adapter-mobile` producer on the customer's device host (A5 holds) that runs mobilewright scripts and uploads results, screenshots and the accessibility hierarchy as evidence; mobile-mcp drives a model only to explore an app and draft test cases (loop step 2).
- **Constraints before adoption:**
  - `mobilecli` is FSL-1.1-ALv2, which `scripts/licence-policy.json` does not allow; shipping it needs an ADR exception after a licence review of "competing use", or a driver without it (`@mobilewright/driver-mobilenext` is Apache-2.0 but targets the paid device cloud ⚠️);
  - screenshots sent to a cloud model fall under `DECISIONS.md` §7;
  - iOS needs a macOS host with Xcode; real iOS devices need signing on the customer side ⚠️;
  - mobilewright is pre-1.0, so pin exact versions.

### 7.2 Google ARTEMIS (assessed first, replaced)

Android-only natural-language agent (iOS is a roadmap item), Python 3.12+ with `uv`, Apache-2.0, created 2026-08-13 with no tagged release, a model call per step (~3–5 s Flash, ~15–40 s Pro), and no documented export to a deterministic script ✅ [github.com/google/artemis](https://github.com/google/artemis) README, GitHub API read 2026-10-23. It would also have needed a §4.3 language exception and an ADR for a git / PyPI dependency. Replaced by §7.1 for platform coverage and stack fit.

### 7.3 Spike and techniques

- **Spike in G2 (13.7.1, 2.0 d raw):** pin versions; on one Android emulator and one iOS simulator, run 10 real tasks twice through mobile-mcp with a model, then replay them as mobilewright scripts without a model; record pass rate, time, model cost and repeatability; write a go / no-go memo covering `adapter-mobile` shape, the `mobilecli` licence and data transfer. The spike uses our own sample apps with no customer data and commits nothing to G4.
- **Techniques adopted for web now** (our own code, no mobile dependency):
  - **pre-action target check** in `evidgate generate` (13.5.1-T1, G2): before each bound action, check that the element exists, is unique, visible and actionable, and record that; otherwise the step stays unbound and the script stays `draft`;
  - **accessibility-first locating with an OCR / visual fallback** (13.7.2, G3): used only when element-profile similarity is ambiguous. It may propose a candidate, which must still pass all 08.4 validation; it never decides the drift class. Calls go through the gateway with a per-incident budget, and screenshots stay opt-in (§6.6).

## 8. Follow-up edits (made in the same pull request, except row 9)

Plan 13 holds the new epics; rows 3–8 are wired to it.

| # | File | Edit |
|---|---|---|
| 1 | `DECISIONS.md` §1, §3 A4, §5.7 | Rewrite per §1 and §4 of this file; remove the §1 "Direction change (CR-D-2)" pointer |
| 2 | `plans/00-overview.md` §4, §14 | Replace M0–M5 with G1–G4, their gates and the V/M mapping; recompute the roll-up with `scripts/plan_graph.py`; log in §15 |
| 3 | `plans/03-catalog-authoring.md` | 03.7.1-T1/T2 superseded by 13.3.1; authoring epics live in plan 13 (13.1 model, 13.2 AI settings, 13.3 generation, 13.4 screens) |
| 4 | `plans/08-healing-learning.md` | Element profiles (13.6); locator-drift vs UI-change rules inside 08.2.2 `test_drift` (13.8); 08.5.1, 08.5.2, 08.5.3 and 08.5.5 move to G1 (authoring needs them) with 08.5.1-T3, 08.5.2-T2 and 08.5.5-T4 left in G3; 08.5.1-T1 re-parented to 12.3.1-T1 only (was 08.4.3-T3, 12.3.1-T2); every other dependency is pulled into the earliest phase that needs it (13 §4.4), so no task depends on a later phase; 08.2–08.4, 08.6–08.8 at G3; per-project shadow window; screenshots and DOM diff in the 08.6.1 review; G3 gate on the 08.8.1 corpus |
| 5 | `plans/09-web-ui.md` | Authoring screens (tree, table, batched changes, timeline, versions) and the repair review screen; design via `design-ui` |
| 6 | `plans/11-security-compliance.md` and 08.1.2-T2 | CLI-side redaction, accessibility tree by default, screenshots opt-in, transfer assessment before G1 (§6.6) |
| 7 | `plans/12-pilot-commercial.md` | G1 / G2 cohort, price test, credit pricing rule |
| 8 | `plans/04-ingestion-adapters.md` | Script generation and `evidgate run` live in 13.5; `adapter-mobile` (mobilewright / mobile-mcp) at G4 after the 13.7.1 go / no-go memo |
| 9 | `docs/adr/` and `scripts/licence-policy.json` | When G4 starts and the 13.7.1 memo says go: ADR on the `mobilecli` FSL-1.1-ALv2 licence (exception or alternative driver) before any mobile producer ships |
