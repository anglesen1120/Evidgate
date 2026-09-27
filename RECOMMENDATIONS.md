# Recommendations for open decisions and questions

> Status: **recommendations, not decisions** (owner accepted them as the working default on 2026-09-25). Every row is `[INFERENCE]` from BA/QA practice unless marked ✅/⚠️. A row becomes a decision only when it is copied into `DECISIONS.md` §8 or the owning plan's §11 at the Definition of Ready review of the story that needs it (00 §12). Numbers are starting points to be re-checked with pilot data.
> Sources of the questions: `DECISIONS.md` §8 (O1–O10) and §11 "Open questions" of plans 01–12.

## 1. Owner decisions (DECISIONS §8)

| # | Recommendation | Why |
|---|---|---|
| O1 | *Engine part decided (DECISIONS §8, 2026-09-26): Playwright; CI is customer input.* Exactly **two** pilot teams: one web team on Playwright TS + GitHub Actions, one with a pytest/JUnit producer. Each has a few hundred automated tests, visible flakiness, ≥ 2 releases a month and a named QA lead who signs. No mobile-first team in V1. | One pilot can be luck; three or more overload a small team. Mobile is V2. |
| O2 | *Owner note 2026-09-26 in DECISIONS §8, reading `[INFERENCE]`, not confirmed.* At least 2 full-stack TS engineers, one of them Tech Lead; the owner (or a named person) is part-time QA lead / product owner. | Without a QA lead nobody closes O9, 07-O1 or acceptance criteria. |
| O3 | Default: storage in Vietnam if a provider offers managed PostgreSQL 18 and S3-compatible storage (⚠️ not verified); otherwise Singapore plus the 60-day transfer impact assessment (F8 ✅). Keep the S3 API boundary so the provider can change. Criteria and scored shortlist (three VN + two APAC candidates, still no vendor chosen): [docs/decisions/10.8.1-provider-selection-criteria.md](docs/decisions/10.8.1-provider-selection-criteria.md), 2026-09-27 — 10.8.1-T1. | Artifacts carry personal data; in-country storage avoids the cross-border dossier and helps fintech/bank sales. |
| O4 | *Owner note 2026-09-26 in DECISIONS §8 ("tự build hệ thống riêng"), reading `[INFERENCE]`, not confirmed.* Off by default; per-tenant opt-in signed by the data owner; redaction at source; only providers with a contractual no-retention term; choose the provider with an eval on the real locator-break corpus. | Matches DECISIONS §7 and PDPL Art. 9 (F7 ✅: silence is not consent). |
| O5 | No number yet. Structure: onboarding fee + per-project subscription with a results/artifact quota; AI usage as overage. Price floor = cost per tenant measured in M3 (12.4.1) × target margin. Ask willingness to pay in the M0 interviews. | Plan 12 D4 forbids invented prices; this produces a grounded one. |
| O6 | Scope: the severity-weighted threshold of the **V3 gate** (08 FR-08-19, 08.8.3-T2); the V2 gate stays "every forbidden case in the evaluation set is blocked". Critical and sensitive cases: no threshold path, a non-author reviewer always decides (DECISIONS §5.5). Others: the V3 gate passes only while the ~95 % upper bound of the false-heal rate is ≤ 2 % (high) / ≤ 5 % (medium, low). By the rule of three (§5.8) that needs ≥ 150 / ≥ 60 labelled candidates with no false heal. | A wrong heal that turns a test falsely green is worse than a red test; small samples give meaningless rates. |
| O7 | Search then file in Vietnam, classes 9 and 42, before pilot marketing. Madrid/US when a foreign customer appears. | Low cost; avoids a late rename. |
| O8 | Decided: (b). | — |
| O9 | One 1–2 h workshop with the customer's QA lead + PO. Start with 10–20 cases covering money, login/authorization, data loss and legal; each case records its business intent and owner; review quarterly. | A long list gets ignored; a short one gives the gate meaning. |
| O10 | (a) customer CI job driven by the CLI. Adds one customer-side CLI replay task in 04, not yet estimated; size it at the 08.4.3 DoR and update 00 §14. | Keeps A5, cheapest, still measures real replay quality. |

