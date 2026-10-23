# Threat model v1

Version: v1
Status: draft
Last review: 2026-10-11 (M1)
Legend: ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`

## 1. Scope, method and reading guide

**Scope.** This model covers every module of the system — `identity`, `tenancy`, `catalog`, `orchestrator`, `ingestion`, `evidence`, `policy`, `healing`, `usage`, `audit`, `notifications` — and every trust boundary the system crosses: browser, CLI and adapters, agent, healing sandbox, LLM gateway, object storage, database, customer CI, artifact parsers/renderers, outbound webhooks and operator ingress. The catalogue in §2 is the authoritative list; §3 gives one section per module and §4 one section per boundary.

**Method.** Each catalogue entry is swept with STRIDE (Spoofing, Tampering, Repudiation, Information disclosure, Denial of service, Elevation of privilege). Every threat that realistically applies to the entry gets one row: the threat, the attacker, the impact and a disposition. The sweep is per entry, not per endpoint, so a new endpoint does not need a new section — it needs a row (or an amended row) in the section of the module or boundary it crosses.

**How to read a row.** A threat row is `| ID | STRIDE | Threat | Attacker | Impact | Disposition |`. The `Disposition` cell is the only machine-checked cell and is exactly one of:

- `control: <named control> — <task id>[, <task id>…] [implemented: <repo path>…]` or `… [planned]` — the threat is mitigated by a named control owned by one or more tasks; every task id resolves to an area-plan task row (`plans/NN-*.md`), and a sub-task id resolves to its parent task.
- `accepted: RR-<n> — <one-line rationale>` — the threat is knowingly accepted by the owner; `RR-<n>` is a register entry in §5 whose state is `accepted`.

`open:` is not a valid disposition. A question that is not yet decided is carried by a register entry in state `open-decision` (§5); the threat rows it touches carry a real `control:` for the interim or planned control and cite `(see RR-<n>)` inside the control name.

**`[implemented]` vs `[planned]`.** `[implemented: <path>]` means the control exists in the repository at the review date and the cited paths exist — it is a statement about code that is there. `[planned]` means the control is owned by a task that has not shipped yet — it is a statement about intent, not a claim that the control exists. A `[planned]` tag is therefore never evidence that a threat is mitigated today; it names the plan that closes it, and the milestone-exit ritual (§6) re-checks it.

**Completeness check.** [`scripts/check-threat-model.sh`](../../scripts/check-threat-model.sh) parses the catalogue, the section headings and the threat rows, and fails (exit ≠ 0) on a missing module or boundary, a section with no rows, an undispositioned row, a task id that does not resolve, an `[implemented]` path that does not exist, or a register entry that breaks the field rules. It runs in `scripts/verify.sh --stage check-threat-model` and in the CI policy job (`.github/workflows/ci.yml`), and it is also a milestone-gate check (§6).

**Adding a new endpoint or module.** A new module adds a catalogue row (§2), a `### M-<module> — <Name>` section (§3) and at least one threat row. A new endpoint adds or amends a row in the section of the module or boundary it crosses. A new trust boundary adds a catalogue row, a `### B-<slug> — <Name>` section (§4) and its rows. The module list comes from FR-11-1 and the boundary list from the trust-boundary sweep in [`plans/11-security-compliance.md` §4.1](../../plans/11-security-compliance.md).

**Non-goals.** This document does not implement controls (owned by the module plans), does not run the tenant-isolation red-team suite (11.7.1-T1) or the pen test (11.7.1-T3), does not specify CI or sandbox mechanics (11.2.1), and does not walk ASVS with evidence (11.7.1-T5, V2). It produces the model, the register, the ritual and the lint — not product code.

**No customer data.** This document contains no customer data: threats are described generically, and no artifact, tenant identifier or personal datum appears in it.

## 2. Catalogue

| ID | Kind | Name | Source |
|---|---|---|---|
| M-identity | module | Identity | FR-11-1 |
| M-tenancy | module | Tenancy | FR-11-1 |
| M-catalog | module | Catalog | FR-11-1 |
| M-orchestrator | module | Orchestrator | FR-11-1 |
| M-ingestion | module | Ingestion | FR-11-1 |
| M-evidence | module | Evidence | FR-11-1 |
| M-policy | module | Policy | FR-11-1 |
| M-healing | module | Healing | FR-11-1 |
| M-usage | module | Usage | FR-11-1 |
| M-audit | module | Audit | FR-11-1 |
| M-notifications | module | Notifications | FR-11-1 |
| B-web | boundary | Browser → API | `plans/11-security-compliance.md` §4.1 |
| B-cli-adapters | boundary | CLI and adapters → API | FR-11-1 |
| B-agent | boundary | Agent → API | `plans/11-security-compliance.md` §4.1 |
| B-healing-sandbox | boundary | Healing sandbox → host | FR-11-1 |
| B-llm-gateway | boundary | Worker/sandbox → LLM provider | `plans/11-security-compliance.md` §4.1 |
| B-object-storage | boundary | API → object storage | `plans/11-security-compliance.md` §4.1 |
| B-database | boundary | API/worker → PostgreSQL | `plans/11-security-compliance.md` §4.1 |
| B-ci | boundary | Customer CI → API | `plans/11-security-compliance.md` §4.1 |
| B-artifact-parsers | boundary | Untrusted artifact content → parsers/renderers | `plans/11-security-compliance.md` §4.1 |
| B-webhook-out | boundary | API → webhook receivers | `plans/11-security-compliance.md` §4.1 |
| B-operator | boundary | Operator → tenant data | `plans/11-security-compliance.md` §4.1 |

## 3. Module sections

One section per module in the catalogue, each followed by its STRIDE threat table.

### M-identity — Identity

Owns human login and sessions, the GitHub OIDC → `ci_producer` exchange and ingest/API tokens, CI bindings, memberships and roles, and support grants. Security-relevant surface: `/v1/auth/*`, `POST /v1/ci/token`, `POST /v1/ci-bindings`, `POST /v1/ingest-tokens`, `POST /v1/support-grants`, `/v1/orgs/{o}/memberships` (00 §9.3; 02 §4), tables `user`, `user_identity`, `session`, `ci_binding`, `ci_token_issue`, `ingest_token`, `api_token`, `support_grant`, `membership`, `role` (00 §5.4.1, 02 D12).

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-identity-1 | S | A forged or replayed OIDC ID token / GitHub OAuth code mints a session for another user | External attacker with a leaked code or token | Account takeover, tenant data access | control: OIDC login with JWKS verification plus PKCE state/nonce — 02.3.1-T1 [planned] |
| TH-identity-2 | S, E | A CI job mints a `ci_producer` token for a repository it does not control, because the binding never proved repository write access | Fork-PR author or attacker with a leaked OIDC token | Forged evidence for another project | control: CI binding pending-verification challenge T70 — 02.4.1-T2, 02.4.1-T3 [implemented: migrations/0016_identity_ci_binding.sql] |
| TH-identity-3 | S, R | A replayed `ci_token_issue.jti`, or a rotated or revoked ingest token, is accepted | Attacker replaying a captured token | Unauthorised ingestion | control: ingest-token prefix lookup with HMAC-SHA256 storage T5 — 02.4.2-T1 [implemented: migrations/0018_identity_ingest_token.sql] |
| TH-identity-4 | E | A member grants themselves a role or permission they do not hold (self-escalation) | Malicious tenant member | Privilege escalation | control: deny-by-default permission evaluator and self-escalation refusal — 02.2.1-T2, 02.4.4-T3 [implemented: src/Modules/Evidgate.Modules.Identity/Authorization/PermissionEvaluator.cs] |
| TH-identity-5 | E, R | Removal or disable of the last `tenant_admin` locks the org out or leaves an orphaned admin | Malicious member or defect | Org lockout, unauthorised admin retention | control: last-admin guard plus session revocation on removal — 02.1.2-T3 [planned] |
| TH-identity-6 | E, I | A support grant is used beyond its scope or expiry, or with no tenant-visible trace (silent operator access) | Rogue platform operator | Undisclosed tenant-data access | control: scoped expiring support grants, approver not the requester, tenant-visible banner — 02.8.1-T1, 11.5.1-T2 [planned] |
| TH-identity-7 | S, E | A project-scoped API or ingest token is used against another project or tenant | Attacker with a leaked token | Cross-project access | control: project-scope ceiling on service tokens and 403 `project_scope_mismatch` — 02.4.4-T2, 02.9.1-T1 [implemented: src/Modules/Evidgate.Modules.Identity/Authorization/PermissionEvaluator.cs] |
| TH-identity-8 | I | A secret (token, key) leaks into application logs, metric labels or exception messages | Defect or careless logging | Credential disclosure | control: the logger's redaction list covering every `EVIDGATE__*` secret and `authorization`, applied to every registered provider's log state before export (00 §5.7 S3) — 01.1.2-T1 [implemented: src/BuildingBlocks/Observability/Redaction/SecretRedactor.cs]; the metric-label and exception-message parts of S3 are the design rule (00 §5.5 C8: exception text is a bug signal, not a secret store), planned |

