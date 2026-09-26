# 11 - Security and Compliance (M1-M4)

> Contracts: `00-overview.md` (§2 roles, §9.2 errors, §9.4 events, §9.5 webhooks, §11 NFRs, §12 DoD). Facts: `../DECISIONS.md` §3 (A9/A10), §4 (licence rule), §5 (healing rules), §6 F3/F4/F5/F7/F8, §7 (legal position), §8 (O3/O4). Source: design §8 (security and permission boundaries), §9 (operational deployment and acceptance).
> Level: high-level plan; each story gets a detailed plan in `plans/features/` (00 §12) before build.
> Legend: ✅ verified (link opened for this plan, 2026-09-24) · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`.

## 1. Purpose and scope

**Goal:** make the security and data-protection claims in the product true and provable, so a customer QA lead can sign a pilot and their legal reviewer can accept it.

**In scope**

- Threat model (STRIDE) per module and per trust boundary, kept versioned.
- Untrusted CI and fork pull requests: what we tell customers, what we refuse to accept, and how a fork run cannot reach our credentials.
- Sandbox requirements for any code Evidgate executes (V2 agent, healing sandbox) and the boundary required before hosted multi-tenant execution (V3).
- Secrets as references only; masking; rotation; leak response.
- AI permissions and prompt-injection containment.
- Audit, support access, monitoring, break-glass.
- Webhook authentication and replay protection.
- Dependency and supply-chain controls (npm + NuGet lockfiles, pinned actions, provenance, SBOM, licence allowlist) (T40).
- Application security verification: ASVS level decision ⚠️ (L2 proposed), tenant-isolation red team, pen test before the pilot.
- PDPL (Law 91/2025/QH15) program: DPA, privacy notice, data map, impact dossiers, transfer assessment, consent stance, deletion SLAs, breach runbook, sub-processor list.
- Data residency decision support for O3/O4.

**Out of scope (V1-V2):** ISO 27001 / SOC 2 certification, HIPAA/PCI scope, SAML/enterprise identity federation, on-premise packaging, and any hosted runner or device farm (V3, gated by 11.2.1-T5).

**Version split:** V1 = baseline + PDPL program + pen test (M1-M3). V2 = AI security, ASVS walkthrough with evidence (M4). V3 = hosted multi-tenant isolation evaluation (M5).

## 2. Requirements

- **FR-11-1** A versioned threat model enumerates trust boundaries and STRIDE threats per module (`identity`, `tenancy`, `catalog`, `orchestrator`, `ingestion`, `evidence`, `policy`, `healing`, `usage`, `audit`, `notifications`, plus web, CLI/adapters, agent, healing sandbox, LLM gateway, object storage, database, CI). Every threat names a control and a task id; a threat without a control is either accepted in writing or a gap.
- **FR-11-2** Untrusted CI: our published CI recipe (a) never uses `pull_request_target` to check out untrusted code, (b) pins every action to a full commit SHA, (c) grants `GITHUB_TOKEN` read-only by default and elevates per job, (d) registers/masks derived secrets with `::add-mask::`, (e) never puts a long-lived Evidgate credential in a workflow. ✅ Verified guidance: GitHub "Secure use reference" documents exactly these points, including the `pull_request_target`/`workflow_run` risk with untrusted checkout, secret masking, least-privilege `GITHUB_TOKEN`, and SHA pinning ([docs.github.com/en/actions/reference/security/secure-use](https://docs.github.com/en/actions/reference/security/secure-use), read 2026-09-24).
- **FR-11-3** A workflow triggered by a fork pull request must be able to run tests **without any Evidgate credential**; if such a run uploads results, it does so through a separately minted, project-scoped, short-lived token that the maintainer's workflow explicitly provides, or not at all. A fork run must never receive a tenant-level ingest token.
- **FR-11-4** Any code Evidgate executes (V2 agent jobs, healing sandbox, dry runs) runs in a disposable environment with a default-deny network allowlist, a least-privilege identity with no ambient tenant credentials, no host mounts, resource caps, and verified teardown. A single container is **not** assumed sufficient for hostile code `[OPEN]`; hosted multi-tenant execution requires a VM/microVM-grade boundary validated in 11.2.1-T5 before it ships (V3).
- **FR-11-5** Secrets are stored as **references** (customer vault/CI or our secret manager) and resolved per job with the narrowest scope; values never enter the database, logs, artifacts, error `details`, traces or metric labels. Derived values (tokens, signed URLs) are masked/registered; ingestion masks before upload.
- **FR-11-6** AI: the model receives structured input and returns a structured candidate only. It has no shell tool, no repository merge right, no policy edit right, and no access to secrets or other tenants. Retrieval is tenant-filtered. Sending content to a provider is opt-in per tenant, redacted at source, and blocked until the tenant's data owner approves.
- **FR-11-7** Prompt injection is assumed: DOM, logs, documents and artifact content are untrusted input. No instruction found inside content may change a policy, an approval, a permission, a gate decision or a kill switch; the independent rule engine (validator) is the only authority over a candidate, and injection attempts are logged as security events (read endpoint 11.5.1-T5, `security_event:read`, `tenant_admin` only) and visible to the tenant in the Settings security-events view (09.7.1-T9); the V1 event set is the 11.1.1-T4 baseline plus support-access rows (02.8.1-T1, 11.5.1-T2), with injection-attempt events joining it in V2 (11.4.1-T2).
- **FR-11-8** Audit: every security-relevant action records actor, action, object, before/after hash and `request_id`; the log is append-only and readable per tenant. Support access is scoped, time-boxed, tenant-visible in the UI, and itself audited.
- **FR-11-9** Webhooks: outbound signature `v1=hmac-sha256(secret, timestamp + "." + body)` with `Evidgate-Event-Id` and `Evidgate-Timestamp`; receivers reject timestamps older than 5 min (00 §9.5); we keep a per-endpoint dedupe window and never re-sign a replayed body as new.
- **FR-11-10** Supply chain, dual ecosystem (T40): npm side — committed `package-lock.json` and `npm ci`; NuGet side — committed `packages.lock.json` (`<RestorePackagesWithLockFile>`) with `dotnet restore --locked-mode` in CI, `dotnet list package --vulnerable --include-transitive` (plus `--deprecated`/`--outdated` or `dotnet nuget audit`) gating server/worker dependencies; dependency review on pull requests; licence allowlist enforcing the DECISIONS §4 rule (no AGPL/SSPL/BUSL/non-commercial in shipped or hosted code) across **both** ecosystems (NuGet licence tool chosen and licence-checked in 01.9.1 ⚠️); a CycloneDX SBOM per release covering NuGet and npm ⚠️ (tool licence checked in 11.6.1-T3); CodeQL scanning for C# and TypeScript (11.7.1-T2); provenance for published packages; a vulnerability triage SLA.
- **FR-11-11** ASVS: the target level is **⚠️ L2 (proposed)** for web and API; a chapter-level mapping with a published gap list is produced in 11.1.1-T3 before the pilot. This is a verification target, not a certification.
- **FR-11-12** An external penetration test covering tenant isolation, authentication/session, artifact access, injection, webhook and sandbox surfaces is executed before the M3 pilot exit; critical and high findings are fixed and retested.
- **FR-11-13** PDPL: a processing contract (DPA) is signed **before** the first pilot upload (Art. 37.2.a ✅: a processor may receive personal data only after entering into an agreement/contract on personal data processing — [Law 91/2025/QH15 English text](https://english.luatvietnam.vn/dan-su/law-on-personal-data-protection-law-no-91-2025-qh15-405135-d1.html), Art. 37.2, read 2026-09-24); a processing impact dossier is prepared and kept (Art. 21.1/21.3 ✅); a cross-border transfer impact dossier is filed within 60 days of the first transfer (Art. 20.2 ✅); a breach is notified within 72 hours with the processor notifying the controller **promptly** rather than waiting for the clock (Art. 23.1 ✅); consent duties are documented as the customer's (Art. 9.2/9.4 ✅, "silence or non-response is not considered consent").
- **FR-11-14** Artifacts may contain personal data (DECISIONS §7): redaction at source is supported and default-on for known patterns; "artifact content to an LLM provider" is off by default; sensitive artifacts require `artifact:read_sensitive` (00 §2) and are viewed on an isolated origin (source §8).
- **FR-11-15** Deletion: tenant, project, artifact and log deletions execute within a stated SLA, are verifiable (a deletion report the customer can keep), and never leave the deleted object readable through a cached URL or a backup that is not itself covered by the stated retention.
- **FR-11-16** Residency: the platform can state, per store (database, object storage, backups, provider calls), where data physically resides; a transfer outside Vietnam triggers the Art. 20 process. Decision belongs to O3/O4.
- **NFR-11-1** No cross-tenant path exists in API, foreign keys, RLS policies, views, search or presigned URLs, proven by an automated red-team suite on real PostgreSQL (00 §11 tenant isolation).
- **NFR-11-2** Secrets never appear in logs or metric labels, proven by automated scanning of build and runtime logs (00 §11).

**Acceptance criteria (testable)**

1. A fork PR workflow that runs the example suite with no Evidgate credential produces a normal test report; an attempt to use a tenant ingest token from that context fails authorisation and is logged.
2. A prompt-injection payload embedded in a DOM snapshot cannot cause a policy change, an approval, a permission change or a gate verdict; the attempt is recorded as a security event.
3. A webhook delivered twice, or with a timestamp older than 5 minutes, is rejected/ignored exactly once and logged.
4. The tenant-isolation suite (API, FK, RLS, views, presigned URL) passes on a PostgreSQL instance with `FORCE ROW LEVEL SECURITY` and a non-owner application role.
5. A deletion request produces a deletion report; the object is unreadable through its previously issued presigned URL after completion.
6. The pen-test report exists with all critical/high findings closed or accepted in writing by the owner before pilot exit.

## 3. Key decisions

| # | Decision | Rationale | Rejected |
|---|---|---|---|
| D1 | No password storage; OIDC login only in V1 (DECISIONS R5) | Smallest credential surface; enterprise SAML is V3 | Email+password, social login without SSO |
| D2 | Customer CI authenticates with GitHub OIDC → short-lived Evidgate token (DECISIONS R4/F3); other CIs use a hashed project token | No long-lived secret in customer CI; per-job scope | Long-lived API key per repository |
| D3 | ASVS **⚠️ L2 proposed** with a published gap list; no certification claim | Useful, verifiable target at pilot scale; a certification claim we cannot back is worse than none | Claiming L2 compliance without evidence; L3 (cost without a customer asking) |
| D4 | Sandbox: disposable + allowlist + least privilege; a container is not accepted as the isolation boundary for hostile multi-tenant code | Source §8 requires VM/microVM or an equivalent verified boundary for hosted multi-tenant | Treating a single container or a shared runner as sufficient |
| D5 | LLM context is opt-in per tenant, redacted at source, and never includes secrets or other tenants' data (DECISIONS §7) | Cross-border transfer (Art. 20.1.c ✅) and the healing safety boundary | "Send everything, filter later" |
| D6 | Healing runs with kill switches before any apply capability ships (DECISIONS §5.9) | A broken healing path must not be able to stop ingestion or gates | Shipping apply first, kill switch later |
| D7 | Audit is append-only with hashes; support access is time-boxed and tenant-visible | "Who saw what" is part of the product promise for evidence | Editable audit tables; silent support access |
| D8 | Deletion is verified by artifact (a report), not asserted | Customers must be able to answer a regulator | Soft delete only |
| D9 | The external pen test is a pilot exit condition, not a nice-to-have | Cheapest way to find tenant-isolation bugs before customers depend on it | Relying on internal review only |

## 4. Design

### 4.1 Trust boundaries (STRIDE sweep, source §8)

| Boundary | Threats considered | Controls |
|---|---|---|
| Browser → API | spoofing, tampering, XSS-based session theft, CSRF | OIDC, `__Host-` session cookie `HttpOnly; Secure; SameSite=Lax`, ASP.NET Core antiforgery issuing `XSRF-TOKEN` with Angular `withXsrfConfiguration` sending `X-XSRF-TOKEN` on cookie-authenticated mutations (T38), CSP, no token in `localStorage`, isolated origin for artifact HTML |
| Customer CI → API | spoofing (stolen token), replay, flooding | OIDC-issued short-lived token, hashed ingest token, batch limits, per-project scope, sequence uniqueness |
| Agent → API | impersonation, stale lease, job theft | per-agent identity, epoch fencing, capability match, heartbeat, lease expiry |
| API/worker → PostgreSQL | privilege escalation, cross-tenant read | app role not owner, `FORCE ROW LEVEL SECURITY` (DECISIONS F5), parameterised `set_config('evidgate.org_id', …, true)` per transaction via `ITenantScope.BeginAsync` (00 §5.5 C3), composite FKs |
| API → object storage | URL leakage, confused deputy | short-lived presigned URL, checksum verification, server-chosen keys, no public buckets |
| Worker/sandbox → LLM provider | information disclosure, injection, cost abuse | allowlist, redaction, opt-in, structured I/O, token and quota caps, no secret material in prompts |
| Untrusted artifact content → our parsers/renderers | XXE, zip-slip, decompression bomb, HTML/JS | external entities off, path and size limits, MIME allowlist, isolated origin, Angular sanitizer on by default — no `[innerHTML]` binding on untrusted content, no `bypassSecurityTrust*` outside the reviewed viewer bridge (lint-enforced, T38/§4 mapping) |
| API → webhook receivers | spoofing, replay | HMAC over `timestamp.body`, 5-min skew, event-id dedupe, retry/backoff with stable signature |
| Operator → tenant data | abuse of privilege, undisclosed access | scoped+expiring support grants, UI banner, audit, quarterly access review |

**Residual risk (T9):** producer-slot binding (00 §9.4) binds each `producer_slot` to the token identity at the first accepted write; a leaked ingest token that reaches an *unclaimed* slot before the legitimate producer can still forge that shard's evidence. Accepted residual: slots are per-run and short-lived, bound claims must match the run's CI reference, foreign identities get `403 producer_slot_taken` logged as a security event; recorded in the threat model, no further control in V1.

### 4.2 Secrets lifecycle (references only)

| Stage | Rule |
|---|---|
| Store | Secret **values** live in the customer's vault/CI or our secret manager; our database stores a reference (`secret_ref` id, scope, rotation state) — no value column |
| Resolve | Per job, narrowest scope, short TTL; resolution is itself audited |
| Use | Injected into the process environment only; never passed as a command-line argument (visible via `ps`, per GitHub's warning about same-runner job visibility) |
| Mask | Mask before upload; derived values registered as secrets; scanner patterns for common key shapes |
| Rotate | Documented rotation order per system (issue new → verify → revoke old); leak runbook purges logs and rotates |
| Audit | Resolution events appear in the tenant audit view without the value |

### 4.3 Compliance artifacts (PDPL)

Every article citation in this table is from the Law text I opened: Law on Personal Data Protection No. 91/2025/QH15, English text ✅ ([english.luatvietnam.vn](https://english.luatvietnam.vn/dan-su/law-on-personal-data-protection-law-no-91-2025-qh15-405135-d1.html), Arts. 8, 9, 20, 21, 23, 37 read 2026-09-24). The GitHub citation used in FR-11-2 is ✅ ([docs.github.com secure use](https://docs.github.com/en/actions/reference/security/secure-use), read 2026-09-24).

| Artifact | Content | Article | Owner |
|---|---|---|---|
| DPA | controller/processor roles, subject matter, duration, sub-processors, breach cooperation, audit rights, deletion, cross-border clauses | Art. 37.2.a ✅ (contract before receiving data) | Tech Lead + counsel |
| Privacy notice | what the platform processes, purposes, retention, rights, contact; Art. 9.2 elements if we ever collect personal data directly (e.g. account data) | Art. 9.2 ✅ | Tech Lead + counsel |
| Data map / record of processing | stores, fields, purpose, retention, cross-border flag, sub-processor per store | Art. 21 (basis of the dossier) | Tech Lead |
| Processing impact dossier | Evidgate's own processing; plus a template the customer can use | Art. 21.1/21.3 ✅ | Tech Lead + counsel |
| Transfer impact dossier | per provider/store outside Vietnam, filed within 60 days of first transfer | Art. 20.1/20.2 ✅ | Owner (O3/O4) + counsel |
| Sub-processor list | versioned, with a notification and objection process | Art. 37 + contract | Tech Lead |
| Breach runbook | detection, triage, processor→controller prompt notice, 72 h regulator notice support, evidence pack | Art. 23.1 ✅ | Tech Lead |
| Deletion SLA + report | windows per data class, verification method | Art. 37.2 + contract | Tech Lead |
| Fines exposure note | cross-border violation up to 5 % of preceding-year revenue; other violations up to VND 3 billion; buying/selling up to 10× proceeds | Art. 8.3/8.4/8.5 ✅ | Owner |

### 4.4 Sandbox requirements (V1 replay sandbox for shadow healing, 11.2.1-T6; V2 agents; V3 for hosted)

Disposable environment per job · default-deny egress with an explicit allowlist (engine CDN, package registry, customer staging host, LLM gateway) · least-privilege identity, no ambient tenant credentials, no long-lived cloud role · no host mounts, no docker socket, no access to the control-plane network · CPU/memory/disk/time caps with a hard kill · artifact collection through the normal upload path only · teardown verified and logged · escape attempts logged as security events. For V3 hosted multi-tenant execution the boundary must be a VM/microVM or a verified equivalent `[OPEN]`.

The healing validator (`packages/healing-validator`, Node 26 + TypeScript 6.0.x, 08-owned) runs V1 shadow replay validation as a **child process of `Evidgate.Worker`** over JSON stdin/stdout — never inside the API process — with CPU, memory and wall-time limits enforced by the worker and a pinned Node runtime carried in the worker image (T36); the worker's own environment follows the disposable/allowlisted rules above, and the V1 replay host is customer-side (DECISIONS O10 option (a)).

### 4.5 Data flows that trigger Art. 20

1. Artifacts stored in object storage outside Vietnam → transfer. 2. Healing/AI context sent to a provider abroad → transfer. 3. Backups replicated outside Vietnam → transfer. Each is enabled only after the tenant's data owner approves and the transfer dossier is filed or scheduled.

## 5. Failure modes and edge cases

| Case | Handling | Invariant kept |
|---|---|---|
| Secret accidentally logged | Scanner alert → log purge → rotate → incident record; affected customer notified if tenant data was exposed | No secret in logs (NFR-11-2) |
| Fork PR tries to exfiltrate via a workflow | No Evidgate credential reachable from that context; attempt logged | FR-11-3 |
| Malicious artifact (HTML/JS, XXE, zip-slip) | Parsers hardened; render on isolated origin with CSP; rejected artifacts stay `rejected` and are not evidence | 00 §8.4 |
| Prompt injection in DOM/log | Injection is data; validator is the authority; attempt logged and visible; kill switch available | FR-11-7, DECISIONS §5 |
| Webhook replay or timestamp skew | Reject/ignore once, log, alert on bursts | FR-11-9 |
| Compromised dependency or action | Pinned SHAs + lockfile + SBOM + advisory monitoring + triage SLA; emergency rotation path | FR-11-10 |
| Support access misuse | Time-boxed grant, tenant banner, audit, quarterly review; break-glass requires a written reason | Fr-11-8/D7 |
| Cross-tenant probe | 404 for foreign ids (00 §9.2), rate limit, security event, alert on patterns | NFR-11-1 |
| Breach detected on a Friday night | Runbook with a named on-call path; processor notifies the controller promptly, not at hour 71 | Art. 23.1 |
| Customer asks for deletion while evidence is referenced by a decision | Decision rows keep hashes and references; objects are deleted per SLA and the decision shows the artifact as deleted, not missing-and-unknown | A3, FR-11-15 |
| New sub-processor needed mid-pilot | Notification → objection window → list version bump; tenant informed before any data flows | Sub-processor process |
| Provider outage for AI | Healing becomes `unavailable`; tests and gates unaffected | DECISIONS §5.9 |
| Residency requirement appears during the pilot | Deployment decision (O3/O4) or written scope exclusion; no silent transfer | FR-11-16 |

## 6. Security and privacy

This plan **is** the security plan; the product-facing rules it owns are: permission model (00 §2) is enforced server-side for every request; artifacts are personal-data-bearing by default and treated as sensitive; every support/operator access is scoped, time-boxed, visible and audited; all personal data is in customer-controlled projects and can be deleted on request; encryption in transit everywhere and at rest for the database, object storage and backups; presigned URLs are treated as bearer credentials with short TTL. Privacy: our own processing is limited to account, operational and audit data — the customer's test artifacts belong to the customer, and we act as processor.

## 7. Test strategy

| Suite | Covers |
|---|---|
| Tenant isolation red team | API cross-tenant reads/writes, FK violations, RLS with a non-owner role, views, search, presigned URLs, artifact ids from another tenant — over both server data paths: EF Core write-model commands and Dapper read-only queries on module views (a query missing the tenant setting must still return zero foreign rows) (NFR-11-1, F-11-4) |
| Untrusted-CI suite | Fork-PR credential reachability, token scope/TTL/replay, workflow recipe lint (SHA pinning, permissions, masking) |
| Secret-hygiene suite | Scanner on logs/artifacts/error envelopes; metric-label check; command-line visibility check |
| Prompt-injection corpus | DOM/log/document payloads asserting no policy/approval/permission/decision effect and correct security-event logging |
| Webhook suite | Signature mismatch, timestamp skew, replayed body, duplicate event id, retry stability |
| Parser hardening suite | XXE, zip-slip, decompression bomb, MIME mismatch, oversized input (with 06) |
| Deletion and residency | Deletion report correctness, post-deletion URL failure, backup window statement |
| Process | Tabletop breach exercise with a timed rehearsal of the notification path; ASVS checklist walkthrough; external pen test |

**Fixtures:** malicious artifact corpus (synthetic, no real customer data), a red-team tenant pair seeded with identical ids to catch "same id, wrong tenant" bugs, a fork-PR repository fixture.

## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Tenant isolation defect found by a customer | Medium | Severe (trust + PDPL) | Red-team suite in CI + external pen test before pilot; 404 semantics; RLS with `FORCE` and a non-owner role |
| Secret leak into logs/artifacts | Medium | High | Masking, scanning, rotation runbook, no CLI-arg secrets |
| Prompt injection causing a bad patch | Medium | High | Structured I/O, independent validator, negative control, human approval, kill switch; injection corpus |
| PDPL dossier filed late (60-day window) | Medium | High (Art. 8.4 ✅ revenue-based fine) | Transfer decision is an M3 gate item, tracked with a named owner (O3/O4) |
| Breach notification misses 72 h | Low | High | Runbook with prompt-notice rule, tabletop rehearsal, on-call path |
| ASVS L2 declared but not evidenced | Medium | Medium | Published gap list; claim only what is evidenced |
| Pen test finds criticals late (M3) | Medium | Medium | External test scheduled early in M3; remediation buffer in the estimate |
| Supply-chain compromise via an action or dependency | Low | Severe | SHA pinning, lockfile, SBOM, advisory monitoring, minimal permissions |
| Residency demand arrives after data has flowed | Medium | High | FR-11-16, per-store residency statement, no silent transfers |
| Compliance work expands into a certification project | Medium | Medium | Explicit out-of-scope list; owner sign-off needed to add ISO/SOC 2 |

## 9. Epics, stories and tasks

### Epic 11.1 - Threat model and security baseline (M1-M2, V1)

#### Story 11.1.1 A living threat model - acceptance: v1 covers every module and boundary, each threat maps to a control or a written acceptance, and it is re-reviewed at each milestone exit.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 11.1.1-T1 | Threat model v1: trust boundaries + STRIDE per module, versioned file, milestone review ritual | L | V1 | M1 | 02.1.1-T1, 02.6.1-T1 |
| 11.1.1-T2 | Security review checklist wired into the feature-plan Definition of Ready (authz, retention/deletion, telemetry, failure path) | S | V1 | M1 | - |
| 11.1.1-T3 | ASVS L2 decision: chapter mapping to plans, gap list, published level statement | M | V1 | M2 | 11.1.1-T1 |
| 11.1.1-T4 | Security event monitoring baseline: auth failures, cross-tenant 404 patterns, secret-scanner alerts, kill-switch use | S | V1 | M2 | 02.7.1-T1 |

Epic 11.1 total: 4.0

### Epic 11.2 - Untrusted CI, runners and sandbox (M1-M5)

#### Story 11.2.1 A fork pull request cannot reach our credentials - acceptance: the published recipe and the fork-PR test prove no Evidgate credential is reachable from an untrusted workflow context.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 11.2.1-T1 | Customer CI security guide: fork-PR rules, `pull_request_target` prohibition, SHA pinning, least-privilege `GITHUB_TOKEN`, masking, rotation ✅ (GitHub secure-use) | M | V1 | M1 | - |
| 11.2.1-T2 | Fork-PR test: demonstrate no credential reachability and that an attempted use fails authorisation + is logged | M | V1 | M2 | 02.4.1-T1 |
| 11.2.1-T3 | Ingest token security design review (with 02): scope, TTL, OIDC claim validation, revocation, replay — and the decided storage design: ≥128-bit random token, public prefix, `HMAC-SHA256(pepper_vN, secret)` + `pepper_version`, prefix lookup + `CryptographicOperations.FixedTimeEquals`; review covers pepper custody in the secret store and the versioned rotation runbook (10.7.1-T4) (T5) | M | V1 | M2 | 02.4.1-T1, 02.4.2-T1 |
| 11.2.1-T4 | Sandbox requirements spec + verification plan for V2 agent/healing (disposable, allowlist, least privilege, teardown proof) | L | V2 | M4 | 05.7.4-T1, 11.2.1-T6 |
| 11.2.1-T5 | Hosted multi-tenant isolation evaluation: VM/microVM boundary options and evidence | M | V3 | M5 | 11.2.1-T4 |
| 11.2.1-T6 | V1 replay-sandbox requirements for shadow healing (08.4.3): one-shot workspace, network allowlist, no tenant secrets, resource caps, teardown check; includes the `packages/healing-validator` child-process contract — JSON stdin/stdout, CPU/memory/wall-time limits enforced by the worker, pinned Node runtime in the worker image, never in the API process (T36); 11.2.1-T4 extends it to V2 agents | M | V1 | M3 | 08.4.3-T1 |
| 11.2.1-T7 | `evidgate_resolve` SECURITY DEFINER review (with 02/03): pinned `search_path`, return limited to `(org_id, project_id, binding_id, status)`, app role `EXECUTE`-only, every call audited; abuse cases checked (binding enumeration, timing oracle, revoked-but-resolvable bindings) (T1) | S | V1 | M2 | 02.6.1-T3, 03.4.1-T2 |

Epic 11.2 total: 7.5

### Epic 11.3 - Secrets and key management (M1-M3, V1)

#### Story 11.3.1 Secrets never leave a reference - acceptance: no secret value is stored in the database, logged, or visible in an error envelope; leak response is rehearsed.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 11.3.1-T1 | Secret-reference model and resolution path review (vault/CI, per-job scope, no value in DB/logs) | M | V1 | M2 | 02.4.2-T1, 06.2.3-T1 |
| 11.3.1-T2 | Leak runbook: mask-before-upload, log purge, rotation order per system, customer notice criteria | M | V1 | M2 | 11.3.1-T1 |
| 11.3.1-T3 | Automated secret scanning in our CI and over ingested artifacts (fail-closed), with an exception process | M | V1 | M3 | 04.1.1-T1, 06.2.3-T1 |
| 11.3.1-T4 | Encryption and key lifecycle review: at rest, in transit, presigned URLs, token rotation, backup keys | M | V1 | M3 | 10.1.1-T2, 10.5.1-T1, 10.7.1-T4 |
| 11.3.1-T5 | Redaction-at-source: documented patterns, tenant toggle, verification that redaction ran | L | V1 | M3 | 06.2.2-T1 |

Epic 11.3 total: 6.0

### Epic 11.4 - AI security and prompt injection (M4, V2)

#### Story 11.4.1 A model cannot change the product's mind - acceptance: the injection corpus produces zero policy/approval/permission/decision effects, and every outbound request is opt-in, redacted and audited.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 11.4.1-T1 | AI permission boundary spec + enforcement review (structured I/O, no shell/merge/policy, tenant-filtered retrieval) | M | V2 | M4 | 08.5.1-T1, 08.5.2-T1 |
| 11.4.1-T2 | Prompt-injection corpus and harness: DOM/log/document payloads with assertions on state and security logging; the injection-attempt events it produces join the security-event set from V2 (11.5.1-T5) | L | V2 | M4 | 08.5.2-T1 |
| 11.4.1-T3 | Outbound context control: per-tenant opt-in record, redaction step, provider allowlist, per-request audit | L | V2 | M4 | 08.5.3-T1, 11.3.1-T5 |
| 11.4.1-T4 | LLM provider due diligence: DPA, sub-processor entry, residency input for O4, retention terms | M | V2 | M4 | 11.8.1-T1 |

Epic 11.4 total: 6.0

### Epic 11.5 - Audit, support access and monitoring (M2-M3, V1)

#### Story 11.5.1 Who saw what, provably - acceptance: audit rows are append-only with before/after hashes, and any operator access is scoped, expiring, tenant-visible and audited.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 11.5.1-T1 | Audit event taxonomy + immutability approach (append-only, hash-linked) and retention | M | V1 | M2 | 02.7.1-T1 |
| 11.5.1-T2 | Support-access verification on top of 02.8.1-T1: tenant-visible banner, per-read audit assertions, access-review log and quarterly report | M | V1 | M3 | 02.8.1-T1, 09.1.1-T4 |
| 11.5.1-T3 | Security monitoring and alerting on the 11.1.1-T4 baseline, with an on-call routing decision | M | V1 | M3 | 11.1.1-T4 |
| 11.5.1-T4 | Break-glass procedure and quarterly operator-access review | S | V1 | M3 | 11.5.1-T2 |
| 11.5.1-T5 | `GET /v1/orgs/{o}/security-events` read endpoint + authorisation (`security_event:read`, `tenant_admin` only), filters by project/type/severity/time over the V1 event set: the 11.1.1-T4 baseline plus support-access rows from 02.8.1-T1 and 11.5.1-T2 (injection-attempt events join in V2 via 11.4.1-T2) | M | V1 | M3 | 11.1.1-T4 |

Epic 11.5 total: 4.5

### Epic 11.6 - Supply chain and dependency controls (M1-M3, V1)

#### Story 11.6.1 Every dependency is a decision - acceptance: licence allowlist and lockfile enforced in CI, actions pinned, SBOM produced per release.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 11.6.1-T1 | Dual-ecosystem lockfile enforcement: `package-lock.json` + `npm ci` for CLI/adapters/web; `packages.lock.json` + `dotnet restore --locked-mode` for server/worker; dependency review on PRs; licence allowlist automation for npm and NuGet (DECISIONS §4 rule; NuGet licence tool chosen + licence-checked in 01.9.1 ⚠️) (T40) | L | V1 | M1 | 01.1.1-T2, 01.7.1-T1 |
| 11.6.1-T2 | Pin all our GitHub Actions to full commit SHAs, minimal permissions, document the same for customers; dependency-update automation (e.g. Dependabot) covers npm and NuGet manifests (T40) | S | V1 | M1 | 11.2.1-T1 |
| 11.6.1-T3 | CycloneDX SBOM per release covering both ecosystems — NuGet and npm (tool licence-checked against DECISIONS §4 here ⚠️, T40) + provenance/attestation for published packages (CLI, adapters, SDK) | M | V1 | M3 | 01.7.1-T1 |
| 11.6.1-T4 | Vulnerability triage SLA, patch cadence, base-image refresh schedule | M | V1 | M3 | 10.2.1-T1, 10.2.1-T2 |

Epic 11.6 total: 4.5

### Epic 11.7 - Application security verification and pen test (M2-M4)

#### Story 11.7.1 Proved against isolation, not asserted - acceptance: the tenant-isolation red-team suite is green in CI, and an external pen test report exists with criticals/highs closed before pilot exit.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 11.7.1-T1 | Tenant isolation red-team suite: API, FK, RLS, views, search, presigned URLs, artifact ids — covering both server data paths: EF Core write-model commands and Dapper read-model queries over module views (a Dapper query missing the tenant setting must still return zero foreign rows) (F-11-4) | L | V1 | M2 | 02.9.1-T1, 06.3.3-T1 |
| 11.7.1-T2 | SAST/DAST baseline: CodeQL for C# and TypeScript, Roslyn security analyzers on the .NET solution, ESLint on `apps/web`/`packages/*`, plus a staging DAST scan, with triage loop (T40, F-11-4) | M | V1 | M3 | 01.7.1-T1 |
| 11.7.1-T3 | External pen test: scope, vendor selection, execution window | L | V1 | M3 | 10.1.1-T3, 10.2.2-T4 |
| 11.7.1-T4 | Pen-test remediation, retest of criticals, customer-facing summary | M | V1 | M3 | 11.7.1-T3 |
| 11.7.1-T5 | ASVS L2 walkthrough with evidence and a published gap list | M | V2 | M4 | 11.1.1-T3, 11.7.1-T4 |

Epic 11.7 total: 7.0

### Epic 11.8 - PDPL compliance program (M1-M3, V1)

#### Story 11.8.1 The paperwork exists before the data does - acceptance: DPA, privacy notice, data map, dossiers and sub-processor list are versioned, counsel-reviewed and available before the first pilot upload.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 11.8.1-T1 | DPA template (roles, sub-processors, breach cooperation, audit, deletion, cross-border) + counsel review | L | V1 | M1 | - |
| 11.8.1-T2 | Privacy notice for the platform + counsel review (Art. 9.2 elements ✅) | M | V1 | M1 | - |
| 11.8.1-T3 | Data map / record of processing across stores, fields, retention, cross-border flags | M | V1 | M2 | 06.4.1-T1, 10.8.1-T3 |
| 11.8.1-T4 | Processing impact dossier (Art. 21 ✅) for Evidgate + a customer-facing template | L | V1 | M3 | 11.8.1-T3 |
| 11.8.1-T5 | Cross-border transfer dossier within 60 days of first transfer (Art. 20.2 ✅), per store/provider; residency input for O3/O4 | L | V1 | M3 | 11.8.1-T3 |
| 11.8.1-T6 | Consent/redaction stance documented: Evidgate is processor; consent sits with the customer; redaction and no-LLM defaults | M | V1 | M2 | 11.8.1-T1 |

Epic 11.8 total: 9.0

### Epic 11.9 - Data protection operations (M3, V1)

#### Story 11.9.1 Deletion and breach are runnable procedures - acceptance: a deletion request produces a verifiable report, and a breach rehearsal completes the processor→controller notice inside the runbook's target.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 11.9.1-T1 | Deletion SLAs + verified deletion (tenant, project, artifact, log) with a customer-facing report | L | V1 | M3 | 06.4.4-T1, 06.4.5-T1 |
| 11.9.1-T2 | Breach runbook with the 72 h clock (Art. 23.1 ✅), prompt processor→controller notice, evidence pack, regulator-support steps | L | V1 | M3 | 11.5.1-T1 |
| 11.9.1-T3 | Tabletop exercise + timed rehearsal of the breach notification path | S | V1 | M3 | 11.9.1-T2 |
| 11.9.1-T4 | Sub-processor change process: notification, objection window, versioned list | S | V1 | M3 | 11.8.1-T1 |
| 11.9.1-T5 | Retention/deletion verification against plan 06 artifact states (`pending_delete → deleted` with proof) | M | V1 | M3 | 11.9.1-T1 |

Epic 11.9 total: 6.0

## 10. Dependencies

| Needs | From |
|---|---|
| Identity, membership, permission catalogue, RLS + tenant context, audit core, support access, isolation suite | 02.1.1-T1, 02.1.2-T1, 02.2.1-T1, 02.6.1-T1, 02.6.2-T1, 02.7.1-T1, 02.8.1-T1, 02.9.1-T1 |
| CI authentication (OIDC exchange and hashed ingest tokens) | 02.4.1-T1, 02.4.2-T1 |
| Atomic event ingest (quota, epochs) for artifact/log streams | 04.1.1-T1, 04.1.2-T1 |
| Agent executor and sandbox surfaces (lease/epoch, one-shot workspace) | 05.7.1-T1, 05.7.4-T1 |
| Artifact sensitivity, redaction at source, secret masking, parser hardening + hostile fixtures, isolated viewer, retention/deletion | 06.2.1-T1, 06.2.2-T1, 06.2.3-T1, 06.3.1-T1, 06.3.2-T1, 06.3.3-T1, 06.4.1-T1, 06.4.4-T1, 06.4.5-T1 |
| Healing sandbox, LLM gateway, injection containment, per-tenant opt-in, kill switch | 08.4.3-T1, 08.5.1-T1, 08.5.2-T1, 08.5.3-T1, 08.6.3-T1 |
| UI surfaces for the support-access banner and the audit/security-event views | 09.1.1-T4, 09.7.1-T6, 09.7.1-T9 |
| Environments and secrets, images/provenance, staging for the pen test, backups/PITR, key-rotation runbook, provider memo (residency) | 10.1.1-T2, 10.1.1-T3, 10.2.1-T1, 10.2.1-T2, 10.2.2-T4, 10.5.1-T1, 10.7.1-T4, 10.8.1-T3 |
| Provider/residency and cost decisions that shape the transfer dossiers | 12.4.1-T3 |

**Provides to:** 02 (threat model targets), 05/08 (sandbox and AI rules), 09 (which surfaces must honour support banners, untrusted content and sensitive gating), 10 (alert rules 10.3.1-T5, incident process 10.7.1-T6), 12 (DA/DPA availability is a pilot-agreement precondition).

## 11. Open questions

| # | Question | Owner | Needed by |
|---|---|---|---|
| 1 | ASVS target level ⚠️: confirm L2 or accept a documented subset | Tech Lead | M2 (11.1.1-T3) |
| 2 | Cloud provider/region and whether any store may leave Vietnam (O3) | Owner | M2 (first pilot data) |
| 3 | LLM provider(s) and whether tenant content may leave Vietnam (O4). Direction decided 2026-09-26: no third-party LLM provider, so healing adds no cross-border transfer; 11.4.1-T4 due diligence applies only if a hosted provider is ever reintroduced | Owner | V2 healing PR |
| 4 | Whether a container-only isolation boundary is acceptable for any V2 execution, or the agent must stay wholly customer-side `[OPEN]` | Tech Lead | M4 (11.2.1-T4) |
| 5 | Whether the pilot customers require an on-premise or single-tenant deployment | Owner | M3 |
| 6 | Budget and vendor for the external pen test | Owner | M3 start |
| 7 | Cross-plan story ids referenced in `Depends on` were written in parallel across twelve plans; the integrator must reconcile any mismatch found at the first full-program review — **Closed 2026-09-25:** script check over all 487 task rows finds 0 unresolved ids; whether each cited id means what the citation says is checked in each feature plan's Definition of Ready (00 §12) | Tech Lead | before M1 |

## 12. Estimate summary

| Epic | V1 | V2 | V3 | Total |
|---|---|---|---|---|
| 11.1 Threat model and security baseline | 4.0 | 0 | 0 | 4.0 |
| 11.2 Untrusted CI, runners, sandbox | 4.5 | 2.0 | 1.0 | 7.5 |
| 11.3 Secrets and key management | 6.0 | 0 | 0 | 6.0 |
| 11.4 AI security and prompt injection | 0 | 6.0 | 0 | 6.0 |
| 11.5 Audit, support access, monitoring | 4.5 | 0 | 0 | 4.5 |
| 11.6 Supply chain and dependency controls | 4.5 | 0 | 0 | 4.5 |
| 11.7 AppSec verification and pen test | 6.0 | 1.0 | 0 | 7.0 |
| 11.8 PDPL compliance program | 9.0 | 0 | 0 | 9.0 |
| 11.9 Data protection operations | 6.0 | 0 | 0 | 6.0 |
| **Total** | **44.5** | **9.0** | **1.0** | **54.5** |

→ with 20 % buffer **65.4** person-days (sizes XS 0.25 · S 0.5 · M 1 · L 2 · XL 3 person-days; epic totals are the sum of their task rows)

## 13. Contract change requests

> **Status:** 11-CR-1 partially applied 2026-09-24: 00 §11 names L2 as the target and points at 11.1.1-T3; the decided level is written there when 11.1.1-T3 (M2) closes open question 1. Applied 2026-09-24: 11-CR-2 → 00 §9.3 as `GET /v1/orgs/{o}/security-events`; 11-CR-3 → 00 §9.3 (support grants appear in `GET /v1/orgs/{o}/audit`). Applied 2026-09-25 by integrator: 11-CR-4 → 00 §14 (with 11.2.1-T6 added by the integrator: 44 tasks, 53.0 raw / 63.6 buffered).
>
> 2026-09-25 CR-D-1 + TL review: supply-chain controls extended to NuGet + npm — locked-mode restore, `dotnet list package --vulnerable`, NuGet licence scan ⚠️, CycloneDX SBOM ⚠️, CodeQL C#+TS (T40, F-11-1); CSRF/antiforgery + SameSite and Angular sanitizer rules on the browser boundary (T38, F-11-2); ingest-token review names HMAC-SHA256 + pepper custody/rotation (T5, F-11-3); producer-slot leaked-token residual risk recorded (T9); new task 11.2.1-T7 reviewing the `evidgate_resolve` SECURITY DEFINER functions (T1); validator child-process limits named (T36); red-team suite covers EF Core writes + Dapper reads and SAST gains Roslyn analyzers (F-11-4); `SET LOCAL` re-anchored to parameterised `set_config` via `ITenantScope` (§4 mapping).

| # | Request | Why | Status |
|---|---|---|---|
| 11-CR-1 | Update 00 §11 "Security" from "OWASP ASVS L2 ⚠️ level to confirm in 11" to the level decided in 11.1.1-T3, with the gap list referenced | The overview currently carries an undecided level; this plan owns the decision | applied 2026-09-24 |
| 11-CR-2 | Add a `security_event` read endpoint to the 00 §9.3 core table (`GET /v1/security-events`, filtered by tenant/project/severity, `tenant_admin` only) so the security-event view and support-access review have a contract | FR-11-7/11.5.1-T2 require visibility of injection attempts and support access; without a contract the UI would have to invent one | applied 2026-09-24 |
| 11-CR-3 | State in 00 §9.3 that `POST /v1/support-access` style operator grants (scope + expiry) are audited and returned in the tenant audit feed | Support access must be tenant-visible per FR-11-8 | applied 2026-09-24 |
| 11-CR-4 | 00 §14: this plan's row becomes `43 tasks, V1 42, V2 9, V3 1, raw 52.0, buffered 62.4` (new task 11.5.1-T5, M, V1, M3); 2026-09-25 integrator: plus 11.2.1-T6 (M, V1, M3) → `44 tasks, V1 43, V2 9, V3 1, raw 53.0, buffered 63.6`. Note that 11-CR-2's request text still shows the pre-apply path `GET /v1/security-events`; 00 §9.3 already carries the applied path `GET /v1/orgs/{o}/security-events`, so no further 00 edit is needed there | Keep the roll-up equal to the sum of plan §12 tables after every task addition | Applied 2026-09-25 by integrator |
| 11-CR-5 | 00 §14: this plan's row becomes `45 tasks, V1 44.5, V2 9.0, V3 1.0, raw 54.5, buffered 65.4` (new task 11.2.1-T7 S/V1/M2 for the T1 resolver review; 11.6.1-T1 resized M→L, +1.0, for dual-ecosystem lockfile work) | Keep the roll-up equal to the sum of plan §12 tables after every task addition | filed 2026-09-25 (CR-D-1 + TL review) |
