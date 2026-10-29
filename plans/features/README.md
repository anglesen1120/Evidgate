# Feature plans — Tech Lead review, rulings, execution order and index

> Authority: `DECISIONS.md` and `plans/00-overview.md` win; this file's §3 rulings (T-rows) were written into 00 on 2026-09-25 (00 §15) and **override older text in `plans/01`–`13` and in any feature plan**. A feature plan refines its area plan and never silently contradicts it.
> Legend: ✅ verified · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`.
> Review evidence (all findings with file:line quotes and the ruling applied): `REVIEW-2026-09-25.md` in this folder. Code-review fix register for `develop` @ `98738803` (prioritised P0/P1/P2, owner 2026-10-29): `REVIEW-2026-10-29.md`.

## 1. Status

| Item | State (2026-09-25) |
|---|---|
| Stack | Changed by the owner (DECISIONS CR-D-1): .NET 10 LTS + CQRS server/worker, Angular 22 web, PostgreSQL 18; customer-side CLI/adapters/agent stay Node/TypeScript |
| High-level plans 01–12 | Reviewed (§2), migrated to the new stack and patched with the §3 rulings |
| Feature plans | One file per story, `plans/features/<NN>-<area>/<story-id>-<slug>.md`: 201 files for 201 of the 205 stories in §7; the other four (13.7.1, 13.7.2, 13.8.2, 13.9.1) are the "(feature plan pending)" rows, re-verified in S139 (2026-10-28) against `ls plans/features/*/` |
| Definition of Ready | Checked per feature plan at the start of its build (§6); open owner decisions still gate the stories named in §5.3 |

## 2. Tech Lead verdict on the high-level plans

Method: four parallel reviewers (01–03, 04–06, 07–08, 09–12) read every plan against 00, DECISIONS and the new stack, looking for contract drift, gaps, wrong dependencies, untestable acceptance, **performance** of hot paths and **security**. 82 findings (3 B, 21 H, 42 M, 16 L); every one is in `REVIEW-2026-09-25.md` with its ruling.

| Plan | Verdict before fixes | Most important findings |
|---|---|---|
| 01 Foundation | Sound design, stack-bound | pg-boss/Kysely/Ajv/npm-only toolchain; no CQRS pipeline task; one validator "shared" across two runtimes is impossible |
| 02 Identity & tenancy | Strong; three real defects | `current_setting` without `missing_ok` breaks fail-closed RLS; unauthenticated entry points cannot resolve the tenant under RLS; GitHub login is OAuth2, not OIDC; `unique(lower(email))` contradicts "never auto-merge by email"; audit hash chain forks under concurrent writers |
| 03 Catalog & authoring | Good scope; schema gaps | missing `unique (org_id, id)` targets; `case_dependency` references non-existent columns; webhook receiver has no pre-tenant path |
| 04 Ingestion & adapters | Invariants right; one evidence-integrity hole | producer ids not bound to the token → a leaked token can forge missing shards; unknown `execution_key` behaviour unspecified; ledger rows per event on the hot path |
| 05 Runs & orchestration | Good; contradictions with 04 | permanent idempotency unique vs 24 h contract; post-cancel rules contradict 04; `setup_error` not in the contract |
| 06 Evidence & artifacts | Good; three scope issues | tenant deletion under `/v1`; attempt-scoped upload before attempts exist; presigned PUT size unbounded; synchronous secret scan on the hot path |
| 07 Policy & decisions | Core logic right; two integrity gaps | shadow evaluations collide with live rows; `test_maintainer` can quarantine a critical case into PASS; evaluator not pure over waivers/quarantine |
| 08 Healing & learning | Rules right; ordering violates DECISIONS §5.9 | approve/PR path can ship before the kill switch; LLM cost not attributable per project; quota race; validator runtime (TS AST) undefined on .NET |
| 09 Web UI | Must be rewritten for Angular | React/Vite everywhere; no CSRF control for cookie sessions; app-origin CSP missing; performance budget undefined |
| 10 Operations | Mechanism-agnostic, image/migration tasks stale | Node images; migration runner; no .NET runtime metrics (GC, thread pool, Npgsql pool) |
| 11 Security & compliance | Good; supply chain npm-only (B) | NuGet lock/vulnerability/licence scanning missing; CSRF; token hashing re-decision |
| 12 Pilot & commercial | Stack-neutral; ledger modelling issues | `billing_key` contradicts itself; per-pilot prices unrepresentable; `quota_counter` lacks tenant columns |

Verdict [DECISION]: the plan set is **fit to build after the §3 rulings are applied**; no area needs re-scoping. Stack migration + rulings changed the estimate from 487 tasks / 543.5 raw / 652.2 buffered to **502 tasks / 560.25 raw / 672.3 buffered** (+15 tasks, +16.75 raw; 00 §14), including the tasks added while detailing the feature plans (06.3.2-T2, 06.4.4-T3, 05.2.1-T5, 07.2.2-T4, 08.5.3-T3 M→L, 08.6.3-T1 moved to V1). Buffered V1 is now 519.9 person-days. **Known estimate risk [OPEN]:** the security and data-correctness controls of the final review (T70–T100) were placed inside existing task sizes; they must be re-estimated at each story's DoR, and 672.3 buffered days should be read as a lower bound until then. **Updated 2026-10-26 (S92):** the plan graph now holds **655 tasks / 702.75 raw / 843.3 buffered person-days** (702.75 × 1.2, `scripts/plan_graph.py`), after the CR-D-2 G1 authoring story 13.2.2 and the S52/S92 additions were rolled into plan 00 §14; the 502 / 560.25 / 672.3 figures above stay as the 2026-10-02 verdict's record, and **843.3 buffered days** is the lower bound the T70–T100 re-estimate must be applied to.

## 3. TL rulings (binding)

Numbered T-rows; the text below is the contract. "→ 00" means the rule is also written into 00.

### 3.1 Data access, tenancy and identity

| # | Ruling |
|---|---|
| T1 | **Pre-tenant resolution** (→ 00 §5.4.8): login callback, `POST /v1/ci/token`, ingest-token auth and the GitHub webhook resolve the tenant through owner-owned `SECURITY DEFINER` functions in schema `evidgate_resolve` that accept one lookup key and return only `(org_id, project_id, binding_id, status)`; the app role only has `EXECUTE`; every call is audited; then the normal tenant scope opens. Owner task: 02.6.1 (functions for 02) and 03.4.1 (webhook resolver) |
| T2 | RLS helpers `evidgate.current_org_id()`/`current_project_id()` use `current_setting(…, true)` + `NULLIF(…, '')::uuid`; every policy uses the helpers, never a raw `current_setting` (→ 00 §5.4.7) |
| T3 | Every business table declares `unique (org_id, id)` (→ 00 §5.4.6); the 02.6.1-T2 lint fails a table without it. Platform/catalogue exceptions are listed once in 02 §4 (`user`, `user_identity`, `session`, `permission`, built-in `role`/`role_permission`, built-in `template`, `organization`, `report_parser` registry, and 01's `schema_migration` and `task` queue tables) |
| T4 | `organization` is on the platform exception list; its policy is `id = evidgate.current_org_id()`; "which orgs am I in" goes through `evidgate_resolve.member_orgs(user_id)` |
| T5 | Token hashing re-decided [DECISION]: ingest/API tokens are ≥ 128-bit random with a public prefix; stored as `HMAC-SHA256(pepper_vN, secret)` + `pepper_version`; lookup by prefix then constant-time compare (`CryptographicOperations.FixedTimeEquals`). Pepper lives in the secret store; rotation = new version for new tokens, old versions verify until their last token expires (runbook 10.7.1). scrypt is dropped: a slow password KDF adds no security for ≥ 128-bit random secrets, while it costs CPU on every ingest authentication (hot path, 00 §5.7 P1); third-party KDF packages exist (e.g. `Konscious.Security.Cryptography.Argon2` MIT ✅) but are not needed. Recorded as the re-decision of 02 D6 |
| T6 | GitHub human login is **OAuth2 auth-code + PKCE** (GitHub returns no ID token for user login ✅ reviewer citing docs.github.com "Authorizing OAuth apps"); identity = `(github, numeric user id)` from `GET /user`, verified email from `GET /user/emails`. Google Workspace stays OIDC with JWKS verification. `user.email` is not unique (plain index); identities key on `(provider, subject)` |
| T7 | `ci_token_issue.jti` is kept for the OIDC token lifetime + 10 min skew; a daily task purges older rows |
| T8 | Audit hash chain per org is serialised with `pg_advisory_xact_lock(hashtext('audit:' ‖ org_id))` taken as the **last** statement before commit, plus a per-org `seq bigint`; chain verification walks `seq`. Measured in the 10 load test |
| T11 | `idempotency_record (org_id, project_id, operation, key, request_hash, response_status, response_body, expires_at)` owned by 01 is the only arbiter (→ 00 §7); no permanent unique on the key in business tables. Records live ≥ 24 h, purged by task (owner-owned `SECURITY DEFINER` purge function, since the app role only sees its own tenant). Terminal `task` rows (`done`, `dead_letter` after replay window) are kept 30 days, then swept by a schedule |

### 3.2 Ingestion, runs, evidence

| # | Ruling |
|---|---|
| T9 | **Producer-slot binding** (→ 00 §9.4): run create writes one `producer_slot` per manifest shard; the first accepted write binds the slot to the token identity (`jti`, plus GitHub `run_id`/`run_attempt` claims when present, which must match the run's CI reference); other identities get 403 `producer_slot_taken`, unknown producers 422 `unknown_producer`; only the bound identity may `finalize` the slot. Residual risk (a leaked token racing for an unclaimed slot) is recorded in 11 §4 |
| T10 | Unknown `execution_key` rejects the batch: 422 `unknown_execution_key` with indices (→ 00 §9.4) |
| T12 | After cancel: accept-and-mark (`after_cancel = true`), never completes the run (→ 00 §8.1). 05's "refuse new attempts" rule is removed |
| T13 | CI mode: `created → queued` when producer slots are committed, `queued → running` at the first accepted event (→ 00 §8.1) |
| T14 | `setup_error` is a completeness reason (→ 00 §8.5) |
| T15 | `release_run` is the only run↔release binding (`run.release_id` removed); binding at run create when the release is named, or an explicit bind before the first live evaluation |
| T16 | Tenant deletion is `POST /internal/operator/orgs/{o}/deletion` on the operator ingress (→ 00 §9.1) |
| T17 | `upload-url` validates run scope + the caller's bound producer slot + a planned `(execution_key, attempt_no)` from the manifest; the link to `case_attempt` is resolved at evidence freeze; unresolved ⇒ orphan, reported as `artifact_missing` |
| T18 | Presigned PUT signs `Content-Length` and the checksum header (⚠️ parity checked on SeaweedFS and the provider in 01.8.1-T1 / 10.6.1-T2); the `pending/` prefix has a 24 h lifecycle expiry; `complete` re-checks size |
| T19 | Secret scanning is an async task after `complete`: `ready` is not delayed; `scan_state ∈ pending|clean|flagged`; while `pending` the artifact is served as `sensitive` |
| T20 | Artifact kinds + `a11y_snapshot`, `patch`, `ui_hierarchy` (→ 00 §7); device video is `video` |
| T21 | Permissions `project:read` (all member roles), `project:write` (`tenant_admin`) (→ 00 §2) |
| T50 | Event batch insert is one statement: `INSERT … SELECT FROM unnest(@arrays) ON CONFLICT (org_id, run_id, event_id) DO NOTHING RETURNING event_id`, then one select of the conflicting ids to compare payload hashes (duplicate vs 409). Usage is metered **once per batch per metric** (quantity = count), not per event |

### 3.3 Policy, decisions, healing

| # | Ruling |
|---|---|
| T22 | Evaluation unique key `(org_id, run_id, evidence_revision, policy_revision_id, evaluator_version, mode)` + partial unique: one `live` row per `(org_id, run_id, evidence_revision)` — the run pins one policy revision, any other policy is `shadow` (→ 00 §8.5) |
| T23 | A case on the pinned policy's critical list cannot be quarantined: 422 `critical_case_not_quarantinable` (→ 00 §8.6) |
| T24 | The evaluator's inputs are (evidence revision, policy revision, exceptions snapshot); the snapshot = active waiver and quarantine ids resolved at trigger time + `exceptions_as_of`; stored as `exceptions_hash` on the evaluation (→ 00 §8.5). The evaluator stays a pure function |
| T25 | `policy_revision` unique `(org_id, project_id, hash)` |
| T26 | No `/v1` re-evaluation endpoint: late evidence triggers re-evaluation by outbox task; manual re-evaluation is an operator route (→ 00 §9.1) |
| T27 | Non-critical retry fail → pass beyond the flaky budget ⇒ `INCONCLUSIVE`, reason `flaky_budget_exceeded` (→ 00 §8.6) |
| T28 | Plan 07 adds a decision-latency task: histogram `evidence_frozen_at → evaluation.created_at`, asserted p95 < 60 s on the 10 load-test fixture |
| T29 | Kill switch before apply (DECISIONS §5.9): 08.6.3-T1 no longer depends on 08.6.1-T1; 08.6.1-T1 depends on 08.6.3-T1; the approve route ships behind a flag that defaults off |
| T30 | `healing_llm_call` adds `project_id`, `incident_id`, `candidate_id` |
| T31 | Incidents are created at evidence freeze with `context_state = pending`; a task at collection deadline + late window settles `complete | partial`; generation needs the context artifacts it uses to be `ready` (→ 00 §8.7) |
| T32 | Healing quota is consumed atomically: `UPDATE healing_quota SET used = used + 1 WHERE … AND used < cap RETURNING` inside the candidate-mint transaction |
| T33 | Settable switch modes `enabled | shadow | disabled`; `unavailable` is derived (→ 00 §8.7). 08.6.3-T1 (V1/M3) owns `healing_switch` and its commands; in V1 only `shadow` and `disabled` are settable — `enabled` returns 422 `mode_not_available` until the V2 apply path (08.6.1) is switched on; 08.3.3 only reads the resolved mode |
| T34 | One incident per `(org_id, run_id, execution_key)`; retries attach as `incident_attempt` (→ 00 §8.7) |
| T35 | For sensitive cases the approver must differ from (a) the member who triggered or edited the candidate and (b) the member whose human label made the incident `test_drift` |
| T36 | The AST validator and the Playwright healer are TypeScript: `packages/healing-validator` (Node 26, TypeScript compiler API, TS 6.0.x) runs as a child process of `Evidgate.Worker` over JSON stdin/stdout with CPU, memory and wall-time limits; the worker image carries a pinned Node runtime; the API process never runs it |

### 3.4 Web, operations, security, commercial

| # | Ruling |
|---|---|
| T37 | i18n: `@angular/localize` per-locale builds at `/vi/` and `/en/`; switching locale navigates to the other prefix; preference stored on the user profile |
| T38 | CSRF (→ 00 §9.1): `__Host-` session cookie `HttpOnly; Secure; SameSite=Lax`; ASP.NET Core antiforgery issues `XSRF-TOKEN`, Angular `withXsrfConfiguration` sends `X-XSRF-TOKEN`; the "in-memory token + silent refresh" option is dropped |
| T39 | Web performance budgets are 00 §5.6 W7; plan 10 records web-vitals SLIs (LCP, INP) from 09's telemetry |
| T40 | Supply chain covers NuGet and npm: `packages.lock.json` + `dotnet restore --locked-mode`, `dotnet list package --vulnerable --include-transitive`, NuGet licence scan (tool chosen and licence-checked in 01.7.1-T2 ⚠️, recorded in an ADR by 01.9.1-T2), CycloneDX SBOM for both ecosystems ⚠️ tool licence checked in 11.6.1, CodeQL for C# and TypeScript |
| T41 | Images: `dotnet publish` on a pinned `mcr.microsoft.com/dotnet/sdk:10.0` builder; runtime on a digest-pinned `aspnet:10.0` chiseled (non-root) image ⚠️ tag verified in 10.2.1; `Evidgate.Migrator` is its own image; `apps/web` is a static bundle on object storage + CDN |
| T42 | Validator parity across runtimes: server (Corvus) and CLI (Ajv 8) run the same `fixtures/contracts/` accept/reject corpus in CI; any disagreement fails both builds |
| T43 | `billing_key` is unique per source metering event `(org_id, billing_key)`; per-period sums live in the roll-up table; ingest emits `batch:<event_batch_id>:<metric>` keys (T50) |
| T44 | `usage_price` gets nullable `org_id`: global rate card rows + per-org overrides; resolution picks the override first |
| T45 | `quota_counter` has `org_id`, `project_id`, `period` |
| T46 | Plan 03: `suite_member` gets a surrogate `id` + `unique (org_id, id)`, `case_dependency` references it; flag name is `allow_unverified`; suite/fixture revision states are in 00 §8.10; built-in `template` rows use the catalogue policy `org_id IS NULL OR org_id = evidgate.current_org_id()`; the GitHub App uses a typed `HttpClient` + RS256 app JWT via `Microsoft.IdentityModel.JsonWebTokens` (no Octokit dependency) |
| T47 | Module boundaries are enforced by project references + `Architecture.Tests` (00 §5.5), not ESLint, for server code; ESLint stays for `apps/web` and `packages/*` |
| T48 | Dogfooding: server tests emit JUnit XML (xUnit v3 JUnit report ⚠️ format checked in 01.10.1), web/packages tests emit Vitest JUnit; both go through Evidgate's own JUnit mapper |
| T49 | Plan 01 adds the CQRS pipeline story tasks (Mediator behaviours C2, tenant scope C3, architecture tests) |
| T51 | Every operation in `contracts/openapi.yaml` carries its permission as the `x-permission` extension (e.g. `x-permission: run:create`); `Architecture.Tests` asserts that the handler's declared permission equals it and that every `/v1` operation has one (01.3.1, 01.3.2, 01.4.2) |
| T52 | A schema-invalid event batch returns 422 `event_invalid` with per-event indices (parallel to `manifest_invalid`); the full error-code list lives in `contracts/openapi.yaml` and is the only source (04.1.1, 01.3.1) |
| T53 | Quota checks on hot paths go through `Evidgate.Modules.Usage.Contracts.IQuotaGate` (owner 12.3.1): one indexed read of `quota_counter` per batch, never per event; exceeded → 429 `quota_exceeded` with `Retry-After` (04.1.2, 12.3.1) |
| T54 | Producer ids are deterministic and declared by the manifest: `shard-<index>` (1-based, from the CI shard index; `shard-1` when unsharded); the reporter derives the same id, so run create (05) registers exactly the slots the reporter will write (T9; 04.3.3, 05.1.1) |
| T55 | Sidecar manifest = `evidgate.cases.yaml` **next to the test files it describes** (one per directory; `{version: 1, cases: [{file, evidgate.id}]}`), per RECOMMENDATIONS 03-Q1: per-directory files avoid merge conflicts of a single root file and are reviewable in PRs. Parsed with YamlDotNet 18.1.0 (MIT ✅ NuGet nuspec 2026-09-25) into JSON, then validated by Corvus against `contracts/catalog/sidecar.schema.json` (03.1.1) |
| T56 | Reading fixtures reuses `suite:read` (a fixture is always read in a suite's context; no new permission string). Additive read endpoints that feature plans introduce (`GET /v1/suites/{s}`, `GET /v1/fixtures/{f}`, `GET /v1/projects/{p}/case-import-mappings`, `GET /v1/projects/{p}/catalog-syncs`, `GET /v1/drafts/{d}/dry-runs`) are accepted refinements; they enter `contracts/openapi.yaml` with the story that builds them (03) |
| T57 | (a) Playwright traces and DOM/a11y snapshots are `sensitive` by default (RECOMMENDATIONS 06-O4: they carry request bodies, cookies and page text); a project may lower a kind to `normal` only via `PUT /v1/projects/{p}/artifact-kind-policy/{kind}` (`project:write`, audited). (b) With `reject_on_secret` on, a flagged `ready` artifact moves `ready → pending_delete` (existing legal edge, reason `secret_rejected`); the next evidence revision reports it as `artifact_missing`; no new state is added (06.2.1, 06.2.3) |
| T58 | Waivers and quarantine entries are **readable by every member role including `viewer`** (RECOMMENDATIONS 07-O6; 00 §3: a waiver is displayed next to, never instead of, results — hiding it would make a waived PASS look like a clean PASS). Creating/approving/revoking stays with `release_approver` (waiver, `policy:write`) and `test_maintainer` (quarantine, `quarantine:write`, never for critical cases, T23) (07.3.1) |
| T59 | **Permission catalogue additions** (seeded by 02.2.1 into `contracts/permissions.json`; 00 §2 is the source): `policy:read` (every member role, T58), `policy:write` (`release_approver`), `quarantine:write` (`test_maintainer`; 07.3.1-T2 — quarantine writes never carry waiver or policy-revision power), `case:write` and `draft:write` (`qa_engineer`, `test_maintainer`), `ingest_token:read` (`tenant_admin`; metadata only, never plaintext), `service_identity:read` / `service_identity:write` (`tenant_admin`), `sso:read` / `sso:write` (`tenant_admin`, V3), `support:read` (`tenant_admin`; operators see their own grants), `membership:read` (`tenant_admin`; already in 02 §4 for `GET /v1/orgs/{o}/memberships`), `agent_pool:read` (`tenant_admin`, `test_maintainer`, V2), `agent_pool:write` (`tenant_admin`, V2), `job:cleanup` (`qa_engineer`, `test_maintainer`; confirms a side-effecting cleanup, V2), `webhook:read` / `webhook:write` (`tenant_admin`, T61). **Not added:** `job:read` (use `run:read`), `fixture:read` (use `suite:read`, T56) and `device:claim` (device leases are acquired inside `job:claim`, 05.8.1). A permission string used by any plan and absent from 00 §2 fails DoR |
| T60 | **No fabricated metrics.** A metric whose source does not exist yet is stored and shown as `null` / "unavailable" with a reason, never as 0 (e.g. `storage_usage_daily.egress_bytes` until `IObjectStore` exposes egress, 06.5.2). Same rule for usage, cost and healing-quality numbers (12, 08) |
| T61 | **Outbound webhook endpoints** are owned by 07 (07.6.2): table `webhook_endpoint (id, org_id, project_id, url, secret_ciphertext, event_types[], state)` with `unique (org_id, id)`; CRUD `/v1/projects/{p}/webhook-endpoints` under new permission `webhook:write` / `webhook:read` (`tenant_admin`). SSRF controls (00 §5.7 S4): `https` only, no userinfo, port 443 or an allow-listed port; the host is resolved **at every delivery** and the request is sent to the resolved IP only if it is public (loopback, private RFC 1918, link-local incl. `169.254.169.254`, CGNAT, ULA and multicast ranges blocked) — defeats DNS rebinding; no redirects followed; 5 s connect / 10 s total timeout; response body discarded after 4 KiB. Secret stored encrypted with ASP.NET Core Data Protection, shown once. Signature per 00 §9.5 |
| T62 | GitHub delivery = **check run** first, commit status only as fallback when the App lacks `checks:write` (RECOMMENDATIONS 07-O3); `ci_delivery` and `webhook_delivery` rows are kept as long as the evaluation they deliver (RECOMMENDATIONS 07-O6); late window default = 24 h in the default policy template (RECOMMENDATIONS 07-O5) (07.5.1, 07.6.1, 07.6.2) |
| T63 | Web hosting is settled by T41 (static bundle on object storage + CDN); the CDN/edge config emits the app-origin CSP + Trusted Types headers and is owned by 10.2.1, verified by 09.1.1's E2E header test. Onboarding-wizard progress is **derived** from existing resources (project, CI binding, token, first run, first evidence revision) — no wizard-state table or endpoint. The wizard's "enable collection" step activates the project's policy in `advisory` mode via `POST /v1/projects/{p}/policy-activation` (07.7.1, `release_approver`); no `collect_mode` column (09.2.1) |
| T64 | (a) Provider opt-in (healing context leaving Evidgate, DECISIONS §7, PDPL Art. 9/20) is a two-person act: requested by a `tenant_admin`, approved by a **different** `tenant_admin` acting as the tenant's data-owner signatory; the approval row stores signatory name, role, DPA reference and the transfer-assessment reference; silence or expiry is never consent; revocation takes effect before the next provider call. A tenant with one `tenant_admin` must add a second admin before opting in; the 09.2.1 onboarding and the 08.5.3 opt-in screen say so and link to the invite flow (a data-owner signatory that is not a platform user is not supported in V1–V2). (b) No merge capability in V1–V2: approval never merges; the absence of any merge endpoint is the control (08.5.3, 08.6.1) |
| T65 | Healing evaluation: corpus adjudication and metric reports reuse `healing:approve` (no `healing:evaluate`); knowledge records are revoked via `POST /v1/healing/knowledge/{k}/revoke` (`healing:revoke`, audited, cascades per 08.7.2) — accepted additive endpoint; the first corpus is Evidgate's own CI, then the pilot (RECOMMENDATIONS Q-08-2); V3 narrow-context reuse (08.9.1) still requires human approval — skipping approval would be an explicit DECISIONS §5.7 change the owner has not made (08.7.2, 08.8.1, 08.8.2, 08.9.1) |
| T66 | Run detail: per-producer slot state (bound, last sequence, finalized, `after_cancel` count) is part of the `GET /v1/runs/{r}` payload — one request, bounded SQL (00 §5.7 P3), no `/producers` endpoint. Incidents: assignee travels as optional `assignee_membership_id` on `POST /v1/incidents/{i}/label`; closing uses additive `POST /v1/incidents/{i}/close` (`healing:triage`, reason required, 00 §8.7 `triaged → closed`) (09.3.1, 09.4.1, 08.1.1) |
| T67 | **Run purpose** (→ 00 §8.1): `run.purpose ∈ {regular, replay}`, default `regular`, immutable. `replay` requires a server-issued `replay_grant_id` (08.4.3; bound to project, candidate, expiry) or 422 `replay_grant_invalid`. Replay runs: no incident (08.1.1), evaluation only as `shadow` (07.2.2-T4), never bound to a release or counted as a regression (05.2.1-T5, 07.5.3). Enforces DECISIONS §5.3 "shadow results never replace an official run" structurally, not by convention |
| T68 | Finalize contract 05 ↔ 04: `IProducerRegistry.CloseSlotAsync(slotId, callerIdentity, declaredLastSequence, declaredCount, manifestHash)` is idempotent on `producer_slot.closed_at`, rejects a foreign identity (T9) and stores `declared_last_sequence` + `declared_count` on `producer_slot` (columns owned by 04.1.2-T5); 05's reconciler compares them with observed events; no separate finalize-receipt table (04.1.2, 05.3.2) |
| T69 | Commercial (12): tenant usage reads use the existing `usage:read` (`tenant_admin`, 00 §9.3). New `billing:write` (`billing_operator` only) for price-card loads, ledger adjustments and statement issue, served only on `/internal/operator/billing/*`; adjustments and statement issue are two-person (approver ≠ author, CHECK constraint). The append-only `billing_statement` table is accepted. The pilot value report composes data only through other modules' `.Contracts` read interfaces (00 §5.5), never cross-module SQL (12.2.1–12.5.1) |

### 3.5 Final review rulings (security, performance, consistency — 2026-09-25)

| # | Ruling |
|---|---|
| T70 | **CI binding needs proof of repository control** (final security review EVG-TEN-01, critical): a `ci_binding` is created `pending_verification` with a random challenge shown once; it becomes `active` only when a GitHub OIDC exchange from that repository presents the challenge (set by the repo owner as a repository variable `EVIDGATE_BINDING_CHALLENGE`, which requires write access to the repo). `unique (provider, repository_id) WHERE status = 'active'` platform-wide, so the resolver returns at most one row; a second org's attempt stays pending and is visible to both orgs' audit (02.4.1, 02.6.1) |
| T71 | **Artifact viewer origins** (EVG-VIEW-01): the trusted trace-viewer bundle and its Service Worker are served from `viewer-app` origin; tenant bytes are served only from a separate `viewer-content` origin with `Content-Security-Policy: sandbox; default-src 'none'`, `Content-Disposition: attachment` unless the kind is an allow-listed inline type, a server-chosen `Content-Type` from an allow-list, `X-Content-Type-Options: nosniff`; neither origin carries app cookies (06.3.3, 06.3.4) |
| T72 | The system-scope healing switch row has `org_id = NULL`; RLS `USING (org_id IS NULL OR org_id = evidgate.current_org_id()) WITH CHECK (org_id = evidgate.current_org_id())` — tenants read it, never write it; system writes only via `/internal/operator/healing/switch` (EVG-HEAL-01; 08.6.3) |
| T73 | **Approval binds the merged code** (EVG-HEAL-02): approve stores `approved_head_sha`; a `pull_request.synchronize` to a different head demotes the candidate to `proposed` and posts a failing check run; on `merged` the merged diff of the candidate's files must equal the approved patch (`candidate_hash`), otherwise the candidate becomes `revoked` with reason `merge_mismatch`, an alert fires and nothing is recorded as `applied` (08.6.1, 08.6.2) |
| T74 | **Viewer token** (EVG-TEN-02, EVG-SEC-02): `HMAC-SHA256(viewer_key_vN, canonical(v, org_id, project_id, artifact_id, kind, expires_at))` with a dedicated viewer key (not the ingest pepper), `v` carried in the token; the serving handler verifies it, then opens `ITenantScope.BeginAsync(org_id, project_id)` on the app role and re-reads the artifact under RLS (`ready`, sensitivity, scan state). Transport of the token: see T99 (no cookie); `Referrer-Policy: no-referrer`. Listed as a pre-tenant entry in 00 §5.4.8 (06.3.3) |
| T75 | **Invites** (EVG-TEN-03, EVG-AUTH-01, EVG-AUTH-03): activation requires the single-use invite secret (resolved by `evidgate_resolve.invite_by_secret(prefix)` → `(org_id, membership_id)`) **and** a provider-verified email equal to `invited_email`. Email-only auto-binding exists only as a per-org opt-in for Google Workspace domains the org has verified. A SAML assertion binds invites only in the SAML connection's own org and only for its DNS-verified domains (02.1.2, 02.3.3, 02.6.1) |
| T76 | **Operator access in the database** (EVG-AUTHZ-01): `evidgate_operator` gets its own RLS policies (`TO evidgate_operator`) calling owner function `evidgate.operator_grant_ok(org_id, project_id, action)` that validates `current_setting('evidgate.support_grant_id', true)` against an approved, unexpired grant for that operator; read-only unless the grant scope names a write action (02.8.1) |
| T77 | **V1 replay host residual risk** (EVG-HEAL-03): replay in customer CI (O10 a) runs untrusted-context patches next to CI secrets; mitigations are a reference workflow (`permissions: {}`, no `secrets:`, `workflow_dispatch` on an environment without secrets, network limited to the app under test) and a CLI that refuses `replay` when the job exposes `GITHUB_TOKEN` write scopes or secret-like env vars ⚠️ heuristics; the residual risk is recorded in 11 §4 and the 11.1.1 threat model (08.4.3, 11.1.1, 11.2.1) |
| T78 | The CLI never prints a bearer token: `auth login --json` returns `{ok, expires_at, project_id}`; the token goes to a 0600 file or `$GITHUB_ENV` after `::add-mask::` (EVG-SEC-01; 04.2.1) |
| T79 | `permission.tenant_assignable boolean`; custom roles containing non-assignable codes (`support:*`, `billing:*`, `platform:*`, agent-only) → 422 `permission_not_assignable`; the evaluator never grants a non-assignable code to a member principal (EVG-AUTHZ-02; 02.2.1) |
| T80 | **Every `SECURITY DEFINER` function in any schema** (`evidgate_resolve`, `evidgate`, `evidgate_ops`): `SET search_path = ''`, schema-qualified references, `EXECUTE` granted to a dedicated role (`evidgate_app` for resolvers, `evidgate_worker_sched` for sweeps), every call audited, covered by the 11.2.1-T7 review and the 02.6.1-T2 lint (EVG-TEN-04; 01.5.1, 02.6.1, 06.4.2, 06.4.3, 06.5.2) |
| T81 | **Late evidence is accepted** (final perf/data review): events for a run in `incomplete`, or `complete` within `late_window` since completion, are stored with `late = true` and trigger a new evidence revision + re-evaluation (07.5.1); after the late window → 409 `run_closed`; canceled runs keep accept-and-mark (T12) (04.1.1, 05.3.2, 07.5.1) |
| T81a | The late window is checked **once, at ingest**; every late revision produced from accepted events is re-evaluated — evaluation never re-checks the window (07.5.1) |
| T82 | Run state transitions are idempotent: a request for a state the run already holds (or has passed on the forward path) is a no-op returning the current state, never an error (05.3.1; fixes concurrent first-batch races) |
| T83 | Usage is metered only for **accepted** (non-duplicate) events: `quantity = accepted`, no ledger row when `accepted = 0`, so a resent batch never bills again (04.1.1, T43/T50) |
| T84 | Producer restart = same deterministic `producer_id` (T54), `sequence` resumes after the stored checkpoint; a new `producer_id` → 422 `unknown_producer` (04.1.1) |
| T85 | `POST /v1/artifacts/upload-url` carries `producer_id`; the first accepted write of **any** kind (upload-url or events) binds the slot (T9); upload-url requires `Idempotency-Key` (00 §9.1) (04.1.2, 06.1.2) |
| T86 | `complete` order: HEAD + checksum verify → idempotent copy `pending/…` → final key → **commit** `ready` with the final key → delete the staging object by outbox task after commit. A failed commit leaves staging (24 h lifecycle) and a final object that the orphan sweeper (06.4.3) removes; a retried `complete` whose staging is gone but whose final object matches the checksum proceeds to commit (06.1.2) |
| T87 | Release decision: `GET /v1/releases/{id}/decision` is a pure query (returns `pending` when no row exists); aggregation is an outbox task on `evaluation.created` that persists `release_decision` (with `inputs_hash`); 07.5.4 extends that persisted aggregate with `component_kind`/mobile digests, never a read-time projection (07.5.3, 07.5.4) |
| T88 | `run.ci_run_ref` (`provider`, `repository_id`, `run_id`, `run_attempt`) is set at run create from the caller's OIDC claims and is what `producer_slot.bound_ci_run_ref` is compared with (T9) (05.2.1, 04.1.2) |
| T89 | Job claim: index `(org_id, pool, created_at, id) WHERE state = 'queued'`; the claim transaction first re-queues up to 10 same-pool jobs whose lease `expires_at < now()` (`FOR UPDATE SKIP LOCKED`, epoch + 1), then claims; the sweeper (05.10.1) remains the backstop (05.7.2) |
| T90 | Batch `execution_key` validation is one probe `WHERE org_id = @o AND run_id = @r AND execution_key = ANY(@keys)` on the existing unique index — never loading the run's whole planned-execution set (04.1.1, P3) |
| T91 | `after_cancel` and `late` flags are derived inside the ingest transaction from the run row taken `FOR SHARE`; cancel and completion take the run row `FOR UPDATE`, so the flag can never miss a committed cancel (04.1.1, 05.4.1) |
| T92 | Every mutation that creates a resource requires `Idempotency-Key`, including `upload-url` (T85) and `complete` (06.1.2) |
| T93 | Run deadlines: at create, `collection_deadline_at = created_at + policy.collection_timeout` if a policy is pinned, else `created_at + EVIDGATE__Runs__DefaultCollectionTimeout` (default 2 h ⚠️, tuned per RECOMMENDATIONS 05-Q2 to ≈ p95 × 1.5); the run stores an immutable `late_window` snapshot copied from the pinned policy (default 24 h, T62) (05.2.1) |
| T94 | `idempotency_record.project_id` is nullable for org-scoped operations; uniqueness via `unique nulls not distinct (org_id, project_id, operation, key)` (PostgreSQL 15+ ✅, we run 18) (01.5.1, → 00 §7) |
| T95 | Query scopes open with `BEGIN READ ONLY` (or `SET TRANSACTION READ ONLY` as the transaction's first statement, before `set_config`) so the read-only mode is in force for every statement of the query path — probed on the pinned `postgres:18.6`: a late `SET TRANSACTION READ ONLY` is accepted and takes effect (a write then fails 25006), while `SET TRANSACTION READ WRITE`/`ISOLATION LEVEL` after the first statement are rejected with 25001 (01.4.2, → 00 §5.5 C1) |
| T96 | The healing UI has one lazy feature root `apps/web/src/app/features/healing-review/` for 09.5.1 (V1) and 09.5.2 (V2); the no-V1-apply-control check covers that root (09.5.1, 09.5.2) |
| T97 | Capture/redaction options that 06.2.2 decides server-side (per-project capture flags, redaction patterns, max sizes per kind) are delivered to adapters in the adapter protocol v1 **configuration document** returned at run create, schema owned by 04.7.1-T1 (`contracts/adapter/config.v1.json`); adapters must honour it or declare `capture_control: false` in their capability document, in which case the run is labelled `capture_uncontrolled` (06.2.2, 04.7.1) |
| T98 | The member who creates an org receives **both** `tenant_admin` and `release_approver` (a pilot may be one person); either role can later be moved to someone else, subject to the last-admin guard. The onboarding wizard's "enable collection" step (T63) therefore works for a lone creator (02.1.1, 09.2.1) |
| T99 | **No viewer cookie** (replaces the cookie part of T74): the trace-viewer Service Worker on `viewer-app` keeps the HMAC viewer token in memory and sends it as `Authorization: Viewer <token>` on every request to `viewer-content`; `viewer-content` allows CORS only from `viewer-app`, never reads cookies, and raw-report documents are loaded via a one-time `?t=` token that is exchanged server-side and stripped with a redirect. Reason: a `SameSite=None` cookie inside a cross-site iframe is blocked by Safari ITP and partitioned by Firefox (06.3.3, 06.3.4) |
| T100 | Evaluation adjudication (T65): the adjudicator of a candidate in the evaluation set must differ from the member who approved that candidate and from its author (T35); enforced by a CHECK on the adjudication row and a 403 `adjudicator_conflict` (08.8.1, 08.8.2) |
| T101 | T91's run-row `FOR SHARE` on the ingest hot path is accepted: shared locks do not block each other, only cancel/complete (`FOR UPDATE`) wait. The 10.6.1-T1 load test measures batch p95 and multixact growth at 833 events/s; if either misses budget, the fallback is to drop the lock and derive `after_cancel`/`late` at evidence freeze from `event.accepted_at` versus `run.canceled_at` / `run.completed_at` (04.1.1, 05.4.1, 10.6.1) |

## 4. Stack mapping used by every plan (old → new)

| Old term | New term (DECISIONS §4, 00 §5.5–§5.7) |
|---|---|
| NestJS module `src/modules/<x>` | `src/Modules/Evidgate.Modules.<X>` + `.Contracts` project |
| NestJS controller / guard / interceptor | minimal-API route group / Mediator authorization behaviour / endpoint filter |
| service method that writes | `ICommand<T>` + handler (EF Core 10 write model) |
| service method that reads | `IQuery<T>` + handler (Dapper, read-only transaction) |
| Kysely + `withTenant()` + `SET LOCAL` | `ITenantScope.BeginAsync` + parameterised `set_config(…, true)` |
| pg-boss task / cron | outbox row → `task` queue table (`SKIP LOCKED`) / Cronos schedule fired idempotently via unique `(schedule_name, fire_at)` on the task row (no leader; 00 §5.3) |
| Ajv (server) | Corvus.Json.Validator (server); Ajv stays in the TypeScript CLI (T42) |
| `canonicalize` npm | `Evidgate.Canonicalization` (server) and `canonicalize` 5.1 (CLI), same RFC 8785 vectors |
| pino / nestjs-pino | `Microsoft.Extensions.Logging` + OpenTelemetry + redaction |
| Vitest + Testcontainers 12 (server) | xUnit v3 + Shouldly + Testcontainers.PostgreSQL 4.15 + Respawn |
| scrypt token hash | HMAC-SHA256 + versioned pepper (T5) |
| migration runner over `pg` | `Evidgate.Migrator` (DbUp 7 + checksum journal) |
| React 19 + Vite 8, react-router, react-query | Angular 22.2 standalone + router + `HttpClient`, signals + `@ngrx/signals` |
| `dangerouslySetInnerHTML` ban | no `[innerHTML]` on untrusted content, no `bypassSecurityTrust*` outside the reviewed viewer bridge (lint) |
| npm-only supply chain | NuGet + npm (T40) |
| "senior full-stack TS developer" | "senior full-stack .NET + Angular developer" |
| Unchanged | CLI `evidgate`, Playwright reporter, JUnit/Appium mappers, V2 agent (Node 22+/TS 6.0.x, Vitest) |

## 5. Execution order

Generated from the task dependency graph by `scripts/feature_index.py` (`--order`, `--critical`); regenerate after any change to a `Depends on` cell. Principles [DECISION]:

1. **Phase gates first** (00 §4, CR-D-2): a phase ends when its exit gate passes; work of phase G*n*+1 may start early only if it does not delay a G*n* gate task.
2. **Inside a milestone, dependency waves**: a story starts when every task it depends on is done (not the whole predecessor story). Wave = depth in the story graph.
3. **Bundles** (story-level cycles, §5.3) are planned as one unit and sequenced by task, never story by story.
4. **Contract-first**: `contracts/` changes (01.3.x) and the 00 §5.5 pipeline (01.4.2-T4) precede every endpoint story; a story whose contract is not in `contracts/` fails its DoR.
5. **Security before exposure**: tenancy/RLS (01.4.1, 02.6.x, 02.9.1), producer binding (04.1.2-T5) and the kill switch (08.6.3) precede the features that rely on them — already encoded as dependencies (T9, T29).
6. **Dogfood early**: 01.10.1 (G2) lands before the run pilot so Evidgate's own CI exercises ingest → evidence → decision weekly.

### 5.1 Waves per milestone

| Milestone | Wave | Stories (placed at their earliest task milestone; bundles in brackets) | Story raw days (all milestones) |
|---|---|---|---|
| G1 | 1 | 01.1.1, 01.9.1, 10.8.1, 12.6.1 | 11.25 |
| G1 | 2 | 01.1.2, 01.2.1, 01.3.1, 01.4.1 | 19 |
| G1 | 3 | 01.3.2, 01.6.1, [02.1.1 + 02.2.1], 04.7.1, 10.1.1, 10.2.1 | 27 |
| G1 | 4 | [01.4.2 + 01.5.1], 01.7.1, [02.1.2 + 02.3.1 + 02.3.2], 02.6.1, 03.1.1, 10.5.1 | 37 |
| G1 | 5 | 02.4.1, 02.6.2, 02.7.1, 03.1.3, 06.1.1, 09.1.1 | 21.5 |
| G1 | 6 | 02.4.2, 02.9.1, 03.2.1, 04.3.2, 06.2.2, 06.4.1, 11.1.1, 13.1.1 | 23 |
| G1 | 7 | 03.1.4, 03.4.1, 05.1.1, 06.4.2, 11.5.1, 11.8.1, 13.1.2 | 27.5 |
| G1 | 8 | 05.1.2, 06.4.3 | 2.5 |
| G1 | 9 | [04.1.1 + 04.1.2 + 05.2.1 + 05.3.3], 06.4.5 | 16.5 |
| G1 | 10 | 06.1.2, 07.1.1, 10.11.1 | 7.5 |
| G1 | 11 | 01.8.1, 04.4.1, 06.2.3 | 10.5 |
| G1 | 12 | 03.1.2 | 2 |
| G1 | 16 | [04.8.1 + 06.4.4 + 06.5.2 + 08.5.1 + 08.6.3 + 10.3.1 + 10.3.2 + 10.4.1 + 10.7.1 + 11.2.1 + 11.3.1 + 11.9.1 + 12.3.1 — bundle 02.5.1 + 03.3.1 + 03.4.2 + 03.6.1 + 04.8.1 + 04.9.1 + 05.7.4 + 06.4.4 + 06.5.2 + 08.3.1 + 08.3.2 + 08.4.3 + 08.5.1 + 08.6.1 + 08.6.2 + 08.6.3 + 08.7.1 + 10.3.1 + 10.3.2 + 10.4.1 + 10.7.1 + 11.2.1 + 11.3.1 + 11.9.1 + 12.3.1] | 56.5 |
| G1 | 17 | 08.5.2, 08.5.3, 10.1.2, 10.2.2, 11.5.2, 11.6.1, 13.2.1 | 24 |
| G1 | 18 | 09.2.1, 11.4.1, 11.9.2 | 18.5 |
| G1 | 19 | 12.2.1 | 5.5 |
| G1 | 20 | 08.5.5, 09.7.1 | 17 |
| G1 | 21 | 13.3.1 | 7 |
| G1 | 22 | 13.4.1, 13.9.1 | 11.5 |
| G1 | 23 | 13.2.2 | 9.5 |
| G2 | 1 | 13.7.1 | 2 |
| G2 | 4 | 04.3.1, 05.7.1 | 3 |
| G2 | 5 | 01.10.1, 05.7.2, 10.5.2 | 9 |
| G2 | 6 | 02.4.3, 02.8.1, 06.3.1 | 6 |
| G2 | 7 | 02.4.4, 04.2.1, 06.3.2, 06.3.3, 06.3.6 | 10.5 |
| G2 | 8 | 06.2.1, 06.3.4, 13.5.2 | 6 |
| G2 | 10 | 04.2.2, 04.6.1, 05.2.2, 05.3.1, 06.4.6 | 11.5 |
| G2 | 11 | 05.4.1, 06.1.3 | 3 |
| G2 | 12 | [05.3.2 + 06.5.1 + 07.2.1 + 07.2.2 + 07.5.1] | 14.75 |
| G2 | 13 | 04.1.3, 04.2.3, 05.6.1, 07.4.1, 08.1.1 | 12.5 |
| G2 | 14 | 04.2.4, 05.5.1, 08.1.2 | 11 |
| G2 | 15 | 04.3.3, 05.5.2, 08.2.1, 13.6.1 | 13 |
| G2 | 16 | [03.3.1 + 03.4.2 + 03.6.1 + 08.3.1 — bundle 02.5.1 + 03.3.1 + 03.4.2 + 03.6.1 + 04.8.1 + 04.9.1 + 05.7.4 + 06.4.4 + 06.5.2 + 08.3.1 + 08.3.2 + 08.4.3 + 08.5.1 + 08.6.1 + 08.6.2 + 08.6.3 + 08.7.1 + 10.3.1 + 10.3.2 + 10.4.1 + 10.7.1 + 11.2.1 + 11.3.1 + 11.9.1 + 12.3.1] | 11.5 |
| G2 | 17 | 02.10.1, 05.10.1, 08.4.1, 09.3.1, 10.6.1, 10.7.2, 10.9.1 | 37 |
| G2 | 18 | 12.4.1 | 4.5 |
| G2 | 19 | 03.7.1 | 3.5 |
| G2 | 21 | 13.5.1 | 9.5 |
| G3 | 6 | 09.8.1 | 6 |
| G3 | 13 | 03.9.1, 04.4.2, 07.5.2 | 4 |
| G3 | 14 | 03.9.2, 08.2.2 | 6.5 |
| G3 | 15 | 04.9.2 | 3 |
| G3 | 16 | [04.9.1 + 08.3.2 + 08.4.3 + 08.6.1 + 08.6.2 + 08.7.1 — bundle 02.5.1 + 03.3.1 + 03.4.2 + 03.6.1 + 04.8.1 + 04.9.1 + 05.7.4 + 06.4.4 + 06.5.2 + 08.3.1 + 08.3.2 + 08.4.3 + 08.5.1 + 08.6.1 + 08.6.2 + 08.6.3 + 08.7.1 + 10.3.1 + 10.3.2 + 10.4.1 + 10.7.1 + 11.2.1 + 11.3.1 + 11.9.1 + 12.3.1] | 23.25 |
| G3 | 17 | 08.7.2 | 2 |
| G3 | 18 | 08.4.2, 09.4.1, 09.5.2 | 17.5 |
| G3 | 19 | 08.2.3, 08.8.1, 09.5.1 | 9 |
| G3 | 20 | 08.8.2, 12.5.1, 13.7.2, 13.8.1 | 15.5 |
| G3 | 21 | 08.3.3, 08.8.3, 13.8.2 | 7 |
| G4 | 1 | 12.1.1 | 5 |
| G4 | 5 | 02.3.3 | 3 |
| G4 | 9 | 05.7.3 | 5 |
| G4 | 10 | 05.8.1 | 3 |
| G4 | 11 | 04.5.1, 05.9.1, 07.3.1 | 8 |
| G4 | 12 | 03.5.1 | 3.5 |
| G4 | 13 | 07.3.2 | 1.5 |
| G4 | 14 | 07.4.2, 07.5.3, 07.6.1, 09.9.1 | 7 |
| G4 | 15 | 07.5.4, 07.7.1 | 1.5 |
| G4 | 16 | [02.5.1 + 05.7.4 — bundle 02.5.1 + 03.3.1 + 03.4.2 + 03.6.1 + 04.8.1 + 04.9.1 + 05.7.4 + 06.4.4 + 06.5.2 + 08.3.1 + 08.3.2 + 08.4.3 + 08.5.1 + 08.6.1 + 08.6.2 + 08.6.3 + 08.7.1 + 10.3.1 + 10.3.2 + 10.4.1 + 10.7.1 + 11.2.1 + 11.3.1 + 11.9.1 + 12.3.1] | 7 |
| G4 | 17 | 05.7.5, 05.7.6, 05.9.2, 06.3.5, 10.10.1 | 6 |
| G4 | 18 | 02.10.2, 07.6.2, 07.7.2, 08.5.4, 11.7.1 | 13 |
| G4 | 19 | 07.6.3, 07.8.1 | 7 |
| G4 | 20 | 03.8.1, 09.6.1 | 11 |
| G4 | 21 | 09.7.2 | 2 |
| G4 | 22 | 08.9.1 | 3 |
| G4 | 23 | 08.9.2 | 2 |

Checked 2026-10-30 against `scripts/plan_graph.py --json`: every story of §7 appears in exactly one row as a placement (the bundle rosters repeated after the em dash are names, not placements; a bracket's `+`-joined stories are placed in the row that carries the bracket), each row's day figure is the story's full size, and the rows sum to 705.25 raw person-days (the graph's 658-task total), split 354.75 G1 · 168.25 G2 · 93.75 G3 · 88.5 G4. That split counts each story once, in the phase of its earliest task; the task-level split in `plans/13-product-phases.md` §4.4 is 283.75 · 195.5 · 106.75 · 119.25 because it counts every task in its own Milestone cell, and the two agree only in their sum (705.25).

### 5.2 Critical chain (task level)

| Up to | Work in this milestone (raw d) | Longest dependency chain from start (d) | Chain |
|---|---|---|---|
| G1 | 283.75 | 29 | 01.1.1-T1 → 01.1.1-T2 → 01.1.1-T3 → 01.4.1-T1 → 01.4.1-T2 → 01.4.1-T3 → 02.1.1-T1 → 03.1.1-T1 → 03.1.3-T1 → 03.2.1-T1 → 03.2.1-T2 → 05.1.1-T2 → 05.1.1-T4 → 05.1.2-T1 → 05.2.1-T1 → 06.1.2-T1 → 06.1.2-T3 → 13.3.1-T1 → 13.3.1-T2 → 13.3.1-T3 → 13.2.2-T2 → 13.2.2-T5 → 13.2.2-T6 |
| G2 | 195.5 | 39.5 | 01.1.1-T1 → 01.1.1-T2 → 01.1.1-T3 → 01.4.1-T1 → 01.4.1-T2 → 01.4.1-T3 → 02.1.1-T1 → 03.1.1-T1 → 03.1.3-T1 → 03.2.1-T1 → 03.2.1-T2 → 05.1.1-T2 → 05.1.1-T4 → 05.1.2-T1 → 05.2.1-T1 → 05.3.2-T1 → 05.3.2-T5 → 08.1.1-T1 → 08.1.2-T1 → 08.1.2-T3 → 08.2.1-T1 → 08.2.1-T2 → 08.2.1-T3 → 08.3.1-T1 → 08.4.1-T1 → 08.4.1-T2 → 13.5.1-T2 → 13.5.1-T7 |
| G3 | 106.75 | 52.5 | 01.1.1-T1 → 01.1.1-T2 → 01.1.1-T3 → 01.4.1-T1 → 01.4.1-T2 → 01.4.1-T3 → 02.1.1-T1 → 03.1.1-T1 → 03.1.3-T1 → 03.2.1-T1 → 03.2.1-T2 → 05.1.1-T2 → 05.1.1-T4 → 05.1.2-T1 → 05.2.1-T1 → 05.3.2-T1 → 05.3.2-T5 → 08.1.1-T1 → 08.1.2-T1 → 08.1.2-T3 → 08.2.1-T1 → 08.2.1-T2 → 08.2.1-T3 → 08.3.1-T1 → 08.3.2-T1 → 08.4.3-T1 → 11.2.1-T6 → 08.4.3-T2 → 08.4.3-T3 → 08.6.1-T1 → 08.6.1-T2 → 08.6.2-T1 → 08.6.2-T2 → 08.7.1-T2 → 08.8.2-T1 → 08.8.2-T2 → 08.8.3-T1 → 08.8.3-T2 |
| G4 | 119.25 | 57.5 | 01.1.1-T1 → 01.1.1-T2 → 01.1.1-T3 → 01.4.1-T1 → 01.4.1-T2 → 01.4.1-T3 → 02.1.1-T1 → 03.1.1-T1 → 03.1.3-T1 → 03.2.1-T1 → 03.2.1-T2 → 05.1.1-T2 → 05.1.1-T4 → 05.1.2-T1 → 05.2.1-T1 → 05.3.2-T1 → 05.3.2-T5 → 08.1.1-T1 → 08.1.2-T1 → 08.1.2-T3 → 08.2.1-T1 → 08.2.1-T2 → 08.2.1-T3 → 08.3.1-T1 → 08.3.2-T1 → 08.4.3-T1 → 11.2.1-T6 → 08.4.3-T2 → 08.4.3-T3 → 08.6.1-T1 → 08.6.1-T2 → 08.6.2-T1 → 08.6.2-T2 → 08.7.1-T2 → 08.8.2-T1 → 08.8.2-T2 → 08.8.3-T1 → 08.8.3-T2 → 08.9.1-T1 → 08.9.1-T2 → 08.9.2-T1 → 08.9.2-T2 |

Reading [INFERENCE] (arithmetic over the graph): the longest dependency chain to the end of G1 is 29 raw days (28 before story 13.2.2; the role-profile chain through 13.2.2-T6 is now the longest) while G1 holds 283.75 raw days of work (G2: 39.5 against 195.5; both totals include work already merged), so the schedule is **capacity-bound, not dependency-bound**: adding engineers shortens the calendar almost linearly up to roughly 8–10 parallel lanes. The G3 chain runs through tenancy (01.4.1 → 02.1.1) → catalog (03.1–03.2) → manifest/run (05.1–05.2) → ingest/attempts bundle → evidence freeze → healing (08.1–08.4): these tasks get the most senior owner and are never blocked on review. Re-checked 2026-10-30 against `scripts/plan_graph.py --json` as the longest weighted path over the dependency edges restricted to each phase prefix: 29 / 39.5 / 52.5 / 57.5 with the chains above unchanged, and 28 with the 13.2.2 tasks removed.

### 5.3 Bundles (build together, ordered by task)

| Bundle (stories built as one unit) | Raw days | Task-level build order (topological; parallel tasks may start together) |
|---|---|---|
| 01.4.2 + 01.5.1 | 13.5 | 01.4.2-T1 → 01.4.2-T2, 01.4.2-T3, 01.5.1-T1, 01.5.1-T2, 01.5.1-T6 → 01.4.2-T4, 01.5.1-T3 → 01.5.1-T4 → 01.5.1-T5 |
| 02.1.1 + 02.2.1 | 5.5 | 02.1.1-T1 → 02.1.1-T3, 02.2.1-T1 → 02.2.1-T2 → 02.1.1-T2, 02.2.1-T3 |
| 02.1.2 + 02.3.1 + 02.3.2 | 8 | 02.1.2-T1 → 02.3.1-T1 → 02.3.1-T2, 02.3.1-T3, 02.3.2-T1 → 02.1.2-T2, 02.3.2-T2 → 02.1.2-T3, 02.3.2-T3 |
| 02.5.1 + 03.3.1 + 03.4.2 + 03.6.1 + 04.8.1 + 04.9.1 + 05.7.4 + 06.4.4 + 06.5.2 + 08.3.1 + 08.3.2 + 08.4.3 + 08.5.1 + 08.6.1 + 08.6.2 + 08.6.3 + 08.7.1 + 10.3.1 + 10.3.2 + 10.4.1 + 10.7.1 + 11.2.1 + 11.3.1 + 11.9.1 + 12.3.1 | 98.25 | 02.5.1-T1, 03.3.1-T1, 03.6.1-T1, 04.9.1-T1, 06.4.4-T1, 08.6.3-T1, 10.3.1-T1, 10.7.1-T1, 10.7.1-T3, 10.7.1-T4, 11.2.1-T1, 11.2.1-T2, 11.2.1-T3, 11.2.1-T7, 11.3.1-T1, 11.3.1-T3, 11.3.1-T5, 11.9.1-T2, 11.9.1-T4, 12.3.1-T1 → 02.5.1-T2, 02.5.1-T3, 03.3.1-T2, 03.3.1-T3, 03.4.2-T1, 04.8.1-T1, 04.9.1-T3, 04.9.1-T4, 06.4.4-T3, 08.3.1-T1, 08.5.1-T1, 10.3.2-T1, 10.7.1-T6, 11.3.1-T2, 11.3.1-T4, 11.9.1-T1, 11.9.1-T3, 12.3.1-T3, 12.3.1-T5 → 04.8.1-T2, 05.7.4-T1, 08.3.2-T1, 08.5.1-T3, 08.6.3-T2, 10.3.1-T2, 11.9.1-T5, 12.3.1-T6 → 05.7.4-T2, 08.3.2-T2, 08.3.2-T3, 08.4.3-T1, 10.3.1-T3, 10.3.2-T3 → 03.6.1-T2, 05.7.4-T3, 06.5.2-T1, 10.3.1-T4, 10.3.1-T5, 10.4.1-T1, 10.7.1-T2, 11.2.1-T6 → 03.6.1-T3, 04.9.1-T2, 06.5.2-T2, 08.4.3-T2, 10.3.2-T2, 10.4.1-T2, 10.7.1-T5, 11.2.1-T4 → 03.6.1-T4, 08.4.3-T3, 08.6.3-T3, 11.2.1-T5, 12.3.1-T2 → 08.6.1-T1, 12.3.1-T4 → 08.6.1-T2, 08.6.1-T3 → 08.6.2-T1, 08.7.1-T1 → 06.4.4-T2, 08.6.2-T2, 08.7.1-T3 → 08.7.1-T2 |
| 04.1.1 + 04.1.2 + 05.2.1 + 05.3.3 | 16 | 04.1.2-T4 → 05.2.1-T1 → 05.2.1-T2, 05.2.1-T3, 05.2.1-T4, 05.2.1-T5, 05.3.3-T1 → 05.3.3-T2 → 04.1.1-T1 → 04.1.1-T2, 04.1.1-T3, 04.1.1-T4, 04.1.2-T5 → 04.1.2-T1, 04.1.2-T2, 04.1.2-T3 |
| 05.3.2 + 06.5.1 + 07.2.1 + 07.2.2 + 07.5.1 | 14.75 | 05.3.2-T1 → 05.3.2-T5 → 06.5.1-T1 → 06.5.1-T2, 07.2.1-T1 → 05.3.2-T2, 07.2.1-T2 → 05.3.2-T3, 05.3.2-T4, 07.2.2-T1, 07.2.2-T2 → 07.2.2-T3, 07.2.2-T4, 07.5.1-T1 → 07.5.1-T2 → 06.5.1-T3 |

### 5.4 Owner-decision gates (DECISIONS §8)

| Decision | Blocks (must be closed before the DoR of) |
|---|---|
| O1 pilot customers, CI, engines | M1 start; 04.3.x/04.4.x adapter scope; 12.2.1 |
| O2 team size, Tech Lead / QA lead | M1 start; lane assignment (§5.5); calendar dates |
| O3 cloud provider/region | first pilot data in M2: 10.1.x, 10.5.1, 10.6.1-T2, 06.1.x provider parity |
| O9 critical cases | moving any project's policy from advisory to gating (07.6.x) |
| O10 V1 replay host | 08.4.3 (recommended (a): customer CI via the CLI) |
| O5 pricing / pilot contract | 12.2.1 pilot agreement, M3 |
| O4 LLM provider, data egress | 08.5.x (M4) |
| O6 false-heal threshold | V3 gate 08.8.3-T2 (M5) |
| O7 trademark | public launch |

### 5.5 Lanes for 2–3 engineers [INFERENCE]

> **Superseded for scheduling (2026-10-02):** the team is one developer (DECISIONS §8 O2). Use [SOLO-PATH.md](SOLO-PATH.md). The lane table below stays as the dependency view for a larger team.

| Lane | Areas | Why |
|---|---|---|
| A — platform & core (Tech Lead) | 01, 02, 05, 04 server side | owns the critical chain and the shared contracts |
| B — evidence & decisions | 06, 07, 12 ledger, 08 server side | consumes A's pipeline; decision correctness needs one owner |
| C — web & customer packages | 09, 04 CLI/adapters/reporter, 08 healing-validator | Angular + TypeScript skills; starts 09.1.1 in M2 |
| All, rotating | 10 operations, 11 security reviews | reviews must be done by someone other than the author (DECISIONS §5.5 spirit) |

## 6. Feature plan template and Definition of Ready

File: `plans/features/<NN>-<area>/<story-id>-<slug>.md`. Header and sections are fixed (00 §12):

```markdown
# <story-id> — <story title>

| Area plan | Milestone | Version | Size (area plan) | Re-estimate | Depends on (stories) | Blocks (stories) |
|---|---|---|---|---|---|---|

> Contracts: 00 §… · DECISIONS §… · rulings T… · Area plan: `plans/NN-<area>.md` §… (story NN.E.S)

## 1. Goal and non-goals
## 2. User stories and acceptance criteria
## 3. Contracts touched
## 4. Data model and migrations
## 5. API shapes (commands, queries, endpoints)
## 6. States and transitions
## 7. Jobs and integrations
## 8. Security, privacy and performance
## 9. Test plan
## 10. Tasks, sub-tasks and re-estimate
## 11. Rollout
```

Rules for section 10: one `### NN.E.S-Tn — <task> (<size>)` block per area-plan task, containing a table `| Sub-task | Work | Size | Done when |` with ids `NN.E.S-Tn.k`, each ≤ 0.5 person-day (XS 0.25 / S 0.5), each "Done when" a runnable check (test name, command, screenshot). The sub-task sizes of a task sum to the task's size in the area plan. If a task needs more, its row says so in "Re-estimate" and the area plan is updated.

Rules for section 8: name every 00 §5.7 rule (P1–P8, S1–S8) and §5.5/§5.6 rule the story touches, with the test that proves it. The section also carries the **four security review questions** (11.1.1-T2), each answered on its own labelled line — a table row whose first cell is the label, or a line that starts with it:

- `Authorization:` which role/permission every command/query of the story needs, or `none` when the story has no endpoint or handler;
- `Retention/deletion:` what the story writes or reads, how long it is kept, and what deletion does to it (00 §11 and the owning retention rule);
- `Telemetry:` which metric, trace span, audit record or `security_event` it emits, or `none`;
- `Failure path:` what a caller or operator sees when it fails — error code/envelope, retry policy, dead-letter, runbook note.

`scripts/check-dor-security.sh` greps those four answers here, in `.github/PULL_REQUEST_TEMPLATE.md` and in every feature plan added or changed in the change set under review, so a plan touched without answering them fails its DoR review note.

**Definition of Ready (checked at build start):** contracts in 00/`contracts/` updated; acceptance criteria testable; dependencies done or stubbed by contract; estimate re-checked; security review done for identity, tenancy, artifacts and healing stories; the four review questions of §8 answered (authorization, retention/deletion, telemetry, failure path — `scripts/check-dor-security.sh`); blocking owner decisions (§5.3) closed.

## 7. Index

Generated by `scripts/regen_docs.py`; one row per story. Check with `scripts/check_features.py` (structure, sub-task sums, ids, header sync).

| Story | Title | Milestone | Wave | Bundle | Raw days | Depends on | Blocks |
|---|---|---|---|---|---|---|---|
| [01.1.1](01-foundation/01.1.1-toolchain-and-workspace.md) | Toolchain and workspace | G1 | 1 | — | 2.75 | — | 01.1.2, 01.2.1, 01.3.1, 01.3.2, 01.4.1, 01.6.1, 04.2.1, 09.1.1, 11.6.1 |
| [01.1.2](01-foundation/01.1.2-two-process-skeleton.md) | Two-process skeleton | G1 | 2 | — | 4.5 | 01.1.1 | 02.3.1, 10.1.1, 10.2.1 |
| [01.2.1](01-foundation/01.2.1-analyzers-format-types-and-architecture.md) | Analyzers, format, types and architecture | G1 | 2 | — | 5 | 01.1.1 | — |
| [01.3.1](01-foundation/01.3.1-spec-and-schemas.md) | Spec and schemas | G1 | 2 | — | 3.5 | 01.1.1 | 01.3.2, 04.1.1, 04.7.1, 05.1.1, 07.5.2 |
| [01.3.2](01-foundation/01.3.2-generated-sdk-validators-and-contract-tests.md) | Generated SDK, validators and contract tests | G1 | 3 | — | 6 | 01.1.1, 01.3.1 | 01.5.1, 09.1.1 |
| [01.4.1](01-foundation/01.4.1-migrations-roles-and-rls.md) | Migrations, roles and RLS | G1 | 2 | — | 6 | 01.1.1 | 01.4.2, 01.6.1, 02.1.1, 03.1.1, 04.1.2, 06.1.1, 07.1.1, 10.2.2, 13.1.1 |
| [01.4.2](01-foundation/01.4.2-tenant-scope-data-access-and-pipeline.md) | Tenant scope, data access and pipeline | G1 | 4 | yes | 6.5 | 01.4.1, 01.5.1 | 01.5.1, 02.6.2, 05.1.2 |
| [01.5.1](01-foundation/01.5.1-durable-internal-tasks.md) | Durable internal tasks | G1 | 4 | yes | 7 | 01.3.2, 01.4.2 | 01.4.2, 02.10.1, 03.3.1, 05.2.1, 06.2.3, 06.3.6, 06.4.2, 06.5.2, 07.2.2, 07.6.2, 10.7.1 |
| [01.6.1](01-foundation/01.6.1-integration-harness-and-fixtures.md) | Integration harness and fixtures | G1 | 3 | — | 5 | 01.1.1, 01.4.1 | 01.7.1, 04.1.3, 09.1.1, 11.7.1 |
| [01.7.1](01-foundation/01.7.1-evidgate-s-own-pipeline.md) | Evidgate's own pipeline | G1 | 4 | — | 4.5 | 01.6.1 | 01.10.1, 04.6.1, 04.9.2, 10.1.2, 11.6.1, 11.7.1 |
| [01.8.1](01-foundation/01.8.1-one-command-local-stack.md) | One-command local stack | G1 | 11 | — | 3 | 02.1.2, 02.4.1, 02.4.2, 07.1.1 | — |
| [01.9.1](01-foundation/01.9.1-decision-records.md) | Decision records | G1 | 1 | — | 2 | — | — |
| [01.10.1](01-foundation/01.10.1-evidgate-gates-evidgate.md) | Evidgate gates Evidgate | G2 | 5 | — | 3.5 | 01.7.1 | — |
| [02.1.1](02-identity-tenancy/02.1.1-org-and-project-registry.md) | Org and project registry | G1 | 3 | yes | 2.5 | 01.4.1, 02.2.1 | 02.1.2, 02.2.1, 02.4.1, 02.4.3, 02.6.1, 02.9.1, 03.1.1, 03.4.1, 07.8.1, 09.7.1, 11.1.1, 12.3.1, 13.1.1 |
| [02.1.2](02-identity-tenancy/02.1.2-membership-and-invites.md) | Membership and invites | G1 | 4 | yes | 2.5 | 02.1.1, 02.3.1, 02.3.2 | 01.8.1, 02.3.1, 02.3.3, 02.8.1, 02.10.1, 09.7.1 |
| [02.2.1](02-identity-tenancy/02.2.1-permission-catalogue-and-enforcement.md) | Permission catalogue and enforcement | G1 | 3 | yes | 3 | 02.1.1 | 02.1.1, 02.4.1, 02.4.4, 02.5.1, 02.7.1, 02.9.1, 03.7.1, 06.2.1, 06.4.1, 07.3.1, 07.5.2, 07.6.3, 09.7.1, 13.1.1, 13.2.2 |
| [02.3.1](02-identity-tenancy/02.3.1-federated-login.md) | Federated login | G1 | 4 | yes | 3.5 | 01.1.2, 02.1.2 | 02.1.2, 02.3.2, 09.1.1 |
| [02.3.2](02-identity-tenancy/02.3.2-sessions-and-revocation.md) | Sessions and revocation | G1 | 4 | yes | 2 | 02.3.1 | 02.1.2, 02.3.3 |
| [02.3.3](02-identity-tenancy/02.3.3-enterprise-saml.md) | Enterprise SAML | G4 | 5 | — | 3 | 02.1.2, 02.3.2 | — |
| [02.4.1](02-identity-tenancy/02.4.1-github-oidc-exchange.md) | GitHub OIDC exchange | G1 | 5 | — | 5 | 02.1.1, 02.2.1, 02.6.1 | 01.8.1, 02.4.2, 02.4.3, 02.5.1, 04.2.1, 05.2.2, 07.6.1, 09.2.1, 11.2.1 |
| [02.4.2](02-identity-tenancy/02.4.2-non-github-ci-ingest-tokens.md) | Non-GitHub CI ingest tokens | G1 | 6 | — | 1 | 02.4.1 | 01.8.1, 02.4.4, 04.2.1, 09.2.1, 09.7.1, 11.2.1, 11.3.1 |
| [02.4.3](02-identity-tenancy/02.4.3-github-app-installation.md) | GitHub App installation | G2 | 6 | — | 2 | 02.1.1, 02.4.1 | 05.5.2, 07.6.1 |
| [02.4.4](02-identity-tenancy/02.4.4-api-tokens-for-automation.md) | API tokens for automation | G2 | 7 | — | 2.5 | 02.2.1, 02.4.2, 02.7.1 | 09.7.2 |
| [02.5.1](02-identity-tenancy/02.5.1-agent-identity-per-tenant-and-pool.md) | Agent identity per tenant and pool | G4 | 16 | yes | 2 | 02.2.1, 02.4.1, 05.7.3, 10.7.1 | 05.7.4 |
| [02.6.1](02-identity-tenancy/02.6.1-database-enforced-isolation.md) | Database-enforced isolation | G1 | 4 | — | 3.5 | 02.1.1 | 02.4.1, 02.6.2, 02.7.1, 02.9.1, 06.1.1, 07.1.1, 11.1.1, 11.2.1 |
| [02.6.2](02-identity-tenancy/02.6.2-tenant-context.md) | Tenant context | G1 | 5 | — | 0.5 | 01.4.2, 02.6.1 | 02.8.1, 02.9.1 |
| [02.7.1](02-identity-tenancy/02.7.1-audit-trail.md) | Audit trail | G1 | 5 | — | 2.5 | 02.2.1, 02.6.1 | 02.4.4, 02.8.1, 03.6.1, 05.3.1, 07.6.3, 07.8.1, 09.7.1, 11.1.1, 11.5.1, 13.1.1, 13.2.2 |
| [02.8.1](02-identity-tenancy/02.8.1-time-boxed-operator-access.md) | Time-boxed operator access | G2 | 6 | — | 2 | 02.1.2, 02.6.2, 02.7.1 | 02.10.1, 06.4.4, 09.7.1, 11.5.1 |
| [02.9.1](02-identity-tenancy/02.9.1-isolation-suite.md) | Isolation suite | G1 | 6 | — | 3 | 02.1.1, 02.2.1, 02.6.1, 02.6.2 | 11.7.1 |
| [02.10.1](02-identity-tenancy/02.10.1-one-outbound-email-channel.md) | One outbound email channel | G2 | 17 | — | 4.5 | 01.5.1, 02.1.2, 02.8.1, 12.3.1 | 02.10.2 |
| [02.10.2](02-identity-tenancy/02.10.2-chat-notifications-slack-teams.md) | Chat notifications (Slack, Teams) | G4 | 18 | — | 1.5 | 02.10.1, 11.3.1 | — |
| [03.1.1](03-catalog-authoring/03.1.1-stable-case-identity.md) | Stable case identity | G1 | 4 | — | 2.5 | 01.4.1, 02.1.1 | 03.1.2, 03.1.3, 03.1.4, 03.4.1, 03.5.1, 04.3.2, 09.2.1, 13.1.1 |
| [03.1.2](03-catalog-authoring/03.1.2-imported-identity-and-mapping.md) | Imported identity and mapping | G1 | 12 | — | 2 | 03.1.1, 04.3.2, 04.4.1 | 09.2.1 |
| [03.1.3](03-catalog-authoring/03.1.3-immutable-test-revisions.md) | Immutable test revisions | G1 | 5 | — | 1 | 03.1.1 | 03.2.1, 03.7.1, 04.3.2, 05.1.1, 13.1.1 |
| [03.1.4](03-catalog-authoring/03.1.4-requirement-link-taxonomy-and-coverage-definition.md) | Requirement link, taxonomy and execution-coverage definition | G1 | 7 | — | 1.5 | 03.1.1, 03.2.1 | — |
| [03.2.1](03-catalog-authoring/03.2.1-versioned-suites.md) | Versioned suites | G1 | 6 | — | 4 | 03.1.3 | 03.1.4, 03.3.1, 03.4.1, 03.6.1, 04.5.1, 05.1.1, 09.6.1 |
| [03.3.1](03-catalog-authoring/03.3.1-fixtures-and-per-run-data.md) | Fixtures and per-run data | G2 | 16 | yes | 2.5 | 01.5.1, 03.2.1, 11.3.1 | 03.6.1 |
| [03.4.1](03-catalog-authoring/03.4.1-commit-registration-and-catalog-sync.md) | Commit registration and catalog sync | G1 | 7 | — | 4 | 02.1.1, 03.1.1, 03.2.1 | 03.4.2, 03.6.1, 11.2.1 |
| [03.4.2](03-catalog-authoring/03.4.2-pull-request-plumbing-v2.md) | Pull-request plumbing (V2) | G2 | 16 | yes | 1 | 03.4.1, 03.6.1 | 03.6.1, 08.6.1, 13.5.1 |
| [03.5.1](03-catalog-authoring/03.5.1-template-library-and-git-flow.md) | Template library and Git flow | G4 | 12 | — | 3.5 | 03.1.1, 04.3.1, 04.4.1, 05.7.3 | 09.6.1 |
| [03.6.1](03-catalog-authoring/03.6.1-draft-dry-run-pr.md) | Draft → dry run → PR | G2 | 16 | yes | 5 | 02.7.1, 03.2.1, 03.3.1, 03.4.1, 03.4.2, 05.7.3, 05.7.4 | 03.4.2, 03.7.1, 09.6.1 |
| [03.7.1](03-catalog-authoring/03.7.1-ai-assisted-drafts.md) | AI-assisted drafts | G2 | 19 | — | 3.5 | 02.2.1, 03.1.3, 03.6.1, 11.4.1 | 03.8.1, 09.6.1, 13.5.1 |
| [03.8.1](03-catalog-authoring/03.8.1-regression-candidates-from-confirmed-bugs.md) | Regression candidates from confirmed bugs | G4 | 20 | — | 3 | 03.7.1, 06.1.2, 07.2.1, 08.2.2 | — |
| [03.9.1](03-catalog-authoring/03.9.1-case-result-history-and-baseline.md) | Case result history and baseline read model | G3 | 13 | — | 2.5 | 05.3.2, 06.4.6 | 03.9.2, 09.9.1 |
| [03.9.2](03-catalog-authoring/03.9.2-historical-import-to-seed-baseline.md) | Historical import to seed a baseline | G3 | 14 | — | 2 | 03.9.1, 04.2.3, 04.4.2 | — |
| [04.1.1](04-ingestion-adapters/04.1.1-atomic-batch-ingest-with-per-event.md) | Atomic batch ingest with per-event ack | G1 | 9 | yes | 5 | 01.3.1, 05.3.3 | 04.1.2, 04.1.3, 04.2.3, 04.3.3, 04.6.1, 04.8.1, 06.4.6, 11.3.1 |
| [04.1.2](04-ingestion-adapters/04.1.2-quota-epochs-and-observability.md) | Quota, epochs and observability | G1 | 9 | yes | 4 | 01.4.1, 04.1.1 | 05.2.1, 05.5.2, 06.1.2, 10.11.1 |
| [04.1.3](04-ingestion-adapters/04.1.3-large-report-caps-and-streaming.md) | Large-report caps and streaming | G2 | 13 | — | 2.5 | 01.6.1, 04.1.1, 05.3.2 | — |
| [04.2.1](04-ingestion-adapters/04.2.1-authenticate-from-ci.md) | Authenticate from CI | G2 | 7 | — | 1 | 01.1.1, 02.4.1, 02.4.2 | 04.2.2, 04.2.3, 04.2.4, 07.6.1, 13.5.1, 13.5.2 |
| [04.2.2](04-ingestion-adapters/04.2.2-manifest-and-run.md) | Manifest and run | G2 | 10 | — | 1 | 04.2.1, 05.1.1, 05.2.1 | — |
| [04.2.3](04-ingestion-adapters/04.2.3-spool-send-and-finalize.md) | Spool, send and finalize | G2 | 13 | — | 2.5 | 04.1.1, 04.2.1, 05.3.2 | 03.9.2, 04.2.4, 04.9.1, 05.5.2 |
| [04.2.4](04-ingestion-adapters/04.2.4-artifacts-and-quickstart.md) | Artifacts and quickstart | G2 | 14 | — | 2.5 | 04.2.1, 04.2.3, 04.6.1, 06.1.1, 06.1.2 | 04.3.3, 04.9.2 |
| [04.3.1](04-ingestion-adapters/04.3.1-custom-reporter-bound-to-the-documented.md) | Custom reporter bound to the documented API | G2 | 4 | — | 1 | 04.7.1 | 03.5.1, 05.7.6, 13.5.1, 13.5.2, 13.6.1 |
| [04.3.2](04-ingestion-adapters/04.3.2-identity-retries-and-skips.md) | Identity, retries and skips | G1 | 6 | — | 3 | 03.1.1, 03.1.3 | 03.1.2 |
| [04.3.3](04-ingestion-adapters/04.3.3-shards-and-attachments.md) | Shards and attachments | G2 | 15 | — | 2 | 04.1.1, 04.2.4 | — |
| [04.4.1](04-ingestion-adapters/04.4.1-pytest-junitxml-mapping.md) | pytest `junitxml` mapping | G1 | 11 | — | 6.5 | 04.7.1, 06.1.2, 06.3.1 | 03.1.2, 03.5.1, 04.4.2, 09.2.1 |
| [04.4.2](04-ingestion-adapters/04.4.2-reprocess-a-stored-raw-report.md) | Reprocess a stored raw report | G3 | 13 | — | 1 | 04.4.1, 05.3.2, 06.5.1 | 03.9.2 |
| [04.5.1](04-ingestion-adapters/04.5.1-appium-adapter.md) | Appium adapter | G4 | 11 | — | 5 | 03.2.1, 04.6.1, 04.7.1, 05.8.1 | 06.3.5, 07.5.4, 08.9.2 |
| [04.6.1](04-ingestion-adapters/04.6.1-per-producer-golden-set.md) | Per-producer golden set | G2 | 10 | — | 3.5 | 01.7.1, 04.1.1 | 04.2.4, 04.5.1 |
| [04.7.1](04-ingestion-adapters/04.7.1-protocol-v1-contract.md) | Protocol v1 contract | G1 | 3 | — | 2.5 | 01.3.1 | 04.3.1, 04.4.1, 04.5.1, 05.7.1, 05.7.3, 06.2.2 |
| [04.8.1](04-ingestion-adapters/04.8.1-idempotent-billing-key.md) | Idempotent billing key | G1 | 16 | yes | 1.5 | 04.1.1, 12.3.1 | 12.3.1 |
| [04.9.1](04-ingestion-adapters/04.9.1-customer-side-replay-step.md) | Customer-side replay step (O10 option a) | G3 | 16 | yes | 4.5 | 04.2.3, 05.2.1, 11.2.1 | 08.4.3 |
| [04.9.2](04-ingestion-adapters/04.9.2-cli-sdk-release-compatibility-and-docs.md) | CLI/SDK release, compatibility and customer docs | G3 | 15 | — | 3 | 01.7.1, 04.2.4 | — |
| [05.1.1](05-runs-orchestration/05.1.1-verified-manifest.md) | Verified manifest | G1 | 7 | — | 6 | 01.3.1, 03.1.3, 03.2.1 | 04.2.2, 05.1.2, 05.2.1 |
| [05.1.2](05-runs-orchestration/05.1.2-environment-revisions.md) | Environment revisions | G1 | 8 | — | 1.5 | 01.4.2, 05.1.1 | 05.2.1, 05.7.3 |
| [05.2.1](05-runs-orchestration/05.2.1-one-transaction-run-creation.md) | One-transaction run creation | G1 | 9 | yes | 5 | 01.5.1, 04.1.2, 05.1.1, 05.1.2 | 04.2.2, 04.9.1, 05.2.2, 05.3.1, 05.3.2, 05.3.3, 05.5.2, 05.6.1, 06.1.2, 07.1.1, 07.2.2, 08.1.1, 08.4.3, 09.3.1 |
| [05.2.2](05-runs-orchestration/05.2.2-branch-pr-and-commit-identity-of-a-run.md) | Branch, PR and commit identity of a run | G2 | 10 | — | 2.5 | 02.4.1, 05.2.1 | — |
| [05.3.1](05-runs-orchestration/05.3.1-run-state-machine.md) | Run state machine | G2 | 10 | — | 2 | 02.7.1, 05.2.1 | 05.4.1, 09.3.1 |
| [05.3.2](05-runs-orchestration/05.3.2-finalize-and-reconciliation.md) | Finalize and reconciliation | G2 | 12 | yes | 6 | 05.2.1, 06.5.1, 07.1.1 | 03.9.1, 04.1.3, 04.2.3, 04.4.2, 05.5.1, 05.10.1, 06.5.1, 07.2.1, 07.5.1, 08.1.1, 09.3.1, 13.2.2 |
| [05.3.3](05-runs-orchestration/05.3.3-case-attempts.md) | Case attempts | G1 | 9 | yes | 2 | 05.2.1 | 04.1.1, 06.5.1, 07.4.1, 07.4.2 |
| [05.4.1](05-runs-orchestration/05.4.1-durable-cancel.md) | Durable cancel | G2 | 11 | — | 2.5 | 05.3.1 | 09.3.1 |
| [05.5.1](05-runs-orchestration/05.5.1-rerun-with-parent-and-scope.md) | Rerun with parent and scope | G2 | 14 | — | 2 | 05.3.2, 07.4.1 | 05.5.2, 09.3.1 |
| [05.5.2](05-runs-orchestration/05.5.2-ci-rerun-semantics-and-trigger.md) | CI re-run semantics and trigger | G2 | 15 | — | 3.5 | 02.4.3, 04.1.2, 04.2.3, 05.2.1, 05.5.1 | — |
| [05.6.1](05-runs-orchestration/05.6.1-digest-bound-release.md) | Digest-bound release | G2 | 13 | — | 3 | 05.2.1, 07.2.1 | 07.5.3 |
| [05.7.1](05-runs-orchestration/05.7.1-immutable-jobspec.md) | Immutable JobSpec | G2 | 4 | — | 2 | 04.7.1 | 05.7.2 |
| [05.7.2](05-runs-orchestration/05.7.2-leasing-with-epoch-fencing.md) | Leasing with epoch fencing | G2 | 5 | — | 3 | 05.7.1 | 05.7.4, 05.10.1 |
| [05.7.3](05-runs-orchestration/05.7.3-scheduling-capability-pools-fairness-timeouts.md) | Scheduling: capability, pools, fairness, timeouts | G4 | 9 | — | 5 | 04.7.1, 05.1.2 | 02.5.1, 03.5.1, 03.6.1, 05.8.1, 09.7.1 |
| [05.7.4](05-runs-orchestration/05.7.4-evidgate-agent-executor.md) | `evidgate-agent` executor | G4 | 16 | yes | 5 | 02.5.1, 05.7.2, 06.1.1, 06.1.3 | 03.6.1, 05.7.5, 05.7.6, 06.3.5, 11.2.1 |
| [05.7.5](05-runs-orchestration/05.7.5-cleanup-and-re-run-safety.md) | Cleanup and re-run safety | G4 | 17 | — | 2 | 05.7.4 | — |
| [05.7.6](05-runs-orchestration/05.7.6-agent-end-to-end.md) | Agent end-to-end | G4 | 17 | — | 1 | 04.3.1, 05.7.4 | — |
| [05.8.1](05-runs-orchestration/05.8.1-exclusive-device-lease.md) | Exclusive device lease | G4 | 10 | — | 3 | 05.7.3 | 04.5.1, 05.9.1 |
| [05.9.1](05-runs-orchestration/05.9.1-macos-ios-pool-spike.md) | macOS/iOS pool spike | G4 | 11 | — | 1 | 05.8.1 | — |
| [05.9.2](05-runs-orchestration/05.9.2-hosted-runner-spike.md) | Hosted runner spike | G4 | 17 | — | 1 | 11.2.1 | — |
| [05.10.1](05-runs-orchestration/05.10.1-detect-and-repair-drift.md) | Detect and repair drift | G2 | 17 | — | 2.5 | 05.3.2, 05.7.2, 06.4.3, 12.3.1 | — |
| [06.1.1](06-evidence-artifacts/06.1.1-artifact-record-and-server-chosen-keys.md) | Artifact record and server-chosen keys | G1 | 5 | — | 1.5 | 01.4.1, 02.6.1 | 04.2.4, 05.7.4, 06.1.2, 06.2.1, 06.2.2, 06.3.1, 06.4.1, 08.1.2 |
| [06.1.2](06-evidence-artifacts/06.1.2-presigned-upload-and-completion.md) | Presigned upload and completion | G1 | 10 | — | 2.5 | 04.1.2, 05.2.1, 06.1.1 | 03.8.1, 04.2.4, 04.4.1, 06.1.3, 06.2.3, 06.5.1, 09.2.1, 13.3.1, 13.6.1 |
| [06.1.3](06-evidence-artifacts/06.1.3-state-machine-guards.md) | State-machine guards | G2 | 11 | — | 0.5 | 06.1.2 | 05.7.4 |
| [06.2.1](06-evidence-artifacts/06.2.1-sensitivity-classification-and-access-control.md) | Sensitivity classification and access control | G2 | 8 | — | 1.5 | 02.2.1, 06.1.1, 06.3.3 | 09.4.1 |
| [06.2.2](06-evidence-artifacts/06.2.2-redaction-and-capture-control-at-source.md) | Redaction and capture control at source | G1 | 6 | — | 1 | 04.7.1, 06.1.1 | 11.3.1 |
| [06.2.3](06-evidence-artifacts/06.2.3-secret-masking.md) | Secret masking | G1 | 11 | — | 1 | 01.5.1, 06.1.2 | 11.3.1 |
| [06.3.1](06-evidence-artifacts/06.3.1-parser-hardening.md) | Parser hardening | G2 | 6 | — | 2 | 06.1.1 | 04.4.1, 06.3.2, 06.3.4, 06.3.6 |
| [06.3.2](06-evidence-artifacts/06.3.2-hostile-input-fixtures.md) | Hostile-input fixtures | G2 | 7 | — | 1.5 | 06.3.1 | — |
| [06.3.3](06-evidence-artifacts/06.3.3-isolated-viewer-origin.md) | Isolated viewer origin | G2 | 7 | — | 2 | 10.1.1, 11.1.1 | 06.2.1, 06.3.4, 09.4.1, 11.7.1 |
| [06.3.4](06-evidence-artifacts/06.3.4-rendering-reports-and-traces.md) | Rendering reports and traces | G2 | 8 | — | 2 | 06.3.1, 06.3.3 | — |
| [06.3.5](06-evidence-artifacts/06.3.5-mobile-artifact-kinds.md) | Mobile artifact kinds | G4 | 17 | — | 0.5 | 04.5.1, 05.7.4 | — |
| [06.3.6](06-evidence-artifacts/06.3.6-parsers-run-in-a-resource-limited-child.md) | Parsers run in a resource-limited child with per-tenant concurrency | G2 | 7 | — | 3.5 | 01.5.1, 06.3.1 | — |
| [06.4.1](06-evidence-artifacts/06.4.1-retention-policy.md) | Retention policy | G1 | 6 | — | 0.5 | 02.2.1, 06.1.1 | 06.4.2, 11.8.1 |
| [06.4.2](06-evidence-artifacts/06.4.2-retention-deletion-job.md) | Retention deletion job | G1 | 7 | — | 1 | 01.5.1, 06.4.1 | 06.4.3, 06.4.4, 06.4.5, 06.4.6 |
| [06.4.3](06-evidence-artifacts/06.4.3-orphan-sweeper.md) | Orphan sweeper | G1 | 8 | — | 1 | 06.4.2 | 05.10.1, 06.4.5 |
| [06.4.4](06-evidence-artifacts/06.4.4-tenant-deletion.md) | Tenant deletion | G1 | 16 | yes | 2.5 | 02.8.1, 06.4.2, 08.7.1, 10.5.1 | 11.9.1 |
| [06.4.5](06-evidence-artifacts/06.4.5-deletion-verification.md) | Deletion verification | G1 | 9 | — | 0.5 | 06.4.2, 06.4.3 | 11.9.1 |
| [06.4.6](06-evidence-artifacts/06.4.6-retention-and-partitioning-for-high-volume-tables.md) | Retention and partitioning for high-volume tables | G2 | 10 | — | 2.5 | 04.1.1, 06.4.2 | 03.9.1 |
| [06.5.1](06-evidence-artifacts/06.5.1-freeze-interface-with-05.md) | Freeze interface with 05 | G2 | 12 | yes | 2.5 | 05.3.2, 05.3.3, 06.1.2, 07.5.1 | 04.4.2, 05.3.2, 07.2.1, 09.3.1 |
| [06.5.2](06-evidence-artifacts/06.5.2-storage-usage-metrics.md) | Storage usage metrics | G1 | 16 | yes | 1.5 | 01.5.1, 10.3.1, 12.3.1 | 09.7.1, 12.3.1 |
| [07.1.1](07-policy-decisions/07.1.1-revision-record-hash-activation-and-pinning.md) | Revision record, hash, activation and pinning | G1 | 10 | — | 3 | 01.4.1, 02.6.1, 05.2.1 | 01.8.1, 05.3.2, 07.3.1, 07.8.1, 09.2.1, 09.7.1 |
| [07.2.1](07-policy-decisions/07.2.1-pure-evaluation-completeness-outcome-decision.md) | Pure evaluation: completeness, outcome, decision | G2 | 12 | yes | 2 | 05.3.2, 06.5.1 | 03.8.1, 05.6.1, 07.2.2, 07.3.2, 07.4.1 |
| [07.2.2](07-policy-decisions/07.2.2-persistence-idempotency-and-the-decision-matrix.md) | Persistence, idempotency and the decision matrix | G2 | 12 | yes | 2.75 | 01.5.1, 05.2.1, 07.2.1, 07.3.1 | 07.5.1, 07.5.2, 07.5.3, 07.6.1, 07.6.2, 07.7.1, 07.7.2, 08.3.3 |
| [07.3.1](07-policy-decisions/07.3.1-waiver-and-quarantine-lifecycles.md) | Waiver and quarantine lifecycles | G4 | 11 | — | 2 | 02.2.1, 07.1.1 | 07.2.2, 07.3.2, 09.3.1 |
| [07.3.2](07-policy-decisions/07.3.2-evaluator-effects-expiry-and-audit.md) | Evaluator effects, expiry and audit | G4 | 13 | — | 1.5 | 07.2.1, 07.3.1 | 09.3.1 |
| [07.4.1](07-policy-decisions/07.4.1-in-run-flaky-detection-and-budget.md) | In-run flaky detection and budget | G2 | 13 | — | 2 | 05.3.3, 07.2.1 | 05.5.1, 07.4.2, 09.3.1 |
| [07.4.2](07-policy-decisions/07.4.2-flaky-surface-and-cross-run-trend.md) | Flaky surface and cross-run trend | G4 | 14 | — | 1 | 05.3.3, 07.4.1 | — |
| [07.5.1](07-policy-decisions/07.5.1-late-evidence-and-immutability.md) | Late evidence and immutability | G2 | 12 | yes | 1.5 | 05.3.2, 07.2.2 | 06.5.1 |
| [07.5.2](07-policy-decisions/07.5.2-run-decision-api.md) | Run decision API | G3 | 13 | — | 0.5 | 01.3.1, 02.2.1, 07.2.2 | 07.6.1, 07.6.3, 09.3.1, 09.9.1 |
| [07.5.3](07-policy-decisions/07.5.3-release-aggregation-and-decision-endpoint.md) | Release aggregation and decision endpoint | G4 | 14 | — | 1.5 | 05.6.1, 07.2.2 | 07.5.4, 07.8.1, 09.3.1 |
| [07.5.4](07-policy-decisions/07.5.4-multi-component-releases-with-mobile-digests.md) | Multi-component releases with mobile digests | G4 | 15 | — | 0.5 | 04.5.1, 07.5.3 | — |
| [07.6.1](07-policy-decisions/07.6.1-ci-delivery-github-and-the-cli.md) | CI delivery: GitHub and the CLI gate | G4 | 14 | — | 2 | 02.4.1, 02.4.3, 04.2.1, 07.2.2, 07.5.2 | 07.7.1, 07.8.1 |
| [07.6.2](07-policy-decisions/07.6.2-outbound-webhooks-and-delivery-operations.md) | Outbound webhooks and delivery operations | G4 | 18 | — | 2 | 01.5.1, 07.2.2, 10.3.1, 10.6.1 | 07.6.3, 07.8.1, 09.7.1 |
| [07.6.3](07-policy-decisions/07.6.3-webhook-endpoint-management-and-status-badge.md) | Webhook endpoint management and status badge | G4 | 19 | — | 2.5 | 02.2.1, 02.7.1, 07.5.2, 07.6.2 | 09.7.1 |
| [07.7.1](07-policy-decisions/07.7.1-advisory-and-enforcing-modes.md) | Advisory and enforcing modes | G4 | 15 | — | 1 | 07.2.2, 07.6.1 | 07.7.2 |
| [07.7.2](07-policy-decisions/07.7.2-shadow-evaluation-divergence-and-rollback.md) | Shadow evaluation, divergence and rollback | G4 | 18 | — | 1.5 | 07.2.2, 07.7.1, 10.2.2 | — |
| [07.8.1](07-policy-decisions/07.8.1-promotion-gate-release-to-environment.md) | Promotion gate: release to environment | G4 | 19 | — | 4.5 | 02.1.1, 02.7.1, 07.1.1, 07.5.3, 07.6.1, 07.6.2 | 09.7.2 |
| [08.1.1](08-healing-learning/08.1.1-incident-record-from-verified-failures.md) | Incident record from verified failures | G2 | 13 | — | 2.5 | 05.2.1, 05.3.2 | 08.1.2, 08.2.2, 08.3.1, 09.4.1 |
| [08.1.2](08-healing-learning/08.1.2-filtered-context-capture-and-versioned-fingerprint.md) | Filtered context capture and versioned fingerprint | G2 | 14 | — | 6.5 | 06.1.1, 08.1.1 | 08.2.1, 08.4.2, 09.4.1, 13.6.1 |
| [08.2.1](08-healing-learning/08.2.1-tenant-scoped-retrieval-and-ranker.md) | Tenant-scoped retrieval and ranker | G2 | 15 | — | 2.5 | 08.1.2 | 08.3.1, 08.5.2, 08.7.1 |
| [08.2.2](08-healing-learning/08.2.2-triage-labels-with-provenance.md) | Triage labels with provenance | G3 | 14 | — | 4.5 | 08.1.1 | 03.8.1, 08.2.3, 08.5.1, 09.4.1, 12.5.1, 13.8.1 |
| [08.2.3](08-healing-learning/08.2.3-external-issue-reference-on-an-incident.md) | External issue reference on an incident | G3 | 19 | — | 1 | 08.2.2, 09.4.1 | — |
| [08.3.1](08-healing-learning/08.3.1-candidate-model-and-rule-based-generation.md) | Candidate model and rule-based generation | G2 | 16 | yes | 3 | 08.1.1, 08.2.1, 08.6.3 | 08.3.2, 08.3.3, 08.4.1, 09.3.1, 09.5.1, 13.8.1 |
| [08.3.2](08-healing-learning/08.3.2-bounded-validation-loop.md) | Bounded validation loop | G3 | 16 | yes | 3 | 08.3.1 | 08.4.3 |
| [08.3.3](08-healing-learning/08.3.3-shadow-only-operation-and-measurement.md) | Shadow-only operation and measurement | G3 | 21 | — | 3.5 | 07.2.2, 08.3.1, 08.6.3, 12.5.1 | — |
| [08.4.1](08-healing-learning/08.4.1-ast-allowlist-validator-for-playwright-ts.md) | AST allowlist validator for Playwright TS | G2 | 17 | — | 7 | 08.3.1 | 08.4.2, 08.5.4, 09.5.1, 13.5.1 |
| [08.4.2](08-healing-learning/08.4.2-assertion-hash-semantic-checks-and-negative.md) | Assertion hash, semantic checks and negative control | G3 | 18 | — | 5 | 08.1.2, 08.4.1 | 08.8.1, 09.5.1 |
| [08.4.3](08-healing-learning/08.4.3-sandbox-replay.md) | Sandbox replay | G3 | 16 | yes | 3.5 | 04.9.1, 05.2.1, 08.3.2, 11.2.1 | 08.5.4, 08.6.1, 08.8.1, 11.2.1 |
| [08.5.1](08-healing-learning/08.5.1-llm-gateway.md) | LLM gateway | G1 | 16 | yes | 4 | 08.2.2, 12.3.1 | 08.5.2, 08.5.3, 08.5.5, 08.6.3, 08.7.2, 11.4.1, 13.2.1, 13.3.1, 13.5.1, 13.7.2 |
| [08.5.2](08-healing-learning/08.5.2-untrusted-context-injection-containment-and-tenant.md) | Untrusted context, injection containment and tenant filter | G1 | 17 | — | 3 | 08.2.1, 08.5.1 | 11.4.1, 13.2.2 |
| [08.5.3](08-healing-learning/08.5.3-redaction-and-per-tenant-opt-in.md) | Redaction and per-tenant opt-in | G1 | 17 | — | 5 | 08.5.1 | 11.4.1, 13.2.2 |
| [08.5.4](08-healing-learning/08.5.4-playwright-healer-as-one-sandboxed-generator.md) | Playwright healer as one sandboxed generator | G4 | 18 | — | 2 | 08.4.1, 08.4.3 | — |
| [08.5.5](08-healing-learning/08.5.5-ai-credits-and-spend-control.md) | AI credits and spend control | G1 | 20 | — | 5.5 | 08.5.1, 08.6.3, 08.8.1, 10.7.1, 12.3.1 | 13.3.1, 13.4.1, 13.5.1, 13.9.1 |
| [08.6.1](08-healing-learning/08.6.1-validation-pr-and-approval-binding.md) | Validation PR and approval binding | G3 | 16 | yes | 6 | 03.4.2, 08.4.3, 08.6.3 | 08.6.2, 08.7.1, 09.5.2, 13.8.2 |
| [08.6.2](08-healing-learning/08.6.2-post-merge-canary-and-rollback.md) | Post-merge canary and rollback | G3 | 16 | yes | 3 | 08.6.1 | 08.7.1, 08.8.3 |
| [08.6.3](08-healing-learning/08.6.3-kill-switches-and-degradation.md) | Kill switches and degradation | G1 | 16 | yes | 3 | 08.5.1, 10.7.1 | 08.3.1, 08.3.3, 08.5.5, 08.6.1, 09.5.2, 10.7.1 |
| [08.7.1](08-healing-learning/08.7.1-knowledge-records-ranking-and-feedback.md) | Knowledge records, ranking and feedback | G3 | 16 | yes | 3.25 | 08.2.1, 08.6.1, 08.6.2 | 06.4.4, 08.7.2, 08.8.2 |
| [08.7.2](08-healing-learning/08.7.2-lineage-and-revocation-cascade.md) | Lineage and revocation cascade | G3 | 17 | — | 2 | 08.5.1, 08.7.1 | 09.5.2 |
| [08.8.1](08-healing-learning/08.8.1-evaluation-set-and-holdout.md) | Evaluation set and holdout | G3 | 19 | — | 4 | 08.4.2, 08.4.3 | 08.5.5, 08.8.2, 13.7.2, 13.8.1, 13.9.1 |
| [08.8.2](08-healing-learning/08.8.2-metrics-and-reporting.md) | Metrics and reporting | G3 | 20 | — | 3 | 08.7.1, 08.8.1 | 08.8.3 |
| [08.8.3](08-healing-learning/08.8.3-phase-gates.md) | Phase gates | G3 | 21 | — | 1.5 | 08.6.2, 08.8.2 | 08.9.1 |
| [08.9.1](08-healing-learning/08.9.1-narrow-context-reuse-of-approved-mappings.md) | Narrow-context reuse of approved mappings | G4 | 22 | — | 3 | 08.8.3 | 08.9.2 |
| [08.9.2](08-healing-learning/08.9.2-mobile-locator-healing.md) | Mobile locator healing | G4 | 23 | — | 2 | 04.5.1, 08.9.1 | — |
| [09.1.1](09-web-ui/09.1.1-app-scaffold-and-build-acceptance-npm.md) | App scaffold and build - acceptance: `npm ci && npm run build -w apps/web` runs `ng build` and produces a production bundle inside the W7 budgets; Vitest and Playwright configs run in CI (F-09-1). | G1 | 5 | — | 11 | 01.1.1, 01.3.2, 01.6.1, 02.3.1 | 09.2.1, 09.3.1, 09.8.1, 09.9.1, 11.5.1, 13.4.1 |
| [09.2.1](09-web-ui/09.2.1-onboarding-wizard-acceptance-a-new-tenant.md) | Onboarding wizard - acceptance: a new tenant reaches an enabled collecting project through the five steps, resumable, each step showing the run/evidence it produced. | G1 | 18 | — | 10.5 | 02.4.1, 02.4.2, 03.1.1, 03.1.2, 04.4.1, 06.1.2, 07.1.1, 09.1.1, 09.3.1 | 12.2.1 |
| [09.3.1](09-web-ui/09.3.1-run-detail-is-the-truth-surface.md) | Run detail is the truth surface - acceptance: for any run the page shows manifest, build digests, per-producer finalize state, every attempt and the stored evaluation without computing anything client-side. | G2 | 17 | — | 15 | 05.2.1, 05.3.1, 05.3.2, 05.4.1, 05.5.1, 06.5.1, 07.3.1, 07.3.2, 07.4.1, 07.5.2, 07.5.3, 08.3.1, 09.1.1 | 09.2.1, 09.4.1, 09.5.1, 09.5.2 |
| [09.4.1](09-web-ui/09.4.1-triage-without-losing-the-original-failure.md) | Triage without losing the original failure - acceptance: from the inbox a failure can be labelled, assigned, investigated and either fixed or (V2) sent to healing, with the original evidence intact. | G3 | 18 | — | 8 | 06.2.1, 06.3.3, 08.1.1, 08.1.2, 08.2.2, 09.3.1 | 08.2.3, 13.8.2 |
| [09.5.1](09-web-ui/09.5.1-shadow-review-v1-acceptance-candidates-are.md) | Shadow review (V1) - acceptance: candidates are inspectable with validation and lineage, and no control can apply a patch in V1. | G3 | 19 | — | 4 | 08.3.1, 08.4.1, 08.4.2, 09.3.1 | 13.8.2 |
| [09.5.2](09-web-ui/09.5.2-approval-v2-acceptance-approve-reject-binds.md) | Approval (V2) - acceptance: approve/reject binds to `candidate_hash` + base commit, is re-permissioned, and a stale hash is rejected with a visible conflict. | G3 | 18 | — | 4.5 | 08.6.1, 08.6.3, 08.7.2, 09.3.1 | — |
| [09.6.1](09-web-ui/09.6.1-draft-to-review-to-merge-acceptance.md) | Draft to review to merge - acceptance: a draft created from a template can be edited, reviewed as a diff with its assertion hash, dry-run and then approved by another person. | G4 | 20 | — | 8 | 03.2.1, 03.5.1, 03.6.1, 03.7.1 | — |
| [09.7.1](09-web-ui/09.7.1-administer-without-seeing-secrets-acceptance-an.md) | Administer without seeing secrets - acceptance: an admin manages members, integrations, policy, quota, retention and audit; no screen can render a secret value. | G1 | 20 | — | 11.5 | 02.1.1, 02.1.2, 02.2.1, 02.4.2, 02.7.1, 02.8.1, 05.7.3, 06.5.2, 07.1.1, 07.6.2, 07.6.3, 11.5.1, 12.3.1 | 09.7.2 |
| [09.7.2](09-web-ui/09.7.2-api-tokens-and-deploy-targets-screens.md) | API tokens and deploy targets screens | G4 | 21 | — | 2 | 02.4.4, 07.8.1, 09.7.1 | — |
| [09.8.1](09-web-ui/09.8.1-core-flows-are-usable-and-proven.md) | Core flows are usable and proven end to end - acceptance: axe-clean core flows, keyboard-only completion, and an E2E suite green in Evidgate's CI whose results are ingested into Evidgate. | G3 | 6 | — | 6 | 09.1.1 | — |
| [09.9.1](09-web-ui/09.9.1-quality-overview-for-managers.md) | Quality overview for managers | G4 | 14 | — | 2.5 | 03.9.1, 07.5.2, 09.1.1 | — |
| [10.1.1](10-operations/10.1.1-three-environments.md) | Three environments | G1 | 3 | — | 5 | 01.1.2 | 06.3.3, 10.1.2, 10.3.1, 10.5.1, 10.7.1, 11.3.1, 11.7.1 |
| [10.1.2](10-operations/10.1.2-secrets-inventory-scanning-and-break-glass.md) | Secrets inventory, scanning and break-glass | G1 | 17 | — | 2.75 | 01.7.1, 10.1.1, 10.2.1, 10.7.1 | — |
| [10.2.1](10-operations/10.2.1-images-and-provenance.md) | Images and provenance | G1 | 3 | — | 3 | 01.1.2 | 10.1.2, 10.2.2, 11.6.1 |
| [10.2.2](10-operations/10.2.2-deploy-with-expand-contract-and-canary.md) | Deploy with expand/contract and canary | G1 | 17 | — | 7 | 01.4.1, 10.2.1, 10.3.1 | 07.7.2, 11.7.1 |
| [10.3.1](10-operations/10.3.1-telemetry-and-alerting.md) | Telemetry and alerting | G1 | 16 | yes | 7 | 10.1.1, 10.3.2 | 06.5.2, 07.6.2, 10.2.2, 10.3.2, 10.4.1, 10.6.1, 10.7.1, 10.7.2, 10.9.1, 10.10.1, 11.5.2, 12.4.1 |
| [10.3.2](10-operations/10.3.2-observability-backend-and-external-probe.md) | Observability backend and external probe | G1 | 16 | yes | 2 | 10.3.1, 10.4.1, 11.9.1 | 10.3.1 |
| [10.4.1](10-operations/10.4.1-measured-service-targets.md) | Measured service targets | G1 | 16 | yes | 2 | 10.3.1 | 10.3.2 |
| [10.5.1](10-operations/10.5.1-recoverability.md) | Recoverability | G1 | 4 | — | 5 | 10.1.1 | 06.4.4, 10.5.2, 10.6.1, 10.7.1, 11.3.1 |
| [10.5.2](10-operations/10.5.2-object-storage-delete-protection-and-restore.md) | Object storage delete protection and restore | G2 | 5 | — | 2.5 | 10.5.1 | — |
| [10.6.1](10-operations/10.6.1-proving-the-pilot-load.md) | Proving the pilot load | G2 | 17 | — | 5 | 10.3.1, 10.5.1 | 07.6.2, 12.4.1 |
| [10.7.1](10-operations/10.7.1-operable-by-a-second-person.md) | Operable by a second person | G1 | 16 | yes | 5.5 | 01.5.1, 08.6.3, 10.1.1, 10.3.1, 10.5.1 | 02.5.1, 08.5.5, 08.6.3, 10.1.2, 11.3.1 |
| [10.7.2](10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md) | Paging and on-call at the first alert | G2 | 17 | — | 1.5 | 10.3.1, 11.9.1 | — |
| [10.8.1](10-operations/10.8.1-decision-memo-no-vendor-chosen.md) | Decision memo, no vendor chosen | G1 | 1 | — | 3 | — | 10.11.1, 11.8.1 |
| [10.9.1](10-operations/10.9.1-unit-economics.md) | Unit economics | G2 | 17 | — | 1.5 | 10.3.1 | — |
| [10.10.1](10-operations/10.10.1-trigger-and-spike.md) | Trigger and spike | G4 | 17 | — | 1.5 | 10.3.1 | — |
| [10.11.1](10-operations/10.11.1-ingress-and-abuse-limits.md) | Ingress and abuse limits | G1 | 10 | — | 2 | 04.1.2, 10.8.1 | — |
| [11.1.1](11-security-compliance/11.1.1-a-living-threat-model-acceptance-v1.md) | A living threat model - acceptance: v1 covers every module and boundary, each threat maps to a control or a written acceptance, and it is re-reviewed at each milestone exit. | G1 | 6 | — | 4 | 02.1.1, 02.6.1, 02.7.1 | 06.3.3, 11.5.1, 11.7.1 |
| [11.2.1](11-security-compliance/11.2.1-a-fork-pull-request-cannot-reach.md) | A fork pull request cannot reach our credentials - acceptance: the published recipe and the fork-PR test prove no Evidgate credential is reachable from an untrusted workflow context. | G1 | 16 | yes | 7.5 | 02.4.1, 02.4.2, 02.6.1, 03.4.1, 05.7.4, 08.4.3 | 04.9.1, 05.9.2, 08.4.3, 11.6.1 |
| [11.3.1](11-security-compliance/11.3.1-secrets-never-leave-a-reference-acceptance.md) | Secrets never leave a reference - acceptance: no secret value is stored in the database, logged, or visible in an error envelope; leak response is rehearsed. | G1 | 16 | yes | 6 | 02.4.2, 04.1.1, 06.2.2, 06.2.3, 10.1.1, 10.5.1, 10.7.1 | 02.10.2, 03.3.1, 11.4.1 |
| [11.4.1](11-security-compliance/11.4.1-a-model-cannot-change-the-product.md) | A model cannot change the product's mind - acceptance: the injection corpus produces zero policy/approval/permission/decision effects, and every outbound request is opt-in, redacted and audited. | G1 | 18 | — | 6 | 08.5.1, 08.5.2, 08.5.3, 11.3.1, 11.8.1 | 03.7.1, 13.3.1 |
| [11.5.1](11-security-compliance/11.5.1-who-saw-what-provably-acceptance-audit.md) | Who saw what, provably - acceptance: audit rows are append-only with before/after hashes, and any operator access is scoped, expiring, tenant-visible and audited. | G1 | 7 | — | 4.5 | 02.7.1, 02.8.1, 09.1.1, 11.1.1 | 09.7.1, 11.5.2, 11.9.1 |
| [11.5.2](11-security-compliance/11.5.2-audit-chain-integrity-outside-its-own-database.md) | Audit chain integrity outside its own database | G1 | 17 | — | 2.5 | 10.3.1, 11.5.1 | 11.9.2 |
| [11.6.1](11-security-compliance/11.6.1-every-dependency-is-a-decision-acceptance.md) | Every dependency is a decision - acceptance: licence allowlist and lockfile enforced in CI, actions pinned, SBOM produced per release. | G1 | 17 | — | 2.25 | 01.1.1, 01.7.1, 10.2.1, 11.2.1 | — |
| [11.7.1](11-security-compliance/11.7.1-proved-against-isolation-not-asserted-acceptance.md) | Proved against isolation, not asserted - acceptance: the tenant-isolation red-team suite is green in CI, and an external pen test report exists with criticals/highs closed before pilot exit. | G4 | 18 | — | 6 | 01.6.1, 01.7.1, 02.9.1, 06.3.3, 10.1.1, 10.2.2, 11.1.1 | — |
| [11.8.1](11-security-compliance/11.8.1-the-paperwork-exists-before-the-data.md) | The paperwork exists before the data does - acceptance: DPA, privacy notice, data map, dossiers and sub-processor list are versioned, counsel-reviewed and available before the first pilot upload. | G1 | 7 | — | 9 | 06.4.1, 10.8.1 | 11.4.1, 11.9.1, 12.2.1 |
| [11.9.1](11-security-compliance/11.9.1-deletion-and-breach-are-runnable-procedures.md) | Deletion and breach are runnable procedures - acceptance: a deletion request produces a verifiable report, and a breach rehearsal completes the processor→controller notice inside the runbook's target. | G1 | 16 | yes | 5.5 | 06.4.4, 06.4.5, 11.5.1, 11.8.1 | 10.3.2, 10.7.2, 11.9.2 |
| [11.9.2](11-security-compliance/11.9.2-data-subject-requests-pdpl.md) | Data-subject requests (PDPL) | G1 | 18 | — | 2 | 11.5.2, 11.9.1 | — |
| [12.1.1](12-pilot-commercial/12.1.1-real-problems-real-reports-acceptance-5.md) | Real problems, real reports - acceptance: 5+ interviews recorded, real reports and failure samples collected with permission, and the CI/engine choice (O1) signed off by named teams. | G4 | 1 | — | 5 | — | — |
| [12.2.1](12-pilot-commercial/12.2.1-a-pilot-that-can-be-measured.md) | A pilot that can be measured and paid for - acceptance: agreement + DPA signed before any upload, baseline captured for two sprints, weekly reviews held, support hours metered. | G1 | 19 | — | 5.5 | 09.2.1, 11.8.1, 12.3.1 | 12.5.1 |
| [12.3.1](12-pilot-commercial/12.3.1-every-billed-unit-is-a-row.md) | Every billed unit is a row - acceptance: each metered metric produces exactly one ledger row per source with the applied price, and quota enforcement warns then blocks without losing data. | G1 | 16 | yes | 8.5 | 02.1.1, 04.8.1, 06.5.2 | 02.10.1, 04.8.1, 05.10.1, 06.5.2, 08.5.1, 08.5.5, 09.7.1, 12.2.1, 12.4.1, 12.5.1 |
| [12.4.1](12-pilot-commercial/12.4.1-cost-is-known-before-price-is.md) | Cost is known before price is set - acceptance: measured cost per tenant, a pricing hypothesis pack, and manual invoices that reconcile to the ledger. | G2 | 18 | — | 4.5 | 10.3.1, 10.6.1, 12.3.1 | — |
| [12.5.1](12-pilot-commercial/12.5.1-prove-the-value-or-stop-acceptance.md) | Prove the value or stop - acceptance: the M3 exit review reports every metric with its method, and a go/no-go for V2 with named conditions. | G3 | 20 | — | 4.5 | 08.2.2, 12.2.1, 12.3.1 | 08.3.3 |
| [12.6.1](12-pilot-commercial/12.6.1-evidgate-is-ours-to-use-acceptance.md) | "Evidgate" is ours to use - acceptance: a search is done in classes 9/42, a VN application is filed before public launch, and the international decision is recorded. | G1 | 1 | — | 3.5 | — | — |
| [13.1.1](13-product-phases/13.1.1-feature-tree-and-manual-test-cases.md) | Feature tree and manual test cases | G1 | 6 | — | 6.5 | 01.4.1, 02.1.1, 02.2.1, 02.7.1, 03.1.1, 03.1.3 | 13.1.2, 13.2.1, 13.3.1, 13.4.1, 13.5.1, 13.9.1 |
| [13.1.2](13-product-phases/13.1.2-batched-review.md) | Batched review | G1 | 7 | — | 1.5 | 13.1.1 | 13.4.1, 13.8.1 |
| [13.2.1](13-product-phases/13.2.1-versioned-project-ai-settings.md) | Versioned project AI settings | G1 | 17 | — | 1.5 | 08.5.1, 13.1.1 | 13.2.2, 13.3.1, 13.4.1 |
| [13.2.2](13-product-phases/13.2.2-role-profiles-and-prompt-library.md) | Role profiles and the role prompt library | G1 | 23 | — | 9.5 | 02.2.1, 02.7.1, 05.3.2, 08.5.2, 08.5.3, 13.2.1, 13.3.1, 13.4.1 | — |
| [13.3.1](13-product-phases/13.3.1-generation-pipeline.md) | Generation pipeline | G1 | 21 | — | 7 | 06.1.2, 08.5.1, 08.5.5, 11.4.1, 13.1.1, 13.2.1 | 13.2.2, 13.4.1 |
| [13.4.1](13-product-phases/13.4.1-authoring-ui.md) | Authoring UI | G1 | 22 | — | 10 | 08.5.5, 09.1.1, 13.1.1, 13.1.2, 13.2.1, 13.3.1 | 13.2.2 |
| [13.5.1](13-product-phases/13.5.1-bound-playwright-scripts.md) | Test case → bound Playwright script | G2 | 21 | — | 9.5 | 03.4.2, 03.7.1, 04.2.1, 04.3.1, 08.4.1, 08.5.1, 08.5.5, 13.1.1 | 13.9.1 |
| [13.5.2](13-product-phases/13.5.2-local-runs.md) | Local runs | G2 | 8 | — | 2.5 | 04.2.1, 04.3.1 | — |
| [13.6.1](13-product-phases/13.6.1-element-profiles.md) | Element profiles | G2 | 15 | — | 5 | 04.3.1, 06.1.2, 08.1.2 | 13.7.2, 13.8.1 |
| 13.7.1 (feature plan pending) | Mobile spike (mobile-mcp / mobilewright) | G2 | 1 | — | 2 | — | — |
| 13.7.2 (feature plan pending) | Multimodal locate fallback | G3 | 20 | — | 3 | 08.5.1, 08.8.1, 13.6.1 | — |
| [13.8.1](13-product-phases/13.8.1-locator-drift-vs-ui-change.md) | Locator drift vs UI change | G3 | 20 | — | 5 | 08.2.2, 08.3.1, 08.8.1, 13.1.2, 13.6.1 | 13.8.2, 13.9.1 |
| 13.8.2 (feature plan pending) | Review evidence | G3 | 21 | — | 2 | 08.6.1, 09.4.1, 09.5.1, 13.8.1 | — |
| 13.9.1 (feature plan pending) | Gate metrics | G1 | 22 | — | 1.5 | 08.5.5, 08.8.1, 13.1.1, 13.5.1, 13.8.1 | — |
