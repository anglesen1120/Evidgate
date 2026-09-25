# Evidgate — Decisions and Verified Facts

> Authority: this file (why, verified facts) and `plans/00-overview.md` (contracts) win over `plans/01`–`12`. §3–§5 are locked: reopen only with a written change request in §10.
> Legend: ✅ verified from a primary source (link + date) · ⚠️ unverified or secondary source · `[INFERENCE]` reasoning, not a fact · `[DECISION]` a choice · `[OPEN]` needs the owner.
> Source design: `docs/source/system-design-detailed.vi.md` (Vietnamese system design, 2026-09-23, working name "ReleaseHub"). This repo turns it into an executable plan set. Where this file or `plans/` differ from the source, the source is superseded (delta list in §9).

---

## 1. Product in one paragraph

Evidgate is a multi-tenant platform that collects automated test results (web, API, mobile, unit) from customers' CI or agents, keeps them as **immutable evidence** bound to exact builds, and turns that evidence into a **release decision** (PASS / FAIL / INCONCLUSIVE) through versioned policies. It also manages test identity, suites and authoring, and adds **safe healing**: repairing test *mechanics* (for example a changed locator) without changing test *intent*, starting in shadow mode and never hiding a product bug. Execution engines are reused (Playwright, Appium, the repo's own unit framework); Evidgate does not build a browser or API engine.

## 2. Name [DECISION]

**Evidgate** = *evidence* + *gate*: the two things the product must get right (evidence completeness, release gate), and the thing healing must never corrupt.

| Check (2026-09-24) | Result |
|---|---|
| GitHub repository search `evidgate in:name` | 0 results ✅ (api.github.com search) |
| npm package `evidgate` | 404, free ✅ (registry.npmjs.org) |
| Domain `evidgate.com` | whois "No match" ✅ |
| Web search "Evidgate" | no software product found ✅ |
| Trademark search (VN NOIP, WIPO, USPTO) | not done ⚠️ — required before public launch (12.3) |

