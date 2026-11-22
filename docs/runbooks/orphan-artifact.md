# Orphan artifact runbook

Status: **10.7.1-T3.1 written (document half only); the T3.1 staging drill is NOT done, so AC4 is open.**

**Drill pending.** The second-person staging drill in **10.7.1-T3.1** and AC1/AC4 (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md`) has not been performed. This desk check establishes no live provider lifecycle behaviour, database expiry contents or query plans, producer-credential availability, object presence or 404 cause, incident log availability, or staging execution result. Keep these checks pending until a non-author records the live evidence.

> **Not possible yet:** The actual retention/orphan-reconciler portion of AC4 requires the absent **06.4.2-T1.3** delete/verify job and **06.4.3-T1.1–T1.4** sweep. Keep **10.7.1-T3.1** drill open; a manual completion alone does not satisfy it.

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. This document was desk-checked against wave `fd69e03b`; citations name symbols, migrations, routes or plan tasks rather than unstable source line numbers. The [evidence table](#14-evidence-table) in §14 maps the claims to those sources; ✅ denotes repository evidence, not a staging drill.

## 1. Scope, audience and rules

**Purpose.** Recognise an artifact whose row and whose stored bytes have diverged, decide what it is, and act only through a path this build supports. 00 §8.4 defines the artifact state machine and the rule this runbook enforces — "an object that exists without `ready` is never treated as evidence" (`plans/00-overview.md`) — and 10.7.1 names the retention-sweep/orphan-reconcile trigger and the `pending_upload` TTL check as the operational surface to document (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md`).

**Audience.** A second on-call operator who did not write 06.1.2/06.2.3, holding a member session with `artifact:read` for triage and `project:write` only if a retention setting must move (`SetRetentionPolicy`). A member session can read here but cannot perform the one repair: a member's resolved tenant carries `ProjectId: null` (`TenantResolver`) and `complete` refuses an unprojected principal with 403 `project_required` (`ArtifactCompletionVerifier`), so §5 needs a project-bound `ci_producer` credential. Any session mutation — the retention `PUT` of §8, for example — additionally needs `X-XSRF-TOKEN` (`SessionXsrfMiddleware`; worked example at `docs/runbooks/kill-switch.md`).

**In scope.** The six states of `artifact.state` (`ArtifactState`, CHECK `migrations/0028_evidence_artifact.sql`): a row in `pending_upload`/`uploaded` that will never be completed, a `ready` row whose object cannot be proven present, a `scan_state = 'pending'` that no worker will decide (`migrations/0028_evidence_artifact.sql`), a `pending_delete` row that nothing deletes, and storage bytes with no row.

**Out of scope.** What a run's verdict should be (`docs/runbooks/stuck-run.md`), generic task triage and replay (`docs/runbooks/dead-letter.md`), breach handling for bytes that were served (`docs/runbooks/breach.md`), and the healing switch (`docs/runbooks/kill-switch.md`).

**Prerequisites.**

- A checkout of the release tag the incident is about; resolve the cited symbols and migration names in that tree.
- The org id, and the artifact id (`GET /v1/artifacts/{a}` — metadata read, `artifact:read`, `GetArtifactMetadata`). That route names no org, so a **member** request must send `X-Evidgate-Org` or it answers 422 `request_invalid` before any lookup (`TenantResolver`); a service token takes the org from its claim and needs no header.
- A database session operating as **`evidgate_app` for Q1–Q3 and Q5–Q7**. The raw-table Q4 instead needs a **separate `evidgate_worker` session**, authorised by `0104_platform_worker_role.sql`; its app INSERT-only grants supersede `0009_platform_outbox_dispatch.sql`. `evidgate_operator` reads zero artifact rows: `0028_evidence_artifact.sql` grants SELECT but supplies no operator policy on `public.artifact`. Do not use the worker role for artifact reads or silently substitute a more privileged role.
- Object-storage **metadata** access only if you must confirm one key by hand. Bucket/endpoint/region are in `EVIDGATE:Object` (`ObjectStorageOptions`). Do not GET artifact bytes or list the bucket: that bypasses the read-route sensitivity gate (`IssueArtifactDownloadUrl`). An authorised HEAD of the one exact derived key is the only incident-object storage call allowed. `scripts/env/bootstrap-bucket.sh <env> verify` is a separately approved **write**, not a metadata check: it PUTs a canary under `bootstrap-verify/`, reads it back, deletes it, and attempts cleanup on failure (`scripts/env/bootstrap-bucket.sh`; `infra/README.md` bootstrap verification).

**Rules.**

1. **Never UPDATE/DELETE an artifact row by hand.** `0028_evidence_artifact.sql` grants the app SELECT/INSERT and lifecycle-column UPDATE, not DELETE; `0066_evidence_masking_event.sql` adds sensitivity UPDATE and an invoker-rights scan guard applying even to owner writes. `0095_evidence_artifact_state_guard.sql` allows only pending_upload → uploaded/ready/rejected, uploaded → ready/rejected, ready → rejected/pending_delete, pending_delete → deleted, or same-state writes; other edges raise 23514. A hand write still bypasses application decisions and writes no application audit row.
2. **Never delete a stored object by hand.** No route deletes a final key, and the only shipped delete is the staging delete of one artifact (`ArtifactStagingDeleteTask`). The platform's own description of the failure mode says a failed commit leaves "the final object for the orphan sweeper (06.4.3)" (`plans/00-overview.md`) — and that sweeper does not exist (§2), so no supported way to delete such an object, or to record that it was deleted, is available.
3. **`complete` is the only repair that exists**, it is safe to call twice (a stored outcome answers unchanged — `ArtifactCompletionVerifier`; handler replay `CompleteArtifactUpload`), and it runs on a project-bound `ci_producer` credential, not on a member session (§5). The only other writers of a lifecycle state are background tasks (§3), not operator surfaces.
4. **Record what you did**: the artifact id, the `X-Request-Id` the response echoes (`RequestIdMiddleware`), and the audit filter you used in §11. A completion is audited; a stale row left alone is not.
5. **A row that is not `ready` is not evidence.** Never cite, screenshot or export its bytes as a result, whatever state you find it in — the download route itself refuses every non-`ready` row (`IssueArtifactDownloadUrl`).