### M-tenancy — Tenancy

Owns the org/project registry and the data-access contract: `org_id` on every business table, composite `(org_id, id)` foreign keys, `FORCE ROW LEVEL SECURITY`, the `ITenantScope` transaction, the pre-tenant `SECURITY DEFINER` resolvers, and the four database roles. Security-relevant surface: every `/v1` request and every worker task (00 §5.4 rules 1–8, §5.5 C2/C3; 02 §4).

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-tenancy-1 | I, E | A query runs with the tenant GUC unset and returns all rows or errors instead of zero | Opportunistic attacker or defect | Cross-tenant read | control: NULL-safe RLS helpers plus `FORCE ROW LEVEL SECURITY` T2 — 02.6.1-T1 [implemented: migrations/0003_foundation_rls_probe.sql] |
| TH-tenancy-2 | I | A cross-tenant read through a missing or mispaired composite foreign key | Attacker with a valid session | Cross-tenant disclosure | control: composite `(org_id, id)` foreign keys on tenant tables T3 — 02.1.1-T1 [implemented: migrations/0016_identity_ci_binding.sql] |
| TH-tenancy-3 | E | The application role bypasses RLS because it owns the table or holds `BYPASSRLS` | Insider or misconfiguration | Full cross-tenant compromise | control: app role is not the owner, no `BYPASSRLS`, `FORCE ROW LEVEL SECURITY` — 02.6.1-T1 [implemented: migrations/0001_foundation_roles.sql] |
| TH-tenancy-4 | S, E | A pre-tenant resolver is abused to enumerate bindings or to return more than `(org_id, project_id, binding_id, status)` | Attacker probing the resolver | Tenant enumeration, timing oracle | control: `SECURITY DEFINER` resolvers, pinned `search_path`, `EXECUTE`-only T1/T80 — 02.6.1-T3, 11.2.1-T7 [implemented: migrations/0014_identity_resolve_member_orgs.sql] |
| TH-tenancy-5 | E | `evidgate_operator` reads tenant data without an approved, unexpired grant (operator bypass) | Rogue platform operator | Undisclosed cross-tenant read | control: operator RLS via `evidgate.operator_grant_ok` T76 — 02.8.1-T1 [planned] |
| TH-tenancy-6 | T | GUC misuse: a string-built `set_config`, or a tenant setting that outlives its transaction, leaks context between requests | Attacker or defect | Cross-tenant read or write | control: the tenant command interceptor C3 (a command on a connection without the tenant setting throws) — 01.4.2-T1 [implemented: src/BuildingBlocks/Persistence/Tenancy/TenantCommandInterceptor.cs] |
| TH-tenancy-7 | I | A Dapper read-model query omits the tenant setting and returns foreign rows | Defect | Cross-tenant disclosure | control: tenant-isolation red-team suite over views, search and Dapper reads — 11.7.1-T1, 02.9.1-T3 [planned] |

### M-catalog — Catalog

Owns test identity (`test_case`, `case_identity_source`), immutable `test_revision` and suite revisions, variants, fixtures and the GitHub catalog sync. Security-relevant surface: `/v1/projects/{p}/cases`, `/v1/suites/{s}/revisions`, `/v1/cases/{c}`, `POST /v1/webhooks/github` (00 §9.3; 03 §4), tables `test_case`, `test_revision`, `suite`, `suite_revision`, `variant`, `fixture_revision` (00 §8.10).

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-catalog-1 | T | A `test_revision` is mutated after publication, rewriting the identity a decision was based on | Malicious member or defect | Evidence integrity loss | control: append-only `test_revision` with a fail-closed assertion hash — 03.1.3-T1 [implemented: migrations/0021_catalog_test_revision.sql] |
| TH-catalog-2 | T | A published suite revision is edited in place instead of appended as a new revision | Malicious member | Gate integrity loss | control: suite revision immutability plus the head guard — 03.2.1-T1 [implemented: migrations/0022_catalog_suites.sql] |
| TH-catalog-3 | I | Secret-shaped content (tokens, keys) is stored in a case, suite, variant or fixture | Careless author | Secret disclosure through the catalog | control: secret-shape rejection on variants, suites and fixtures — 03.2.1-T2, 03.3.1-T3 [implemented: src/Modules/Evidgate.Modules.Catalog/Domain/VariantNormalizer.cs] |
| TH-catalog-4 | S, T | A forged GitHub webhook creates or renames cases, or triggers a sync for another repository | External attacker | Catalog tampering | control: `X-Hub-Signature-256` verification, pre-tenant resolver and delivery dedupe — 03.4.1-T2 [planned] |
| TH-catalog-5 | T | Case identity collision: two different tests share a case id, so evidence is attributed to the wrong case | Malicious author or defect | Wrong decision | control: identity-conflict detection and duplicate-id reporting — 03.1.1-T2 [implemented: src/Modules/Evidgate.Modules.Catalog/Domain/CaseDiscovery.cs] |
| TH-catalog-6 | I | A case or suite read returns another tenant's catalog | Attacker with a valid session | Cross-tenant disclosure | control: RLS on catalog tables — 02.6.1-T1 [implemented: migrations/0020_catalog_case_identity.sql] |

### M-orchestrator — Orchestrator

Owns manifests, environment revisions, runs and planned executions, producer-slot registration, releases and (V2) runner job leases. Security-relevant surface: `POST /v1/projects/{p}/manifests`, `POST /v1/projects/{p}/runs`, `POST /v1/runs/{r}/finalize`, `POST /v1/releases/{id}/runs`, `POST /v1/agents/jobs/claim` (00 §9.3; 05 §4), tables `manifest`, `environment_revision`, `run`, `producer_slot`, `release`, `job`, `job_lease` (00 §8.1–§8.2).

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-orchestrator-1 | T | A manifest or environment revision is mutated after a run locked it | Malicious member or defect | Evidence and decision integrity loss | control: manifest immutability — 05.1.1-T3 [implemented: migrations/0023_orchestration_manifest.sql] |
| TH-orchestrator-2 | I | A secret value is stored in an environment revision or manifest instead of a reference | Careless author | Secret disclosure | control: secret-free validation plus referenced-revision immutability — 05.1.2-T2 [implemented: migrations/0024_orchestration_environment.sql] |
| TH-orchestrator-3 | S, E | A stale or foreign agent claims a job lease, or heartbeats an old epoch (job theft) | Rogue agent | Job hijack, forged results | control: per-agent identity, epoch fencing and lease expiry — 05.7.2-T1, 05.7.2-T2 [planned] |
| TH-orchestrator-4 | S, D | SSRF: a user-supplied outbound target (webhook or replay host) reaches private or link-local ranges | External attacker | Internal network access | control: allow-listed `HttpClient` with private-range block S4 — 07.6.3-T1, 07.6.3-T3 [planned] |
| TH-orchestrator-5 | T | A producer slot is bound to the wrong identity, or a foreign producer finalizes it | Attacker with a leaked token | Forged shard evidence | control: producer-slot binding T9 — the `producer_slot` table's unique `(org_id, run_id, producer_id)` / `(org_id, run_id, shard_index)` keys and the two binding columns (`bound_token_jti`, `bound_ci_run_ref`) that only the app role may update — 04.1.2-T4 [implemented: migrations/0029_ingestion_producer_slot.sql]; the bind-or-check enforcement and the finalize check are 04.1.2-T5 and 05.3.2-T1, planned |
| TH-orchestrator-6 | R | A release is joined to a run after the fact (time-based binding), or a run is bound to the wrong release | Malicious member | False PASS | control: `release_run` digest binding, refused once a live evaluation exists T15 — 05.6.1-T2 [planned] |
| TH-orchestrator-7 | D | A flood of run-create or manifest requests exhausts the queue | External attacker | Denial of service | control: per-token and per-tenant rate limiter plus body size limits — 04.1.2-T1 [planned] |
| TH-orchestrator-8 | T | A concurrent writer overwrites a manifest between read and write (lost update) | Malicious member or defect | Evidence and decision integrity loss | control: manifest `ETag`/`If-Match` optimistic concurrency — 05.1.1-T3 [planned] |

### M-ingestion — Ingestion