## 2. Plan questions (§11 of each plan)

| Plan | Question | Recommendation |
|---|---|---|
| 01 | Cloud provider/region (01 §11 first bullet) | = O3; needed by M2, blocks nothing in M0–M1. |
| 01 | Licence gate on the published CLI/adapter release path | Yes: the CLI is shipped code, so the same gate runs on its release job. |
| 01 | Docker on GitHub-hosted runners for Testcontainers ⚠️ | Keep the plan: 01.7.1-T1 verifies it; self-hosted runner only if it fails. |
| 01 | `scripts/` vs `packages/*` for tooling | Keep `scripts/`; promote a tool to a workspace only when it needs its own dependencies or tests. |
| 02 | Q1 identity provider | Both GitHub and Google Workspace are already built in V1 (FR-02-3, 02.3.1-T1); M0 interviews only confirm which one the pilot uses. |
| 02 | Q2 `ci_producer` TTL | 60 min is already decided (02 D7). Open part: no refresh endpoint; long jobs re-exchange the OIDC token. |
| 02 | Q3 custom roles | Built-in roles only in V1. |
| 02 | Q4 `scrypt` parameters | Tokens are random (≥ 128 bit), so moderate `scrypt` parameters **plus** rate limiting. |
| 02 | Q5 support-access scope | Per project. |
| 02 | Q6 email-only invite | Only if a pilot has testers without GitHub accounts (ask in M0). |
| 03 | Q1 sidecar manifest | Precedence is already decided (00 §7: both present and different = conflict). Open part: filename `evidgate.cases.yaml` next to the test file, YAML (reviewable in PRs). |
| 03 | Q2 dependency edges | In the suite definition file (reviewed by PR). |
| 03 | Q3 OpenAPI skeleton scope | Request skeleton + schema-conformance checks only; business assertions are author-supplied (FR-03-9). |
| 03 | Q4 PR from `dry_failed` draft | `tenant_admin` only. |
| 03 | Q5 GitHub App permissions ⚠️ | Start from contents: read + metadata: read; verify against GitHub docs in 03.4.1-T1. |
| 03 | Q6 fixture seed | In the customer's repo. |
| 03 | Q7 unmapped results | Visible, labelled "unmapped", never gate-counted. |
| 03 | Q8 multi-repo projects | Not in V2. |
| 04 | Q1, Q2 producer and sharding | Answered by the M0 interviews (O1). |
| 04 | Q3 gzip cap | Strict cap first; relax after the load test. |
| 04 | Q4 raw report retention | Follow the artifact retention (14 d default, FR-06-10); reprocess works inside that window. A longer raw-report window needs per-kind retention (a change to `retention_policy`) — not in V1. |
| 04 | Q5 `ingest_only` | Keep; it is the onboarding path for unsupported engines. |
| 05 | Q1 lease/heartbeat | Measure first, then tune. |
| 05 | Q2 collection deadline | ≈ p95 duration of the slowest suite × 1.5. |
| 05 | Q3 subset rerun | Counts only for exactly the failed tests on the same commit and revision, labelled and charged to the flaky budget; never replaces a full-regression gate. |
| 05 | Q4 agent mode | CI mode only until a customer asks. |
| 06 | O1 retention | Artifacts 14 d, metadata 90 d; decisions/evaluations ≥ 1 year for audit. |
| 06 | O2 region | = O3. |
| 06 | O3 max artifact size | Per kind (traces and video exceed 2 MB). |
| 06 | O4 traces sensitive | Yes, by default. |
| 06 | O5 storage emulator | Already answered: SeaweedFS (Apache-2.0 ✅), MinIO rejected (AGPL-3.0 ✅) — DECISIONS §4, 01.8.1-T1. Close the question. |
| 06 | O6 `reject_on_secret` | V1 alerts and classifies; `reject_on_secret` exists but defaults off. |
| 06 | O7 deletion period | 30 days, backups included (FR-06-13); so backup/PITR retention is ≤ 30 days (set in 10.5.1-T1). |
| 07 | O1 flaky budget | Critical failure blocks; flaky budget per project. |
| 07 | O2 advisory → enforcing | After ~4 consecutive weeks with no false block and waiver rate < 10 %. |
| 07 | O3 GitHub surface | Check run, set as a required check. |
| 07 | O4 skipped required case | No `PASS`: a skipped *required* case stays `INCONCLUSIVE` even when the policy's allowed skips cover it — allowed skips apply to non-required cases only (07 decision logic: critical skip/blocked ⇒ not `PASS`, M9). A waiver is a separate mechanism (FR-07-8) and never converts a skip into a pass. |
| 07 | O5 late window | 24 h. |
| 07 | O6 waiver visibility; delivery-row retention | Waivers visible to `viewer`. `ci_delivery` rows kept as long as the evaluation they deliver (they explain what the gate told GitHub). |
| 08 | Q-08-1 provider in M3 | No, unless the tenant opted in with data-owner sign-off (O4). |
| 08 | Q-08-2 first corpus | Evidgate's own CI first (R10), then the pilot. |
| 09 | Q1 router / data libraries | Decided by spike 09.1.1-T2. |
| 09 | Q2 session | httpOnly cookie — already specified by 02 FR-02-5; close the question. |
| 09 | Q3 static hosting | Object storage + CDN. |
| 09 | Q4 share link | Not in V1; HTML/PDF export instead. |
| 10 | Observability backend | Managed OTLP backend or Apache-2.0 components; AGPL backends fail the hosted-service licence rule (DECISIONS §4). Licences ⚠️ checked when chosen. |
| 10 | Canary | Keep 1 replica / 30 min; confirm with the pilot. |
| 10 | Staging object storage | Already decided by FR-10-1 (own account/secrets); close the question. |
| 10 | On-call in the pilot | = O2. |
| 11 | Q1 ASVS | L2 for identity, tenancy and artifacts; documented subset elsewhere. |
| 11 | Q3 LLM provider / residency | = O4. |
| 11 | Q4 V2 isolation | Agent stays wholly customer-side. |
| 11 | Q5 on-premise | Not in V1. |
| 11 | Q6 pen test | 2–3 quotes at M3 start. |
| 12 | Q4 pilot fee credit | Credit against the first subscription month. |
| 12 | Q6 support model | Named engineer, capped hours, included in the fee. |