## 2. What "orphan" means here, and what actually ships

00 §8.4 states the transitions and the one rule that governs every case here (`plans/00-overview.md`); 10.7.1 lists the retention-sweep/orphan-reconcile trigger as the runbook's subject (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md`). That trigger is **planned**: 06.4.3 designs the `sweep.scan`/`sweep.org` tasks (`plans/features/06-evidence-artifacts/06.4.3-orphan-sweeper.md`), and the code ships neither of them (table below). This runbook therefore separates the two vocabularies and never mixes them:

| Capability | Shipped? | Evidence |
|---|---|---|
| Artifact mint, presigned `PUT`, `complete` with HEAD + checksum + copy + re-HEAD | ✅ | `EvidenceEndpoints`; `ArtifactCompletionVerifier` |
| `artifact_secret_scan`, `artifact_staging_delete`, `artifact_parse`, `raw_report_parse` task types | ✅ | `EvidenceTaskTypes`; successful completion enqueues them through `CompleteArtifactUploadHandler` |
| One-day expiration configured for `pending/` staging bytes | ✅ repository rule; ⚠️ provider application/timing requires the drill | `infra/README.md` §Dev lifecycle stance; `bootstrap-bucket.sh` lifecycle verification |
| Retention policy stored per project | ✅ (setting only) | `migrations/0062_evidence_retention_policy.sql`; `RetentionEndpoints` |
| Any artifact retention/deletion job (`pending_delete` → `deleted`) | ❌ | `TasksServiceCollectionExtensions`, `IdentityModule`, `OrchestratorModule`, `UsageModule`, `HealingModule`, `EvidenceModule`: twelve composed schedules, none for artifact deletion |
| Orphan-object sweep (`sweep.scan`/`sweep.org`), bucket listing | ❌ | `IObjectStorage` has no list operation (`IObjectStorage`) |
| Stale-upload rejection (`upload_expired`) | ❌ | the literal appears nowhere under `src/` or `contracts/`; `complete` has no expiry branch (`ArtifactCompletionVerifier`) |
| `evidgate_sweep_orphans_repaired_total`, `evidgate_sweep_orphans_open`, `evidgate_retention_pending_delete_backlog` | ❌ | Planned only: metric emission in **06.4.2-T1.4** / **06.4.3-T1.4**, alert rules in **06.4.5-T1.2**, in their feature plans |

Two comments assume unshipped deletion work and are not proof that it exists: `ArtifactFailureCode.SecretRejected` describes a deletion job, and `0028_evidence_artifact.sql` describes a retention freeze read. `RunArtifactSecretScan` moves a secret-rejected row to `pending_delete`; no shipped handler takes it further.

> **Not possible yet:** Final-key deletion and its verification await **06.4.2-T1.3**; orphan scheduling, storage-side checks/listing and grace-protected deletion await **06.4.3-T1.1–T1.3**, with orphan audit rows and metrics in **06.4.3-T1.4**. Do not emulate these jobs by hand.

## 3. The shipped lifecycle, and its four holes

`pending_upload` is the only mint state (`ArtifactState`). `complete` is the only writer of `ready` and writes `rejected` for a never-verified upload (`ArtifactCompletionVerifier`, `CompleteArtifactUploadHandler`). `artifact_parse` can also reject an artifact that is already `ready` through `Artifact.MarkRejected` (`ParseArtifactTask`). Secret scanning is the only shipped writer of decided `clean`/`flagged` scan verdicts (`RunArtifactSecretScan`); completion initialises the scan to `pending` through `Artifact.MarkReady`. Secret scanning is also the only shipped caller moving `ready → pending_delete` (`Artifact.MarkPendingDelete`).

| Hole | Why it exists | What you see |
|---|---|---|
| `uploaded` is never written | no code path writes it, and `complete` accepts it (`ArtifactCompletionVerifier`) | a row that jumps `pending_upload → ready`, or stays `pending_upload` forever |
| `rejected` is terminal, and it is reached from two directions | a rejection is a committed 200 carrying the reason (`Artifact`), and the parse task writes the same state on a `ready` row (`ParseArtifactTask`); no `upload_expired` code exists | `pending_upload` rows past 24 h, `ready` rows rejected by a parse, and rejected rows kept forever — the only bound on staging bytes is the bucket lifecycle (`infra/README.md`) |
| `ready` is not terminal | the secret scan moves `ready → pending_delete` when the project rejects a secret (`RunArtifactSecretScan`) and the parse rejects a `ready` row whose bytes no longer parse (`ParseArtifactTask`) | a `ready` row that later became `pending_delete`/`rejected`; its object is not deleted by anything (below) |
| `expires_at` is never populated by a shipped application writer | the nullable column exists (`0028_evidence_artifact.sql`); `Artifact` accepts and preserves an expiry, but no shipped writer supplies one | application-created rows have no assigned expiry; actual database contents, including manual/history writes, remain drill-pending — do not claim every row is NULL without querying |

## 4. Detection (all statements read-only)