Owns the event envelope, idempotent batch commit, producer-slot binding, batch limits, the hardened report parsers and reprocess. Security-relevant surface: `POST /v1/runs/{r}/events`, `POST /v1/runs/{r}/finalize`, `POST /v1/runs/{r}/reprocess` (00 §9.3–§9.4; 04 §4), tables `event`, `event_batch`, `producer_slot`, `raw_report`, `report_parser` (00 §9.4).

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-ingestion-1 | T | A duplicate `event_id` with a different payload is silently accepted, corrupting evidence | Attacker or defect | Evidence corruption | control: `event_id` dedupe with a payload-hash compare — 04.1.1-T2 [planned] |
| TH-ingestion-2 | T, R | Replay of an accepted batch inflates counts or double-counts usage | Attacker | Wrong decision, billing abuse | control: sequence uniqueness plus idempotent commit and no-double-count test — 04.1.1-T4, 04.8.1-T2 [planned] |
| TH-ingestion-3 | S | A leaked ingest token races for an unclaimed producer slot before the legitimate producer and forges that shard's evidence | Attacker with a leaked token | Forged shard evidence | accepted: RR-1 — the slot-binding enforcement (04.1.2-T5) and the security-event baseline (11.5.1-T5) are planned, so the residual stays open at M1 until they ship; tolerable meanwhile because no pilot data exists yet (pilot zero is the owner's own pipeline, `docs/decisions/O1-pilot-profile.md` §1); re-review when 04.1.2-T5 and 11.5.1-T5 ship |
| TH-ingestion-4 | D | A flooding or batch bomb (oversized or gzip-bomb batch) exhausts memory | External attacker | Denial of service | control: 500 events and 1 MiB pre-decompression cap with a gzip bomb guard — 04.1.1-T3 [planned] |
| TH-ingestion-5 | T, D | A malicious report (XXE, zip-slip, decompression bomb) attacks the parser | Attacker via untrusted report content | Parser RCE or DoS | control: hardened XML and archive parsing, DTD off, depth and size caps S5 — 04.4.1-T2, 06.3.1-T2 [planned] |
| TH-ingestion-6 | T | An event with an unknown `execution_key` is accepted and attributed to a run | Attacker or defect | Evidence corruption | control: unknown `execution_key` batch reject T10 — 04.1.2-T5 [planned] |
| TH-ingestion-7 | S | A foreign producer identity writes events for a run it does not own | Attacker with a leaked token | Forged evidence | control: producer-slot binding T9 — the `producer_slot` table's unique `(org_id, run_id, producer_id)` / `(org_id, run_id, shard_index)` keys and the two binding columns (`bound_token_jti`, `bound_ci_run_ref`) that only the app role may update — 04.1.2-T4 [implemented: migrations/0029_ingestion_producer_slot.sql]; the enforcement that rejects a foreign producer's event batch is 04.1.2-T5 and 04.1.1-T1, planned |

### M-evidence — Evidence

Owns the artifact record, upload-url and complete, presigned URLs, checksums, the isolated viewer origins, retention and deletion, and the async secret scan. Security-relevant surface: `POST /v1/artifacts/upload-url`, `POST /v1/artifacts/{a}/complete`, `GET /v1/artifacts/{a}/download-url` (00 §9.3; 06 §4), tables `artifact`, `artifact_kind_policy`, `retention_policy` (00 §8.4).

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-evidence-1 | I | A presigned URL leaks and is used as a bearer credential to read an artifact | Attacker with a leaked URL | Artifact disclosure | control: short-lived per-view presigned URL over a server-chosen key — 06.1.2-T1, 06.2.1-T2 [planned] |
| TH-evidence-2 | T | A client supplies its own object key, or a mismatched checksum or size, replacing another artifact | Attacker | Artifact substitution | control: server-chosen keys with a key-shape CHECK — 06.1.1-T2 [implemented: migrations/0028_evidence_artifact.sql] |
| TH-evidence-3 | I | A sensitive artifact is served without the `artifact:read_sensitive` gate, or on the application origin | Attacker with a valid session | Sensitive data disclosure | control: kind-to-sensitivity map plus the isolated viewer origin T71/T74/T99 — 06.2.1-T1, 06.3.3-T1 [planned] |
| TH-evidence-4 | I | A deleted artifact stays readable through a cached URL or a backup outside the stated retention | Insider or defect | Retention and PDPL breach | control: verified deletion plus the retention task — 06.4.2-T1, 11.9.1-T1 [planned] |
| TH-evidence-5 | I | A secret inside an uploaded artifact is served before the async scan completes | Attacker | Secret disclosure | control: async secret scan, with pending artifacts served as sensitive — 06.2.3-T1 [planned] |
| TH-evidence-6 | T | An artifact is treated as evidence without reaching `ready` (size and checksum verified) | Defect | Evidence integrity loss | control: the artifact state CHECK list (a row can only hold an allowed state) — 06.1.3-T1 [implemented: migrations/0028_evidence_artifact.sql]; the transition guards and `ready`/`rejected` immutability are 06.1.3-T1, planned |
| TH-evidence-7 | S | A viewer token is forged or replayed to read another artifact | Attacker | Cross-artifact disclosure | control: HMAC viewer token bound to the artifact, re-read under RLS T74 — 06.3.3-T1 [planned] |

### M-policy — Policy

Owns policy revisions and their content hash, the evaluator and decision rules, waivers and quarantine, and the missing-evidence rule. Security-relevant surface: `POST /v1/projects/{p}/policies`, `GET /v1/policies/{id}/revisions`, `GET /v1/runs/{r}/decision`, `POST /v1/waivers` (00 §9.3; 07 §4), tables `policy_revision`, `policy_activation`, `evaluation`, `waiver`, `quarantine_entry` (00 §8.5–§8.6).

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-policy-1 | T | A policy revision is edited in place after it was used for a decision | Malicious member or defect | Decision integrity loss | control: append-only content-addressed policy revisions — 07.1.1-T1 [implemented: migrations/0025_policy_add_policy_revision.sql] |
| TH-policy-2 | T | A policy body is altered so its hash no longer matches the stored revision (hash tamper) | Insider | Decision integrity loss | control: canonical hash verification on read — 07.1.1-T2 [implemented: src/Modules/Evidgate.Modules.Policy/Application/Queries/GetPolicyRevision.cs] |
| TH-policy-3 | D | An oversized policy body exhausts memory | External attacker | Denial of service | control: 64 KiB body cap returning 413 — 07.1.1-T3 [implemented: src/Modules/Evidgate.Modules.Policy/Endpoints/PolicyEndpoints.cs] |
| TH-policy-4 | T, E | Missing evidence is treated as PASS, so a component that never reported does not block | Defect or malicious policy | False PASS | control: completeness rule that never PASSes with a missing component — 07.2.1-T2, 07.5.3-T1 [planned] |
| TH-policy-5 | T | A waiver or quarantine entry is used to turn a FAIL into PASS | Malicious approver | False PASS | control: the waiver row is a project-scoped, time-boxed exception with approver ≠ requester (0027); the "a waiver never changes a result" rule is the 07.3.2-T1 evaluator, planned — 07.3.1-T1, 07.3.2-T1 [implemented: migrations/0027_policy_add_waiver.sql] |
| TH-policy-6 | R | A used decision is rewritten, or late evidence mutates it instead of creating a new revision | Insider | Decision and audit integrity loss | control: immutable evaluations, late evidence creates a new revision — 07.5.1-T1, 07.5.1-T2 [planned] |
| TH-policy-7 | T | A replay or shadow run is evaluated as `live` and enters release aggregation | Defect | False gate | control: replay runs evaluated shadow-only — 07.2.2-T4 [planned] |

### M-healing — Healing

Owns incidents and candidates, the AST validator, shadow replay, approval and the kill switch, and the healing knowledge store. Security-relevant surface: `/v1/projects/{p}/incidents`, `/v1/healing/candidates/{c}/approve`, `GET/PUT /v1/healing/switch` (00 §9.3; 08 §4), tables `incident`, `healing_candidate`, `healing_switch`, `knowledge_record` (00 §8.7).

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-healing-1 | T | A healing patch weakens or removes an assertion, changing test intent | Malicious or over-eager healer | Hidden product defect | control: forbidden-pattern detectors plus the assertion-hash gate — 08.4.1-T2, 08.4.2-T1 [planned] |
| TH-healing-2 | T, E | A shadow candidate is applied to the official run or repository without approval | Attacker or defect | Evidence corruption | control: shadow-only guard and no repository write path — 08.3.3-T1 [planned] |
| TH-healing-3 | E | An apply capability ships before the kill switch exists, so a broken healing path cannot be stopped | Insider | Uncontrolled healing | control: kill switch before any apply, DECISIONS §5.9 — 08.6.3-T1 [planned] |
| TH-healing-4 | T | Approval is bound to a stale head, so the merged code differs from the approved patch | Attacker | Unreviewed code merged | control: approval bound to `approved_head_sha`/`candidate_hash` with `merge_mismatch` revocation T73 — 08.6.1-T1, 08.6.2-T2 [planned] |
| TH-healing-5 | T, I | The healer is prompt-injected by artifact content into changing a policy, approval or gate | Attacker via untrusted content | Policy or approval tampering | control: structured I/O, no tools, independent validator, injection corpus — 08.5.2-T1, 08.5.2-T2 [planned] |
| TH-healing-6 | I | Healing context (DOM, screenshots) leaks secrets or personal data to the provider | Attacker or defect | Data disclosure, cross-border transfer | control: redaction pipeline plus per-tenant opt-in and data-owner approval — 08.5.3-T1, 08.5.3-T3 [planned] |
| TH-healing-7 | D, E | The healing sandbox escapes to the host or the control-plane network | Attacker via an untrusted patch | Host compromise | control: disposable sandbox, default-deny egress, no host mounts, resource caps — 11.2.1-T6, 08.4.3-T2 [planned] |

