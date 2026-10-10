# 10 - Operations: environments, deploy, observability, backup and capacity (M1-M3)

> Contracts: `00-overview.md` §4 (milestones, exit gates), §5.1-§5.3, §8, §11 (targets), §12. Facts: `../DECISIONS.md` §4 (licence rule, PostgreSQL 18, OpenTelemetry), §5.9 (kill switch before apply), §7, §9, A7 (no Kubernetes/Temporal in V1), A10, F3, F5, F6, O2, O3. Source: design §9 (deploy, capacity, SLI, recovery) and §9 acceptance table ✅ (a ✅ attached to a `Source:` reference means the passage was read in this task; a ✅ elsewhere is an external primary source with a link).
> Level: high-level plan; each story gets a detailed plan in `plans/features/` (00 §12) before build.
> Source numbers kept verbatim (recomputed in this task ✅): 5 projects × 10 runs/day × 2 000 results = 100 000 results/day ≈ 98 MiB/day ≈ 8.6 GiB/90 days before index/retry/backup; 50 runs × 20 artifacts × 2 MB = 2 GB/day, 14-day retention ≈ 28 GB; burst 50 000 events/60 s ≈ 833 events/s. These are assumptions, not a benchmark.

## 1. Purpose and scope

**Goal:** run Evidgate so that a pilot customer's evidence is never lost, a bad release is stopped before customers see it, and the team can reconstruct what happened from telemetry alone.

**In scope:** dev/staging/prod environments and configuration; container images pinned by digest; deployment with expand/contract migration ordering; canary rollout with shadow decision compare; OpenTelemetry collector, dashboards and alerts (queue age, lease loss, incomplete rate, parser errors, artifact failures, healing false positives); SLIs from 00 §11; backup with PITR and scheduled restore drills (RPO 15 min / RTO 4 h, drill passed before the pilot); the 50 000 events/60 s load test; the capacity model (recomputed source §9 numbers); runbooks (dead-letter queue, stuck runs, orphan artifacts, key rotation, kill switch); a short cloud-provider evaluation for O3 that produces a memo and chooses no vendor; cost monitoring that feeds plan 12; the Temporal re-evaluation trigger of A7.

**Out of scope:** application behaviour and business logic (02-09), the customer's own CI setup (04), threat model, DPA and PDPL filings (11), pricing itself (12), Kubernetes and any multi-region active-active design (A7).

**V1/V2/V3 split:** V1 = everything needed to run the M3 pilot (envs, images, deploy, canary, telemetry, alerts, SLIs, backup + drill, load test, runbooks, provider memo, tagging). V2 = soak/back-pressure test (10.6.1-T4) and LLM/AI cost attribution (10.9.1-T2). V3 = the Temporal evaluation spike if its trigger fires (10.10.1-T2).

## 2. Requirements

- **FR-10-1** Three environments with separate credentials and data: dev (local stack, 01.8.1-T2), staging (own account/secrets, sanitised data), prod (load balancer, ≥2 `Evidgate.Api` replicas, ≥1 `Evidgate.Worker` replica — ASP.NET Core minimal-API/worker processes — managed PostgreSQL with backup, object storage) — source §9 ✅ (F-10-4).
- **FR-10-2** Images are built once, pinned by digest, and promoted unchanged dev → staging → prod; `latest` is never deployed.
- **FR-10-3** A deploy runs expand migrations before the new version takes traffic — the `Evidgate.Migrator` image (DbUp 7, checksum journal) runs as the owner role (01.4.1-T2) and the expand gate verifies the journal head; contract migrations ship at least one release later; a rollback never requires a schema change and never runs a destructive migration while the previous version is live — source §9 ✅ (F-10-2).
- **FR-10-4** Policy-revision rollouts (and any change that can alter a decision) use canary + shadow decision compare: the same evidence revision is evaluated under the incoming policy revision in shadow, the decisions are diffed, and the diff must be empty or explicitly approved per project before promotion — source §9 ✅.
- **FR-10-5** Telemetry is OTel traces/metrics/logs carrying `request_id`, `run_id`, `job_id`, `trace_id` (00 §11); no secret appears in a log field or a metric label (00 §11, source §9 ✅).
- **FR-10-6** Alerts exist for `task`-queue age, lease loss rate, incomplete rate, parser errors, artifact failures, healing false positives (source §9 ✅) plus `outbox` dispatch lag and dead-letter depth — all measured on Evidgate's own `task`/`outbox` tables (`FOR UPDATE SKIP LOCKED`, retry cap, dead-letter; 00 §5.3) — each linking to its runbook (F-10-4).
- **FR-10-7** The SLIs of 00 §11 are measured continuously: ingest p95 < 1 s per batch within limits, decision p95 < 60 s from complete evidence, availability 99.5 %/month on core endpoints (explicitly not the test pass rate) — source §9 ✅ — plus web-vitals SLIs (LCP, INP) recorded from plan 09's browser telemetry against the 00 §5.6 W7 budgets (T39, F-09-6).
- **FR-10-8** Backup with PITR where the provider supports it, plus a scheduled restore verification; a full restore drill (RPO 15 min / RTO 4 h) passes before the pilot — source §9 ✅ and 00 §4 M2 exit gate.
- **FR-10-9** The burst acceptance load test (50 000 events in 60 s, batches within the 00 §9.3 limits of ≤ 500 events and ≤ 1 MiB) passes on staging before the pilot, and the capacity model is kept current with the source §9 numbers ✅.
- **FR-10-10** Runbooks exist and are exercised at least once: dead-letter triage, stuck run, orphan artifact, key rotation, kill switch, incident/postmortem.
- **FR-10-11** Healing availability (settable `enabled | shadow | disabled`; `unavailable` is derived, 00 §8.7) is switchable at system, tenant and project level before any apply capability ships, and a healing failure never blocks ingest, runs or gates — DECISIONS §5.9, source §9 ✅ (T33).
- **FR-10-12** Cost is monitored per environment and per unit (1000 events, artifact GB-month, run, LLM token) and the model is handed to plan 12 for pricing.
- **FR-10-13** O3 produces a decision memo comparing at least two providers on residency, PITR, S3 compatibility, egress and cost; no vendor is selected in this plan.
- **NFR-10-1** Operator reconstruction: for any run, decision or dead-letter row, the telemetry + audit trail explains what happened without reading application logs by hand.
- **NFR-10-2** Alert-to-runbook coverage 100 %: an alert without a runbook link fails the dashboard review.