Rejected names:
- **ReleaseHub** (source working name): an existing company, Release / ReleaseHub (YC W20, San Francisco, environments-as-a-service used "for previewing and QA'ing features") ✅ ([VentureBeat](https://venturebeat.com/ai/environments-as-a-service-platform-releasehub-raises-20m)). Same buyers, same vocabulary → confusion and trademark risk.
- **Verdikt**: existing release-governance SaaS with a CI gate endpoint ✅ ([docs.useverdikt.com](https://docs.useverdikt.com/connecting-signals/api-push)).
- **Probato**: existing open-source JUnit 5 E2E framework ✅ ([github.com/probato-team/probato](https://github.com/probato-team/probato)).
- **ProofGate**: existing "evidence gate for agents" project ✅ ([github.com/SergioGL-14/ProofGate](https://github.com/SergioGL-14/ProofGate)).
- **Evidra**: used by several evidence/audit products ✅ (evidra.com and GitHub).

Code identifiers: npm scope `@evidgate/*`, CLI binary `evidgate`, env prefix `EVIDGATE_`, test annotation key `evidgate.id`.

## 3. Locked architecture decisions (from the source, confirmed)

| # | Decision | Consequence |
|---|---|---|
| A1 | One modular-monolith backend; API and worker are separate processes from one codebase | Modules own their tables and expose in-process interfaces; no microservices in V1–V2 |
| A2 | Git holds approved test code | Studio/AI produce drafts and PRs; every run pins a commit and immutable revisions |
| A3 | Evidence + policy decide a release | A green dashboard is never the decision; the decision is an immutable evaluation row |
| A4 | Healing starts in shadow/proposal mode | Learns from verified failures; every patch is validated before a human applies it |
| A5 | Customer-operated execution first (CI mode, then agent mode) | No hosted runner in V1–V2; no inbound port into customer networks |
| A6 | PostgreSQL is the system of record; object storage holds raw reports and artifacts | Search/analytics start as read tables and indexes |
| A7 | No n8n, Kafka, Kubernetes or Temporal in V1 | n8n may consume webhooks for notifications only; Temporal re-evaluated when workflows need long waits and recovery (§10 trigger in plan 10) |
| A8 | Evidence completeness, test outcome and gate decision are three separate fields | FAIL with `completeness = false` is valid; missing evidence never yields PASS |
| A9 | Retry upload, retry job and retry test are three different operations with their own ids and audit | No blind retries of side-effecting operations |
| A10 | Delivery is at-least-once; consumers dedupe | Never claim exactly-once for side effects outside the database |

## 4. Stack and versions [DECISION] (checked 2026-09-24)

| Layer | Choice | Evidence | Note |
|---|---|---|---|
| Runtime | **Node.js 26 LTS** | v26 released 2026-05-05, LTS 2026-10-28, maintenance 2027-10-20, EOL 2029-04-30 ✅ ([nodejs/Release schedule.json](https://github.com/nodejs/Release)) | Node 24 leaves Active LTS on 2026-10-20 ✅. M0 (weeks 1–2) writes no production code, so the project starts on 26. Node 27 (first annual line) is LTS only in Oct 2027 ✅ ([nodejs.org releases](https://nodejs.org/en/about/previous-releases)) |
| Language | **TypeScript 6.0.x** (not 7.0) | npm `typescript` latest = 7.0.2 ✅ ([registry](https://registry.npmjs.org/typescript/7.0.2)), but `typescript-eslint` 8.70.1 declares peer `typescript >=4.8.4 <6.1.0` ✅ ([registry](https://registry.npmjs.org/typescript-eslint/8.70.1)) and `@nestjs/swagger` 12.0.2 declares peer `typescript ^5.5.0 \|\| ^6.0.0` ✅ ([registry](https://registry.npmjs.org/@nestjs%2Fswagger/12.0.2)) | Move to 7.x only when **both** peer ranges include 7 (01.1.1-T4, V3) |
| Backend framework | **NestJS 12** | 12.0.0 published 2026-08-27, 12.1.0 on 2026-09-23, MIT, `engines.node >= 20` ✅ ([registry](https://registry.npmjs.org/@nestjs%2Fcore/12.1.0)) | Very new major: check every `@nestjs/*` module we use supports 12 in 01.1 ⚠️ |
| Database | **PostgreSQL 18** | 18.6 current, supported until 2030-11-14; 19 is at Beta 4 on 2026-09-24 ✅ ([versioning policy](https://www.postgresql.org/support/versioning/)) | Managed provider [OPEN] (plan 10) |
| Data access | **Kysely** (typed SQL builder) + plain SQL migration files | kysely 0.29.6, MIT, `node >= 22` ✅ ([registry](https://registry.npmjs.org/kysely/0.29.6)) | Explicit transactions are required for `SET LOCAL` tenant context (RLS) and `FOR UPDATE SKIP LOCKED`; no ORM that hides transactions |
| Internal background tasks | **transactional outbox + pg-boss** | pg-boss 12.34.0, MIT, `node >= 22.12` ✅ ([registry](https://registry.npmjs.org/pg-boss/12.34.0)) | For outbox dispatch, reconcile, retention, sweeps, cron. **Runner job leasing is domain logic** (epoch fencing, capability match) in our own tables (plan 05), not pg-boss |
| Contracts | **Contract-first**: OpenAPI 3.1 + JSON Schema files in `contracts/` | — | The adapter protocol and CLI must be implementable outside TypeScript; TS types are generated from the files |
| Web | **React 19 + Vite 8** | react 19.3.0 MIT ✅ ([registry](https://registry.npmjs.org/react/19.3.0)); vite 8.3.1 MIT ✅ ([registry](https://registry.npmjs.org/vite/8.3.1)) | Router/data libraries chosen in plan 09 |
| Object storage | **S3 API** (AWS SDK v3 `@aws-sdk/client-s3`, Apache-2.0 ✅) | Presigned URLs are bearer credentials with an expiry ✅ ([AWS docs](https://docs.aws.amazon.com/AmazonS3/latest/userguide/using-presigned-url.html)) | Provider [OPEN] (O3). Local emulator: **SeaweedFS** (Apache-2.0 ✅); fallback Scality CloudServer (Apache-2.0 ✅); MinIO rejected (AGPL-3.0 ✅); LocalStack rejected (Community repo archived, free tier non-commercial ✅) — 01.8.1-T1 |
| Tests (our own code) | **Vitest 5**, **Testcontainers 12** (PostgreSQL), **Playwright 1.63** for our web E2E | vitest 5.0.1 MIT ✅ ([registry](https://registry.npmjs.org/vitest/5.0.1)); testcontainers 12.1.0 MIT ✅ ([registry](https://registry.npmjs.org/testcontainers/12.1.0)); @playwright/test 1.63.0 Apache-2.0 ✅ ([registry](https://registry.npmjs.org/@playwright%2Ftest/1.63.0)) | Dogfooding: Evidgate ingests its own CI results from M2 |
| Observability | **OpenTelemetry** SDK + collector | @opentelemetry/sdk-node 0.222.0, Apache-2.0 ✅ ([registry](https://registry.npmjs.org/@opentelemetry%2Fsdk-node/0.222.0)) | 0.x package line: pin exact versions |
| Package manager | **npm workspaces**, committed `package-lock.json`, `npm ci` in CI | ships with Node | No extra tool to install |
| Engines we integrate | Playwright (web/API), Appium 3 with UiAutomator2 / XCUITest drivers, JUnit XML producers | appium 3.7.0 Apache-2.0 ✅ ([registry](https://registry.npmjs.org/appium/3.7.0)); XCUITest needs macOS ✅ ([driver install docs](https://appium.github.io/appium-xcuitest-driver/latest/getting-started/installation/)) | Evidgate never pins "latest" inside runners |

Licence rule [DECISION]: dependencies must allow closed-source commercial SaaS. No AGPL/SSPL/BUSL or non-commercial licences in shipped code or hosted services; dev-only tools are checked case by case in plan 01.

## 5. Safe-healing rules (locked)

1. Healing may change **test mechanics** only: locator, wait condition (proposal only), infra retry within policy. It never changes intent: assertions, expected values, API schema/status, unit expectations, snapshot baselines.
2. Forbidden in any automated patch: removing or weakening an assertion, `skip`/`fixme`, `force: true` clicks, catch-and-ignore, mocking a business result, disabling a network check, unbounded retries, changing test id / build / policy / data to get green.
3. The original failure and its evidence are never deleted or overwritten; shadow results never replace an official run.
4. Keeping the assertion hash equal is **necessary, not sufficient**: the validator also checks semantic identity (role/name/test id), unique match, business region and an independent post-condition, plus a negative control that must still fail.
5. Sensitive cases (payment, authorization, deletion, security) are proposal-only and need a reviewer who is not the author.
6. Approval binds to `candidate_hash` + base commit; any context change or rebase re-validates.
7. Runtime auto-apply is **off in V1 and V2**. V3 may reuse *approved* mappings in a narrow context after an independent evaluation (plan 08 gate).
8. LLM "confidence" is never a safety probability. With 0 failures in *n* independent samples the ~95 % upper bound is ≈ 3/*n* (rule of three; 300 samples → ≈ 1 %) `[INFERENCE: standard statistics]`; correlated failures make real confidence lower.
9. Kill switches exist at system, tenant and project level **before** any apply capability ships; if the LLM or knowledge store fails, tests and gates run normally and healing becomes `unavailable`.

## 6. Verified facts used by the plans

| # | Fact | Source |
|---|---|---|
| F1 | Playwright ships three Test Agents: planner, generator, **healer**. The healer replays failing steps, suggests a patch (locator update, wait adjustment, data fix), re-runs "until it passes or until guardrails stop the loop" and outputs "a passing test, or a **skipped test** if the healer believes that functionality is broken". Agent definitions are regenerated with `npx playwright init-agents` and should be regenerated when Playwright updates. | ✅ [playwright.dev/docs/test-agents](https://playwright.dev/docs/test-agents) (read 2026-09-24) |
| F2 | Playwright Test Agents first shipped in v1.56 (Oct 2025). | ⚠️ secondary (testmuai.com blog) |
| F3 | GitHub Actions can mint a per-job OIDC JWT (claims include `repository`, `sha`, `ref`, `run_id`, `run_attempt`, `job_workflow_ref`); a relying party validates claims and returns a short-lived token. Repositories created after 2026-07-15 use an immutable default `sub` format that includes owner and repository ids. | ✅ [GitHub docs: OpenID Connect](https://docs.github.com/en/actions/concepts/security/openid-connect) |
| F4 | GitHub warns about running untrusted code in workflows (forks, secrets exposure). | ✅ source [9] in the design doc; re-checked in plan 11 |
| F5 | PostgreSQL RLS: table owners and `BYPASSRLS` roles bypass policies unless `FORCE ROW LEVEL SECURITY`. | ✅ [PostgreSQL docs: row security](https://www.postgresql.org/docs/current/ddl-rowsecurity.html) |
| F6 | `SELECT … FOR UPDATE SKIP LOCKED` is documented for queue-like consumers. | ✅ [PostgreSQL docs: SELECT](https://www.postgresql.org/docs/current/sql-select.html) |
| F7 | Vietnam Law on Personal Data Protection No. 91/2025/QH15, issued 2025-06-26. Art. 9: consent must be explicit, per purpose; "silence or non-response is not considered consent". Art. 8.4: max fine for an organisation violating cross-border transfer rules = 5 % of the preceding year's revenue; Art. 8.5: other violations up to VND 3 billion. | ✅ [LuatVietnam English text](https://english.luatvietnam.vn/dan-su/law-on-personal-data-protection-law-no-91-2025-qh15-405135-d1.html) (Arts. 1–9 read 2026-09-24) |
| F8 | PDPL Art. 20.1: cross-border transfer includes storing Vietnam-collected personal data outside Vietnam, sending it to organisations abroad, or processing it on platforms outside Vietnam. Art. 20.2: the transferring party files a transfer impact assessment within 60 days of the first transfer. Art. 21.1/21.3: the controller files a processing impact assessment within 60 days; the processor prepares and keeps one as agreed with the controller. Art. 23.1: 72 h breach notice (processor notifies the controller promptly). Art. 37.2.a: a processor receives data only after a processing contract. Effective date 2026-01-01 and guiding Decree 356/2025/ND-CP ⚠️ secondary (Tilleke & Gibbins). | ✅ same text, Arts. 20–23, 37 (read 2026-09-24); effective date ⚠️; interpretation → counsel (plan 11) |
| F9 | pytest JUnit XML output has compatibility caveats; JUnit XML has no single standard schema across producers. | ✅ source [5]; mapping per producer is required (plan 04) |

## 7. Legal and data position

- Customer test artifacts (DOM snapshots, screenshots, videos, traces, request bodies) can contain personal data. `[INFERENCE]` The customer is the controller and Evidgate the processor for that data (PDPL Art. 2.7–2.8 ✅ definitions), so a processing contract (DPA) must be signed before the first pilot upload (Art. 37.2.a ✅; plan 11).
- Sending artifact content to an LLM provider or a storage region outside Vietnam is a cross-border transfer under Art. 20.1 (F8 ✅) and triggers the 60-day transfer assessment. Healing context sent to a provider is opt-in per tenant, redacted at source, and blocked until the tenant's data owner approves (plan 08 + 11).
- On-premise and data residency are deployment options to decide per pilot, not V1 commitments.

## 8. Open decisions (owner)

Working recommendations for every open item: `RECOMMENDATIONS.md` (not decisions until copied here or into the owning plan).

| # | Decision | Needed before | Plan |
|---|---|---|---|
| O1 | Pilot customers, their CI and engines (assumed GitHub Actions + Playwright TS + one JUnit producer) | M1 start | 12.1 |
| O2 | Team size and who is Tech Lead / QA lead (estimates assume one senior full-stack TS developer; calendar for 2–3) | M1 start | 00 §14 |
| O3 | Cloud provider/region for PostgreSQL + object storage (VN residency?) | M2 (first pilot data) | 10, 11 |
| O4 | LLM provider(s) and whether any tenant data may leave Vietnam | V2 healing PR (M4) | 08, 11 |
| O5 | Pricing and pilot contract (onboarding fee, project/concurrency subscription) | M3 pilot | 12 |
| O6 | False-heal acceptance threshold per severity (V3 gate; the V2 gate is "every forbidden case blocked", 08 FR-08-19) | before the V3 gate (08.8.3-T2, M5) | 08 |
| O7 | Trademark filing for "Evidgate" | before public launch | 12 |
| O8 | Scope vs calendar: buffered V1 = 499.8 person-days ≈ 50 weeks for 2 engineers / ≈ 33 for 3, against the source's 12 weeks to the M3 pilot. **Decided 2026-09-25 [DECISION] (owner): option (b)** — scope is kept; the 00 §4 weeks are sequence only and a milestone ends when its exit gate passes, not on a date (source §1: "Mọi mốc trong tài liệu là thứ tự ưu tiên"). Why not (a) alone [INFERENCE] (script over the task rows, dependency closure included): deferring UI polish, templates, reprocess, outbound webhooks, policy shadow compare, quota enforcement and trademark filing removes 27.5 raw days (V1 466.8 buffered ≈ 46.7 / 31.1 weeks); also deferring V1 shadow healing (08.1.2–08.4.3, 09.5.1) removes 80 (V1 403.8 ≈ 40.4 / 26.9 weeks) — neither reaches 12 weeks. Dates are set only after O2 (team size) | decided | 00 §4, §14 |
| O9 | Which cases are critical and their business intent (source decision row "Case critical và ý định nghiệp vụ": customer QA lead + product owner). Mechanism [DECISION]: the critical-case list is a field of the policy revision (00 §8.6), so it is versioned, hashed and published by a `release_approver`; which cases go on it is the customer's decision. Until it is set, the project stays advisory | before a project's policy moves from advisory to gating | 07, 12.2 |
| O10 | V1 shadow-healing replay host. 05 has no V1 executor (runner jobs 05.7 are V2) and A5 forbids a hosted runner in V1–V2. Options: (a) customer CI job driven by the CLI, (b) move 05.7 runner + 11.2.1-T4 sandbox into V1 (A5 change request), (c) slip sandbox replay to V2/M4. Current estimates carry only 08.4.3-T1..T3 + 11.2.1-T6; option (a) adds a customer-side CLI replay task in 04, option (b) moves V2 rows into V1. Recommended [INFERENCE]: (a) — keeps A5, no hosted execution of customer code in V1, and still measures real replay quality in the pilot; (b) adds ≈ 12 raw days for 05.7.1/05.7.2/05.7.4 + 11.2.1-T4 (≈ 20 if all of 05.7 moves) | before the 08.4.3 feature plan (M3) | 05, 08, 11 |

## 9. Delta from the source design (review findings)

| # | Source says | This plan set says | Why |
|---|---|---|---|
| R1 | Name "ReleaseHub" | **Evidgate** | §2 collision |
| R2 | "NestJS trên Node.js LTS", "SQL migration", "PostgreSQL và outbox", React | Exact lines and libraries in §4 | Source leaves versions open; Node 24 leaves Active LTS on 2026-10-20; TS 7 is not yet supported by typescript-eslint |
| R3 | Healing pipeline built from scratch; no mention of Playwright's healer | Playwright's healer (F1) may be used **only as a candidate generator inside the sandbox**; its output goes through the same AST validator; `skip` output is rejected by rule 5.2 | Reuse engine features (source principle "tái dùng engine") without breaking the no-skip rule |
| R4 | "Một CI phổ biến" | GitHub Actions first, authenticated by **GitHub OIDC → short-lived Evidgate ingest token** (F3); a hashed project ingest token for other CIs | Removes long-lived secrets from customer CI; confirm in M0 (O1) |
| R5 | Human login not specified | OIDC login (GitHub, Google Workspace); no password storage in V1; enterprise SAML later | Smallest secure surface for a B2B pilot |
| R6 | Data residency only "a deployment option" | PDPL facts (F7, F8) drive an explicit plan 11 epic: DPA, redaction, provider opt-in, transfer assessment | Artifacts contain personal data; fines are revenue-based |
| R7 | Milestones only (M0–M5), no task sizes | Stories + tasks with sizes and V1/V2/V3 tags; roll-up in 00 §14 | Needed to plan staffing |
| R8 | Section numbers repeat (five "§4", four "§6") and a page map for the .docx | Plans 01–12 by area; the page map is dropped | Unique ids for cross-references |
| R9 | Background worker pattern implied | Two mechanisms, stated: pg-boss for internal tasks, domain lease tables for runner jobs | Epoch fencing and capability matching are product logic |
| R10 | Product tests itself? not stated | Evidgate's own CI results are ingested into Evidgate from M2 (dogfooding) | Cheapest realistic fixture for adapters and gates |

Checked and kept: capacity arithmetic (100 000 × 1 KiB ≈ 97.7 MiB/day, ≈ 8.6 GiB/90 days; 50 × 20 × 2 MB = 2 GB/day, 28 GB/14 days; 50 000 events/60 s ≈ 833/s) ✅ recomputed; rule-of-three statement ✅.

## 10. Change log

| Date | Change |
|---|---|
| 2026-09-24 | Repo created from the source design; name, stack and review deltas R1–R10 recorded. |
| 2026-09-24 | Area plans 01–12 written; CR-01-1 (local S3 emulator) and CR-01-2 (TS 7 trigger) applied to §4. LocalStack fallback replaced after its Community repository was found archived. |
| 2026-09-25 | Review round: §4 npm facts now link the registry record; O9 (critical cases, source decision row) added; O8 recomputed to 499.8 buffered V1 person-days. |
| 2026-09-25 | Review round 2: O10 (V1 replay execution host) added; O8 numbers unchanged (milestone split in 00 §14 only). |
| 2026-09-25 | O8 decided by the owner: option (b), milestones are sequence + exit gates, scope unchanged; cut simulation recorded in the O8 row. |
| 2026-09-25 | `RECOMMENDATIONS.md` added (owner accepted as working defaults); 06-O5 closed (already answered in §4). |
| 2026-09-25 | `RECOMMENDATIONS.md` consistency pass: added the missing plan-01 provider row (= O3), corrected 07-O4 (allowed skips ≠ waiver; skipped required case never PASSes), scoped O6 to the V3 gate, and made §3 order-to-close exhaustive by each question's deadline. |
| 2026-09-25 | O6 deadline moved to "before the V3 gate (08.8.3-T2, M5)" by the owner, matching 08 FR-08-19; `RECOMMENDATIONS.md` §3 and 07-O6 delivery-row retention updated. |
| 2026-09-25 | `RECOMMENDATIONS.md` review round 2: rows aligned with decided FRs (02 FR-02-3/D7, 03 FR-03-9, 06 FR-06-10/13, 10 FR-10-1, 00 §7); 02-Q2 moved to before M1; 11-Q3 added; O10 (a) task cost flagged. |