### M-usage — Usage

Owns the usage ledger, metering points, quota counters and the roll-up. Security-relevant surface: `GET /v1/orgs/{o}/usage/summary`, `GET /v1/orgs/{o}/usage/ledger` (00 §9.3; 12 §4), tables `usage_ledger_entry`, `usage_rollup`, `price_table` (00 §9.1, 12.3.1).

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-usage-1 | T | A client supplies metering quantities, so usage is taken from the caller | Malicious tenant member | Billing fraud | control: internal-only metering, quantities never taken from a client — 12.3.1-T2 [planned] |
| TH-usage-2 | T | Double-counting: a resend, reprocess or retry inflates usage | Defect | Billing fraud | control: `billing_key` dedupe plus the no-double-count test — 04.8.1-T2, 12.3.1-T1 [planned] |
| TH-usage-3 | D, E | Quota or cost abuse: a tenant floods ingestion or model calls to exhaust quota and spend | Malicious tenant member | Cost denial of service | control: quota counters with 80 percent warn and 100 percent block, plus the model budget hard stop — 12.3.1-T4, 08.5.1-T1 [planned] |
| TH-usage-4 | T | The ledger is edited or a row is deleted (metering integrity) | Insider | Billing fraud | control: append-only ledger privileges plus the adjustment model — 12.3.1-T1 [planned] |
| TH-usage-5 | R | A usage roll-up gap or duplicate is not detected, so a bill cannot be reconciled | Defect | Billing dispute | control: roll-up plus a reconciliation query reporting gaps and duplicates — 12.3.1-T5 [planned] |
| TH-usage-6 | I | A usage read returns another tenant's counters | Attacker with a valid session | Cross-tenant disclosure | control: RLS on the ledger plus `usage:read` for `tenant_admin` — 12.3.1-T6, 02.6.1-T1 [planned] |

### M-audit — Audit

Owns the append-only `audit_log` with its per-org hash chain, the audit read and export, the security-event set and support-access audit. Security-relevant surface: `GET /v1/orgs/{o}/audit`, `GET /v1/orgs/{o}/audit/export`, `GET /v1/orgs/{o}/security-events` (00 §9.3; 02.7.1, 11.5.1), tables `audit_log`, `security_event` (00 §5.4, 11.5.1-T5).

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-audit-1 | T | An audit row is edited or deleted (tamper) | Insider with database access | Repudiation | control: append-only grants plus the per-org hash chain T8 — 02.7.1-T1 [implemented: migrations/0015_identity_audit_log.sql] |
| TH-audit-2 | T | The audit chain is truncated or rewritten and the tamper is not detected | Insider with database access | Repudiation | control: chain-verification task — 02.7.1-T3 [implemented: src/BuildingBlocks/Persistence/Audit/VerifyAuditChainTask.cs] |
| TH-audit-3 | R | A security-relevant action is not recorded (missing actor, action, object or hash) | Defect | Repudiation | control: audit writer in the C2 pipeline with `[AuditedAction]` — 02.7.1-T2 [implemented: src/BuildingBlocks/Persistence/Audit/AuditTrail.cs] |
| TH-audit-4 | I | An audit export leaks another tenant's rows or exceeds its range | Attacker with a valid session | Cross-tenant disclosure | control: `audit:read` plus the export range cap under RLS — 02.7.1-T3 [implemented: src/Modules/Evidgate.Modules.Identity/Application/Commands/ExportAudit.cs] |
| TH-audit-5 | I, E | Support or operator access is not visible to the tenant (silent access) | Rogue platform operator | Undisclosed access | control: support-grant actions in the tenant audit plus the UI banner — 02.8.1-T1, 11.5.1-T2 [planned] |
| TH-audit-6 | R | Security events (auth failures, cross-tenant probes, kill-switch use) are not emitted or not readable | Defect | Undetected attack | control: security-event baseline plus the read endpoint — 11.1.1-T4, 11.5.1-T5 [planned] |
| TH-audit-7 | T | The audit-chain key is compromised and rows are re-signed | Insider | Repudiation | control: HMAC-keyed chain with key custody and rotation — 11.5.2-T1 [planned] |
| TH-audit-8 | T | The audit chain is truncated or rewritten and the tamper is not detected by an external anchor | Insider with database access | Repudiation | control: an external signed checkpoint of the chain head — 11.5.2-T2, 11.5.2-T3 [planned] |

### M-notifications — Notifications

Owns outbound webhooks, transactional email and incoming-webhook channels, with their templates and signing secrets. Security-relevant surface: `POST /v1/webhooks/github` (inbound), the outbound `run.state_changed`/`evaluation.created` deliveries (00 §9.5; 07.6.2, 02.10.1), tables `webhook_endpoint`, `notification` (00 §9.5, 02.10.1-T2).

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-notifications-1 | S | A forged outbound webhook is accepted by a receiver because the signature is missing or unchecked | External attacker | Spoofed events | control: HMAC over `timestamp.body` with a 5-minute skew window — 07.6.2-T1 [planned] |
| TH-notifications-2 | R | A webhook body is replayed and treated as a new event | External attacker | Duplicate side effects | control: `Evidgate-Event-Id` dedupe window, never re-signing a replayed body — 07.6.2-T1 [planned] |
| TH-notifications-3 | T | Template injection: untrusted content is interpolated into a notification template | Attacker via artifact content | Content injection | control: templates carry no test content or secrets — 02.10.2-T2 [planned] |
| TH-notifications-4 | S, I | A stored notification or webhook link is a phishing vector (any https URL) | External attacker | Phishing | control: https-only with a private-address guard and the host shown — 02.10.2-T1, 08.2.3-T1 [planned] |
| TH-notifications-5 | I | A notification leaks PII or secrets into logs | Defect | Data disclosure | control: no PII beyond recipient and org name in logs — 02.10.1-T2 [planned] |
| TH-notifications-6 | D | A webhook receiver is flooded, or a delivery loop causes retry storms | External attacker | Denial of service | control: at-least-once retry with backoff and a dead-letter state — 07.6.2-T2 [planned] |
| TH-notifications-7 | S | An incoming webhook channel is spoofed, or points at a private address (SSRF) | External attacker | Spoofing, SSRF | control: https-only with a private-address guard and a secret reference — 02.10.2-T1 [planned] |

## 4. Boundary sections

The eleven trust boundaries below are the cross-system edges of the catalogue (§2); each carries the STRIDE threats that realistically apply, dispositioned to a named control or a written acceptance.

### B-web — Browser → API