**Acceptance criteria (testable):** a worker killed mid-run leaves no lost acknowledged event and the run settles in a terminal state from 00 §8.1; a dead-letter row is triaged and replayed with one side effect; the restore drill restores a backup into a clean environment within RTO and the record is attached to the M2 exit gate; the load test absorbs 50 000 events in 60 s without error-rate breach; the kill switch flips healing to `disabled` while runs and gates continue; a canary that changes a decision is blocked by the shadow compare; a deliberately mis-typed policy field in the canary produces an alert within one evaluation window.

## 3. Key decisions

- **No Kubernetes in V1-V2** [DECISION: A7] — containers on a managed runtime with a load balancer; source §9 ✅ says Kubernetes is not required while simple operation suffices. Rejected: managed K8s (operational surface for one team), serverless functions (long-running webhook and dispatcher work, connection pooling against PostgreSQL).
- **Migrations as a one-shot job, not an app-start side effect** [DECISION] — the deploy runs the `Evidgate.Migrator` image (DbUp 7, checksum journal) with the owner role and only then starts the new version; the app role has no DDL rights (01.4.1-T2). Rejected: migrate-on-boot (two replicas racing, no dry run, deploy and schema change indistinguishable) (F-10-2).
- **Deploy artefacts: three images + one static bundle** [DECISION] — `dotnet publish` produces the `Evidgate.Api`, `Evidgate.Worker` and `Evidgate.Migrator` images; `apps/web` ships as a static bundle on object storage + CDN (T41). Configuration is injected as environment variables and validated at boot (01.1.2-T1); no per-environment code branch.
- **Shadow decision compare is mandatory for anything that can change a decision** [DECISION] — policy revisions, evaluator changes, adapter normalisation changes. Rejected: trusting unit tests alone (a decision change is a product-visible event), and comparing on production after rollout.
- **OpenTelemetry collector as the only egress for telemetry** [DECISION] — applications export OTLP to the collector; the backend is swap­pable and no vendor SDK enters application code (DECISIONS §4 row). Rejected: vendor agents per host, direct app → SaaS export (secret/redaction risk, licence lock-in).
- **Alert threshold ownership** [DECISION] — every alert has a numeric threshold, an owner and a runbook link in one file per environment; an alert without all three is not merged.
- **Load tests run against staging only** [DECISION] — with a synthetic tenant; production is never the load-test target (a run's evidence must stay trustworthy).
- **PITR is a provider selection criterion** [DECISION] — O3 shortlist scores PITR, residency and S3 compatibility before price; a provider without PITR cannot be the M3 pilot provider.
- **Temporal stays out until its trigger fires** [DECISION: A7] — epic 10.10 defines the trigger (multi-step workflows with long human waits and recovery that our queue plus outbox cannot maintain) and owns the spike.

## 4. Design

**Data owned:** plan 10 owns no business tables. It owns `infra/` (container definitions, compose, collector config, dashboards, alert rules — see 01 §13 CR-01-3), the deploy workflow, and `docs/runbooks/`. Database objects it touches are only those created by 01.4 (roles, RLS helpers); module tables stay with 02-08.

**Operational endpoints used:** `GET /internal/health/live`, `GET /internal/health/ready`, `GET /metrics` (outside `/v1`, per 01 §13 CR-01-4), implemented as ASP.NET Core health checks (`AddHealthChecks`/`MapHealthChecks` with Npgsql, object-store and DbUp-journal-head checks) (F-10-4). Readiness is green only with a reachable database, migrations at the expected head and a writable object store.

**Flows:**
1. *Build → promote:* multi-stage `dotnet publish` images for api, worker and migrator with digest pins + CycloneDX SBOM → push; publish the `apps/web` static bundle to object storage + CDN → staging deploy (`Evidgate.Migrator` expand migrations → journal-head check → canary 1 replica → shadow decision compare → promote) → prod deploy with the same digests (T41, F-10-1/F-10-2).
2. *Decision-change rollout:* replay a fixed evidence set through the incoming policy revision in shadow; diff decisions; empty diff or approved diff → promote; otherwise abort and keep the current revision.
3. *Alert → runbook:* metric breach → routing → runbook step → operator action recorded (DLQ replay, kill switch, restore).
4. *Restore drill:* take a PITR backup → restore into a clean project → apply migrations head → run the tenant-isolation + smoke suite → record duration against RPO/RTO.
5. *Cost:* resource tags → per-unit consumption rows (usage module, 12) → monthly cost report per environment and per tenant.

**States:** plan 10 introduces no business state. The internal task lifecycle (queued → active → retry → dead_letter → replayed) and healing availability values come from 00 §5.3 and §8.7 unchanged.

## 5. Failure modes and edge cases

| Case | Handling | Invariant kept |
|---|---|---|
| Deploy fails after expand migration | Nothing destructive ran; old version keeps serving; contract migration is skipped until a later release — source §9 ✅ | Rolling back code never needs schema rollback |
| Canary changes a decision | Shadow compare blocks promotion; diff and evaluation ids are attached to the incident | A decision never changes silently |
| Worker crash mid-run | Job lease expires, epoch bumps, events from the old epoch are stored as stale evidence and never complete the job (00 §5.3, F6) | No lost acknowledged event; no double completion |
| Object storage outage | Uploads fail with `retryable` errors; artifacts stay `pending_upload` and are reconciled later (00 §8.4) | An object without `ready` is never evidence |
| Database lost | Restore from PITR to the last durable point; acknowledgment is not claimed past the measured RPO — source §9 ✅ | Disaster contract is RPO-based, not absolute |
| Queue/outbox backlog | Queue-age alert → runbook; dispatcher is scaled or throughput is shed by quota (00 §9.3 429/503) | Ingest keeps accepting within quota instead of failing open |
| Alert storm | Alerts are grouped per service and per tenant-class; thresholds have owners and are tuned after every drill | Operators keep trusting alerts |
| Healing produces a false positive | False-positive metric (10.3.1-T3) → kill switch to `disabled`/`shadow` per tenant or project; runs and gates continue — DECISIONS §5.9 | Evidence and gates never depend on healing |
| Monthly cost spike (artifacts, video, traces) | Per-unit cost report, retention policy and quota enforcement (06/12) reviewed monthly | Unit economics stay visible before pricing locks |
| Restore drill fails | Treated as a P1 with a fix task; the pilot cannot start (00 §4 M2 gate) | The gate is a condition, not a schedule |
| Provider cannot provide residency/PITR | Recorded in the O3 memo as a disqualifier; plan 11 residency options reopened | No vendor is chosen by default |

## 6. Security and privacy

- Least privilege in deploy: the deploy identity may push images and run the migration job, nothing else; the app role has no DDL (01.4.1-T2); migrations never run against prod from a developer machine.
- Secrets come from the environment's secret store, injected at runtime and never baked into an image or written to logs/metrics (00 §11); rotation is a runbook (10.7.1-T4).
- Telemetry is redacted at the collector: `authorization`, cookie and `EVIDGATE_*` values are dropped; trace attributes never carry artifact content (06 owns artifact redaction).
- Operator/support access to a tenant is scoped, time-boxed and audited (00 §2 `platform_operator`), and the access request is visible in telemetry.
- Artifact access stays on short-lived presigned URLs (DECISIONS §4 ✅); expiry and 403 rates are monitored to catch leaked links.
- Cross-border transfer and residency are plan 11's; this plan only guarantees that a provider change can be stated in the O3 memo before any customer data lands (O3, DECISIONS §8).

## 7. Test strategy

- **Drills executed as tests (staging):** dead-letter triage and replay; stuck run (kill the worker mid-run) per source §9 acceptance ✅; object outage simulation; PITR restore timed against RTO; code rollback with the schema already expanded; agent upgrade with a leased job; kill switch flip while ingest continues.
- **Load:** 50 000 events in 60 s with valid batches; sustained artifact upload at the pilot profile (2 GB/day, ≥ 20 MB artifacts); soak and back-pressure in V2; results recorded in the capacity model.
- **Alert tests:** each alert rule is triggered synthetically in staging and must fire with the right severity and runbook link within one evaluation window.
- **Shadow compare test:** a deliberately different policy revision must produce a decision diff that blocks promotion.
- **Restore evidence:** the drill record (duration, point restored, verification result) is attached to the M2 exit gate and kept for the pilot review.
- **Fixtures:** the synthetic tenant used for load tests is a fixture set owned jointly with 05 (runs) and 06 (artifacts).

## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Provider lacks PITR or residency (O3 open) | Medium | High | PITR/residency are selection criteria, not preferences; memo before M2 (10.8.1-T3) |
| RTO unproven until the first real drill | High | High | Drill in M2, repeat quarterly; automation task 10.5.1-T3 |
| Alert noise hides the real signal during the pilot | High | Medium | Small initial rule set, owners, post-drill tuning; every alert has a runbook (NFR-10-2) |
| Artifact storage cost grows faster than revenue | Medium | Medium | Retention policy + per-unit cost report; video/trace quotas (06/12) |
| Healing false positives invisible in aggregate | Medium | High | Dedicated metric + threshold (10.3.1-T3), reviewed weekly during shadow (08) |
| Single operator bus factor (O2 open) | High | Medium | Runbooks written for a second person; drill handover at M3 |
| Canary/shadow compare slows every policy rollout | Medium | Low | Automate the compare as a pipeline step, not a manual checklist |
| Telemetry backend lock-in costs or licence violation | Medium | Medium | OTLP-only egress; backend licence checked against the DECISIONS §4 rule before adoption |

## 9. Epics, stories and tasks

### Epic 10.1 Environments and configuration (M1-M2)

#### Story 10.1.1 Three environments — acceptance: staging and prod have separate credentials and data; a config error stops the boot with a non-zero exit
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.1.1-T1 | Environment model and configuration matrix: dev/staging/prod differences, `EVIDGATE_*` schema validated at boot, per-environment inventory doc | M | V1 | M1 | 01.1.2-T1 |
| 10.1.1-T2 | Secret handling behind one interface (provider-agnostic: env injection first, managed secret store when O3 lands) with no values in the repo or images | M | V1 | M1 | 10.1.1-T1 |
| 10.1.1-T3 | Environment bootstrap automation: database + owner/app/operator roles (01.4.1-T2), object storage buckets and policies, secret placeholders, network rules | L | V1 | M2 | 10.1.1-T2 |
| 10.1.1-T4 | Isolated artifact-viewer origin: separate registrable domain, DNS record and TLS certificate, hosting/deploy of the viewer bundle, CSP/CORS headers, no app cookies on that origin | M | V1 | M2 | - |

Note: 10.1.1-T4 owns the origin's infrastructure (domain, DNS, TLS, hosting, headers); the viewer application and its iframe/CSP behaviour stay in 06.3.3-T1, which already lists the 10 DNS/TLS dependency. Static hosting for `apps/web` is settled by T41: the Angular build is a static bundle on object storage + CDN (closes 09 §11 #3).

#### Story 10.1.2 Secrets inventory, scanning and break-glass — acceptance: every secret has an owner, a store and a rotation cadence in one checked inventory, pushes containing secrets are blocked, a documented break-glass path exists, and built images are scanned for known vulnerabilities.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.1.2-T1 | Secret inventory (name, owner, store, rotation cadence, break-glass holder) as a machine-readable file; CI check that every secret in `infra/config-matrix.json` appears in it | M | V1 | M1 | 10.1.1-T2 |
| 10.1.2-T2 | Enable repository secret scanning and push protection and record the setting and who can bypass it | XS | V1 | M1 | 01.7.1-T1 |
| 10.1.2-T3 | Break-glass procedure for secrets: sealed offline copy, who may open it, forced rotation after use, audit row | S | V1 | M2 | 10.1.2-T1, 10.7.1-T4 |
| 10.1.2-T4 | Image vulnerability scan on built images in CI with a severity gate and a documented exception list (tool licence checked per DECISIONS §4 ⚠️) | M | V1 | M2 | 10.2.1-T1 |

**Story 10.1.2 total: 2.75** — `[INFERENCE]` sizes; added from review 2026-10-02 §5.4. Triage SLA and base-image refresh cadence stay in 11.6.1-T4.

### Epic 10.2 Images, registry and deployment (M1-M2)

#### Story 10.2.1 Images and provenance — acceptance: one image per app built once, pinned by digest, promoted unchanged; `latest` never reachable in a deploy file
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.2.1-T1 | Container images for `src/Evidgate.Api`, `src/Evidgate.Worker` and `src/Evidgate.Migrator`: multi-stage `dotnet publish` on a pinned `mcr.microsoft.com/dotnet/sdk:10.0` builder, runtime on a digest-pinned chiseled non-root `mcr.microsoft.com/dotnet/aspnet:10.0` (⚠️ exact chiseled tag/digest verified at build), CycloneDX SBOM per image; `apps/web` is an Angular static bundle on object storage + CDN, not a container (T41, F-10-1) | L | V1 | M1 | 01.1.2-T1 |
| 10.2.1-T2 | Registry, image signing and provenance (attestation), digest promotion rule enforced by the deploy workflow | M | V1 | M1 | 10.2.1-T1 |

#### Story 10.2.2 Deploy with expand/contract and canary — acceptance: a deploy never takes traffic on a version whose migrations have not run, and a decision-changing canary is blocked by the shadow compare
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.2.2-T1 | Deploy pipeline: `Evidgate.Migrator` job (owner role, DbUp 7) → expand gate that verifies the DbUp checksum journal head → rollout → post-deploy verification; refuses a deploy whose migration set is not expand-only while the old version lives (F-10-2) | L | V1 | M1 | 10.2.1-T2, 01.4.1-T1 |
| 10.2.2-T2 | Canary rollout + automatic rollback on SLI breach (error rate, latency, readiness) with a documented rollback that never rewrites the schema | L | V1 | M2 | 10.2.2-T1, 10.3.1-T5 |
| 10.2.2-T3 | Shadow decision compare: replay a fixed evidence set under the incoming policy revision, diff decisions, block promotion on unexplained diff, per-project rollout — source §9 ✅ | L | V1 | M2 | 10.2.2-T1 |
| 10.2.2-T4 | Release runbook: who deploys, order of steps, verification checklist, rollback drill, post-deploy watch window | M | V1 | M2 | 10.2.2-T2 |

### Epic 10.3 Instrumentation, dashboards and alerts (M1-M2)

#### Story 10.3.1 Telemetry and alerting — acceptance: every alert in FR-10-6 fires in staging with a severity and a runbook link; no secret appears in a label or log field
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.3.1-T1 | OpenTelemetry instrumentation in `Evidgate.Api` and `Evidgate.Worker`: traces, metrics, logs with `request_id`/`run_id`/`job_id`/`trace_id`, sampling policy, resource attributes (service, env, version); browser web-vitals are emitted by 09's telemetry (09.1.1-T9) and only recorded here (T39) | L | V1 | M1 | 10.1.1-T1 |
| 10.3.1-T2 | Collector deployment and exporters with retention, batching and redaction of secrets (00 §11) | M | V1 | M1 | 10.3.1-T1, 10.3.2-T1 |
| 10.3.1-T3 | Metrics catalogue: `task`-queue age, `outbox` dispatch lag, lease loss, dead-letter depth (own queue tables, 00 §5.3), incomplete rate, parser errors, artifact failures, healing false-positive rate, ingest p95, decision p95, .NET runtime metrics (GC heap/gen-2/LOH collections, thread-pool worker counts and starvation, Npgsql pool occupancy/wait, EF Core command durations), plus per-unit cost counters (10.9.1-T1) (F-10-3/F-10-4) | M | V1 | M1 | 10.3.1-T2 |
| 10.3.1-T4 | Dashboards: service health, ingest/decision SLI, queue and lease state, tenant-neutral cost view | M | V1 | M2 | 10.3.1-T3 |
| 10.3.1-T5 | Alert rules with thresholds, owners and runbook links, routing to the on-call channel; consumed by 01.5.1-T4 (dead-letter hook) and 10.2.2-T2 (rollback trigger) | L | V1 | M2 | 10.3.1-T3 |

#### Story 10.3.2 Observability backend and external probe — acceptance: the telemetry backend is decided before the collector lands, availability is measured from outside the service network, and log retention is tied to deletion promises.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.3.2-T1 | ADR: observability backend for M1 (compose-local stack now, managed OTLP after O3) with the retention and cost limits; unblocks 10.3.1-T2 | S | V1 | M1 | 10.3.1-T1 |
| 10.3.2-T2 | External synthetic probe: a scheduled check from outside the service network against health and a canary ingest-to-decision path on a dedicated tenant, feeding the availability SLI (probe location depends on O3) | M | V1 | M2 | 10.4.1-T1, 10.3.1-T3 |
| 10.3.2-T3 | Log and trace retention per signal, no customer content in logs beyond ids, purge path on tenant deletion verified against 11.9.1-T1 | S | V1 | M3 | 10.3.1-T2, 11.9.1-T1 |

**Story 10.3.2 total: 2** — `[INFERENCE]` sizes; added from review 2026-10-02 §5.4.

### Epic 10.4 SLIs, SLOs and error budget (M2)

#### Story 10.4.1 Measured service targets — acceptance: the three 00 §11 targets are queryable per environment with a monthly report, and a burn-rate alert fires on a staged breach
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.4.1-T1 | SLI definitions and recording rules for ingest p95, decision p95 and availability (core endpoints only, never the test pass rate), per environment and per tenant class; plus web-vitals SLIs (LCP, INP) recorded from 09's vitals telemetry (09.1.1-T9) against the 00 §5.6 W7 budgets (T39, F-09-6) | M | V1 | M2 | 10.3.1-T3 |
| 10.4.1-T2 | Error budget: burn-rate alerts, monthly review ritual, and the rule that a budget breach pauses feature rollout instead of lowering the target | M | V1 | M2 | 10.4.1-T1 |

### Epic 10.5 Backup, PITR and restore drills (M2)

#### Story 10.5.1 Recoverability — acceptance: a restore into a clean environment reproduces a run, its evidences and its decision, within RPO 15 min / RTO 4 h
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.5.1-T1 | Database backup and PITR configuration (provider feature per O3), retention, and a documented recovery point expectation | M | V1 | M2 | 10.1.1-T3 |
| 10.5.1-T2 | Object storage protection: versioning, lifecycle, orphan-object sweep hook (reconcile job from 06) and integrity verification | M | V1 | M2 | 10.1.1-T3 |
| 10.5.1-T3 | Restore drill automation: restore into a clean project, apply migrations head, run isolation + smoke suites, record duration against RTO, attach evidence to the M2 exit gate | L | V1 | M2 | 10.5.1-T1 |
| 10.5.1-T4 | DR runbook: failover decision, communication, data-loss statement at the measured RPO, recovery order (database → object storage → queues → app) | M | V1 | M2 | 10.5.1-T3 |

#### Story 10.5.2 Object storage delete protection and restore — acceptance: a compromised application credential cannot delete evidence or its versions, and an object restore is tested.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.5.2-T1 | Separate write-only app credential and a deletion-only retention credential; bucket policy or object lock where the provider supports it; note the PDPL transfer implication of offshore replicas | M | V1 | M3 | 10.5.1-T2 |
| 10.5.2-T2 | Automated object restore test: delete an object, restore from version/copy, verify checksum and artifact `ready` state | M | V1 | M3 | 10.5.1-T3, 10.5.2-T1 |
| 10.5.2-T3 | Runbook entry and drill result attached to the restore evidence | S | V1 | M3 | 10.5.2-T2, 10.5.1-T4 |

**Story 10.5.2 total: 2.5** — `[INFERENCE]` sizes; added from review 2026-10-02 §5.4.

### Epic 10.6 Load, performance and capacity (M2-M4)

#### Story 10.6.1 Proving the pilot load — acceptance: 50 000 events in 60 s and the pilot artifact profile pass on staging, and the capacity model names the next bottleneck
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.6.1-T1 | Ingest load generator: valid batches within the 00 §9.3 limits (≤ 500 events, ≤ 1 MiB), 833 events/s target, quota and error-rate measurement; measures the hot path end to end — the single `INSERT … SELECT … FROM unnest(…) ON CONFLICT DO NOTHING RETURNING` batch insert (T50) and the per-org audit hash chain serialised by `pg_advisory_xact_lock` (T8) | L | V1 | M2 | 10.3.1-T3 |
| 10.6.1-T2 | Artifact upload load profile (presigned URL concurrency, 2 GB/day, ≥ 20 MB objects) + one smoke against the real provider S3 endpoint to close the emulator-parity gap of 01.8.1-T1, verifying presigned-PUT parity: `Content-Length` and the checksum header are signed (⚠️ parity on SeaweedFS and the provider, T18) | M | V1 | M3 | 10.5.1-T2 |
| 10.6.1-T3 | Capacity model: recompute the source §9 numbers ✅ (100 000 results/day ≈ 98 MiB/day ≈ 8.6 GiB/90 d; 2 GB/day artifacts, 28 GB/14 d; 833 events/s burst), add Npgsql pool sizes/connection counts, container memory limits and .NET GC settings (`DOTNET_GCServer`, heap hard-limit), thread-pool headroom and growth triggers (F-10-3) | M | V1 | M2 | 10.6.1-T1 |
| 10.6.1-T4 | Soak and back-pressure test: sustained multi-hour ingest, quota behaviour (429/503 with `Retry-After`), queue growth limits, recovery after overload | M | V2 | M4 | 10.6.1-T2 |

### Epic 10.7 Runbooks and incident readiness (M1-M3)

#### Story 10.7.1 Operable by a second person — acceptance: each runbook is executed once by someone who did not write it, in staging, and its steps are correct without tribal knowledge
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.7.1-T1 | Dead-letter runbook for the `task` queue table: triage query, classification, safe replay, escalation — the linked action for 01.5.1-T4 and alert 10.3.1-T5 (F-10-4) | M | V1 | M2 | 01.5.1-T4 |
| 10.7.1-T2 | Stuck run runbook: detect runs past the collection deadline, inspect lease/epoch state (00 §5.3), reconcile or cancel without inventing evidence | M | V1 | M2 | 10.3.1-T3 |
| 10.7.1-T3 | Orphan artifact runbook: objects without `ready`, `pending_upload` past TTL, retention sweep failures (00 §8.4) | S | V1 | M2 | 10.5.1-T2 |
| 10.7.1-T4 | Key rotation runbook: ingest tokens, CI OIDC trust, JWT signing keys, webhook secrets, database credentials, provider keys, and the ingest/API token HMAC pepper — a new `pepper_vN` signs new tokens while old versions verify until their last token expires (T5) — rotation without downtime or lost events; GitHub App private key and webhook secret per [`docs/runbooks/github-app-key-custody.md`](../docs/runbooks/github-app-key-custody.md) (stub from 02.4.3-T1, ADR-0007) | M | V1 | M2 | 10.1.1-T2 |
| 10.7.1-T5 | Kill switch runbook + drill: system → tenant → project healing availability (00 §8.7, DECISIONS §5.9) flipped while ingest, runs and gates continue | M | V1 | M3 | 10.3.1-T5, 08.6.3-T1 |
| 10.7.1-T6 | Incident process: severity ladder, on-call expectation for the pilot, communication, postmortem template with a follow-up task rule | M | V1 | M3 | 10.7.1-T1 |

#### Story 10.7.2 Paging and on-call at the first alert — acceptance: every alert in 10.3.1-T5 reaches a named destination and a person, including out of hours.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.7.2-T1 | Choose the paging tool and route alert rules to it; record the decision | S | V1 | M2 | 10.3.1-T5 |
| 10.7.2-T2 | Escalation and out-of-hours cover for a single operator (primary, secondary or auto-ack fallback); a test page each month | S | V1 | M2 | 10.7.2-T1 |
| 10.7.2-T3 | Tie the breach runbook (11.9.1-T2) to the paging path and name the 72 h clock owner | S | V1 | M3 | 10.7.2-T1, 11.9.1-T2 |

**Story 10.7.2 total: 1.5** — `[INFERENCE]` sizes; added from review 2026-10-02 §5.4.

### Epic 10.8 Cloud provider evaluation (O3) (M1-M2)

#### Story 10.8.1 Decision memo, no vendor chosen — acceptance: the owner can pick a provider from the memo without further research, and each candidate has measured restore and latency numbers
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.8.1-T1 | Criteria and shortlist: residency options (VN/APAC), managed PostgreSQL 18 with PITR, S3-compatible storage, egress cost, latency from the pilot's CI region, operational burden for one team | S | V1 | M1 | — |
| 10.8.1-T2 | Hands-on spike on two candidates: create managed PostgreSQL 18, measure PITR restore time, object storage presigned-URL behaviour, and ingest latency from a CI-like runner | L | V1 | M2 | 10.8.1-T1 |
| 10.8.1-T3 | Decision memo for the owner (O3): recommendation, disqualifiers, cost estimate, residency note for plan 11; no procurement in this plan | S | V1 | M2 | 10.8.1-T2 |

### Epic 10.9 Cost monitoring (M2-M3)

#### Story 10.9.1 Unit economics — acceptance: cost per 1 000 events, per artifact GB-month and per run is reported monthly per environment and handed to plan 12
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.9.1-T1 | Resource tagging + budget alerts per environment, with the per-unit counters exposed from 10.3.1-T3 | S | V1 | M2 | 10.3.1-T3 |
| 10.9.1-T2 | Unit-cost model: storage/egress/runner-less V1 baseline, then LLM and healing-replay cost per tenant (V2), handed to plan 12 for pricing and quota | M | V2 | M4 | 10.9.1-T1 |

### Epic 10.10 Temporal re-evaluation trigger (A7) (M2, M5)

#### Story 10.10.1 Trigger and spike — acceptance: the trigger is either not met (and the queue stays) or fires with evidence, and then a bounded spike answers whether Temporal reduces operational risk
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.10.1-T1 | Define and measure the trigger: number of workflows with long human waits, recovery incidents caused by queue/outbox limitations, and the manual work each costs (metrics from 10.3.1-T3) | S | V1 | M2 | 10.3.1-T3 |
| 10.10.1-T2 | Temporal evaluation spike (only if the trigger fires): model the healing PR flow and approval waits, cost of running one more system, migration path; output memo to plan 08/00 | M | V3 | M5 | 10.10.1-T1 |

### Epic 10.11 Edge and abuse limits (M2) [PROPOSAL review 2026-10-02]
#### Story 10.11.1 Ingress and abuse limits — acceptance: unauthenticated endpoints, presign issuance and org-level reads have limits, and oversized or slow requests are cut at the edge.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 10.11.1-T1 | Request body-size, header and timeout limits in Kestrel; per-IP limits for unauthenticated routes; per-org limits for reads and presign issuance, all with `Retry-After` | M | V1 | M2 | 04.1.2-T1 |
| 10.11.1-T2 | Abuse test in CI: burst on `/v1/ci/token`, slow-body and oversized-body requests, presign flood; expected status codes asserted | S | V1 | M2 | 10.11.1-T1 |
| 10.11.1-T3 | Record the WAF/edge decision as dependent on O3 and list what runs without it | S | V1 | M2 | 10.8.1-T1 |

**Story 10.11.1 total: 2** — `[INFERENCE]` sizes; added from review 2026-10-02 §5.4.

**Epic 10.11 total: 2**

## 10. Dependencies

- **01-foundation:** CI pipeline (01.7.1-T1), `Evidgate.Migrator` DbUp runner and role split (01.4.1-T1/T2), dev stack (01.8.1-T2), `outbox`/`task` dead-letter + operator CLI (01.5.1-T4), event schema and limits (01.3.1-T2), Testcontainers.PostgreSQL harness (01.6.1-T2). Plan 10 needs the layout additions of `01 §13 CR-01-3` (`infra/`, `scripts/`).
- **02-identity-tenancy:** token and signing keys for the rotation runbook (10.7.1-T4), audit trail for support access (§6), GitHub App key custody stub (02.4.3-T1).
- **03-catalog-authoring / 05-runs-orchestration:** stuck-run detection signals and lease/epoch state for 10.7.1-T2; outbox task types for the queue-age alert.
- **06-evidence-artifacts:** artifact lifecycle states, retention sweep and orphan reconciliation feeding 10.7.1-T3 and 10.5.1-T2.
- **07-policy-decisions:** policy revision rollout path used by the shadow decision compare (10.2.2-T3); webhook delivery metrics for the alert set.
- **08-healing-learning:** false-positive measurement (10.3.1-T3) and the healing availability switch exercised by 10.7.1-T5.
- **11-security-compliance:** residency/PDPL input to the O3 memo (10.8.1-T3), support-access rules for §6, pen-test window coordination.
- **12-pilot-commercial:** consumes the unit-cost model (10.9.1-T2) and the capacity model (10.6.1-T3) for pricing and quota.

## 11. Open questions

- **[OPEN]** O3 cloud provider/region — owner decision; needed before the first pilot data lands (M2). Blocks 10.1.1-T3 final targets, 10.5.1-T1 and 10.8.1-T3.
- **[OPEN]** O2 team size and who is on-call during the pilot — needed before M3 (10.7.1-T6 takes the owner's answer).
- **[OPEN]** Observability backend: self-hosted collector storage vs a managed OTLP backend; cost and licence checked against DECISIONS §4. Owner: Tech Lead, needed by M2.
- **[OPEN]** Canary percentage and the watch window after promotion ⚠️ (starting point: 1 replica, 30 min) — confirm with the pilot's tolerance in 12.
- **[OPEN]** Whether staging shares the production object-storage account (recommended: separate accounts; source §9 asks for separate account/secret and data ✅). Owner: Tech Lead, needed by M2.

## 12. Estimate summary

Task sizes use the shared units (XS 0.25 · S 0.5 · M 1 · L 2 · XL 3 person-days); each epic total is the sum of its task rows.

| Epic | V1 | V2 | V3 | Total |
|---|---|---|---|---|
| 10.1 Environments and configuration | 7.75 | 0 | 0 | 7.75 |
| 10.2 Images, registry and deployment | 10.0 | 0 | 0 | 10.0 |
| 10.3 Instrumentation, dashboards and alerts | 9.0 | 0 | 0 | 9.0 |
| 10.4 SLIs, SLOs and error budget | 2.0 | 0 | 0 | 2.0 |
| 10.5 Backup, PITR and restore drills | 7.5 | 0 | 0 | 7.5 |
| 10.6 Load, performance and capacity | 4.0 | 1.0 | 0 | 5.0 |
| 10.7 Runbooks and incident readiness | 7.0 | 0 | 0 | 7.0 |
| 10.8 Cloud provider evaluation (O3) | 3.0 | 0 | 0 | 3.0 |
| 10.9 Cost monitoring | 0.5 | 1.0 | 0 | 1.5 |
| 10.10 Temporal re-evaluation trigger | 0.5 | 0 | 1.0 | 1.5 |
| 10.11 Edge and abuse limits | 2.0 | 0 | 0 | 2.0 |
| **Total** | **53.25** | **2.0** | **1.0** | **56.25** |

→ with 20 % buffer **67.5** person-days

## 13. Contract change requests

> **Status:** Applied 2026-09-24: CR-10-1 → 00 §11. Applied 2026-09-25 by integrator: CR-10-2 → 00 §14.
> 2026-09-25 CR-D-1 + TL review: images retargeted to `dotnet publish` + digest-pinned chiseled non-root `aspnet:10.0`, `Evidgate.Migrator` as its own image, `apps/web` a static bundle on object storage + CDN (T41, F-10-1); deploy gate verifies the DbUp checksum journal (F-10-2); metrics catalogue and capacity model gain .NET runtime metrics, GC/container-memory settings and Npgsql pool sizing (F-10-3); health checks, queue/dead-letter alerts and runbooks re-anchored to the own `task`/`outbox` tables (F-10-4); pepper rotation added to 10.7.1-T4 (T5); load test measures the audit hash chain and the batch insert (T8, T50); presign parity in 10.6.1-T2 (T18); web-vitals SLIs in FR-10-7 and 10.4.1-T1 (T39); healing switch modes corrected (T33). No task added, removed or resized; totals unchanged.

| # | Target | Request (verbatim) | Owner |
|---|---|---|---|
| CR-10-1 | 00 §11 Non-functional requirements | Add an operational row so the plan-10 SLIs trace to the shared table: "Operational SLIs (measured, not SLA): queue/outbox lag, dead-letter depth, lease-loss rate, incomplete rate, parser-error rate, artifact-failure rate, healing false-positive rate (targets and thresholds owned by plan 10)". | Tech Lead |
| CR-10-2 | 00 §14 Estimate roll-up | This plan's row becomes `38 tasks, V1 42.5, V2 2, V3 1, raw 45.5, buffered 54.6` (new task 10.1.1-T4, M, V1, M2). Two notes for the same pass: (a) plan 10 has no per-epic total lines in §9 (only §12 carries sums), unlike 09/11/12 - decide whether to add them file-wide; (b) plan 10's §11 has no static-hosting open question; the viewer origin is now 10.1.1-T4 and the `apps/web` static-host question stays in 09 §11 (#3). | Integrator - applied 2026-09-25 |
