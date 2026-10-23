# CR-D-2 — Direction (a): requirements → test cases → scripts → self-learning repair

> Status: **owner decision, 2026-10-23 (chat).** The owner chose direction (a) and accepted the risk plan in §6. The owner accepted the recommendation to keep A5 (customer-operated execution) for phases G1–G3 when asking for this note. This file records the decision and the plan; the edits it implies to `DECISIONS.md` §1/§3/§4.3/§5 and to `plans/00`, `03`, `04`, `08`, `09`, `11`, `12` are listed in §8 and are made by follow-up docs PRs. Until those land, this file wins over the old text where they conflict (`DECISIONS.md` §10 row 2026-10-23).
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

Mapping `[INFERENCE]`: G1–G2 replace the V1 build order, G3 takes the place of the V2 healing milestone (M4), G4 covers the remaining V2 and V3 scope. Follow-up edit 2 (§8) writes the exact mapping into 00 §4.

## 5. Phases and exit gates

Phases are sequence plus exit gates, not dates (same rule as O8). A phase does not start before the previous gate passes.

| Phase | Scope | Exit gate (go) | Stop / pivot signal |
|---|---|---|---|
| G1 Author | Project + AI settings, requirement → checklist → test cases, batched updates, versions, timeline, export (csv / xlsx / json), vi + en; model gateway, credits and redaction (§6.2, §6.6) | At least 5 external teams use it for at least 3 weeks and export or use the test cases | Fewer than 3 teams return after week 1 |
| G2 Run | Test case → Playwright script bound to live elements; CI / local run; ingest results and redacted snapshots; element profiles | At least 70 % of generated scripts pass 3 of 3 runs on the same build without manual edits; at least 3 teams agree to pay | Most scripts need manual repair |
| G3 Learn and repair | Failure classification, locator proposals, validation, review screen, pull requests, per-project feedback ranking | Shadow window: **0 adjudicated false repairs** on the 08.8.1 corpus over at least 300 proposals, reported with the rule-of-three caveat (08.8.2-T2: true rate may still be up to ~1 %); the acceptance rate is reported but is not the gate; the severity-weighted threshold stays O6 `[OPEN]` | Any false repair whose cause is not understood and fixed |
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
- `[INFERENCE]` Price credits at no less than 3× the measured model cost; review monthly.

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

## 7. Mobile: Google ARTEMIS (assessment, 2026-10-23)

| Fact | Source |
|---|---|
| Natural-language Android automation; MCP server, CLI, Python SDK (`artemis-client`), web console; Flash profile ~3–5 s per step, Pro ~15–40 s per step | ✅ [github.com/google/artemis](https://github.com/google/artemis) README |
| Android only; iOS is an unchecked roadmap item | ✅ same README, "Roadmap" |
| Apache-2.0; Python 3.12+ with `uv`; needs ADB with a device or emulator on the host; installs an accessibility helper app on the device; uses cloud multimodal models (Gemini, Claude, GPT-4o, Qwen-VL) | ✅ same README |
| Created 2026-08-13, no tagged release yet (install from git, pin by commit), 13.8k stars | ✅ GitHub API, read 2026-10-23 |
| 99 %+ on AndroidWorld | ⚠️ self-reported in the README |
| Exports a deterministic, replayable script from a run | ⚠️ not documented; "replay" in the README is video replay |

Fit `[INFERENCE]`:

- **Usable, at G4 only.** Mobile stays deferred (§3); G1–G3 are web.
- **Role: explorer, not the regression runner.** A model call per step makes runs slow, costly and non-deterministic, which conflicts with immutable evidence and §6.3 stability. Use it to explore an app and draft mobile test cases (loop step 2), and as a fallback locator when a deterministic mobile script fails.
- **Integration shape:** an `adapter-artemis` producer that runs on the customer's device host (A5 holds), calls the Python SDK, and uploads steps, screenshots and the accessibility hierarchy as evidence; element profiles reuse the same model as web.
- **Constraints before adoption:**
  - screenshots go to a cloud model, so `DECISIONS.md` §7 applies;
  - a Python producer needs an exception to `DECISIONS.md` §4.3 (customer side is Node + TypeScript);
  - ADR-0002 and ADR-0005 cover only NuGet and npm, so a git / PyPI dependency needs an ADR extending them;
  - a spike runs 10 real tasks twice and measures pass rate, time and model cost, and checks whether a trajectory can become a deterministic Appium / UIAutomator script.

## 8. Follow-up edits (separate docs PRs)

| # | File | Edit |
|---|---|---|
| 1 | `DECISIONS.md` §1, §3 A4, §5.7 | Rewrite per §1 and §4 of this file; remove the §1 "Direction change (CR-D-2)" pointer |
| 2 | `plans/00-overview.md` §4, §14 | Replace M0–M5 with G1–G4, their gates and the V/M mapping; recompute the roll-up with `scripts/plan_graph.py`; log in §15 |
| 3 | `plans/03-catalog-authoring.md` | New epics: feature / category / checklist / manual test case with versions and timeline; project AI settings with templates; export; requirement → checklist → test case (grows 03.7) |
| 4 | `plans/08-healing-learning.md` | Element profiles; locator-drift vs UI-change rules inside 08.2.2 `test_drift`; move 08.5.1, 08.5.3 and 08.5.5 to G1 (authoring needs them): re-parent 08.5.1-T1 off 08.4.3-T3 and 08.5.5-T4 off 08.8.1-T1, and pull 08.6.3-T1 (kill switch), 10.7.1-T4 and the 12.3.1 usage ledger into G1 so no task depends on a later phase (00 §14); move 08.2–08.4 and the rest of 08.6 to G3; per-project shadow window; screenshots and DOM diff in the 08.6.1 review; G3 gate on the 08.8.1 corpus |
| 5 | `plans/09-web-ui.md` | Authoring screens (tree, table, batched changes, timeline, versions) and the repair review screen; design via `design-ui` |
| 6 | `plans/11-security-compliance.md` and 08.1.2-T2 | CLI-side redaction, accessibility tree by default, screenshots opt-in, transfer assessment before G1 (§6.6) |
| 7 | `plans/12-pilot-commercial.md` | G1 / G2 cohort, price test, credit pricing rule |
| 8 | `plans/04-ingestion-adapters.md` | Script generation and `evidgate run`; `adapter-artemis` row at G4 |
| 9 | `docs/adr/` and `DECISIONS.md` §4.3 | When G4 starts: ADR extending ADR-0002 / ADR-0005 beyond NuGet and npm, plus a §4.3 language exception for a Python producer |