The Evidgate app origin and the two viewer origins cross into the API over `/v1/auth/*` and the cookie-authenticated `/v1` surface (00 §9.1); on the other side are the ASP.NET Core API, the C2 pipeline and the pre-tenant resolvers (T1). The browser holds no bearer token: the only app credential is the `__Host-` session cookie, and the viewer surface holds only an in-memory HMAC viewer token (T38, T71, T99).

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-web-1 | S | Session hijack through a stolen or forged session cookie | Network attacker or XSS | Account takeover | control: OIDC-only login (no password path, D1) with an opaque `__Host-` HttpOnly Secure SameSite=Lax session cookie (T38) — 02.3.1-T1, 02.3.2-T1 [planned] |
| TH-web-2 | T | Cross-site request forgery on a cookie-authenticated mutation | Malicious site | Unauthorised state change | control: ASP.NET Core antiforgery issuing `XSRF-TOKEN` with Angular `withXsrfConfiguration` sending `X-XSRF-TOKEN`, else 403 `csrf_failed` (T38, 00 §9.1) — 02.3.2-T1, 09.1.1-T5 [planned] |
| TH-web-3 | I | Bearer token theft from browser storage | XSS or malicious extension | Credential theft | control: no token in `localStorage`; the session lives only in the `__Host-` cookie and the viewer token only in Service-Worker memory (T38, T99) — 02.3.2-T1, 09.1.1-T5 [planned] |
| TH-web-4 | T | Script injection into the app origin (XSS) | Hostile content or reflected input | Session theft, data tampering | control: app-origin strict CSP plus Trusted Types with a `bypassSecurityTrust*` lint rule (00 §5.6 W6, 00 §5.7 S7) — 09.1.1-T10, 11.1.1-T1 [planned] |
| TH-web-5 | T, I | Hostile artifact HTML or JS pivots into app credentials | Hostile artifact content | Account takeover | control: viewer-app and viewer-content split origins with `Content-Security-Policy: sandbox; default-src 'none'`, `nosniff` and no shared credential or storage (T71, T74, T99) — 06.3.3-T1, 09.4.1-T5 [planned] |
| TH-web-6 | E | Client-side permission bypass — the UI hides a control but the API trusts the client | Authenticated member | Privilege escalation | control: authorization enforced in the C2 pipeline, not in endpoints (00 §5.5 C2, 00 §5.7 S2) — 02.2.1-T2 [implemented: src/BuildingBlocks/Pipeline/Behaviours/AuthorizationBehaviour.cs] |
| TH-web-7 | I | Cross-tenant read through a guessed or foreign id in a browser request | Member of another org | Data disclosure | control: a project scope mismatch answers 403 (00 §9.2, NFR-11-1) — 02.9.1-T1 [implemented: src/Modules/Evidgate.Modules.Identity/Authorization/PermissionEvaluator.cs] |
| TH-web-8 | S | Phishing through a stored tracker link rendered in the UI (attacker-chosen host) | Malicious tracker URL | Credential phishing, drive-by | control: hardened link rendering — https-only, length-capped, audited, never fetched by the server, `rel="noopener noreferrer"`, host shown (see RR-4) — 08.2.3-T1 [planned] |

#### Security-header baseline (consumed by 06.3.3-T1.3)

| Header | Origin | Value | Source |
|---|---|---|---|
| `Content-Security-Policy` | viewer-app | the org-wide baseline CSP shape (strict, Trusted Types); the concrete directive values are owned by 09.1.1-T10 | 00 §5.7 S7, 06.3.3 AC2, 09.1.1-T10 |
| `Content-Security-Policy` | viewer-content | `sandbox; default-src 'none'` | 06.3.3 AC2, T71 |
| `X-Content-Type-Options` | viewer-app, viewer-content | `nosniff` | 06.3.3 AC2, T71 |
| `Referrer-Policy` | viewer-content | `no-referrer` | 06.3.3 AC2, T74 |
| `Content-Disposition` | viewer-content | `attachment` for every kind except the allow-listed inline kinds (`raw_report`, `trace` resources) | 06.3.3 AC2 |
| `Content-Type` | viewer-content | server-chosen from an allow-list, never client-supplied | 06.3.3 AC2 |
| `Set-Cookie` | viewer-app, viewer-content | none — neither viewer origin is issued the app session credential or any XSRF token, and viewer-content reads no browser credential transport | 06.3.3 AC3, T71, T99 |
| `frame-ancestors` | app origin | set by the single security-header middleware; the concrete value is owned by 09.1.1-T10 | 00 §5.7 S7, 09.1.1-T10 |
| `Strict-Transport-Security` | app origin | set by the single security-header middleware; the concrete value is owned by 09.1.1-T10 | 00 §5.7 S7, 09.1.1-T10 |

Every header row above is planned at the review date (the header middleware, the app-origin CSP and the viewer origins are not built yet). The header values are taken from the plans; what each value blocks in a browser is ⚠️ unverified against a specification link in this draft.

### B-cli-adapters — CLI and adapters → API

The `evidgate` CLI and the per-engine adapters (Playwright reporter, JUnit mapper) cross into the API over `/v1` with a bearer `ci_producer` or ingest token (00 §9.1, §10); on the other side are the API, the token exchange and the capability negotiator.

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-cli-adapters-1 | S | Token theft from the developer machine (a cached credential read by another process) | Local attacker or another user | Impersonation of the CI identity | control: token cached in a `0600` file inside a `0700` user-owned directory, `O_NOFOLLOW` read, never printed (T78) — 04.2.1-T1 [implemented: packages/cli/src/auth/tokenCache.ts] |
| TH-cli-adapters-2 | I | Secret or token leakage into CLI output and logs | Accidental or hostile log capture | Credential disclosure | control: registered-secret redaction of stdout and stderr including trimmed, encoded and escaped forms (T78) — 04.2.1-T2 [implemented: packages/cli/src/redact.ts] |
| TH-cli-adapters-3 | T | An adapter declares a capability it cannot honour (capture control, protocol major) | Malicious or buggy adapter | Evidence integrity loss | control: server-side capability validation, major rejection and `ingest_only` never gate-eligible (00 §10) — 04.7.1-T2 [implemented: src/Modules/Evidgate.Modules.Ingestion/Domain/ProtocolNegotiator.cs] |
| TH-cli-adapters-4 | S | A fork-PR workflow presents a tenant ingest token | Fork contributor | Cross-tenant write | control: a fork run is credential-free and never receives a tenant-level ingest token (FR-11-3) — 11.2.1-T1, 02.4.2-T2 [planned] |
| TH-cli-adapters-5 | R | The CLI reports a gate result it did not verify | Operator error or tampering | False release evidence | control: gate exit codes 0/1/2/3 recorded in `contracts/cli.md`, with the decision read from the server (00 §9.3) — 07.6.1-T2, 04.2.3-T1 [planned] |

### B-agent — Agent → API

The V2 `evidgate-agent` executor crosses into the API over `POST /v1/agents/jobs/claim`, `POST /v1/jobs/{j}/heartbeat` and the artifact and event routes (00 §9.3); on the other side is the orchestrator's lease and epoch machinery (00 §8.2).

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-agent-1 | S | A rogue agent impersonates a pool identity and claims jobs | Compromised agent host | Job theft, evidence forgery | control: per-agent identity per tenant and pool with secret-reference resolution whose values are never logged or returned — 05.7.4-T1, 02.5.1-T1 [planned] |
| TH-agent-2 | T | Double-lease of one job by two agents | Concurrent claim or replay | Duplicate execution, mixed evidence | control: short `FOR UPDATE SKIP LOCKED` claim, 90 s lease and epoch bump (00 §8.2) — 05.7.2-T1 [planned] |
| TH-agent-3 | T | A stale-epoch agent keeps writing after its lease expired | Delayed agent | Stale evidence accepted as current | control: heartbeat extends only the current epoch; foreign-epoch events are stored as stale evidence and never complete a job — 05.7.2-T2, 04.1.2-T2 [planned] |
| TH-agent-4 | E | An agent claims a job its pool cannot run (capability mismatch) | Misconfigured pool | Wrong-engine execution | control: capability matching on protocol major, platform, engine family and version and `supports.cancel`; an unmatchable job is `unschedulable` — 05.7.3-T1 [planned] |
| TH-agent-5 | I | An agent collects artifacts outside the allow-list | Hostile agent | Data exfiltration | control: artifact policy enforcement (allow-listed paths, size caps, sensitivity) at the `collect` boundary — 05.7.4-T3, 06.2.2-T1 [planned] |
| TH-agent-6 | D | A lost agent holds a lease and starves the pool | Agent crash | Queue starvation | control: an expired-lease sweeper re-queues the job as a new lease with an epoch bump — 05.10.1-T2 [planned] |

### B-healing-sandbox — Healing sandbox → host

