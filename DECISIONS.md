# Evidgate — Decisions and Verified Facts

> Authority: this file (why, verified facts) and `plans/00-overview.md` (contracts) win over `plans/01`–`13`. §3–§5 are locked: reopen only with a written change request in §10.
> Legend: ✅ verified from a primary source (link + date) · ⚠️ unverified or secondary source · `[INFERENCE]` reasoning, not a fact · `[DECISION]` a choice · `[OPEN]` needs the owner.
> Source design: `docs/source/system-design-detailed.vi.md` (Vietnamese system design, 2026-09-23, working name "ReleaseHub"). This repo turns it into an executable plan set. Where this file or `plans/` differ from the source, the source is superseded (delta list in §9).

---

## 1. Product in one paragraph

Evidgate helps a QA team write and maintain its tests (CR-D-2, owner, 2026-10-23). The team writes requirements; Evidgate drafts a checklist and manual test cases for review, turns approved cases into Playwright scripts bound to real elements, and remembers per project how each element looked when a run was green. When a run fails it classifies the failure as locator drift, UI change or product bug, and proposes a **safe repair** as a pull request for human review: it repairs test *mechanics* (for example a changed locator) without changing test *intent*, starts in shadow mode, never auto-applies, and never hides a product bug. Underneath sits the evidence platform: multi-tenant, results from customers' CI or agents kept as **immutable evidence** bound to exact builds, and a versioned-policy **release decision** (PASS / FAIL / INCONCLUSIVE) that is reported, not the lead feature. Delivery runs in phases G1 Author → G2 Run → G3 Learn and repair → G4 Expand with exit gates ([`docs/decisions/CR-D-2-direction-a.md`](docs/decisions/CR-D-2-direction-a.md) §5, [`plans/13-product-phases.md`](plans/13-product-phases.md)). Execution engines are reused (Playwright; mobile later via mobilewright, CR-D-2 §7); Evidgate does not build a browser or API engine.

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

Code identifiers: .NET root namespace and assembly prefix `Evidgate.*`, npm scope `@evidgate/*`, CLI binary `evidgate`, env prefix `EVIDGATE_` (.NET configuration binds `EVIDGATE__Section__Key`), test annotation key `evidgate.id`.

## 3. Locked architecture decisions (from the source, confirmed)

| # | Decision | Consequence |
|---|---|---|
| A1 | One modular-monolith backend; API and worker are separate processes from one codebase | Modules own their tables and expose in-process interfaces; no microservices in V1–V2 |
| A2 | Git holds approved test code | Studio/AI produce drafts and PRs; every run pins a commit and immutable revisions |
| A3 | Evidence + policy decide a release | A green dashboard is never the decision; the decision is an immutable evaluation row |
| A4 | Healing starts in shadow/proposal mode | Learns from verified failures; every patch is validated before a human applies it. Timing (CR-D-2): repair proposals and approved pull requests from phase G3, after a per-project shadow window |
| A5 | Customer-operated execution first (CI mode, then agent mode) | No hosted runner in V1–V2; no inbound port into customer networks. Kept for G1–G3 (customer CI or local `evidgate run`); a hosted runner is reconsidered at G4 only on paying demand (CR-D-2) |
| A6 | PostgreSQL is the system of record; object storage holds raw reports and artifacts | Search/analytics start as read tables and indexes |
| A7 | No n8n, Kafka, Kubernetes or Temporal in V1 | n8n may consume webhooks for notifications only; Temporal re-evaluated when workflows need long waits and recovery (§10 trigger in plan 10) |
| A8 | Evidence completeness, test outcome and gate decision are three separate fields | FAIL with `completeness = false` is valid; missing evidence never yields PASS |
| A9 | Retry upload, retry job and retry test are three different operations with their own ids and audit | No blind retries of side-effecting operations |
| A10 | Delivery is at-least-once; consumers dedupe | Never claim exactly-once for side effects outside the database |

## 4. Stack and versions [DECISION] (checked 2026-09-25; CR-D-1 in §10 replaces the 2026-09-24 Node/NestJS/React server stack)

Owner direction 2026-09-25: web on **Angular**, backend on the **latest .NET** with **CQRS**, database **PostgreSQL**. Customer-side packages that run inside customer CI stay on Node/TypeScript because Playwright reporters are JavaScript and CI runners already carry Node.

### 4.1 Server and worker (.NET)