## 3. Order to close

Deadlines below come from each question's "Needed by" column; every open row in §1–§2 appears exactly once (06-O5 is already closed — recorded for completeness).

1. **Before M1:** O1 (pilot names only), O2; 01 Docker check (01.7.1-T1), 01 `scripts/` vs `packages/*`; 02-Q1, 02-Q2, 02-Q3, 02-Q6; 03-Q1, 03-Q2; 04-Q1, 04-Q2; 12-Q4, 12-Q6. Already closed: 06-O5.
2. **M2:** O3 (covers the 01, 06-O2, 10, 11-Q2 and 12-Q5 provider/region questions); 02-Q4, 02-Q5; 03-Q3, 03-Q5, 03-Q6, 03-Q7, 03-Q8; 04-Q3, 04-Q4, 04-Q5; 05-Q1, 05-Q2; 06-O1, 06-O3, 06-O4, 06-O6, 06-O7; 07-O1, 07-O2, 07-O4, 07-O5, 07-O6; 09-Q1, 09-Q3; 10 observability backend and canary; 11-Q1. Close now (already decided): 09-Q2, 10 staging account.
3. **M3:** O5, O9, O10; 05-Q3; 07-O3 (before 07.6.1-T1, with the pilot team); 08-Q-08-1, 08-Q-08-2; 09-Q4; 10 on-call (= O2 answer); 11-Q5, 11-Q6 (pen test).
4. **M4 / before V2:** O4 (covers 11-Q3); 01 licence gate on the CLI release path; 03-Q4; 05-Q4; 11-Q4.
5. **M5 / before the V3 gate:** O6 (08.8.3-T2).
6. **Before public launch:** O7 (trademark; the VN search task 12.6.1-T1 is already scheduled in M1).