The healing validator and the V1 replay sandbox cross from `Evidgate.Worker` into a disposable execution environment (11 §4.4, T36); on the other side is the customer-side replay host (O10 option a) and the child-process validator.

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-healing-sandbox-1 | E | A container-only boundary is treated as sufficient for hostile multi-tenant code | Hostile patch | Host escape, cross-tenant compromise | control: disposable environment with allowlist and least privilege, a container not accepted as the isolation boundary for hostile multi-tenant code (see RR-3) — 11.2.1-T4, 11.2.1-T5 [planned] |
| TH-healing-sandbox-2 | E | Sandbox escape through host mounts, the docker socket or the control-plane network | Hostile patch | Host compromise | control: no host mounts, no docker socket, no control-plane network and a default-deny egress allowlist (FR-11-4) — 11.2.1-T6, 11.2.1-T4 [planned] |
| TH-healing-sandbox-3 | I | Tenant secrets reach the sandbox as ambient credentials | Hostile patch | Secret disclosure | control: least-privilege identity with no ambient tenant credentials and no long-lived cloud role — 11.2.1-T6, 05.7.4-T2 [planned] |
| TH-healing-sandbox-4 | D | A pathological patch consumes all CPU, memory or disk | Hostile patch | Worker starvation | control: CPU, memory, disk and wall-time caps with a hard kill enforced by the worker (T36) — 11.2.1-T6, 08.4.1-T3 [planned] |
| TH-healing-sandbox-5 | T | The validator is run inside the API process | Misconfiguration | API compromise | control: `packages/healing-validator` runs as a child process of `Evidgate.Worker` over JSON stdin and stdout, never in the API process (T36) — 08.4.1-T3, 11.2.1-T6 [planned] |
| TH-healing-sandbox-6 | R | A replay patch is applied without a kill switch | Operator error | Uncontrolled change | control: kill switches at system, tenant and project level exist before any apply capability ships (DECISIONS §5.9) — 08.6.3-T1, 10.7.1-T5 [planned] |
| TH-healing-sandbox-7 | T | Teardown is not verified, leaving a live environment behind | Operator error | Residual exposure | control: teardown verified and logged, and escape attempts logged as security events (FR-11-4) — 11.2.1-T6, 08.4.3-T2 [planned] |

### B-llm-gateway — Worker/sandbox → LLM provider

The healing gateway crosses from `Evidgate.Worker` to a provider through the Evidgate-controlled gateway (O4, D5); on the other side is the provider API.

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-llm-gateway-1 | I | Tenant content leaves the boundary without opt-in | Operator error | Cross-border transfer, PDPL breach | control: per-tenant opt-in with a data-owner approval record, default off and revocation (D5, FR-11-6) — 08.5.3-T3, 11.4.1-T3 [planned] |
| TH-llm-gateway-2 | I | Secrets or personal data reach the provider unredacted | Operator error | Secret or personal-data disclosure | control: redaction pipeline before the provider with a redaction report hash; a call without it is refused — 08.5.3-T1, 11.3.1-T5 [planned] |
| TH-llm-gateway-3 | T | Prompt injection in DOM, log or document content changes a policy, approval, permission or gate decision | Hostile artifact content | Unauthorised change | control: untrusted content is delimited data with no tool, shell, secret or merge capability, and the validator is the only authority (FR-11-7) — 08.5.2-T1, 11.4.1-T2 [planned] |
| TH-llm-gateway-4 | E | The model returns a patch that weakens an assertion | Hostile content or model error | False green | control: structured I/O plus the AST allowlist validator and the negative control (DECISIONS §5) — 08.4.1-T1, 08.4.2-T2 [planned] |
| TH-llm-gateway-5 | D | Cost abuse — unbounded provider spend | Buggy loop or hostile tenant | Financial loss | control: per-tenant and per-project budget with a hard stop, a global daily cap, an alert at 80 % and the kill switch — 08.5.1-T1, 08.5.5-T3 [planned] |
| TH-llm-gateway-6 | I | Cross-tenant retrieval reaches a prompt | Retrieval bug | Cross-tenant disclosure | control: tenant-filtered retrieval, so a cross-tenant record never reaches a prompt — 08.5.2-T2, 08.2.1-T1 [planned] |
| TH-llm-gateway-7 | S | A tenant obtains the provider key | Hostile tenant | Provider account abuse | control: the provider key is held only by Evidgate in the secret store and never reaches a tenant, a log or an artifact — 08.5.5-T3, 10.1.1-T2 [planned] |

### B-object-storage — API → object storage

The API and worker cross into S3-compatible object storage for artifact upload and download (00 §9.3 `POST /v1/artifacts/upload-url`, `GET /v1/artifacts/{a}/download-url`); on the other side is the bucket.

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-object-storage-1 | S | A leaked presigned URL is replayed as a bearer credential | Anyone holding the URL | Artifact disclosure | control: a short-lived per-view presigned URL treated as a bearer credential with a short TTL — 06.2.1-T2, 11.3.1-T4 [planned] |
| TH-object-storage-2 | T | A client chooses its own object key (path traversal or overwrite) | Malicious client | Object overwrite, confused deputy | control: server-chosen keys with a key-shape CHECK, and client keys rejected — 06.1.1-T1, 06.1.1-T2 [implemented: migrations/0028_evidence_artifact.sql] |
| TH-object-storage-3 | T | Uploaded bytes do not match the declared size or checksum | Malicious client | Corrupt evidence accepted | control: `complete` re-checks size and checksum before `ready`, and a mismatch becomes `rejected` (00 §8.4) — 06.1.2-T2 [planned] |
| TH-object-storage-4 | I | A public bucket exposes artifacts | Misconfiguration | Mass disclosure | control: no public buckets, with a separate write-only app credential and a deletion-only retention credential — 10.5.2-T1, 11.3.1-T4 [planned] |
| TH-object-storage-5 | I | A secret planted in an artifact is served as evidence | Customer mistake | Secret disclosure | control: an async secret scan on `complete` sets `scan_state`, raises sensitivity and enforces `reject_on_secret` (T19) — 06.2.3-T1 [planned] |
| TH-object-storage-6 | D | A compromised app credential deletes evidence | Compromised credential | Evidence loss | control: object versioning and lifecycle with a deletion-only retention credential, plus an orphan-object sweep — 10.5.1-T2, 10.5.2-T1 [planned] |

### B-database — API/worker → PostgreSQL

The API and worker cross into PostgreSQL through `ITenantScope` (00 §5.4, §5.5 C3); on the other side is the database with the owner, app and operator role split.

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-database-1 | E | The app role is the table owner or has `BYPASSRLS`, so RLS is bypassed | Misconfiguration | Cross-tenant read and write | control: role split `evidgate_owner`, `evidgate_app` and `evidgate_operator`, with the app role not the owner and no `BYPASSRLS` (DECISIONS F5) — 01.4.1-T2, 02.6.1-T1 [implemented: migrations/0001_foundation_roles.sql] |
| TH-database-2 | I | A query runs without the tenant setting and returns all rows | Code defect | Cross-tenant disclosure | control: `FORCE ROW LEVEL SECURITY` with the `evidgate.current_org_id()` helpers, so an unset GUC yields zero rows (T2) — 01.4.1-T3, 02.6.1-T1 [implemented: migrations/0003_foundation_rls_probe.sql] |
| TH-database-3 | T | The tenant GUC leaks across pooled connections | Connection reuse | Cross-tenant write | control: parameterised `set_config('evidgate.org_id', …, true)` per transaction via `ITenantScope.BeginAsync` (00 §5.5 C3) — 01.4.2-T1, 02.6.2-T1 [implemented: src/BuildingBlocks/Persistence/Tenancy/TenantScope.cs] |
| TH-database-4 | T | SQL injection through string-built SQL | Malicious input | Data tampering | control: parameterised SQL only (Dapper parameters, EF LINQ), no string-built SQL including `set_config` (00 §5.7 S1) — 01.4.2-T1, 11.7.1-T2 [planned] |
| TH-database-5 | E | A new table ships without RLS or without `unique (org_id, id)` | Code defect | Silent isolation gap | control: an RLS lint in CI fails a table without `org_id`, a policy or `unique (org_id, id)` (T3) — 02.6.1-T2 [implemented: scripts/check-rls.py] |
| TH-database-6 | I | A cross-tenant read succeeds through a view, search or a Dapper read model | Code defect | Cross-tenant disclosure | control: the tenant-isolation suite over API, FK, RLS, views and Dapper reads (NFR-11-1) — 02.9.1-T3, 11.7.1-T1 [planned] |
| TH-database-7 | E | A `SECURITY DEFINER` resolver is abused to enumerate bindings or leak rows | Authenticated caller | Cross-tenant disclosure | control: `SECURITY DEFINER` resolvers with pinned `search_path`, a return limited to `(org_id, project_id, binding_id, status)`, app role `EXECUTE`-only (T1, T80) — the member_orgs resolvers (0014) and the ci-binding/ingest-token resolvers (0016/0018); the per-call audit is 02.7.1-T2.2, planned — 02.6.1-T3, 11.2.1-T7, 02.7.1-T2.2 [implemented: migrations/0014_identity_resolve_member_orgs.sql, migrations/0016_identity_ci_binding.sql, migrations/0018_identity_ingest_token.sql] |
| TH-database-8 | R | The audit chain is edited or truncated | Privileged insider | Unprovable audit | control: an append-only audit with a per-org hash chain serialised by `pg_advisory_xact_lock` (T8) — 02.7.1-T1, 11.5.2-T1 [implemented: migrations/0015_identity_audit_log.sql] |
| TH-database-9 | I | The data-protection key ring is compromised, so stored ciphertext (webhook signing secrets, invite secrets) is decryptable | Insider or attacker with database access | Secret disclosure | control: data-protection keys stored in PostgreSQL and encrypted at rest by the provider, with a versioned key rotation runbook (00 §5.7 S8) — 10.7.1-T4, 11.3.1-T4 [planned] |