| Layer | Choice | Evidence | Note |
|---|---|---|---|
| Runtime | **.NET 10 LTS** (SDK/runtime 10.0.x, pinned by `global.json`) | 10.0 `active`, `lts`, latest 10.0.12 (2026-09-08), EOL 2028-11-14; 11.0 is `go-live` **STS** (11.0.0-rc.1) ✅ ([dotnet/core releases-index.json](https://github.com/dotnet/core/blob/main/release-notes/releases-index.json), read 2026-09-25) | "Latest .NET" = latest GA LTS. STS 11 is skipped; move to .NET 12 LTS when it is GA (01.1.1-T4, V3) |
| Web framework | **ASP.NET Core 10 minimal APIs**, route groups per module | `Microsoft.AspNetCore.App.Ref` 10.0.12 ✅ ([NuGet](https://www.nuget.org/packages/Microsoft.AspNetCore.App.Ref/10.0.12)) | No MVC controllers; endpoint filters for auth/idempotency |
| CQRS dispatch | **`Mediator` 3.0.2** (martinothamar, source-generated) | MIT (LICENSE in nupkg) ✅ ([NuGet](https://www.nuget.org/packages/Mediator.Abstractions/3.0.2)) | Compile-time handler registration, no reflection on the hot path. Rejected: **MediatR 14.2.0** — RPL-1.5 or paid commercial licence ✅ (LICENSE.md in nupkg), fails the licence rule; **WolverineFx 6.40.0** MIT ✅ — runtime code generation and its own envelope tables are more machinery than we need |
| Write model | **EF Core 10 + Npgsql EF provider 10** mapped onto the SQL-migrated schema | `Microsoft.EntityFrameworkCore` 10.0.12 MIT ✅; `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 PostgreSQL licence ✅ ([NuGet](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/10.0.3)) | Always on the connection + transaction opened by the tenant scope (00 §5.5). EF migrations are **not** used |
| Read model and hot paths | **Dapper 2.1** over `Npgsql` 10 | Dapper 2.1.89 Apache-2.0 ✅; Npgsql 10.0.3 PostgreSQL licence ✅ ([NuGet](https://www.nuget.org/packages/Npgsql/10.0.3)) | Queries, event batch insert (`unnest` arrays, one round trip), lease claim `FOR UPDATE SKIP LOCKED` (F6) |
| Database | **PostgreSQL 18** | 18.6 current, supported until 2030-11-14 ✅ ([versioning policy](https://www.postgresql.org/support/versioning/)) | Managed provider [OPEN] (O3) |
| Migrations | Plain SQL files run by **DbUp 7** (`dbup-postgresql` 7.0.1) in `Evidgate.Migrator` as the owner role | MIT ✅ ([NuGet](https://www.nuget.org/packages/dbup-postgresql/7.0.1)) | Custom journal adds the checksum column 01 FR-01-5 requires ⚠️ (DbUp's default journal stores names only — confirm in 01.4.1) |
| Internal background tasks | **Transactional outbox + own `task` queue table** (`FOR UPDATE SKIP LOCKED`, retry cap, dead letter); schedules parsed by **Cronos** 0.13.0, fired idempotently by unique `(schedule_name, fire_at)` on the task row (no leader election; transaction-scoped advisory lock only against contention) | Cronos MIT ✅ ([NuGet](https://www.nuget.org/packages/Cronos/0.13.0)) | Replaces pg-boss (a Node library). One queue primitive shared with runner leases (plan 05). Rejected: Hangfire 1.8.25 (LGPL-3.0 ✅, extra storage schema), Quartz.NET 4.1.1 (Apache-2.0 ✅, but a second scheduler model for a need Cronos + a unique fire key covers) |
| Validation | **FluentValidation 12** for command rules; **Corvus.Json.Validator 5.6** for JSON Schema 2020-12 wire validation | FluentValidation 12.1.1 Apache-2.0 ✅; Corvus.Json.Validator 5.6.1 Apache-2.0 ✅ ([NuGet](https://www.nuget.org/packages/Corvus.Json.Validator/5.6.1)) | Rejected: **JsonSchema.Net 9.4.0** — binary release under a paid "Open Source Maintenance Fee" EULA ✅ (OSMFEULA.txt in nupkg); NJsonSchema 11.6.1 MIT ✅ but its 2020-12 support is partial ⚠️ |
| Canonical hashing | RFC 8785 JCS implemented in `Evidgate.BuildingBlocks.Canonicalization` (`JsonCanonicalizer` + `CanonicalHash`, ~780 lines with XML docs) and enforced by the vendored RFC 8785 reference pairs ✅ (`fixtures/rfc8785/` — cyberphone/json-canonicalization test data, Apache-2.0, README attribution): `Rfc8785VectorTests` + `CrossRuntimeParityVectorTests` (xUnit) and `packages/cli/src/canonical.test.ts` (Vitest) assert the same bytes, and both runtimes assert the golden digests in `fixtures/canonical/*.json` (01.3.2 AC5) | — | No maintained, licensed NuGet package found (`JsonCanonicalizer` 1.0.0 declares no licence ✅, NuGet nuspec). Duplicate names, lone surrogates (including an unpaired surrogate in a .NET text input, which the strict UTF-8 encoder rejects instead of substituting U+FFFD), numbers outside the IEEE 754 double range and nesting deeper than 64 levels are rejected, never repaired |
| Serialization | `System.Text.Json` source-generated contexts, `JsonNamingPolicy.SnakeCaseLower` | in-box | Matches 00 §7 snake_case |
| OpenAPI | Contract file `contracts/openapi.yaml` is the source; `Microsoft.AspNetCore.OpenApi` 10 emits the served document and CI diffs it against the contract | 10.0.12 MIT ✅ | OpenAPI 3.1 output of the in-box generator ⚠️ — confirm in 01.3.1; fallback: contract tests only |
| Auth libraries | ASP.NET Core OpenID Connect + cookie handlers; `Microsoft.IdentityModel.JsonWebTokens` 8.23 for GitHub OIDC JWT validation; `Microsoft.AspNetCore.DataProtection` keys in PostgreSQL | MIT ✅ (NuGet 10.0.12 / 8.23.0) | No password storage (R5) |
| Object storage | **S3 API** via `AWSSDK.S3` 4.0 | 4.0.103.4 Apache-2.0 ✅ ([NuGet](https://www.nuget.org/packages/AWSSDK.S3/4.0.103.4)); presigned URLs are bearer credentials with an expiry ✅ ([AWS docs](https://docs.aws.amazon.com/AmazonS3/latest/userguide/using-presigned-url.html)) | Provider [OPEN] (O3). Local emulator **SeaweedFS** (Apache-2.0 ✅); fallback Scality CloudServer (Apache-2.0 ✅); MinIO rejected (AGPL-3.0 ✅); LocalStack rejected (archived Community repo ✅) — 01.8.1-T1 |
| Resilience | `Microsoft.Extensions.Http.Resilience` 10.10 (Polly 8 underneath) | MIT ✅; Polly.Core 8.8.0 BSD-3-Clause ✅ | Outbound GitHub, webhooks, LLM gateway |
| Observability | **OpenTelemetry .NET** (`OpenTelemetry.Extensions.Hosting` 1.19) + `Microsoft.Extensions.Logging`; log redaction via `Microsoft.Extensions.Compliance.Redaction` ⚠️ | 1.19.1 Apache-2.0 ✅ ([NuGet](https://www.nuget.org/packages/OpenTelemetry.Extensions.Hosting/1.19.1)) | No Serilog: one logging pipeline |
| Tests (server) | **xUnit v3** 4.0, **Shouldly** 4.3, **NSubstitute** 6.2, **Testcontainers.PostgreSQL** 4.15, **Respawn** 7.0; `WebApplicationFactory` | xunit.v3 4.0.1 Apache-2.0 ✅; Shouldly 4.3.0 BSD-3-Clause ✅; NSubstitute 6.2.0 BSD-3-Clause ✅; Testcontainers.PostgreSQL 4.15.0 MIT ✅; Respawn 7.0.0 Apache-2.0 ✅ (NuGet nuspec) | Rejected: **FluentAssertions 8.11.0** — Xceed Community Licence is non-commercial ✅ (LICENSE in nupkg) |
| Package management | NuGet **Central Package Management** (`Directory.Packages.props`), `packages.lock.json` committed, `dotnet restore --locked-mode` in CI | in-box | Exact versions; no floating ranges |

### 4.2 Web (Angular)

| Layer | Choice | Evidence | Note |
|---|---|---|---|
| Framework | **Angular 22.2** (`@angular/core`, `@angular/cli`, `@angular/build`, `@angular/cdk`) | 22.2.0 MIT, published 2026-09-23, `engines.node ^22.22.3 \|\| ^24.15.0 \|\| >=26.0.0` ✅ ([registry](https://registry.npmjs.org/@angular/core/22.2.0)) | Standalone components, signals, zoneless change detection ✅ (stable, `@publicApi`; no-zone.js run in [ADR-0008](docs/adr/0008-web-integration-fit.md)), `OnPush`, built-in control flow, lazy routes per feature |
| Language | **TypeScript 6.0.x** | `@angular/build` 22.2.0 peer `typescript >=6.0 <6.1` ✅ ([registry](https://registry.npmjs.org/@angular/build/22.2.0)); npm `typescript` latest 7.0.2 ✅ | Same pin as customer-side packages; TS 7 only when Angular's peer range allows it |
| State | **`@ngrx/signals` 22** (SignalStore) for feature state; plain signals for local state | 22.0.1 MIT ✅ ([registry](https://registry.npmjs.org/@ngrx/signals/22.0.1)) | No global Redux store |
| API client | SDK generated from `contracts/openapi.yaml` by `@hey-api/openapi-ts` 0.99 | 0.99.0 MIT ✅ | Shared with the CLI (`packages/sdk`); Angular wraps it behind `HttpClient` interceptors ✅ through a `fetch` adapter ([ADR-0008](docs/adr/0008-web-integration-fit.md); fallback `@hey-api/client-angular`) |
| i18n | **`@angular/localize`** (first-party), vi + en builds | 22.2.0 MIT, same release line (exact `@angular/compiler` peers) ✅ ([registry](https://registry.npmjs.org/@angular/localize/22.2.0)); `ng build --localize` emits `/en/` and `/vi/` ([ADR-0008](docs/adr/0008-web-integration-fit.md)) | Runtime switch = locale-prefixed builds (`/vi/`, `/en/`) |
| Lint | `angular-eslint` 22.5 | 22.5.0 MIT ✅ ([registry](https://registry.npmjs.org/@angular-eslint/eslint-plugin/22.5.0)) | |
| Tests (web) | **Vitest 5** unit tests; **Playwright 1.63** E2E | vitest 5.0.1 MIT ✅; @playwright/test 1.63.0 Apache-2.0 ✅ | Angular unit-test builder with Vitest ✅ (`@angular/build:unit-test`, JUnit reporter; [ADR-0008](docs/adr/0008-web-integration-fit.md)) |

### 4.3 Customer-side packages (unchanged language: Node + TypeScript)

| Layer | Choice | Evidence | Note |
|---|---|---|---|
| Runtime | **Node.js 26** (Current; becomes LTS 2026-10-28) to build; published packages support **Node 22+** (00 §11) | v26 released 2026-05-05, LTS 2026-10-28, EOL 2029-04-30 ✅ ([nodejs/Release](https://github.com/nodejs/Release)) | CLI `evidgate`, Playwright reporter, JUnit/Appium mappers, V2 agent |
| Language | **TypeScript 6.0.x** | `typescript-eslint` 8.70.1 peer `typescript >=4.8.4 <6.1.0` ✅ ([registry](https://registry.npmjs.org/typescript-eslint/8.70.1)) | Move to 7.x with the web (01.1.1-T4, V3) |
| Tests | **Vitest 5** | as above | |
| Package manager | **npm workspaces** for `apps/web` + `packages/*`, committed `package-lock.json`, `npm ci` | ships with Node | |
| Engines we integrate | Playwright (web/API), Appium 3 with UiAutomator2 / XCUITest drivers, JUnit XML producers | appium 3.7.0 Apache-2.0 ✅ ([registry](https://registry.npmjs.org/appium/3.7.0)); XCUITest needs macOS ✅ ([driver docs](https://appium.github.io/appium-xcuitest-driver/latest/getting-started/installation/)) | Evidgate never pins "latest" inside runners |

Contract-first keeps the two languages honest: `contracts/` (OpenAPI 3.1 + JSON Schema) is the only source of wire shapes; C# DTOs are checked against it by contract tests and the served-document diff, TS types are generated from it.

Licence rule [DECISION]: dependencies must allow closed-source commercial SaaS. No AGPL/SSPL/BUSL/RPL, paid-maintenance EULAs or non-commercial licences in shipped code or hosted services; LGPL needs an ADR. The rule is enforced for **both** NuGet and npm (01.9.1). Dev-only tools are checked case by case in plan 01.

## 5. Safe-healing rules (locked)

1. Healing may change **test mechanics** only: locator, wait condition (proposal only), infra retry within policy. It never changes intent: assertions, expected values, API schema/status, unit expectations, snapshot baselines.
2. Forbidden in any automated patch: removing or weakening an assertion, `skip`/`fixme`, `force: true` clicks, catch-and-ignore, mocking a business result, disabling a network check, unbounded retries, changing test id / build / policy / data to get green.
3. The original failure and its evidence are never deleted or overwritten; shadow results never replace an official run.
4. Keeping the assertion hash equal is **necessary, not sufficient**: the validator also checks semantic identity (role/name/test id), unique match, business region and an independent post-condition, plus a negative control that must still fail.
5. Sensitive cases (payment, authorization, deletion, security) are proposal-only and need a reviewer who is not the author.
6. Approval binds to `candidate_hash` + base commit; any context change or rebase re-validates.
7. Runtime auto-apply is **off in V1 and V2**, and **off in phases G1–G3** (CR-D-2). Reuse of *approved* mappings in a narrow context is considered at G4 at the earliest, after an independent evaluation (plan 08 gate, O6).
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
| O1 | Pilot customers, their CI and engines (assumed GitHub Actions + Playwright TS + one JUnit producer). **Engine decided 2026-09-26 [DECISION] (owner):** UI tests run on **Playwright** (TypeScript) — Evidgate's own product choice, not a pilot input. Which CI and which extra producers a customer uses is customer input, configured per project; the V1 adapter set of 00 §1.3 is unchanged. **Decided 2026-10-02 [DECISION] (owner): no customer interviews are required**; the pilot profile and acceptance sheet are defined by the Test Lead view in [docs/decisions/O1-pilot-profile.md](docs/decisions/O1-pilot-profile.md) (pilot zero = Evidgate's own pipeline, pilot one = a friendly external team recruited after the demo slice). Consequence recorded: the M0 gate has no customer evidence; the first external team is a 4-week decision point. Still open: the names | M1 start (engine: closed; interviews waived) | 12.1 |
| O2 | Team size and who is Tech Lead / QA lead (estimates assume one senior full-stack .NET + Angular developer who can also maintain the TypeScript customer-side packages; calendar for 2–3). Owner note 2026-09-26, verbatim: "Cái này sẽ do họ config nhé, này là thông tin input từ khách hàng". `[INFERENCE]` reading, not yet confirmed: a project's QA lead / release approver is customer input, set in the customer's tenant through the 00 §2 roles; Evidgate's own team size and Tech Lead remain open. **Decided 2026-10-02 [DECISION] (owner, chat): the team is one person (the owner) — one developer who is also Tech Lead and QA/product owner.** The 00 §14 unit (one senior full-stack developer) now equals the whole team; **Review model decided 2026-10-02 [DECISION] (owner):** reviews may be done by other people or by AI sessions, provided the story has a standard plan and spec; the reviewer works from the spec package in `plans/features/SOLO-PATH.md` §3, and the author never closes a review thread alone. The multi-lane plan (`plans/features/README.md` §5.5) is replaced for scheduling by the staged path in `plans/features/SOLO-PATH.md` | M1 start | 00 §14 |
| O3 | Cloud provider/region for PostgreSQL + object storage (VN residency?). **Deferred 2026-10-02 [DECISION] (owner): decided later, "phase 2"** — read `[INFERENCE]` as after the Stage B decision point and before any real customer data (`plans/features/SOLO-PATH.md`). Until then dev/staging run on the compose stack, and 10.2.2 deploy and the 10.5 restore drill are not started | before any real customer data (was M2) | 10, 11 |
| O4 | LLM provider(s) and whether any tenant data may leave Vietnam. Owner note 2026-09-26, verbatim: "bỏ qua phần dùng claude, mình sẽ tự build hệ thống riêng của mình nhé". `[INFERENCE]` reading, not yet confirmed: no third-party hosted LLM provider for healing. **Superseded by the owner's decision of 2026-10-02 [DECISION] (chat): Evidgate provides AI credit that tenants consume, and the API (gateway, provider key, limits) is controlled on Evidgate's side** — i.e. the 00 §5.1 LLM gateway with an Evidgate-held provider key, not a tenant-supplied key and not a self-trained model. Consequences still to specify in a CR: per-tenant opt-in signed by the data owner, redaction at source, credit balance and metering as a usage-ledger metric (12.3.1), per-tenant and global spend caps, kill switch, provider chosen by eval on the locator-break corpus, contractual no-retention term, and a PDPL review of the cross-border transfer. V1 healing stays rule-based; model use starts at V2. Plan change made 2026-10-02: story 08.5.5 (AI credits and spend control, 5.5 d) and its feature plan | V2 healing PR (M4); CR before 08.5 DoR | 08, 11, 12 |
| O5 | Pricing and pilot contract (onboarding fee, project/concurrency subscription) | M3 pilot | 12 |
| O6 | False-heal acceptance threshold per severity (V3 gate; the V2 gate is "every forbidden case blocked", 08 FR-08-19) | before the V3 gate (08.8.3-T2, M5) | 08 |
| O7 | Trademark filing for "Evidgate" | before public launch | 12 |
| O8 | Scope vs calendar: buffered V1 = 499.8 person-days ≈ 50 weeks for 2 engineers / ≈ 33 for 3 (519.9 ≈ 52.0 / 34.7 weeks after CR-D-1, the TL review and feature detailing, 00 §14; the decision below is unchanged), against the source's 12 weeks to the M3 pilot. **Decided 2026-09-25 [DECISION] (owner): option (b)** — scope is kept; the 00 §4 weeks are sequence only and a milestone ends when its exit gate passes, not on a date (source §1: "Mọi mốc trong tài liệu là thứ tự ưu tiên"). Why not (a) alone [INFERENCE] (script over the task rows, dependency closure included): deferring UI polish, templates, reprocess, outbound webhooks, policy shadow compare, quota enforcement and trademark filing removes 27.5 raw days (V1 466.8 buffered ≈ 46.7 / 31.1 weeks); also deferring V1 shadow healing (08.1.2–08.4.3, 09.5.1) removes 80 (V1 403.8 ≈ 40.4 / 26.9 weeks) — neither reaches 12 weeks. Dates are set only after O2 (team size) | decided | 00 §4, §14 |
| O9 | Which cases are critical and their business intent (source decision row "Case critical và ý định nghiệp vụ": customer QA lead + product owner). Mechanism [DECISION]: the critical-case list is a field of the policy revision (00 §8.6), so it is versioned, hashed and published by a `release_approver`; which cases go on it is the customer's decision. Until it is set, the project stays advisory | before a project's policy moves from advisory to gating | 07, 12.2 |
| O10 | **Decided 2026-10-02 [DECISION] (owner): option (a)** — customer CI job driven by the CLI (story 04.9.1). V1 shadow-healing replay host. 05 has no V1 executor (runner jobs 05.7 are V2) and A5 forbids a hosted runner in V1–V2. Options: (a) customer CI job driven by the CLI, (b) move 05.7 runner + 11.2.1-T4 sandbox into V1 (A5 change request), (c) slip sandbox replay to V2/M4. Current estimates carry only 08.4.3-T1..T3 + 11.2.1-T6; option (a) adds a customer-side CLI replay task in 04, option (b) moves V2 rows into V1. Recommended [INFERENCE]: (a) — keeps A5, no hosted execution of customer code in V1, and still measures real replay quality in the pilot; (b) adds ≈ 12 raw days for 05.7.1/05.7.2/05.7.4 + 11.2.1-T4 (≈ 20 if all of 05.7 moves) | before the 08.4.3 feature plan (M3) | 05, 08, 11 |
| O11 | `[OPEN]` GitHub App name, the GitHub App key custodian (role defined in [ADR-0007](docs/adr/0007-single-github-app.md) / [`docs/runbooks/github-app-key-custody.md`](docs/runbooks/github-app-key-custody.md)), and the second org owner / recovery contact for the App-owning organization; recommendation `[INFERENCE]`: **a single App per environment class (production + non-production), not one App per feature** — the production App is public (installable by any account; no Marketplace listing required) and the non-production App is private, owned by ONE Evidgate-owned test organization and installable only on that account (dev, staging and CI share that one installation account) ✅ [Making a GitHub App public or private](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/making-a-github-app-public-or-private) ("can only be installed on the account that owns the app" for private); custodian = the owner (D-A4 precedent, O2 one-person team); see [`plans/open-items/01-core-identity.md`](plans/open-items/01-core-identity.md) OI-02-14 | before 03.4.1-T1 registers the App and before `github_app_enabled` is on in any environment | 02 (02.4.3), 10 (10.7.1-T4) |
| O12 | `[OPEN]` GitHub App install-binding ownership check: whether the install callback binds an installation only when the OAuth user both sees it in `GET /user/installations` (paginated) and administers the installation account (User account: `account.id` equals the id from `GET /user`; Organization account: `GET /user/memberships/orgs/{org}` returns `state: active`, `role: admin`), otherwise 403 `installation_not_owned`; recommendation `[INFERENCE]`: adopt the two-part check ([ADR-0007](docs/adr/0007-single-github-app.md) §Install binding) | before 02.4.3-T2 implements the install callback | 02 (02.4.3) |

### 8.1 Stage A defaults [DECISION] (owner, 2026-10-02)

The owner accepted the recommended defaults of the eleven Stage A owner-decision rows of `plans/open-items/README.md` §2 in chat on 2026-10-02. Numbers are owner-accepted defaults, not verified facts; D-A8 and D-A9 are revisited after the 10.6.1 load test. The contract edits they imply (00 §7/§8.6/§9) are made at each story's DoR and logged in 00 §15.

| # | Register row | Decision | Unblocks |
|---|---|---|---|
| D-A1 | OI-02-01 | Slug grammar `^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$` (2–63 chars, no leading or trailing hyphen), in the validator and in a new migration replacing the 0004/0005 slug checks | 02.1.1-T2 |
| D-A2 | OI-10-04 | Secrets reach the process by environment injection through the one secret-provider interface (10.1.1-T2); each is `secret: true` with a versioned name in `infra/config-matrix.json`, generated per environment, never committed. Managed store after O3 | 02.4.1-T3, 02.4.2-T1 |
| D-A3 | OI-02-02 | `ci_producer` token TTL 60 min (02 D7), no refresh endpoint; long jobs exchange a fresh GitHub OIDC token | 02.4.1-T3 |
| D-A4 | OI-02-03 | Ingest-token pepper lives in the secret store (D-A2), the owner is custodian, versioned `pepper_vN`, rotated per 10.7.1-T4 | 02.4.2-T1 |
| D-A5 | OI-02-04 | Ingest-token rotation overlap window 24 h | 02.4.2-T1 |
| D-A6 | OI-03-02 | Keep `requirement_ref`: optional, ≤ 128 chars `[A-Za-z0-9._:/#-]`, metadata only, never gating, sidecar key `requirement` (annotation optional); no requirement table in V1–V2 | 03.1.1-T1, 03.1.4-T1 |
| D-A7 | OI-03-03 | The 03.1.4 taxonomy lists and defaults are a closed V1 catalogue in `contracts/schemas/taxonomy.v1.json`; the columns land in 03.2.1-T1 so the suite revision hash never changes later | 03.2.1-T1, 03.1.4-T2 |
| D-A8 | OI-04-04 | Ingest gzip: expanded size ≤ 8 MiB per batch and ratio ≤ 20:1, streaming abort → 413 `batch_too_large`; relax only after the 10.6.1 load test | 04.1.1-T3 |
| D-A9 | OI-06-03 | Per-kind artifact caps: screenshot 10 MiB, log 20 MiB, trace 100 MiB, html_report 100 MiB, video 200 MiB, raw_report 256 MiB (configurable down, never up without a decision) | 06.1.2-T3 |
| D-A10 | OI-05-01 | One policy per project with a `gating_refs` rule: runs on the default branch (and an optional release-branch glob) gate; runs on other refs are evaluated and reported as advisory | 07.1.1-T1 |
| D-A11 | OI-05-02 | A run with `unverified_ref = true` or from a fork is recorded with `advisory = true` and never gates; no override in V1 | 07.2.1-T1 |

## 9. Delta from the source design (review findings)

| # | Source says | This plan set says | Why |
|---|---|---|---|
| R1 | Name "ReleaseHub" | **Evidgate** | §2 collision |
| R2 | "NestJS trên Node.js LTS", "SQL migration", "PostgreSQL và outbox", React | Owner changed the stack on 2026-09-25 (CR-D-1): .NET 10 LTS + CQRS server, Angular 22 web, PostgreSQL 18, plain SQL migrations + outbox kept; customer-side packages on Node/TS. Exact lines and libraries in §4 | Source leaves versions open; the owner's team direction wins over the source's NestJS suggestion; the source's invariants (SQL migrations, outbox, RLS) carry over unchanged |
| R3 | Healing pipeline built from scratch; no mention of Playwright's healer | Playwright's healer (F1) may be used **only as a candidate generator inside the sandbox**; its output goes through the same AST validator; `skip` output is rejected by rule 5.2 | Reuse engine features (source principle "tái dùng engine") without breaking the no-skip rule |
| R4 | "Một CI phổ biến" | GitHub Actions first, authenticated by **GitHub OIDC → short-lived Evidgate ingest token** (F3); a hashed project ingest token for other CIs | Removes long-lived secrets from customer CI; confirm in M0 (O1) |
| R5 | Human login not specified | OIDC login (GitHub, Google Workspace); no password storage in V1; enterprise SAML later | Smallest secure surface for a B2B pilot |
| R6 | Data residency only "a deployment option" | PDPL facts (F7, F8) drive an explicit plan 11 epic: DPA, redaction, provider opt-in, transfer assessment | Artifacts contain personal data; fines are revenue-based |
| R7 | Milestones only (M0–M5), no task sizes | Stories + tasks with sizes and V1/V2/V3 tags; roll-up in 00 §14 | Needed to plan staffing |
| R8 | Section numbers repeat (five "§4", four "§6") and a page map for the .docx | Plans 01–12 by area; the page map is dropped | Unique ids for cross-references |
| R9 | Background worker pattern implied | Two mechanisms, stated: own outbox + `task` queue table for internal tasks (CR-D-1; was pg-boss), domain lease tables for runner jobs; both use `FOR UPDATE SKIP LOCKED` | Epoch fencing and capability matching are product logic |
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
| 2026-09-25 | **CR-D-1 (owner): stack change.** §4 rewritten: server/worker on .NET 10 LTS + ASP.NET Core minimal APIs + CQRS (`Mediator` 3.0.2, EF Core 10 write model, Dapper read model), DbUp plain-SQL migrations, own outbox + task queue (pg-boss removed), web on Angular 22.2 + `@ngrx/signals`; customer-side CLI/adapters/agent stay Node 26/TS 6.0.x. Rejected on the licence rule: MediatR 14 (RPL-1.5), JsonSchema.Net 9 (maintenance-fee EULA), FluentAssertions 8 (non-commercial); Hangfire (LGPL) avoided. Estimate unit becomes one senior full-stack .NET + Angular developer. Contract edits in 00 §5.1–§5.5, §6, §11, §12, §15; plan edits in 01–12. |
| 2026-09-26 | O1 engine decided by the owner: Playwright for UI tests; customer CI/producers are per-project customer input. Owner notes recorded verbatim on O2 and O4 with `[INFERENCE]` readings; both rows stay open until the owner confirms the reading. |
| 2026-10-02 | §8.1 Stage A defaults D-A1…D-A11 recorded (owner accepted the open-items register defaults). |
| 2026-10-23 | **CR-D-2 (owner): direction (a).** Test authoring and self-learning maintenance on the evidence foundation; phases G1 Author → G2 Run → G3 Learn and repair → G4 Expand with exit gates; A4 timing changed (proposals and approved PRs from G3 after a per-project shadow window); §5.7 runtime auto-apply stays off in G1–G3; A5 kept for G1–G3; §5 rules 1–6, 8, 9 unchanged; §1 rewritten. Master plan [`plans/13-product-phases.md`](plans/13-product-phases.md); the Milestone column of plans 01–12 now holds phases G1–G4 (00 §4, §15). Mobile: G2 spike with mobile-mcp / mobilewright (iOS + Android), chosen over Google ARTEMIS; mobile product work at G4. Record, risk register and edit list: [`docs/decisions/CR-D-2-direction-a.md`](docs/decisions/CR-D-2-direction-a.md). |
| 2026-10-23 | **CR-D-2 follow-up (owner): AI credit pricing rule.** Credits are priced at no less than 3× the model cost per action measured in the G1 free trial, reviewed monthly; the amounts stay `[OPEN]` until the G2 offer (12.4.1-T4). Recorded in [CR-D-2 §6.2](docs/decisions/CR-D-2-direction-a.md), plan 08 story 08.5.5, plan 12 D10 and plan 13 Q2. |