Set the guard first, then one tenant at a time — the tenant GUC is `evidgate.org_id` (`0002_foundation_evidgate_schema.sql`), and `public.artifact` is FORCE RLS with a policy per org (`0028_evidence_artifact.sql`). Follow [`dead-letter.md` §3.1](dead-letter.md#31-roles-rls-grants-and-the-read-only-guard). Q1–Q3 and Q5–Q7 use `evidgate_app`; Q4 explicitly switches to a separate worker-role session under `0104_platform_worker_role.sql`.

```sql
-- role: evidgate_app, triage session; then per org:
begin transaction read only;
select set_config('evidgate.org_id', '<org uuid>', true);
set local statement_timeout = '30s';
```

Run Q1–Q3 inside this app-role transaction, then end it with `commit;` or `rollback;` before Q4. Q1 has project and trailing-90-day bounds; Q2/Q3 have project and older-than thresholds but no lower date bound. Q7 reads `audit_log`, not `artifact`, and is scoped by tenant, artifact id and date rather than project. Q5/Q6 are keyed lookups. LIMIT bounds returned rows, not necessarily rows visited: actual plans and runtimes remain drill-pending.

Q1 — rows that are not `ready`, newest first. Every row it returns is one that 00 §8.4's rule forbids treating as evidence (`plans/00-overview.md`), and it is the artifact-side read an operator needs before any action:

```sql
select a.id, a.project_id, a.run_id, a.kind, a.state, a.scan_state,
       a.failure_code, a.created_at, a.updated_at, a.version
  from public.artifact a
 where a.project_id = '<project uuid>'
   and a.created_at >= now() - interval '90 days'
   and a.state <> 'ready'
 order by a.created_at desc
 limit 100;
```

`state` and `scan_state` are closed lists in `0028_evidence_artifact.sql`. That migration defines `artifact_project_idx (org_id, project_id)`, `artifact_run_idx (org_id, run_id) where state <> 'deleted'`, and `artifact_expiry_idx (expires_at) where state = 'ready'`; `0113_artifact_ready_slot_index.sql` adds another ready-only index. Do not infer an org-wide heap scan from the state filter, or promise a short read without a live EXPLAIN. Keep the timeout and project/date bounds; widen the date window only for an older incident.

Q2 — older rows counted by current non-ready state. `pending_delete` is included and was previously `ready` under the shipped secret-rejection path (`Artifact.MarkPendingDelete`, `RunArtifactSecretScan`). `rejected` may also follow `ready` via `ParseArtifactTask`. These counts do not establish that every row never became evidence:

```sql
select a.state, count(*) as rows, min(a.created_at) as oldest
  from public.artifact a
 where a.project_id = '<project uuid>'
   and a.created_at < now() - interval '24 hours'
   and a.state in ('pending_upload', 'uploaded', 'rejected', 'pending_delete')
 group by a.state;
```

24 h is a triage threshold based on the configured staging lifecycle, not a shipped artifact-expiry window (`infra/README.md`, lifecycle stance). Provider application/timing and the presence of any particular object remain drill-pending; age alone does not prove deletion.

Q3 — rows whose masking scan was never decided (`scan_state` default is `pending`, `migrations/0028_evidence_artifact.sql`):

```sql
select a.id, a.created_at, a.scan_state, a.sensitivity
  from public.artifact a
 where a.project_id = '<project uuid>'
   and a.state = 'ready' and a.scan_state = 'pending'
   and a.created_at < now() - interval '1 hour'
 order by a.created_at
 limit 100;
```

An hour is the triage threshold, not a shipped setting: a scan decided minutes ago is still normal, one still `pending` after an hour is the anomaly.

This is not cosmetic: `scan_state <> 'clean'` makes the effective sensitivity `sensitive` and gates the bytes (`ArtifactReadModel`).

Q4 — the background rows behind one artifact. **Finish the app transaction first. Open a separate session operating as `evidgate_worker` and repeat the read-only BEGIN/timeout guard above before this SELECT.** `0104_platform_worker_role.sql` grants worker SELECT on raw `public.outbox`/`public.task`; it revokes app SELECT on those tables. These raw tables have no RLS: retain the explicit org predicate. Completion's keys are `evidence:artifact:<id>:<type>` (`CompleteArtifactUploadHandler`); `outbox_org_idem_uq` is unique per org (`0006_platform_outbox.sql`). `OutboxDispatchSql` derives task key `'outbox:' || outbox.id`; the LEFT JOIN retains undispatched rows. Exact keys support indexed access, but the live plan remains drill-pending.

```sql
select o.id as outbox_id, o.task_type, o.created_at as enqueued_at, o.dispatched_at,
       t.id as task_id, t.queue, t.state, t.attempts, t.max_attempts, t.last_error, t.updated_at
  from public.outbox o
  left join public.task t on t.idempotency_key = 'outbox:' || o.id::text
 where o.org_id = '<org uuid>'
   and o.idempotency_key in (
         'evidence:artifact:<artifact uuid>:secret_scan',
         'evidence:artifact:<artifact uuid>:staging_delete',
         'evidence:artifact:<artifact uuid>:parse',
         'evidence:artifact:<artifact uuid>:raw_report_parse')
 order by o.created_at
 limit 10;
```

A newly successful verified completion writes scan + staging-delete rows; a `raw_report` with out-of-process parsing enabled also gets `parse`/`raw_report_parse` (`CompleteArtifactUploadHandler`). Rejected completions and stored-outcome replays enqueue none. App-role alternatives are `outbox_in_scope`, `task_in_scope`, and `outbox_task_in_scope`, granted by `0104_platform_worker_role.sql`; their reduced projections cannot reproduce every Q4 diagnostic column. App has no outbox DELETE. Task retention is different: `task.sweep` removes only `done`/`dead_letter` rows whose `created_at` is over 30 days old (`TaskSweepTaskHandler`, `EvidgateSweeps`), never queued/retry/active rows. End the worker transaction with COMMIT/ROLLBACK, then reconnect as `evidgate_app` and repeat the guard for Q5–Q7.

Read a `dead_letter` task id with [`dead-letter.md` §3.2 Q5](dead-letter.md#32-the-statements), respecting that runbook's worker-role raw-table access rule.

Q5 — the retention setting that would govern the row, if a deletion job existed:

```sql
select r.artifact_days, r.metadata_days, r.version, r.updated_at
  from public.retention_policy r
 where r.org_id = '<org uuid>'
   and r.project_id = '<project uuid>';
```

No row means the platform defaults are served, not stored (`migrations/0062_evidence_retention_policy.sql`): artifact 14 days, metadata 90 by default, both retunable by `EVIDGATE__RETENTION__*` (`RetentionLimits`).

Q6 — the stored `reject_on_secret` switch (`0028_evidence_artifact.sql`, `artifact_kind_policy`). Use this SQL for the switch. `GET /v1/projects/{p}/artifact-kind-policy` exists and requires `project:read`, but the `ArtifactKindPolicyEndpoints` GET response exposes capture switches and redaction rules, not `reject_on_secret`, `default_sensitivity` or `max_bytes`; it cannot replace this query.

```sql
select kp.kind, kp.capture_enabled, kp.reject_on_secret, kp.default_sensitivity, kp.max_bytes
  from public.artifact_kind_policy kp
 where kp.org_id = '<org uuid>' and kp.project_id = '<project uuid>'
 order by kp.kind;
```

Q7 — the audit rows that exist for this row (§11):

```sql
select al.id, al.created_at, al.action, al.actor_type, al.object_type, al.object_id
  from public.audit_log al
 where al.object_id = '<artifact uuid>'
   and al.object_type = 'artifact'
   and al.created_at >= now() - interval '90 days'
 order by al.created_at desc
 limit 200;
```

`audit_log_org_created_id_idx` is `(org_id, created_at desc, id desc)` (`0015_identity_audit_log.sql`). The date predicate can support selective access but does not prove a particular live plan. Widen only for an older incident and retain LIMIT. `action` is also filterable through `GET /v1/orgs/{o}/audit?action=…` with `audit:read` (`ListAudit`, `AuditEndpoints`).

## 5. Scenario A — a `pending_upload` row that will never be completed

**Detect.** Q1/Q2 with `state = 'pending_upload'` and `created_at` older than 24 h. This exceeds the configured one-day staging-expiration threshold (`infra/README.md`, lifecycle stance), but whether the provider applied the rule or collected this object's bytes remains drill-pending.

**Diagnose.** Read the row (`GET /v1/artifacts/{a}`) and Q4. A `secret_scan` outbox row proves a verified completion made the artifact ready at least once. Its absence alone does not distinguish an uncompleted artifact from a committed rejection, which enqueues no scan; inspect current state, completion failure code and `artifact.complete` audit together (`CompleteArtifactUploadHandler`). If the row is still `pending_upload`, it has no committed completion outcome under the shipped state transitions (`0095_evidence_artifact_state_guard.sql`). Nothing writes an expired marker, and `ArtifactCompletionVerifier` has no expiry branch. Neither completion class declares a dedicated log event, but that does not establish that incident logs are useless: general request failures may be logged by `EnvelopeExceptionMiddleware` (events 1201/1202). Available/retained incident logs remain drill-pending.

**Act.** A member session cannot perform this repair: `TenantResolver` gives it no project, and `ArtifactCompletionVerifier` returns 403 `project_required` before storage reads. The supported credential is a project-bound `ci_producer` token carrying `artifact:write` (`contracts/permissions.json`, `CiProducerTokens`). For a CI-bound artifact run, its GitHub run identity must match the original binding; an ingest-token producer has no such binding. **Drill pending:** establish that an unexpired credential for the original binding can actually be issued; a token from a different run/attempt is not a workaround.

**Confirm with the producer before completing.** `complete` promotes the staged object to the final key after re-verifying size and SHA-256, and the outcome is `ready` — still `scan_state = 'pending'`, so the bytes stay sensitive-gated for callers without `artifact:read_sensitive` until a scan decides (`ArtifactCompletionVerifier`; `ArtifactReadModel`). Completing an upload the producer is still writing, or intends to retry, commits a verdict nobody asked for.

The repair is therefore the producer re-running `complete` once with a fresh `Idempotency-Key` on its own credential:

```console
POST $API/v1/artifacts/$ARTIFACT/complete
Authorization: Bearer <ci_producer token for the artifact's project>
Idempotency-Key: <new key>
```

Rules that decide the outcome (`ArtifactCompletionVerifier`): only `pending_upload`/`uploaded` are completable; the staging key is derived; a missing staging object falls through to final-key verification. `object_missing` is a committed 200 rejection when final verification cannot find its object — either on that fallback or after a copy — and therefore does not prove staging was absent. Stored ready/rejected outcomes replay unchanged. A mismatched CI run binding gets the same 404 as an unknown id.

**Verify.** Read `failure_code` from `CompleteResponse` or Q1; `ArtifactMetadataView` does not include it (`EvidenceEndpoints`). `ready` means the final object was found and verified at completion time, not that it remains present now. `rejected` + `object_missing` means final-object verification failed, not necessarily that both keys were absent (`ArtifactCompletionVerifier`). A newly committed completion gains an `artifact.complete` audit row (§11); replaying its same idempotency key must return the identical body (`CompleteArtifactUploadHandler`).

**Rollback.** None exists and none is needed: `rejected` is terminal (`Artifact`), and `ready` is not reversible either — the operator surface has no transition back to `pending_upload`, and the row can only move on (`ready → pending_delete` from a secret rejection, `ready → rejected` from a parse verdict, §3). Record it, do not try to reverse it; a producer that needs a new attempt mints a new artifact.

## 6. Scenario B — a `ready` row whose object you cannot prove present

**Detect.** A consumer reports storage 404 for a ready row's presigned GET. `ArtifactCompletionVerifier` replays stored ready outcomes without storage reads, and `IssueArtifactDownloadUrl` presigns without HEAD. Parse and scan handlers throw on absent reads, but retry/dead-letter alone does not establish object loss: child-spawn failures and lost scan write races also retry. Q4's `last_error` carries the exception type and first message line, masked and capped at 1000 characters (`TaskErrorText`, `0008_platform_task_queue.sql`). Missing-object indicators include “the artifact's stored object is not readable” (`ParseArtifactTask`), “the raw report's stored object is not readable” (`ParseRawReportTask`), “the reprocessed report's stored object is not readable” (`ReprocessReportTask`), and “the stored object of artifact … is absent” (`RunArtifactSecretScan`). They establish absence at that attempt, not current presence.

**Diagnose.** Q4 can show whether staging deletion ran; that handler deletes only `pending/<org>/<artifact>`, never the final key (`ArtifactStagingDeleteTask`). Storage-side loss or bucket/endpoint misconfiguration are [INFERENCE] hypotheses, not a conclusion from ready + 404. Confirm the intended configuration and exact key with an authorised metadata-only HEAD (§1); actual object presence and the 404 cause remain drill-pending.

**Act.** Do not re-complete to refresh an already-ready artifact: stored-outcome replay performs no storage reconciliation (`ArtifactCompletionVerifier`). Do not delete/rewrite its row. Hand off artifact id, exact key, timestamp, request id and intended storage configuration. `scripts/env/bootstrap-bucket.sh <env> verify` is a separately approved **write**, not metadata-only inspection: it PUTs, reads and deletes a canary (`bootstrap-bucket.sh`, `infra/README.md` bootstrap verification). Never GET/list artifact bytes or prefixes to bypass the sensitivity gate.

> **Not possible yet:** Reconcile an already-ready row against storage through the planned **06.4.3-T1.3** storage-side pass (ready-row `HEAD` check). Re-completion does not implement that repair.

**Verify / rollback.** This runbook changed no artifact row and no object. If the bootstrap `verify` canary ran, say so in the handoff: it wrote and deleted one object under `bootstrap-verify/`, which is why "nothing was changed" may not be claimed for that step. The alert that would have caught the loss earlier is planned, not emitted: `[OPEN]` **06.4.5-T1.2** (`evidgate_sweep_orphans_open`, `plans/features/06-evidence-artifacts/06.4.5-deletion-verification.md`).

## 7. Scenario C — a `scan_state = 'pending'` that no worker will decide

**Detect.** Use Q3 and Q4's `secret_scan` row. A dead-letter row shows failed attempts, not their root cause; inspect `last_error`. Persistent queued work warrants checking handler registration, the actual routed queue, due time and worker polling/health (`TasksOptions`, `TaskQueueSql`). `dispatched_at is null` means this row has not been dispatched, not that the dispatcher never ran: bounded batches and SKIP LOCKED can leave rows behind (`OutboxDispatchSql`).

**Diagnose.** `RunArtifactSecretScan` logs skips at event **2450** with `absent`, `already_scanned`, `not_ready`, or the write-side race reason `left_scan_pending`; it logs a committed decision at **2451**. A decided replay writes nothing. Task type is `artifact_secret_scan`; `TasksOptions.QueueRoutes` can override its queue, otherwise `DispatchDefaultQueue` applies (default value `default`). Inspect the actual Q4 task queue rather than assuming its routing.

**Act.** Keep the row and escalate with task id and `last_error`; a dead-lettered scan has no usable operator replay today. The pending scan keeps bytes sensitive-gated for callers without `artifact:read_sensitive` (`ArtifactReadModel`). A later scan sets `clean` when it finds no secret hits. It moves ready to `pending_delete` with `secret_rejected` **only when both** `reject_on_secret` is true and at least one secret hit exists (`RunArtifactSecretScan`, `Artifact.MarkPendingDelete`).

> **Not possible yet:** Operator HTTP replay is blocked by `DenyAllOperatorGate`. The access path is **02.8.1-T2.1**, with operator authentication still open in `plans/features/02-identity-tenancy/02.8.1-time-boxed-operator-access.md`; a replay route existing is not usable authorisation.

**Verify.** A completed scan decides `clean` or `flagged`; `0066_evidence_masking_event.sql` makes that verdict immutable and prohibits lowering sensitivity. Do not attempt a second decision or reset.

**Audit.** No audit row exists for a scan. Masking findings are events, not audit: `public.masking_event`, one row per artifact and class, `action` `flagged`/`rejected` (`migrations/0066_evidence_masking_event.sql`).

## 8. Scenario D — a `pending_delete` row that nothing deletes

**Detect.** Q1/Q2 with `state = 'pending_delete'`. Its only shipped transition caller is a secret scan with findings and `reject_on_secret = true`, which sets `secret_rejected` (`RunArtifactSecretScan`, `Artifact.MarkPendingDelete`). Retention is only an intended future caller. No shipped handler deletes its final key; actual byte presence nevertheless remains drill-pending and requires an authorised exact-key HEAD, not an assumption.

**Diagnose.** No final-object deletion task exists. Completion enqueues the four types in §2; Evidence also registers `ingest.reprocess_report` (`ReprocessReportTask`, `ParseRunnerServiceCollectionExtensions`), which reparses bytes rather than deleting them. `GET /v1/artifacts/{a}/download-url` returns 409 for pending-delete/deleted regardless of sensitive-read permission (`IssueArtifactDownloadUrl`). Metadata GET still returns 200 with `content_gated: true` and content-bearing fields withheld (`GetArtifactMetadata`, `ArtifactReadModel`).

**Act.** Do not hand-delete bytes or hand-update rows: those are unsupported and unaudited; `evidgate_app` has no artifact DELETE grant (`0028_evidence_artifact.sql`). Escalate exact Q2 counts to **06.4.2-T1.3**. `PUT /v1/projects/{p}/retention` requires `project:write` and changes a stored setting only (`SetRetentionPolicy`). No shipped writer populates `expires_at`; do not infer that every live row is NULL without querying it (drill pending).

> **Not possible yet:** The final-object delete/HEAD-verify transition awaits **06.4.2-T1.3**; changing the retention setting cannot execute it.

**Verify / rollback.** Nothing changed. Record the row ids; they are the input the 06.4.x job will need.

## 9. Scenario E — "the retention sweep failed"

**Detect.** No artifact retention sweep is registered. Common Tasks composition has four schedules: idempotency purge, authoring-change purge, task sweep and audit-chain verification (`TasksServiceCollectionExtensions`, `EvidgateSweeps`, `AuthoringChangePurge`, `VerifyAuditChainTask`). `WorkerHost` also composes eight module schedules: Identity's CI token-issue purge, session cleanup, GitHub installation-state purge and invite expiry (`IdentityModule`); Orchestrator's collection-deadline reconcile (`OrchestratorModule`); Usage's daily and period-close roll-ups (`UsageModule`); Healing's LLM-budget reset (`HealingModule`). None of these twelve is artifact retention or object reconciliation. `EvidenceModule` has task handlers but no schedule. `task.sweep` deletes only done/dead-letter task rows older than 30 days by created_at (`TaskSweepTaskHandler`, `EvidgateSweeps`).

**Diagnose.** If a project's `artifact_days` is set (Q5) and nobody sees deletion, the correct answer is "not implemented", not "misconfigured". A zero-row Q1 with old `ready` rows is exactly that state.

**Act.** Record it as `[OPEN]` **06.4.2-T1** and **06.4.3-T1**; do not emulate the job with ad-hoc `DELETE`s. Evidence must not be destroyed by hand: the audit chain and the run's verdict reference the row, and the app role's grants exist to make this impossible (`migrations/0028_evidence_artifact.sql`).

> **Not possible yet:** Schedule and execute artifact retention under **06.4.2-T1** or orphan reconciliation under **06.4.3-T1**. No existing maintenance schedule is a substitute.

**Verify.** None; nothing ran.

## 10. Scenario F — objects with no row (the unobservable direction)

**Detect.** Not possible with shipped tooling. `IObjectStorage` has single-key presign, HEAD, ranged/full GET, copy and delete operations, but no listing. **06.4.3-T1.3** plans the storage-side prefix listing under `pending/<org>/` and `org/<org>/`, with pagination, and **06.4.3-T1.1** the schedule that triggers it (`plans/features/06-evidence-artifacts/06.4.3-orphan-sweeper.md`).

**Diagnose.** Two leftovers are known by design: (1) staging bytes from an uncompleted/rejected upload, covered by the configured one-day lifecycle but with actual provider collection still drill-pending (`infra/README.md`); (2) final bytes copied before a failed database commit (`ArtifactCompletionVerifier`). A subsequent completion can verify/adopt those final bytes; no later completion is guaranteed. Do not infer physical existence or absence from age or row state alone.

**Act.** No supported cleanup action is available. Keep the object/key context in the handoff. Never clean up a prefix by hand: a wrong prefix destroys evidence and no shipped orphan-delete audit exists.

> **Not possible yet:** Discover unknown objects and delete them under grace through **06.4.3-T1.3**; audit rows, metrics and concurrency guards arrive with **06.4.3-T1.4**. The current storage seam cannot discover such objects.

**Verify / rollback.** None; nothing was done.

## 11. Audit, and what is not recorded

| Action you take | Audit row | Evidence |
|---|---|---|
| `POST /v1/artifacts/{a}/complete` (new committed ready/rejected outcome) | `artifact.complete`, object `artifact`/`<id>` | `CompleteArtifactUploadHandler` audited action; `docs/security/audit-taxonomy.md` |
| Mint / upload-url | `artifact.mint` | `CreateArtifactRecord`; `RequestArtifactUploadUrl`; `docs/security/audit-taxonomy.md` |
| `PUT /v1/projects/{p}/retention` | `project.retention.set` | `SetRetentionPolicy`; absent from `docs/security/audit-taxonomy.md` `[OPEN]` |
| Refused sensitive download | `artifact.download_denied`, metadata `outcome=sensitive_forbidden`, `run_id` | `IssueArtifactDownloadUrl`; absent from the taxonomy `[OPEN]` |
| Staging delete task | none — task row is the record | `ArtifactStagingDeleteTask` |
| Secret scan decision | none — masking events instead (§7) | `RunArtifactSecretScan`; `0066_evidence_masking_event.sql` |
| Planned orphan repair by a sweep | **Not possible yet:** no shipped sweep/audit | **06.4.3-T1.4**, planned `artifact.swept_orphan` (`plans/features/06-evidence-artifacts/06.4.3-orphan-sweeper.md`) |

Read the trail either through the API (`GET /v1/orgs/{o}/audit?action=artifact.complete`, `audit:read`) or Q7. The audit row is written by the pipeline for the committed command — `TenantTransactionBehaviour` commits the command's audit row with the tenant transaction and rolls both back on failure ("the pipeline writes no audit entry for a failing command and rolls the tenant transaction back", `IssueArtifactDownloadUrl` doc comment) — so a `complete` that returns 4xx writes none.

## 12. Known gaps (each with its receiver)

| Gap | Consequence for this runbook | Receiver |
|---|---|---|
| No orphan sweep, no bucket listing, no audited orphan-object delete | scenario F is unobservable through shipped tooling; scenario B has no supported reconciler (an authorised exact-key HEAD can still establish presence/absence) | **06.4.3-T1.1–T1.4** (storage-side listing/delete `T1.3`, audit/metrics `T1.4`) |
| No stale-upload rejection, no `upload_expired` code, no upload-window setting | `pending_upload` rows are only ever closed by a human calling `complete` (§5) | **06.4.3-T1.2** |
| No artifact retention/deletion job; no shipped writer populates expires_at | pending_delete has no shipped transition to deleted; actual database expiry contents remain drill-pending | **06.4.2-T1** |
| Operator routes deny real callers (`DenyAllOperatorGate`) | **Not possible yet:** HTTP replay of dead-lettered scan/staging-delete work | **02.8.1-T2.1** operator access path, with the open operator-authentication dependency documented in `plans/features/02-identity-tenancy/02.8.1-time-boxed-operator-access.md` |
| No orphan/backlog/current-object-presence gauge. Existing artifact-related counters include `evidgate_raw_report_enqueued_total{issue}`, `evidgate_ingest_cap_exceeded_total{cap}`, and **`evidgate_artifact_failure_total{stage,reason}`**. The last counts committed upload/artifact-parse rejections, including upload `object_missing`; it does not count uncompleted uploads, pending-delete backlog or later loss of a ready object's bytes. Queue signals are `task_dead_letter_total`, `task_deferred_total`, `task_dispatch_lag_seconds`, `outbox_undispatched` | some failures are counted, but no live reconciliation/backlog signal exists | planned metrics **06.4.2-T1.4** / **06.4.3-T1.4**, alert rules **06.4.5-T1.2**; current `EvidgateTelemetry`, `infra/metrics/catalogue.yaml`, `TaskQueueMetrics` |
| No deployed artifact health-alert configuration; `docs/security/alert-rules.md` specifies `AR-secret-scanner-flagged` but explicitly does not deploy rules | that design is not a stale-upload/missing-object detector; no reconciler emits the planned signals | **06.4.5-T1.2**, `plans/features/06-evidence-artifacts/06.4.5-deletion-verification.md` |
| Audit taxonomy lacks `artifact.download_denied` and `project.retention.set` | §11 cites code, not the taxonomy | taxonomy owner, `[OPEN]` |
| Staging drill not evidenced; live lifecycle, data/plans, credentials, storage diagnosis and log availability unverified | drill-pending note above applies; do not claim AC4 or live execution | **10.7.1-T3.1 drill half**, after required **06.4.2/06.4.3** capabilities |

> **Not possible yet:** Page on reconciliation/backlog signals only once the metrics land with **06.4.2-T1.4** / **06.4.3-T1.4** and the alert rules with **06.4.5-T1.2**. The alert-design document does not deploy an alert, and no live signal/drill is claimed here.

## 13. Related runbooks

- [`dead-letter.md`](dead-letter.md) — classification, Q5 by task id, and the replay route that is denied today.
- [`stuck-run.md`](stuck-run.md) — the run-side half: what "stuck" means and when a run may be reconciled.
- [`telemetry.md`](telemetry.md) — resource attributes, sampling and the app-side redaction list, including what is not shipped.
- [`environments.md`](environments.md) — which host and bucket an operator is looking at.
- [`breach.md`](breach.md) — when bytes may have been served that should not have been.

## 14. Evidence table

| # | Claim | Cite |
|---|---|---|
| 1 | Six artifact states, CHECK, and pending_upload mint default | `0028_evidence_artifact.sql`; `ArtifactState` |
| 2 | scan_state closed list and pending default | `0028_evidence_artifact.sql` |
| 3 | Nullable expires_at exists; no shipped writer populates it; live contents remain drill-pending | `0028_evidence_artifact.sql`; `Artifact` copy/constructor |
| 4 | public.artifact FORCE RLS, org policy, no operator policy | `0028_evidence_artifact.sql` |
| 5 | App artifact SELECT/INSERT, column-scoped UPDATE plus sensitivity, no DELETE | `0028_evidence_artifact.sql`; `0066_evidence_masking_event.sql` |
| 6 | Tenant GUC helper reads evidgate.org_id | `0002_foundation_evidgate_schema.sql`, `evidgate.current_org_id` |
| 7 | Upload-url POST, complete POST, metadata GET, download-url GET | `EvidenceEndpoints`; `contracts/openapi.yaml` artifact routes |
| 8 | Complete requires artifact:write and is audited | `CompleteArtifactUploadHandler` attributes |
| 8a | Member tenant has no project; completion refuses project_required; CI producer carries project and artifact:write | `TenantResolver`; `ArtifactCompletionVerifier`; `CiProducerTokens`; `contracts/permissions.json` |
| 8b | Cookie-authenticated mutation needs X-XSRF-TOKEN | `SessionXsrfMiddleware` |
| 8c | Complete response has failure_code; metadata response does not | `EvidenceEndpoints.CompleteResponse`; `EvidenceEndpoints.ArtifactMetadataView` |
| 9 | Only pending_upload/uploaded are completable; no expiry branch | `ArtifactCompletionVerifier` |
| 10 | Stored outcome replay and project-deleting guard | `ArtifactCompletionVerifier` |
| 11 | Staging HEAD/checksum/copy/final HEAD; object_missing does not prove staging absent | `ArtifactCompletionVerifier` |
| 12 | Service-principal CI-run binding | `ArtifactCompletionVerifier`; `CiProducerTokens` |
| 13 | Successful verified completion enqueues scan/staging delete and conditional raw-report parses; rejected/replayed outcomes enqueue none | `CompleteArtifactUploadHandler` |
| 13a | Completion writes MarkReady or MarkRejected; MarkReady initialises scan pending | `CompleteArtifactUploadHandler`; `Artifact.MarkReady` |
| 13b | Parse can reject a ready row | `ParseArtifactTask`; `Artifact.MarkRejected` |
| 13c | Secret scan is the only shipped pending-delete caller; it requires ready | `RunArtifactSecretScan`; `Artifact.MarkPendingDelete` |
| 13d | Completion outbox keys; dispatcher task key outbox:<outbox id>; worker read grant, not app raw SELECT | `CompleteArtifactUploadHandler`; `OutboxDispatchSql`; `0006_platform_outbox.sql`; `0104_platform_worker_role.sql` |
| 14 | Four completion task types | `EvidenceTaskTypes` |
| 15 | Four common plus eight module schedules; none is artifact retention; task sweep retains terminal tasks 30 days by created_at | `TasksServiceCollectionExtensions`; `EvidgateSweeps`; `TaskSweepTaskHandler`; `IdentityModule`; `OrchestratorModule`; `UsageModule`; `HealingModule`; `WorkerHost` |
| 16 | Four scan skip reasons, no-op decided replay, events 2450/2451 and configurable queue routing | `RunArtifactSecretScan`; `TasksOptions.QueueRoutes`; `TasksOptions.DispatchDefaultQueue` |
| 17 | Decided scan cannot change; sensitivity cannot be lowered | `0066_evidence_masking_event.sql` |
| 18 | Masking events are unique per artifact/pattern and have flagged/rejected actions | `0066_evidence_masking_event.sql` |
| 19 | Nonclean scan implies sensitive content | `ArtifactReadModel` |
| 20 | Download returns 409 for pending-delete/deleted; metadata GET remains 200/content_gated | `IssueArtifactDownloadUrl`; `GetArtifactMetadata`; `contracts/openapi.yaml` artifact metadata/download routes |
| 21 | Retention row optional, keyed by org/project; app cannot DELETE | `0062_evidence_retention_policy.sql` |
| 22 | Retention route permissions, defaults and config section | `RetentionEndpoints`; `SetRetentionPolicy`; `RetentionLimits` |
| 23 | Stored kind policy has reject_on_secret; GET response does not expose it | `0028_evidence_artifact.sql`; `ArtifactKindPolicyEndpoints` |
| 24 | Object storage config section, endpoint/region/bucket | `ObjectStorageOptions` |
| 25 | Single-object storage operations, no listing | `IObjectStorage` |
| 26 | Configured one-day pending lifecycle; verify writes a canary; provider behaviour remains drill-pending | `infra/README.md` lifecycle/bootstrap sections; `scripts/env/bootstrap-bucket.sh` |
| 27 | Task columns, state list, key uniqueness and error-size limit | `0008_platform_task_queue.sql` |
| 28 | Dead-letter events 2306/2320, lease-expiry event 2350 | `TaskConsumer`; `LoggingTaskAlertSink`; `TaskClaimer` |
| 29 | Operator HTTP replay is fail-closed | `DenyAllOperatorGate`; `OperatorEndpoints`; receiver `02.8.1-T2.1` in the operator-access plan |
| 30 | Queue counters/gauges and their tags | `TaskQueueMetrics`; `TaskAlertNotifier`; `TaskConsumer`; `OutboxDispatcher`; `infra/metrics/catalogue.yaml` |
| 31 | Artifact failure counter exists; reconciliation gauges/deployed artifact health alerts do not; secret-scanner alert is only specified | `EvidgateTelemetry`; `infra/metrics/catalogue.yaml`; `docs/security/alert-rules.md`; planned metrics `06.4.2-T1.4`/`06.4.3-T1.4`, alert rules `06.4.5-T1.2` |
| 32 | Artifact state rule: objects without ready are not evidence | `plans/00-overview.md` §8.4; `10.7.1-T3.1` |
| 33 | Planned sweeper states, grace, tasks, metrics | `plans/features/06-evidence-artifacts/06.4.3-orphan-sweeper.md`, `06.4.3-T1.1–T1.4` |
| 34 | Orphan/backlog metrics and alerts remain planned: emission with **06.4.2-T1.4** / **06.4.3-T1.4**, alert rules with **06.4.5-T1.2** | `plans/features/06-evidence-artifacts/06.4.5-deletion-verification.md`, `06.4.5-T1.2` |
| 35 | Taxonomy documents artifact.mint/complete but omits retention.set/download_denied | `docs/security/audit-taxonomy.md`; `SetRetentionPolicy`; `IssueArtifactDownloadUrl` |
| 36 | Document half only; second-person live drill remains open | `plans/features/10-operations/10.7.1-operable-by-a-second-person.md`, `10.7.1-T3.1`, AC1/AC4 |

Desk check: 2026-11-21, wave fd69e03b; not checkable without a live environment: staging drill; provider lifecycle; live expiry data and query plans; original-bound producer credentials; object presence and 404 cause; available incident logs.