### B-ci — Customer CI → API

The customer's CI job crosses into the API over `POST /v1/ci/token` (GitHub OIDC exchange or ingest-token auth) and the ingest routes, and the GitHub App crosses in over `POST /v1/webhooks/github` (00 §9.3); on the other side are the pre-tenant resolvers (T1) and the `ci_producer` token.

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-ci-1 | S | A stolen long-lived CI credential is replayed | Attacker holding the secret | Cross-tenant write | control: a GitHub OIDC exchange for a short-lived `ci_producer` token scoped to one project (D2, F3) — 02.4.1-T1, 02.4.1-T3 [implemented: src/Modules/Evidgate.Modules.Identity/Ci/GitHubOidc.cs] |
| TH-ci-2 | S | A replayed OIDC JWT mints a second token | Attacker with a captured JWT | Token duplication | control: a `ci_token_issue` replay cache keyed on `jti` for the token lifetime plus 10 min skew (T7) — 02.4.1-T4 [implemented: migrations/0017_identity_ci_token_issue.sql] |
| TH-ci-3 | S | An ingest token is guessed or brute-forced | Unauthenticated attacker | Cross-tenant write | control: a ≥128-bit random ingest token with a public prefix (T5) — 02.4.2-T1 [implemented: src/Modules/Evidgate.Modules.Identity/Ci/IngestTokenFormat.cs] |
| TH-ci-4 | S | A second org binds a repository it does not control | Attacker org | Evidence forgery | control: a `ci_binding` is created `pending_verification` and activates only when an OIDC exchange presents the repo-owner challenge (T70) — 02.4.1-T2, 02.4.1-T3 [implemented: migrations/0016_identity_ci_binding.sql] |
| TH-ci-5 | S | A forged GitHub webhook delivery changes catalog data | Attacker without the webhook secret | Catalog tampering | control: `X-Hub-Signature-256` verify, then pre-tenant resolution and `webhook_delivery` dedupe before any tenant scope opens (T1) — 03.4.1-T2 [planned] |
| TH-ci-6 | T | A leaked ingest token forges a shard before the legitimate producer claims its slot | Attacker with a leaked token | Forged evidence | accepted: RR-1 — the slot-binding enforcement (04.1.2-T5) and the security-event baseline (11.5.1-T5) are planned, so the residual stays open at M1 until they ship; tolerable meanwhile because no pilot data exists yet (pilot zero is the owner's own pipeline, `docs/decisions/O1-pilot-profile.md` §1); re-review when 04.1.2-T5 and 11.5.1-T5 ship |
| TH-ci-7 | T | A healing replay patch runs next to the customer's CI secrets | Hostile patch in customer CI | Secret disclosure | accepted: RR-2 — the reference workflow (`permissions: {}`, no `secrets:`, `workflow_dispatch` on a secretless environment, limited egress) and the CLI replay refusal (11.2.1-T6) are planned, so the residual stays open at M1 until they ship; tolerable meanwhile because no pilot data exists yet (pilot zero is the owner's own pipeline, `docs/decisions/O1-pilot-profile.md` §1); re-review when 11.2.1-T6 ships (⚠️ heuristics) |
| TH-ci-8 | D | A burst of batches or token exchanges exhausts the API | Unauthenticated attacker | Availability loss | control: a per-project rate limit and quota with `Retry-After`, batches of ≤ 500 events and ≤ 1 MiB, and per-IP limits on unauthenticated routes — 04.1.2-T1, 04.1.1-T3, 10.11.1-T1 [planned] |
| TH-ci-9 | E | A `ci_producer` token scoped to one project is used on another | Attacker with a token | Cross-project write | control: the project in the URL must match the token scope, else 403 `project_scope_mismatch` (00 §9.1) — 02.4.1-T3, 02.9.1-T1 [implemented: src/Modules/Evidgate.Modules.Identity/Authorization/PermissionEvaluator.cs] |
| TH-ci-10 | T | A dependency with a known vulnerability or a disallowed licence ships into the build (supply chain) | Upstream package or attacker | Compromised build, licence breach | control: dual-ecosystem supply chain T40 — committed lock files, a vulnerability gate, a licence allowlist gate and a CycloneDX SBOM per build (00 §5.7 S6) — 01.7.1-T2, 01.7.1-T4, 11.6.1-T1 [implemented: scripts/audit-deps.sh, scripts/licence-scan.sh, scripts/sbom.sh, package-lock.json, src/Evidgate.Api/packages.lock.json, .github/dependabot.yml] |

### B-artifact-parsers — Untrusted artifact content → parsers/renderers

Untrusted artifact bytes (JUnit XML, HTML reports, Playwright traces, archives) cross into the parser and the renderer (11 §4.1, 00 §5.7 S5); on the other side are the parse runner and the isolated viewer.

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-artifact-parsers-1 | I | XXE or external entity expansion reads local files or reaches the network | Hostile artifact | File disclosure, SSRF | control: `XmlReader` with `DtdProcessing.Prohibit` and `XmlResolver = null`, plus entity, depth and size caps and a cooperative 30 s time cap (checked between reads and per node, so it cannot interrupt a blocking read; hard isolation is 06.3.6-T1) (00 §5.7 S5) — 06.3.1-T1, 04.4.1-T2 [implemented: src/Modules/Evidgate.Modules.Evidence/Infrastructure/Parsing/SafeXml.cs]; the ingestion-side report parser that consumes the same profile is 04.4.1-T2, planned |
| TH-artifact-parsers-2 | T | Zip-slip — an archive entry escapes the extraction root | Hostile artifact | Arbitrary file write | control: archive limits (entries, ratio, nesting) and path-traversal rejection — 06.3.1-T2, 06.3.4-T2 [planned] |
| TH-artifact-parsers-3 | D | A decompression bomb or memory bomb exhausts the worker | Hostile artifact | Worker starvation | control: the parse runner as a child process with memory, CPU and wall-time limits and no network, plus a per-tenant parse concurrency cap — 06.3.6-T1, 06.3.6-T2 [planned] |
| TH-artifact-parsers-4 | T | A MIME mismatch makes a hostile file render as a trusted type | Hostile artifact | Script execution | control: a MIME allowlist and a sniff-versus-declared check, with a server-chosen `Content-Type` from an allow-list — 06.1.2-T3, 06.3.1-T2 [planned] |
| TH-artifact-parsers-5 | T | Hostile HTML or JS in a report executes in the app origin | Hostile artifact | Session theft | control: the isolated viewer origin with `Content-Security-Policy: sandbox; default-src 'none'` and `nosniff` (T71) — 06.3.3-T1, 06.3.4-T1 [planned] |
| TH-artifact-parsers-6 | T | A parser regression silently accepts a hostile input | Code defect | Guard bypass | control: a hostile-input fixture suite and a fuzz harness that fails the build on a guard regression — 06.3.2-T1, 06.3.2-T2 [planned] |

### B-webhook-out — API → webhook receivers

The API crosses out to customer webhook receivers (00 §9.5, `Evidgate-Signature`); on the other side is the customer's endpoint.

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-webhook-out-1 | S | A forged webhook is accepted as genuine | Attacker | Spoofed event | control: `v1=hmac-sha256(secret, timestamp + "." + body)` with `Evidgate-Event-Id` and `Evidgate-Timestamp` (FR-11-9) — 07.6.2-T1 [planned] |
| TH-webhook-out-2 | T | A captured webhook body is replayed | Attacker with a body | Duplicate side effect | control: receivers reject timestamps older than 5 min, a per-endpoint dedupe window applies, and a replayed body is never re-signed as new (00 §9.5) — 07.6.2-T1, 07.6.2-T2 [planned] |
| TH-webhook-out-3 | T | A duplicate delivery is processed twice | Retry or network | Double side effect | control: event-id dedupe and at-least-once retry with a stable signature for 24 h — 07.6.2-T2 [planned] |
| TH-webhook-out-4 | I | A webhook target is a private or link-local address (SSRF) | Malicious tenant | Internal network probe | control: an https-only endpoint with a private-address and SSRF guard and redirect refusal (00 §5.7 S4) — 07.6.3-T1, 07.6.3-T3 [planned] |
| TH-webhook-out-5 | I | The signing secret leaks through delivery history or logs | Operator error | Forgery | control: a show-once signing secret kept as a secret reference, with rotation under an overlap window and an audit row — 07.6.3-T1, 07.6.3-T2 [planned] |

