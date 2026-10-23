# 12 - Pilot and Commercial (G1–G4)

> Contracts: `00-overview.md` (§2 roles, §4 milestones, §6 estimate units, §9.1 cursor/Idempotency, §11 NFRs). Facts: `../DECISIONS.md` §2 (name search), §6 F7/F8 (PDPL), §8 (O1, O5, O7), §9 R4/R5/R10. Source: design §9 (usage ledger, commercial suggestion, value measurement), §11 (decisions to close), §12 (milestones).
> Level: high-level plan; each story gets a detailed plan in `plans/features/` (00 §12) before build.
> Legend: ✅ verified (link opened for this plan, 2026-09-24) · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`.
> **Phases (CR-D-2):** the Milestone column holds the CR-D-2 phase (G1–G4) from [13 §4.4](13-product-phases.md); M0–M5 in this plan's prose refers to the pre-CR-D-2 sequence. Phase totals (raw): G1 13 · G2 9 · G3 4.5 · G4 5 person-days.

## 1. Purpose and scope

**Goal:** convert "the product is interesting" into evidence that it pays: real pilot teams, measured before/after value, a metered cost base, and a written go/no-go for V2.

**In scope**

- V1 (M0-M3): discovery interviews and artifact intake, CI/engine choice (O1), pilot selection and agreement (with DPA), baseline and after metrics, usage ledger and quota enforcement, cost per tenant, manual invoicing, pricing hypotheses as `[OPEN]` O5 closed by the O5 decision pack (12.4.1-T4) on the G2 price test, trademark search and filing (O7), M3 exit value review.
- V2 (M4): customer reference evidence, subscription/billing boundary decision, WIPO/USPTO trademark follow-through.
- V3 (M5+): automated billing, dunning, self-serve plans, enterprise contracts.

**Out of scope:** published price list before O5 is closed with pilot data, revenue forecasts, a payment gateway, automated tax handling, and any promise of hosted runners or device farms (00 §1.3).

**Rule for this plan:** it contains **no invented prices**. Every price field is `[OPEN] O5` until the cost and willingness-to-pay data from the G2 price test exists.

**CR-D-2 pilot cohorts.** The pilot runs as two cohorts, one per phase gate ([13 §4.3](13-product-phases.md), CR-D-2 §5 and §6.7), instead of one V1 cohort:

- **G1 authoring cohort (5–10 teams, free trial).** Teams start from requirements: they use requirement → checklist → manual test case authoring, batched review, versions and export for at least 3 weeks. The exit signal is at least 5 teams for 3 weeks with exports or real use of the test cases; the stop signal is fewer than 3 teams returning after week 1 (`13.9.1-T1`). A free trial is the only way to reach that count — it measures adoption and return, not willingness to pay, so D1 applies from G2 (below).
- **G2 run-and-remember cohort (at least 3 teams).** The same teams, plus new ones, turn approved test cases into Playwright scripts bound to live elements, run them in their own CI (A5) and let Evidgate store element profiles. Exit signal: at least 70 % of generated scripts pass 3 of 3 runs on one build without manual edits, and at least 3 teams agree to pay before G3 starts (`13.9.1-T2`).
- Discovery (12.1.1), the agreement pack (12.2.1) and the frozen metric definitions (12.2.1-T2) serve both cohorts; baseline and after windows are measured per cohort, so a team that stays from G1 into G2 keeps one continuous baseline, and a team joining at G2 gets its own.

**Price test (CR-D-2 §6.7).** No price is published before the test, and the test happens in a fixed order: G1 measures free-trial use only — features created, test cases exported, weekly return — and is explicitly **not** a price signal; the real offer is made at the end of G2, where at least 3 teams must agree to pay before G3 starts. O5 (12.4.1-T4) closes on that evidence. The AI credit rate card is **not** decided in this plan: credits per AI action, the per-tenant / per-project budget and the "no less than 3× the measured model cost, reviewed monthly" rule are owned by `08.5.5` (CR-D-2 §6.2; rule decided by the owner 2026-10-23); this plan only meters the `ai_credit` usage-ledger rows that `08.5.5-T1` writes under the `08.5.5-T2` billing key `ai:<call_id>`, additively to `ai_tokens` — the metric-list rename stays `[OPEN]` (OI-08-06, decided at the 12.3.1 review).

## 2. Requirements

- **FR-12-1** Discovery: at least 5 structured interviews with QA leads/engineers from teams that already run automation, plus a documented intake of their real reports (Playwright JSON/HTML, JUnit XML) and failure samples, with provenance and written permission to reuse. Output feeds O1 (CI/engine) and the scope decisions in 00 §1.3.
- **FR-12-2** Pilot selection criteria (all must hold): already has automated tests in a supported engine; at least one web suite that runs on pull requests; releases at least twice a month; a named owner who can sign a DPA and a pilot agreement; willing to share before-metrics; no unresolved data-residency blocker; and willing to pay a pilot fee at G2. Target: 5–10 teams in the G1 free authoring cohort and at least 3 paying teams in the G2 run cohort (CR-D-2 §5, §6.7). **CR-D-2 cohort split:** the G1 authoring cohort does not need an existing automation suite — a team that starts from requirements and has a named owner, a signable DPA and a realistic path to a paid G2 pilot qualifies, and the first two criteria (a supported engine, a web suite on pull requests) apply from the G2 run cohort.
- **FR-12-3** A pilot agreement is signed before any upload, containing: scope and duration, price and payment terms, the DPA (plan 11) as an annex, data handling and deletion terms, success metrics, exit criteria, support expectations, and an explicit "not included" list (hosted runners, device farm, platform breadth beyond 00 §1.3).
- **FR-12-4** Baseline is measured **before** the gate is enabled: triage time per failure and test-maintenance hours per sprint, using the method fixed in 12.2.1-T2, for at least two sprints, with raw data retained so the comparison can be audited.
- **FR-12-5** After metrics: triage time, maintenance time, repeated incidents handled correctly (counted against the customer's own labels — only human labels count as ground truth, 00 §8.7), retention (still using it after the pilot window), and contribution margin per tenant.
- **FR-12-6** Guardrail metric: heal rate and missed-bug rate are reviewed together every month. A rising heal rate with rising missed bugs is a **failed** pilot outcome, not a success (source §9: "tỷ lệ healed thành công cao nhưng bỏ sót bug tăng là kết quả không đạt").
- **FR-12-7** Usage ledger: append-only entries, each with a unique `billing_key` per source metering event — `unique (org_id, billing_key)` (T43), quantity, unit, period, the applied price reference and the resulting amount. The `billing_key` is produced by the emitting plan, not by plan 12: 04 `04.8.1-T1` for accepted results (per-batch `batch:<event_batch_id>:<metric>` keys, T50), 06 `06.5.2-T2` for storage/egress usage, 08 `08.5.5-T1` / `08.5.5-T2` for AI credit (`ai_credit`, key `ai:<call_id>`, additive to `ai_tokens` — OI-08-06). Corrections are new adjustment rows referencing the corrected row - never edits to an issued row (source §9).
- **FR-12-8** Metered metrics (each with a defined measurement point and a reconciliation query): results count; artifact GB-month; egress GB; runner minutes (later, only if we ever run tests); device minutes (later); AI tokens (`ai_tokens`) and AI credits (`ai_credit`, written by `08.5.5-T1` as an additive metric — OI-08-06); support hours.
- **FR-12-9** Quota enforcement: per-project and per-tenant limits with warn at 80 % and block at 100 %. A blocked upload is an explicit error (429/`quota_exceeded` style), never a silent drop; ingestion already committed is unaffected.
- **FR-12-10** Pricing hypotheses (not commitments): an onboarding fee plus a subscription per project (and/or per concurrency level) with an included results-and-artifact quota; AI usage priced as an overage once measured. In V1 the customer's CI pays for runner minutes, so we do not bill runner minutes - but our own cloud and support cost still exist. AI credit pricing is not set here: credits per action and their budget are owned by `08.5.5` (CR-D-2 §6.2); this plan meters the `ai_credit` ledger rows that `08.5.5-T1` writes, additively to `ai_tokens` (OI-08-06).
- **FR-12-11** Invoicing in the pilot is manual: generated from a documented ledger query, checked by two people, issued with a monthly statement the customer can reconcile. No invoices are generated by hand-editing numbers.
- **FR-12-12** Trademark: search "Evidgate" in classes 9 and 42 (Vietnam first), then file; WIPO Madrid designation and US filing follow. Verified fee facts to use when budgeting: WIPO basic fee 653 CHF (black-and-white mark) or 903 CHF (colour), plus a complementary fee of 100 CHF per designated Madrid member and a supplementary fee of 100 CHF per class beyond three ✅ ([wipo.int Madrid fees](https://www.wipo.int/en/web/madrid-system/how_to/file/fees), read 2026-09-24); USPTO base application filing fee $350 per class for a Section 1/44 application, plus $200 per class if the free-form text box is used and $100 per class for insufficient information, with a $150 per class statement of use for an intent-to-use filing ✅ ([uspto.gov trademark fee information](https://www.uspto.gov/trademarks/trademark-fee-information), page last updated 2025-01-18, read 2026-09-24). Vietnam NOIP official fees and attorney costs are ⚠️ unverified until quoting.
- **FR-12-13** M3 exit value review (the G2 gate under CR-D-2, [13 §4.3](13-product-phases.md)): a written review with the measured metrics, cost per tenant, churn/stop reasons, and a V2 go/no-go with named conditions and an owner.
- **FR-12-14** Any discovery or pilot material reused as a fixture or in marketing is redacted and covered by written permission; customer names appear publicly only with written consent.
- **NFR-12-1** Ledger arithmetic is stable: re-running the roll-up for a closed period reproduces the same totals; every invoice total reconciles to ledger rows plus adjustments.

**Acceptance criteria (testable)**

1. For each pilot, a baseline record exists from before enablement with the raw measurements, and the after-report uses the same definitions.
2. A duplicate or retried metering event with the same `billing_key` produces exactly one ledger row.
3. A correction to an issued period appears as an adjustment row; the original row is unchanged and both are visible in the statement.
4. Reaching the results quota blocks further uploads with an explicit error while a run already in progress completes.
5. The invoice for a period equals the sum of that period's ledger rows and adjustments, verified by a second person's check.
6. The M3 exit review document contains all metrics in FR-12-5/FR-12-6 and a go/no-go with conditions.

## 3. Key decisions

| # | Decision | Rationale | Rejected |
|---|---|---|---|
| D1 | The **paid** pilot is the G2 run cohort: at least 3 teams agree to pay before G3 starts; the G1 authoring cohort is a free trial and is explicitly not a price signal (CR-D-2 §6.7) | A free trial measures adoption and return but cannot measure willingness to pay, which is what a price offer must test (source §11: a team ready to provide data **and try paying**) | Revenue-share pilots; charging the G1 authoring cohort, which would keep it below the 5–10 teams the G1 gate needs |
| D2 | Measurement definitions are frozen before enablement (12.2.1-T2) | A before/after comparison with a moved definition is worthless | "We'll figure out the metric afterwards" |
| D3 | The ledger is append-only with adjustments, and is the only billing source | Auditable billing; accidental overwrites cannot be detected later otherwise | Editing amounts in place |
| D4 | No published prices until O5 is closed with pilot data (source §9: "chưa có dữ liệu để chốt giá bán") | Any number now is a guess; a wrong public price is hard to withdraw | Inventing an indicative price list |
| D5 | Heal rate alone is never a success metric; it is always paired with missed bugs | A healed test that hides a bug is the worst product outcome | Reporting heal rate as a headline KPI |
| D6 | Quota block, never silent drop | Silent data loss destroys trust in evidence, which is the product | Throttling or discarding results |
| D7 | Manual invoicing in the pilot, with a two-person check | Cheapest correct process while volume is tiny | Building billing automation in V1 |
| D8 | Trademark search and VN filing happen in M1-M2, before public launch | DECISIONS §2 lists the search as not done; a late collision could force a rename after launch | Filing only at launch |
| D9 | Support hours are metered from day one | Support is the most likely hidden cost in a pilot and must be visible in the margin | Treating support as free |
| D10 | The price test is the G2 offer and O5 (12.4.1-T4) closes on it; AI credit pricing stays with `08.5.5` (CR-D-2 §6.2, §6.7) | One price question, one evidence point: the G1 free trial measures the real cost per AI action; the G2 cohort is the first with generated scripts and run results, so the G2 offer is the one price test | Publishing an indicative price after G1; setting credit prices in the commercial plan |

## 4. Design

### 4.1 Discovery outputs (M0)

| Output | Content | Feeds |
|---|---|---|
| Interview notes + pain map | current process, tooling, where triage and maintenance time goes, gate process, compliance constraints | O1, 00 §1.3 scope, 09 screens |
| Artifact intake | real reports/failure samples, provenance, permission, redaction state | 04 fixtures, 08 evaluation set |
| CI/engine decision (O1) | GitHub Actions + Playwright TypeScript + one JUnit producer, confirmed with named teams | 01/04/05 planning |
| Pilot pipeline | 8-10 screened teams, ranked, with ≥2 letters of intent | 12.2 |

### 4.2 Pilot pipeline

screen against FR-12-2 → letter of intent → agreement + DPA signed → onboarding using the product's own wizard → baseline captured (≥2 sprints) → collection enabled → instrumented pilot with weekly review → mid-point health check → value review at M3 exit.

### 4.3 Metric definitions (frozen before enablement)

| Metric | Definition (fixed at 12.2.1-T2) | Source | Cadence | Decision rule |
|---|---|---|---|---|
| Triage time per failure | median time from a failure being reported to a label+owner being set (self-reported where the tool is not ours) | ticket system + product timestamps | weekly | must improve vs baseline, or a written reason |
| Test-maintenance time | hours per sprint spent fixing test mechanics (excludes product fixes) | customer timesheet | per sprint | must improve, or no-healing value claim |
| Repeated incidents handled correctly | repeated failures whose label matched the human label later applied | product labels | monthly | >50 % correct before any V2 PR capability `[DECISION]` |
| Retention | still running the pilot after the window, with ≥N runs/week | product usage | monthly | continues or has a dated reason |
| Contribution margin | revenue − (API/DB share + object GB-month + egress + AI tokens + support hours) | ledger + cost model | monthly | must be non-negative by M3 exit, or a written plan |
| Heal rate | accepted candidates ÷ eligible failures | product | monthly | reported only next to missed bugs |
| Missed bugs | bugs reaching production that a healed/quarantined/flaky-bypassed test should have caught | customer labels | monthly | a rise is a stop signal |

### 4.4 Usage ledger (high level tables)

`usage_ledger_entry`: `id`, `org_id`, `project_id`, `metric` (results | artifact_gb_month | egress_gb | ai_tokens | ai_credit | support_hours | runner_minutes | device_minutes), `quantity`, `unit`, `period` (YYYY-MM), `billing_key` (the dedupe key, unique per source metering event: `unique (org_id, billing_key)` — T43), `occurred_at`, `price_ref`, `amount`, `currency`, `adjustment_of` (nullable), `reason` (required for adjustments), `created_at`. `billing_key` is produced by the emitting plan: ingestion emits per-batch keys `batch:<event_batch_id>:<metric>` (one ledger row per batch per metric, quantity = count — T50/T43), 06 emits per-period storage/egress keys (`06.5.2-T2`), 08 emits `ai_credit` keys `ai:<call_id>` once per gateway call (`08.5.5-T2`). `ai_credit` is **additive**: `08.5.5` extends the check constraint in its own migration while `ai_tokens` stays for token counts; whether the metric list is ever renamed instead is `[OPEN]` (OI-08-06, decided at the 12.3.1 review); per-period sums live in the roll-up table, not as a uniqueness rule (T43). Append-only: no `updated_at`, no deletes; the application role has no `UPDATE`/`DELETE` privilege on this table.

`usage_price`: `id`, `org_id` (nullable — `NULL` rows are the global rate card, non-`NULL` rows are negotiated per-org overrides; price resolution picks the org override first, then the global row — T44), `metric`, `unit_price`, `currency`, `effective_from`, `effective_to`, `included_quota`, `version`, `unique (org_id, id)` (T3). Loading a new price creates a new row; existing ledger rows keep the price they were written with.

`quota_counter` (materialised per org/project/period): `id`, `org_id`, `project_id`, `period` (YYYY-MM), `metric`, `used`, `limit`, `warned_at`, `blocked_at`, `unique (org_id, id)` (T45/T3).

Enforcement flow: metering event → validate + dedupe by `billing_key` (idempotent insert) → counter increment in the same transaction → 80 % warn (notification) / 100 % block (explicit error on new uploads only). Quota changes are audited.

Reconciliation: a documented query per metric per period plus adjustments; the roll-up job is idempotent and reports gaps (a period with no entries while usage is known to have occurred) and duplicates.

### 4.5 Pricing hypotheses (`[OPEN] O5` - no numbers)

| Component | Basis | Number |
|---|---|---|
| Onboarding fee | one-off, covers repo/CI connection, first suite mapping, fixture work | `[OPEN] O5` |
| Subscription per project | monthly, per project under test | `[OPEN] O5` |
| Concurrency add-on | if parallel CI shards require a raised limit | `[OPEN] O5` |
| Included quota | results and artifact GB-month per project | `[OPEN] O5` |
| AI overage | per 1k tokens (healing), measured after the V2 beta | `[OPEN] O5` |
| Support | included hours per month, then hourly | `[OPEN] O5` |
| Hosted runner/device minutes | not offered in V1-V2; customer CI pays its own | n/a |

### 4.6 Trademark plan (O7)

Fee figures below are ✅ from the pages I opened: WIPO Madrid fees ([wipo.int](https://www.wipo.int/en/web/madrid-system/how_to/file/fees), read 2026-09-24) and the USPTO trademark fee page ([uspto.gov](https://www.uspto.gov/trademarks/trademark-fee-information), page last updated 2025-01-18, read 2026-09-24). Vietnam NOIP official fees and attorney costs are ⚠️ unverified (no primary English fee schedule opened).

| Jurisdiction | Classes | Cost basis | Status |
|---|---|---|---|
| Vietnam (NOIP) | 9, 42 | official fee + attorney ⚠️ unverified | search in M1, file in M2 |
| WIPO Madrid (designating VN's partners of interest) | 9, 42 | 653 CHF basic (B/W) + 100 CHF per designated member + 100 CHF per class beyond three ✅ (WIPO) | after the VN search result |
| United States (USPTO) | 9, 42 | $350 per class base ✅; +$200/class free-form text box; +$150/class statement of use if intent-to-use ✅ (USPTO) | decision in M4 |

## 5. Failure modes and edge cases

| Case | Handling | Invariant kept |
|---|---|---|
| Customer refuses to share baseline data | Pilot cannot measure value → offer a self-reported estimate with the method documented, or defer that pilot | Measurement definitions (D2) |
| Metric definition was ambiguous and the numbers move | Baseline is re-cut with the frozen definition and the discrepancy written down | Auditability of the comparison |
| Duplicate/retried metering event | Unique `billing_key` + idempotent insert; duplicates counted and reported | Exactly one ledger row |
| Quota blocks a release-critical run | Block applies to new uploads only; an explicit error names the quota, the period and the raise path; escalation can raise the limit with an audit entry | No silent data loss (D6) |
| Artifact GB-month computed over the wrong retention window | GB-month is computed from actual stored bytes × time, from the retention states in 06; a mismatch with the storage bill is a bug with an owner | Reconcilable metering |
| Invoice dispute | Statement + evidence rows, then an adjustment row with a reason - never an edit | Append-only ledger (D3) |
| Customer asks for deletion mid-pilot | Plan 11 SLA applies; ledger rows for billing are retained as required for accounting, artifacts are deleted with a report | Deletion + accounting both satisfied |
| Pilot customer's data would leave Vietnam | Residency decision (O3) or scope exclusion in writing; no silent transfer | Plan 11 FR-11-16 |
| Heal rate looks great, missed bugs rise | Stop-signal review; healing narrowed or disabled by kill switch | D5 |
| Trademark collision found in the VN search | Rename decision before launch, with the cost of the rename accepted explicitly | O7 |
| No pilot team signs a paying agreement by the G2 exit | The G2 gate fails; re-scope or stop rather than start G3 | 13 §4.3 (G2 gate) |

## 6. Security and privacy

- Discovery and pilot material (reports, failures, screenshots) is handled under the same rules as production artifacts even before a DPA exists: stored in a restricted workspace, redacted before reuse, never uploaded to a third-party tool, and deleted on request.
- The DPA (11.8.1-T1) is signed before the first pilot upload; the privacy notice and data map (11.8.1-T2, 11.8.1-T3) are prerequisites for the pilot agreement pack.
- Personal data found in a shared report during discovery is reported back to the customer as a finding (it is also evidence of why redaction-at-source matters).
- Ledger and invoice data are accessible to `tenant_admin` (their usage) and internally only through the `billing:read` permission held by the `billing_operator` role (00 §2): cross-tenant read of usage ledger and invoice data, no artifact or test data access, and every read audited (12-CR-1).
- Financial and contract records are retained per accounting law rather than by the artifact retention policy; the split is documented in the data map.
- Fines context for the compliance appendix: cross-border transfer violations can reach 5 % of the preceding year's revenue, other personal-data violations up to VND 3 billion, and trading in personal data up to 10× proceeds ✅ (PDPL Art. 8.3/8.4/8.5, [English text](https://english.luatvietnam.info/dan-su/law-on-personal-data-protection-law-no-91-2025-qh15-405135-d1.html) as read in 11).
- Marketing use of customer names, logos and numbers requires written consent; interim results are shared with the customer only.

## 7. Test strategy

| Suite | Covers |
|---|---|
| Ledger arithmetic (property tests) | Idempotent inserts, adjustment chains, period sums, currency and rounding rules; a re-run of the roll-up reproduces totals |
| Quota enforcement (integration, real PostgreSQL) | 80 % warn, 100 % block, in-flight run unaffected, limit raise audited, concurrent uploads do not overshoot past the block |
| Metering correctness | GB-month from actual bytes×time over the retention window; AI tokens from the gateway; support hours from the support log; each metric has a reconciliation query whose result matches the source system |
| Reconciliation | Invoice total = ledger + adjustments for the period; the generation is dry-run twice and must be identical |
| Process | Baseline/after worksheet review per pilot; M3 exit review checklist completeness (FR-12-13) |

**Fixtures:** a synthetic tenant with known usage (N results, X GB stored for a defined time) whose expected ledger and invoice totals are asserted; a "duplicate + retry + late event" stream covering the dedupe paths.

## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| No team signs a paying pilot by the G2 exit | Medium | Severe (whole plan) | Discovery starts week 1 with 8-10 screened teams; letters of intent before G1; the G2 gate fails rather than slips |
| Before/after comparison is not credible | Medium | High | Definitions frozen before enablement, raw data kept, method documented; report limitations honestly |
| Hidden cost (egress, GB-month, support) makes margin negative | Medium | High | All metrics metered from day one (D9), cost per tenant measured per cohort, pricing decided only at the G2 price test |
| Metering bugs destroy invoice trust | Medium | High | Append-only ledger, adjustment path, reconciliation query, two-person invoice check |
| Quota block blocks a customer release | Medium | Medium | Block only new uploads, explicit error, documented raise path, escalation |
| Trademark collision or refusal | Low-Medium | High (rename after launch) | Search first (M1), file VN before public launch, WIPO/US after |
| Support load unbounded in the pilot | Medium | Medium | Support hours metered, severity definitions and response expectation in the agreement |
| Pilot customer churns and takes the reference value with it | Medium | Medium | Value review per pilot, not one aggregate; early mid-point health check |
| Regulatory demand changes (residency) mid-pilot | Low | High | Written scope exclusions; residency decision tracked as O3 |
| V2 built on optimism rather than measured demand | Medium | High | M3 exit review is a hard gate with conditions (FR-12-13) |

## 9. Epics, stories and tasks

### Epic 12.1 - Discovery and pilot selection (M0, V1)

#### Story 12.1.1 Real problems, real reports - acceptance: 5+ interviews recorded, real reports and failure samples collected with permission, and the CI/engine choice (O1) signed off by named teams.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 12.1.1-T1 | Interview guide + 5+ structured interviews with QA leads/engineers (current gate process, triage and maintenance cost, compliance constraints) | L | V1 | G4 | - |
| 12.1.1-T2 | Artifact intake: real reports/failure samples with provenance, permission and redaction state recorded | M | V1 | G4 | - |
| 12.1.1-T3 | Discovery log and pain-to-feature map feeding the scope decisions in 00 §1.3 | S | V1 | G4 | 12.1.1-T1 |
| 12.1.1-T4 | CI/engine decision (O1): GitHub Actions + Playwright TypeScript + one JUnit producer, confirmed with named teams | S | V1 | G4 | 12.1.1-T1, 12.1.1-T2 |
| 12.1.1-T5 | Pilot pipeline: screen 8-10 teams against FR-12-2, rank, secure ≥2 letters of intent | M | V1 | G4 | 12.1.1-T1 |

Epic 12.1 total: 5.0

### Epic 12.2 - Pilot agreement and delivery (M1-M3, V1)

#### Story 12.2.1 A pilot that can be measured and paid for - acceptance: agreement + DPA signed before any upload, baseline captured for two sprints, weekly reviews held, support hours metered.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 12.2.1-T1 | Pilot agreement template (scope, price, DPA annex, data terms, metrics, exit criteria, "not included" list) + counsel review | M | V1 | G1 | 11.8.1-T1 |
| 12.2.1-T2 | Baseline measurement kit: frozen metric definitions, worksheet, and a two-sprint baseline per pilot | M | V1 | G1 | - |
| 12.2.1-T3 | Pilot onboarding run using the product's own onboarding flow; friction log handed to 09 | M | V1 | G1 | 09.2.1-T1 |
| 12.2.1-T4 | Weekly pilot review ritual: usage, failures, labels, feedback log, blocker escalation | M | V1 | G1 | 12.2.1-T3 |
| 12.2.1-T5 | Pilot support process: severity definitions, response expectation, support-hour logging as a metered metric | M | V1 | G1 | 12.3.1-T2 |
| 12.2.1-T6 | Mid-point pilot health check with continue/adjust/stop decision and reasons | S | V1 | G1 | 12.2.1-T4 |

Epic 12.2 total: 5.5

### Epic 12.3 - Usage ledger and quotas (M2-M3, V1)

#### Story 12.3.1 Every billed unit is a row - acceptance: each metered metric produces exactly one ledger row per source with the applied price, and quota enforcement warns then blocks without losing data.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 12.3.1-T1 | Ledger schema + append-only privileges + adjustment model + migrations | L | V1 | G1 | 02.1.1-T1 |
| 12.3.1-T2 | Metering points for results, artifact GB-month, egress, AI tokens (V2 dependency), support hours - each with its `billing_key` dedupe key (04 `04.8.1-T1`, 06 `06.5.2-T2`) and emission transaction | L | V1 | G1 | 04.8.1-T1, 06.5.2-T1, 06.5.2-T2, 12.3.1-T1 |
| 12.3.1-T3 | Versioned price table + loader/validation: global rate card rows plus nullable-`org_id` per-pilot overrides resolved override-first (T44); no numbers until O5 | M | V1 | G2 | 12.3.1-T1 |
| 12.3.1-T4 | Quota counters and enforcement (80 % warn, 100 % block, explicit error, audited limit raise) | L | V1 | G2 | 12.3.1-T2 |
| 12.3.1-T5 | Roll-up job + reconciliation query (idempotent; reports gaps and duplicates) | M | V1 | G2 | 12.3.1-T1 |
| 12.3.1-T6 | Usage summary data for the quota screen | S | V1 | G2 | 12.3.1-T5 |

Epic 12.3 total: 8.5

### Epic 12.4 - Pricing, cost and invoicing (M3-M4)

#### Story 12.4.1 Cost is known before price is set - acceptance: measured cost per tenant, a pricing hypothesis pack, and manual invoices that reconcile to the ledger.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 12.4.1-T1 | Pricing hypothesis model (onboarding, per-project subscription, concurrency, included quota, AI/artifact overage) built on the measured cost floor | M | V1 | G2 | 12.4.1-T3 |
| 12.4.1-T2 | Manual invoice generation from the ledger: documented query, two-person review, monthly customer statement | M | V1 | G2 | 12.3.1-T5 |
| 12.4.1-T3 | Cost per tenant measurement: API/DB share, object GB-month, egress, AI tokens, support hours | M | V1 | G2 | 10.3.1-T3, 10.6.1-T3, 12.3.1-T2 |
| 12.4.1-T4 | O5 decision pack: margin scenarios, willingness-to-pay evidence, contract template update with the chosen price | M | V1 | G2 | 12.4.1-T1 |
| 12.4.1-T5 | Subscription/billing boundary decision for V2 (stay manual vs minimal automation) | S | V2 | G2 | 12.4.1-T4 |

Epic 12.4 total: 4.5

### Epic 12.5 - Value measurement and exit review (M3-M4)

#### Story 12.5.1 Prove the value or stop - acceptance: the M3 exit review reports every metric with its method, and a go/no-go for V2 with named conditions.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 12.5.1-T1 | Metric pipeline: triage time, maintenance time, repeated incidents, retention, contribution margin (product data + baseline kit) | L | V1 | G3 | 12.2.1-T2, 12.3.1-T2 |
| 12.5.1-T2 | Guardrail review: heal rate vs missed bugs, monthly, using the customer's human labels | M | V1 | G3 | 08.2.2-T1, 12.5.1-T1 |
| 12.5.1-T3 | M3 exit value review + V2 go/no-go with conditions and an owner | M | V1 | G3 | 12.5.1-T1, 12.5.1-T2 |
| 12.5.1-T4 | Customer reference evidence pack (per-pilot value report, customer-approved quotes only) | S | V2 | G3 | 12.5.1-T3 |

Epic 12.5 total: 4.5

### Epic 12.6 - Brand and trademark (M1-M4)

#### Story 12.6.1 "Evidgate" is ours to use - acceptance: a search is done in classes 9/42, a VN application is filed before public launch, and the international decision is recorded.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 12.6.1-T1 | Trademark search for "Evidgate" in classes 9/42 in Vietnam (and a watch on WIPO/US registers), attorney-supported | M | V1 | G1 | - |
| 12.6.1-T2 | File the VN NOIP application; costs recorded from the actual quote (official fee + attorney ⚠️ unverified until quoting) | M | V1 | G1 | 12.6.1-T1 |
| 12.6.1-T3 | WIPO Madrid designation + USPTO filing decision after the VN search result, budgeted from the verified fee pages (653/903 CHF basic, 100 CHF per designation ✅; $350 per class base ✅) | M | V2 | G1 | 12.6.1-T2 |
| 12.6.1-T4 | Domain and handle consistency check with defensive registration inside a fixed budget | S | V1 | G1 | 12.6.1-T1 |

Epic 12.6 total: 3.5

## 10. Dependencies

| Needs | From |
|---|---|
| Product usable enough to onboard a pilot customer end to end | 09.2.1-T1 |
| Artifact storage usage facts for GB-month metering, with the `billing_key` handoff | 06.5.2-T1, 06.5.2-T2 |
| Idempotent usage events per accepted result (carrying `billing_key`) | 04.8.1-T1 |
| DPA, privacy notice and data map before uploads | 11.8.1-T1, 11.8.1-T2, 11.8.1-T3 |
| Deletion SLA for pilot exits | 11.9.1-T1 |
| Incident labels (human ground truth) for the guardrail metric | 08.2.2-T1 |
| Cost inputs: per-unit cost counters, capacity model | 10.3.1-T3, 10.6.1-T3 |
| Identity/tenancy tables for ledger and quota rows | 02.1.1-T1 |
| UI surface for quota and usage | 09.7.1-T5 |

**Provides to:** 05 (quota limits shape run creation limits), 09 (quota screen data), 11 (transfer and sub-processor decisions are driven by provider/residency choices made here), 00 §14 (roll-up).

## 11. Open questions

| # | Question | Owner | Needed by |
|---|---|---|---|
| 1 | O1: pilot customers, their CI and engines | Owner + QA lead | M1 start |
| 2 | O5: pricing and pilot contract shape | Owner | G2 exit, on the price test (CR-D-2 §6.7) |
| 3 | O7: trademark filing scope and budget (VN only vs Madrid vs US) | Owner | before public launch |
| 4 | Whether the pilot fee is credited against the first subscription month | Owner | M1 (agreement template) |
| 5 | Residency (O3) if a pilot customer's contract forbids storage outside Vietnam | Owner | M2 |
| 6 | Support model in the pilot (named engineer vs shared inbox) and its inclusion in the fee | Tech Lead | M1 |
| 7 | Cross-plan story ids referenced in `Depends on` were written in parallel across twelve plans; the integrator must reconcile any mismatch found at the first full-program review — **Closed 2026-09-25:** script check over all 487 task rows finds 0 unresolved ids; whether each cited id means what the citation says is checked in each feature plan's Definition of Ready (00 §12) | Tech Lead | before M1 |

## 12. Estimate summary

| Epic | V1 | V2 | V3 | Total |
|---|---|---|---|---|
| 12.1 Discovery and pilot selection | 5.0 | 0 | 0 | 5.0 |
| 12.2 Pilot agreement and delivery | 5.5 | 0 | 0 | 5.5 |
| 12.3 Usage ledger and quotas | 8.5 | 0 | 0 | 8.5 |
| 12.4 Pricing, cost and invoicing | 4.0 | 0.5 | 0 | 4.5 |
| 12.5 Value measurement and exit review | 4.0 | 0.5 | 0 | 4.5 |
| 12.6 Brand and trademark | 2.5 | 1.0 | 0 | 3.5 |
| **Total** | **29.5** | **2.0** | **0** | **31.5** |

→ with 20 % buffer **37.8** person-days

Estimate unit: sizes assume one senior full-stack .NET + Angular developer (00 §6, CR-D-1).

## 13. Contract change requests

> **Status:** Applied 2026-09-24: 12-CR-1 → 00 §2; 12-CR-2 → 00 §9.3 (`/v1/orgs/{o}/usage/*`) + 00 §9.2 `quota_exceeded`; 12-CR-3 → 00 §9.1.

| # | Request | Why | Status |
|---|---|---|---|
| 12-CR-1 | Add an internal role `billing_operator` to 00 §2 (read ledger/invoice data across tenants, no artifact access, audited) | Invoicing and reconciliation need a role that is neither `tenant_admin` nor `platform_operator` | applied 2026-09-24; permission string `billing:read` (00 §2) |
| 12-CR-2 | Add `GET /v1/usage/summary` and `GET /v1/usage/ledger` (tenant-scoped, cursor-paginated) to the 00 §9.3 core table, plus a documented quota error code in 00 §9.2 | The quota screen and customer reconciliation need a contract; without it both 09 and 12 would invent one | applied 2026-09-24 |
| 12-CR-3 | State in 00 §9.1 that metering/ledger writes are **not** exposed as a public API (internal module only) and never accept a client-supplied quantity | Billing data must not be client-writable | applied 2026-09-24 |

2026-09-25 CR-D-1 + TL review: `billing_key` re-scoped to `unique (org_id, billing_key)` per source event with per-batch ingest keys (T43/T50); `usage_price` gained nullable `org_id` per-pilot overrides (T44, applied in 12.3.1-T3); `quota_counter` gained `org_id`/`project_id`/`period`/`unique (org_id, id)` (T45); estimate unit re-based to senior .NET + Angular (F-12-4). No task ids added, removed or resized.

2026-10-23 CR-D-2: pilot split into a G1 authoring cohort (free trial, 5–10 teams, at least 3 weeks) and a G2 run-and-remember cohort (at least 3 teams agree to pay before G3); D1 amended and D10 added; O5 now closes on the G2 price test (§1, §3, §5, §6, §11); AI credit pricing left with `08.5.5`; FR-12-2 cohort qualification and FR-12-10 boundary added. No task ids added, removed or resized. Later the same day the owner decided the credit pricing rule (at least 3× the cost per action measured in the G1 free trial, reviewed monthly; amounts open until the G2 offer).