### B-operator — Operator → tenant data

The platform operator crosses into tenant data over `/internal/operator/*` and `POST /v1/support-grants` (00 §9.1, §9.3); on the other side is the tenant's data under RLS.

| ID | STRIDE | Threat | Attacker | Impact | Disposition |
|---|---|---|---|---|---|
| TH-operator-1 | E | An operator reads tenant data without an approved grant | Operator | Undisclosed access | control: `evidgate_operator` RLS policies call `evidgate.operator_grant_ok(org_id, project_id, action)` validating `evidgate.support_grant_id` against an approved, unexpired grant (T76) — 02.8.1-T1 [planned] |
| TH-operator-2 | E | An operator writes tenant data beyond the grant scope | Operator | Data tampering | control: operator access is read-only unless the grant scope names a write action (T76) — 02.8.1-T1, 02.8.1-T2 [planned] |
| TH-operator-3 | R | Support access is not visible to the tenant | Operator | Undisclosed access | control: a tenant-visible support banner and support-grant rows in the tenant audit feed (FR-11-8, D7) — 02.8.1-T2, 09.7.1-T10 [planned] |
| TH-operator-4 | R | An operator action is not audited | Operator | Unprovable access | control: every operator read and write is audited with the grant id, and break-glass requires a written reason — 11.5.1-T2, 11.5.1-T4 [planned] |
| TH-operator-5 | E | A support grant never expires | Operator | Standing access | control: scoped and expiring grants with expiry enforced without manual action, plus a quarterly operator-access review — 02.8.1-T1, 11.5.1-T4 [planned] |
| TH-operator-6 | S | An unauthenticated caller reaches the operator ingress | Attacker | Privileged access | control: the fail-closed operator gate (deny-all default) on `/internal/operator/*` — 01.5.1-T4 [implemented: src/Evidgate.Api/OperatorIngress/OperatorGate.cs]; authentication as `platform_operator` is 02.8.1-T2, planned |

## 5. Residual-risk register

| ID | Title | State | Owner | Reviewed | Next review | Receiving task | Rationale |
|---|---|---|---|---|---|---|---|
| RR-1 | T9 producer-slot residual: a leaked ingest token racing for an unclaimed slot | accepted | Tech Lead | 2026-10-11 | M2 | 04.1.2-T4, 02.4.1-T3 | Producer-slot binding (00 §9.4) binds each `producer_slot` to the token identity at the first accepted write; a leaked ingest token that reaches an *unclaimed* slot before the legitimate producer can still forge that shard's evidence. The enforcement that rejects a foreign identity (04.1.2-T5) and the security-event baseline that records it (11.5.1-T5) are planned, so the residual stays open at M1 until they ship. Tolerable meanwhile because no pilot data exists yet: pilot zero is the owner's own pipeline (`docs/decisions/O1-pilot-profile.md` §1). Re-review when 04.1.2-T5 and 11.5.1-T5 ship (plan 11 §4.1 "Residual risk (T9)"). |
| RR-2 | T77 replay-in-customer-CI residual: untrusted-context patches run next to CI secrets | accepted | Tech Lead | 2026-10-11 | M3 | 11.2.1-T6 | Replay in customer CI (O10 option a) runs untrusted-context patches next to CI secrets. Controls: the reference workflow (`permissions: {}`, no `secrets:`, `workflow_dispatch` on a secretless environment, egress limited to the app under test) and a CLI that refuses `replay` on `GITHUB_TOKEN` write scopes or secret-like env vars. Both are planned under 11.2.1-T6, so the residual stays open at M1 until it ships; tolerable meanwhile because no pilot data exists yet (pilot zero is the owner's own pipeline, `docs/decisions/O1-pilot-profile.md` §1). Re-review when 11.2.1-T6 ships. The refusal heuristics are ⚠️ unverified (OI-11-20, `plans/open-items/04-ops-security-commercial.md`; verify against docs.github.com). |
| RR-3 | Container-only isolation of the healing sandbox | open-decision | Tech Lead | 2026-10-11 | M3 | 11.2.1-T4 | [OPEN] Whether a container-only isolation boundary is acceptable for any V2 execution, or the agent must stay wholly customer-side (area plan 11 §3 D4 + §11 open question 4). Owner decision pending; V1–V2 execution stays wholly customer-side until a microVM-grade boundary passes 11.2.1-T5. |
| RR-4 | Tracker-host allow-list vs "any https URL" (phishing via stored links) | open-decision | Tech Lead | 2026-10-11 | M3 | 08.2.3-T1.1 | [OPEN] Whether `incident.external_issue_url` is restricted to an org allow-list of tracker hosts or accepts any `https` URL (OI-08-12, `plans/open-items/03-policy-healing-web.md`). Recommendation `[INFERENCE]`: accept "any https" for V1 with the host shown, `rel="noopener noreferrer"` and no userinfo; add an allow-list only on a pilot request. |

Notes (source and hand-over per entry):

- RR-1 — source: `plans/11-security-compliance.md` §4.1 "Residual risk (T9)"; carried by 04.1.2-T4 (the `producer_slot` table) and 02.4.1-T3 (mints the `ci_producer` token); hand-over row: STATUS §6 (11.1.1-T1).
- RR-2 — source: ruling T77 (`plans/features/README.md` §3.5) and O10 option (a) (`DECISIONS.md` §8); open question OI-11-20 (`plans/open-items/04-ops-security-commercial.md`); carried by 11.2.1-T6; hand-over row: STATUS §6 (11.1.1-T1).
- RR-3 — source: `plans/11-security-compliance.md` §3 D4 and §11 open question 4; carried by 11.2.1-T4; hand-over row: STATUS §6 (11.1.1-T1).
- RR-4 — source: OI-08-12 (`plans/open-items/03-policy-healing-web.md`) and `plans/08-healing-learning.md` 08.2.3 §3; carried by 08.2.3-T1.1 (parent 08.2.3-T1); hand-over row: STATUS §6 (11.1.1-T1).

## 6. Milestone-exit ritual

Each milestone exit (00 §4) carries a **threat model re-review** gate item. The reviewer is not the author of the change under review (rotating-review rule, DECISIONS §5.5 spirit); the owner alone (`Tech Lead`, O2) signs the register.

Steps at each milestone exit:

1. Re-read the catalogue (§2) against the then-current module list (00 §5.1) and the trust-boundary sweep (`plans/11-security-compliance.md` §4.1); add or remove catalogue rows and their sections.
2. Run `scripts/check-threat-model.sh`; it must exit 0.
3. Update the threat rows for endpoints and controls that shipped or changed since the last review; a new endpoint or module adds rows (§1).
4. Update the register (§5): set `Reviewed` to the review date and move `Next review` forward.
5. Append a dated row to the log table below.

**Rule.** A milestone gate cannot pass while `scripts/check-threat-model.sh` is red (exit ≠ 0).

| Milestone | Re-review slot | Last review | Result |
|---|---|---|---|
| M1 | 00 §4 M1 exit gate — "Mapping fixtures, dedupe and tenant isolation tests pass" | 2026-10-11 | draft v1 written; checker green |
| M2 | 00 §4 M2 exit gate — "A run with missing evidence never yields PASS; a restore drill passes" | pending | pending |
| M3 | 00 §4 M3 exit gate — "Customer uses it repeatedly; cost and quality measured" | pending | pending |

| Date | Milestone | Reviewer | Change |
|---|---|---|---|
| 2026-10-11 | M1 | Tech Lead | v1 written (T1.1–T1.4) |

#### Hand-overs recorded

- OI-11-20 (T77 replay-refusal heuristics, `plans/open-items/04-ops-security-commercial.md`) → RR-2.
- OI-08-12 (tracker-host allow-list, `plans/open-items/03-policy-healing-web.md`) → RR-4.
- The CSP/header baseline consumed by 06.3.3-T1.3 (`plans/features/06-evidence-artifacts/06.3.3-isolated-viewer-origin.md`) → the B-web subsection (§4).
- The concrete viewer-app CSP / `frame-ancestors` / `Strict-Transport-Security` directive values are owned by 09.1.1-T10 (`plans/09-web-ui.md`); this document fixes only the policy shape (the header table in §4), not the values.
- `plans/00-overview.md` §4 exit-gate cells do not yet mention the threat-model re-review; that edit is recorded in STATUS §6 rather than made in 00 here.

## 7. Disposition summary

| State | Count |
|---|---|
| control (implemented) | 49 |
| control (planned) | 101 |
| accepted | 3 |
| open | 0 |

Counts are computed by `scripts/check-threat-model.sh --summary`, and the checker fails the build when this table drifts from the rows above.
