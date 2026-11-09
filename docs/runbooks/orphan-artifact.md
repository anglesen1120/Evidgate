# Orphan artifact runbook

Status: **10.7.1-T3.1 written (document half only); the T3.1 staging drill is NOT done, so AC4 is open.**

The drill half of T3.1 — an artifact row made to diverge from its object on staging by a second person, who then repairs it with this document (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:112`) — has **not** been performed. `[OPEN]` (T3.1 drill half; AC4 `:25`, AC1 `:22`).

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Every table, column, state, route, task type and config key below is cited to this worktree's `develop` (`0196af73`), and the full mapping is the [evidence table](#14-evidence-table) in §14.

## 1. Scope, audience and rules

**Purpose.** Recognise an artifact whose row and whose stored bytes have diverged, decide what it is, and act only through a path this build supports. 00 §8.4 defines the artifact state machine and the rule this runbook enforces — "an object that exists without `ready` is never treated as evidence" (`plans/00-overview.md:273-275`) — and 10.7.1 names the retention-sweep/orphan-reconcile trigger and the `pending_upload` TTL check as the operational surface to document (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:49`).

**Audience.** A second on-call operator who did not write 06.1.2/06.2.3, holding a member session with `artifact:read` for triage and `project:write` only if a retention setting must move (`SetRetentionPolicy.cs:38`). A member session can read here but cannot perform the one repair: a member's resolved tenant carries `ProjectId: null` (`src/Evidgate.Api/Tenancy/TenantResolver.cs:68-70`) and `complete` refuses an unprojected principal with 403 `project_required` (`ArtifactCompletionVerifier.cs:150-156`), so §5 needs a project-bound `ci_producer` credential. Any session mutation — the retention `PUT` of §8, for example — additionally needs `X-XSRF-TOKEN` (`src/Evidgate.Api/Middleware/SessionXsrfMiddleware.cs:52-71`; worked example at `docs/runbooks/kill-switch.md:115-116`).

**In scope.** The six states of `artifact.state` (`src/Modules/Evidgate.Modules.Evidence/Domain/Artifact.cs:126-161`, CHECK `migrations/0028_evidence_artifact.sql:73`): a row in `pending_upload`/`uploaded` that will never be completed, a `ready` row whose object cannot be proven present, a `scan_state = 'pending'` that no worker will decide (`migrations/0028_evidence_artifact.sql:72`), a `pending_delete` row that nothing deletes, and storage bytes with no row.

**Out of scope.** What a run's verdict should be (`docs/runbooks/stuck-run.md`), generic task triage and replay (`docs/runbooks/dead-letter.md`), breach handling for bytes that were served (`docs/runbooks/breach.md`), and the healing switch (`docs/runbooks/kill-switch.md`).

**Prerequisites.**

- A checkout of the release tag the incident is about; every cite is a line in that tree.
- The org id, and the artifact id (`GET /v1/artifacts/{a}` — metadata read, `artifact:read`, `GetArtifactMetadata.cs:30`). That route names no org, so a **member** request must send `X-Evidgate-Org` or it answers 422 `request_invalid` before any lookup (`src/Evidgate.Api/Tenancy/TenantResolver.cs:57-62`); a service token takes the org from its claim and needs no header.
- A database session **as `evidgate_app`** for the read-only queries in §4. As `evidgate_operator` you read **zero** artifact rows: the operator holds a `SELECT` grant on `public.artifact` (`migrations/0028_evidence_artifact.sql:129`) but has no policy on the table, and the migration says so (`:112-115`). That posture is deliberate (`:31-32`).
- Object-storage **metadata** access only if you must confirm one key by hand; the bucket is `EVIDGATE:Object:Bucket` (`src/BuildingBlocks/ObjectStorage/ObjectStorageOptions.cs:20`, `:29`), and its endpoint/region are the same section (`:23`, `:26`). Do **not** `GET` an object and do **not** list the bucket: either returns bytes or keys that the read routes gate (`IssueArtifactDownloadUrl.cs:115-141`), and this runbook carries no gate. A `HEAD` of the one exact derived key is the only storage call allowed. `scripts/env/bootstrap-bucket.sh <env> verify` is a **write**, not a check: it signs a `PUT` canary under `bootstrap-verify/`, reads it back and deletes it (`scripts/env/bootstrap-bucket.sh:14-18`, `:215-243`, trap `:249-258`; `infra/README.md:94-103`).

**Rules.**

1. **Never `UPDATE`/`DELETE` an artifact row by hand.** The app role cannot delete one at all (`migrations/0028_evidence_artifact.sql:128`), and its `UPDATE` is column-scoped to the lifecycle columns plus `sensitivity` (`:127`; `migrations/0066_evidence_masking_event.sql:124`). A hand write does not skip the database guards — the invoker-rights scan guard fires on every artifact `UPDATE`, the owner's included (`0066_evidence_masking_event.sql:88-121`) — but the database has no artifact **state-transition** trigger (only `artifact_set_updated_at_and_version`, `0028_evidence_artifact.sql:98-101`, and that scan guard), so a hand write skips the application state machine (`Artifact.cs:126-161`) and writes no audit row.
2. **Never delete a stored object by hand.** No route deletes a final key, and the only shipped delete is the staging delete of one artifact (`ArtifactStagingDeleteTask.cs:53`). The platform's own description of the failure mode says a failed commit leaves "the final object for the orphan sweeper (06.4.3)" (`plans/00-overview.md:358`) — and that sweeper does not exist (§2), so no supported way to delete such an object, or to record that it was deleted, is available.
3. **`complete` is the only repair that exists**, it is safe to call twice (a stored outcome answers unchanged — `ArtifactCompletionVerifier.cs:215-223`; handler replay `CompleteArtifactUpload.cs:123-135`), and it runs on a project-bound `ci_producer` credential, not on a member session (§5). The only other writers of a lifecycle state are background tasks (§3), not operator surfaces.
4. **Record what you did**: the artifact id, the `X-Request-Id` the response echoes (`src/Evidgate.Api/Middleware/RequestIdMiddleware.cs:38-49`), and the audit filter you used in §11. A completion is audited; a stale row left alone is not.
5. **A row that is not `ready` is not evidence.** Never cite, screenshot or export its bytes as a result, whatever state you find it in — the download route itself refuses every non-`ready` row (`IssueArtifactDownloadUrl.cs:137-141`).

## 2. What "orphan" means here, and what actually ships

00 §8.4 states the transitions and the one rule that governs every case here (`plans/00-overview.md:273-275`); 10.7.1 lists the retention-sweep/orphan-reconcile trigger as the runbook's subject (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:49`). That trigger is **planned**: 06.4.3 designs the `sweep.scan`/`sweep.org` tasks (`plans/features/06-evidence-artifacts/06.4.3-orphan-sweeper.md:51-54`, `:68-71`), and the code ships neither of them (table below). This runbook therefore separates the two vocabularies and never mixes them:

| Capability | Shipped? | Evidence |
|---|---|---|
| Artifact mint, presigned `PUT`, `complete` with HEAD + checksum + copy + re-HEAD | ✅ | `EvidenceEndpoints.cs:42-46`; `ArtifactCompletionVerifier.cs:143-315` |
| `artifact_secret_scan`, `artifact_staging_delete`, `artifact_parse`, `raw_report_parse` task types | ✅ | `CompleteArtifactUpload.cs:29-49`, enqueued `:332-364` |
| `pending/` staging bytes removed after 24 h by the bucket lifecycle | ✅ | `infra/README.md:63-80`, rule verified `:94-96` |
| Retention policy stored per project | ✅ (setting only) | `migrations/0062_evidence_retention_policy.sql:43-60`; `RetentionEndpoints.cs:32-36` |
| Any artifact retention/deletion job (`pending_delete` → `deleted`) | ❌ | not composed: the common Tasks composition registers four schedules and no module registers one that touches `artifact` (`TasksServiceCollectionExtensions.cs:81-84`; module schedules `IdentityModule.cs:158,164,195`, `OrchestratorModule.cs:114`, `UsageModule.cs:61-62`) |
| Orphan-object sweep (`sweep.scan`/`sweep.org`), bucket listing | ❌ | `IObjectStorage` has no list operation (`src/BuildingBlocks/ObjectStorage/IObjectStorage.cs:18-129`) |
| Stale-upload rejection (`upload_expired`) | ❌ | the literal appears nowhere under `src/` or `contracts/`; `complete` has no expiry branch (`ArtifactCompletionVerifier.cs:231-234`) |
| `evidgate_sweep_orphans_repaired_total`, `evidgate_sweep_orphans_open`, `evidgate_retention_pending_delete_backlog` | ❌ | planned, never emitted: metric list `plans/features/06-evidence-artifacts/06.4.3-orphan-sweeper.md:70`; backlog name `06.4.5-deletion-verification.md:25`, `:31`; alert ACs `:26` |

Two shipped comments already assume the unshipped job and must not be read as evidence that it exists: `ArtifactSecretRejected` says "the deletion job removes its object" (`Artifact.cs:203-207`) and the freeze read describes "the retention job (06.4.2)" (`migrations/0028_evidence_artifact.sql:88-92`). The row is moved to `pending_delete` and nothing takes it further (`RunArtifactSecretScan.cs:232-235`).

## 3. The shipped lifecycle, and its four holes

`pending_upload` is the only mint state (`Artifact.cs:128-129`). `complete` is the only writer of `ready`, and the writer of the `rejected` a never-verified upload earns (`ArtifactCompletionVerifier.cs:215-223`; `CompleteArtifactUpload.cs:227-228`). A second writer moves an artifact that **is** `ready`: the `artifact_parse` task rejects it when its stored bytes stop being parseable, through the same `MarkRejected` transition (`ParseArtifactTask.cs:301`; `Artifact.cs:302-307`). The secret scan is the only writer of `scan_state` (`RunArtifactSecretScan.cs:230-235`) and the only shipped caller that moves `ready → pending_delete` (`:234`, `Artifact.cs:359-373`).

| Hole | Why it exists | What you see |
|---|---|---|
| `uploaded` is never written | no code path writes it, and `complete` accepts it (`ArtifactCompletionVerifier.cs:318-321`) | a row that jumps `pending_upload → ready`, or stays `pending_upload` forever |
| `rejected` is terminal, and it is reached from two directions | a rejection is a committed 200 carrying the reason (`Artifact.cs:310-315`), and the parse task writes the same state on a `ready` row (`ParseArtifactTask.cs:301`); no `upload_expired` code exists | `pending_upload` rows past 24 h, `ready` rows rejected by a parse, and rejected rows kept forever — the only bound on staging bytes is the bucket lifecycle (`infra/README.md:63-80`) |
| `ready` is not terminal | the secret scan moves `ready → pending_delete` when the project rejects a secret (`RunArtifactSecretScan.cs:232-235`) and the parse rejects a `ready` row whose bytes no longer parse (`ParseArtifactTask.cs:301`) | a `ready` row that later became `pending_delete`/`rejected`; its object is not deleted by anything (below) |
| `expires_at` is never set | the column exists (`migrations/0028_evidence_artifact.sql:61`) and only travels through the domain copy (`Artifact.cs:275`, `:294`); no writer assigns it | `expires_at is null` on every row, so a retention-by-expiry job would have no input |

## 4. Detection (all statements read-only)

Set the guard first, then one tenant at a time — the tenant GUC is `evidgate.org_id` (`migrations/0002_foundation_evidgate_schema.sql:30-33`), and `public.artifact` is `FORCE` RLS with a policy per org (`migrations/0028_evidence_artifact.sql:103-110`). The session recipe is [`dead-letter.md` §3.1](dead-letter.md#31-roles-rls-grants-and-the-read-only-guard) (`set session characteristics as transaction read only;` at `docs/runbooks/dead-letter.md:57-62`, tenant block `:66-76`).

```sql
-- role: evidgate_app, triage session; then per org:
begin transaction read only;
select set_config('evidgate.org_id', '<org uuid>', true);
set local statement_timeout = '30s';
```

Run the queries below inside this transaction and end it with `commit;` (a read-only transaction writes nothing) or `rollback;`. The artifact reads (Q1–Q3, Q7) are bounded by project and by time, Q4 by the four exact keys, and Q5/Q6 are keyed lookups: the table is `FORCE` RLS and no index serves a `state` predicate (Q1), so an unbounded artifact read walks the organization's heap.

Q1 — rows that are not `ready`, newest first. Every row it returns is one that 00 §8.4's rule forbids treating as evidence (`plans/00-overview.md:273-275`), and it is the artifact-side read an operator needs before any action:

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

`state` and `scan_state` are the closed lists at `migrations/0028_evidence_artifact.sql:72-73`. No index serves a `state` predicate: `artifact_run_idx` is `(org_id, run_id) where state <> 'deleted'` and `artifact_expiry_idx` is `(expires_at) where state = 'ready'` (`:91-92`), so `state <> 'ready'` is a heap filter inside the org — the project and date bounds are what keep the read short. Drop the lower bound only when the incident window is genuinely older.

Q2 — stale rows that never became evidence, counted by state. `pending_delete` is **not** in that group: today its only shipped writer is a secret-scan rejection of a `ready` row (`Artifact.cs:352-373`; `RunArtifactSecretScan.cs:232-235`), so a `pending_delete` row *was* evidence once:

```sql
select a.state, count(*) as rows, min(a.created_at) as oldest
  from public.artifact a
 where a.project_id = '<project uuid>'
   and a.created_at < now() - interval '24 hours'
   and a.state in ('pending_upload', 'uploaded', 'rejected', 'pending_delete')
 group by a.state;
```

24 h is not a shipped artifact window — it is the staging lifecycle that decides whether these rows still have an object at all (`infra/README.md:72-75`).

Q3 — rows whose masking scan was never decided (`scan_state` default is `pending`, `migrations/0028_evidence_artifact.sql:59`):

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

This is not cosmetic: `scan_state <> 'clean'` makes the effective sensitivity `sensitive` and gates the bytes (`src/Modules/Evidgate.Modules.Evidence/Application/Artifacts/ArtifactReadModel.cs:30-33`, `:47-51`).

Q4 — the background rows behind one artifact, whatever their state. The completion writes **outbox** rows keyed `evidence:artifact:<id>:<type>` on `public.outbox.idempotency_key` (`CompleteArtifactUpload.cs:336-361`, report-parse key `:390`; insert shape `src/BuildingBlocks/Persistence/Outbox/OutboxWriter.cs:22-27`), one per task type, and that key is unique per org (`outbox_org_idem_uq`, `migrations/0006_platform_outbox.sql:46-51`, `:94`), so the four exact keys are an index probe rather than a scan. The dispatcher derives the **task** key as `'outbox:' || outbox.id` (`src/BuildingBlocks/Persistence/Outbox/OutboxDispatchSql.cs:70-87`; `migrations/0006:50-51`; `migrations/0008_platform_task_queue.sql:34-38`; `docs/runbooks/dead-letter.md:105`), so a `left join` keeps an outbox row that was never dispatched — that row, with `dispatched_at is null` and no task, is the anomaly there:

```sql
select o.id as outbox_id, o.task_type, o.created_at as enqueued_at, o.dispatched_at,
       t.id as task_id, t.state, t.attempts, t.max_attempts, t.last_error, t.updated_at
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

`parse`/`raw_report_parse` exist only for a `raw_report` completed with out-of-process parsing on (`CompleteArtifactUpload.cs:352-364`), so two rows are the normal count for every other kind. `evidgate_app` holds `select` on `public.outbox` (`migrations/0009_platform_outbox_dispatch.sql:58`) but the table has **no** RLS, so the `org_id` predicate is what scopes it — never drop it. Outbox rows are never deleted by an application role (`docs/runbooks/dead-letter.md:114`). The ~30-day window applies to **task** rows only, and only once they are terminal: `task.sweep` deletes rows whose `state` is `done`/`dead_letter` **and** whose `created_at` is more than 30 days old — the predicate is `created_at`, not the terminal timestamp (`SweepTaskHandlers.cs:27-37`; `EvidgateSweeps.cs:119`) — so a `queued`/`retry`/`active` row is never swept, however old.

Read a `dead_letter` task id with [`dead-letter.md` §3.2 Q5](dead-letter.md#32-the-statements) (`docs/runbooks/dead-letter.md:134-143`).

Q5 — the retention setting that would govern the row, if a deletion job existed:

```sql
select r.artifact_days, r.metadata_days, r.version, r.updated_at
  from public.retention_policy r
 where r.org_id = '<org uuid>'
   and r.project_id = '<project uuid>';
```

No row means the platform defaults are served, not stored (`migrations/0062_evidence_retention_policy.sql:1-8`): artifact 14 days, metadata 90 by default, both retunable by `EVIDGATE__RETENTION__*` (`src/Modules/Evidgate.Modules.Evidence/Application/Retention/RetentionLimits.cs:20`, `:28`, `:36`, `:40`, `:44`).

Q6 — whether a capture policy would reject a secret-bearing artifact at all (the `reject_on_secret` switch, `migrations/0028_evidence_artifact.sql:140`). Prefer the read route (`GET /v1/projects/{p}/artifact-kind-policy`, `project:read`, `ArtifactKindPolicyEndpoints.cs:32`, `GetCaptureOptions.cs:23`); the SQL is:

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

The date bound keeps the read on `audit_log_org_created_id_idx` `(org_id, created_at desc, id desc)` (`migrations/0015_identity_audit_log.sql:52-54`); widen it only for an incident older than that, and keep a `limit` either way. `action` is filterable through the API as well (`GET /v1/orgs/{o}/audit?action=…`, `audit:read`, `ListAudit.cs:19-34`; route `AuditEndpoints.cs:42`).

## 5. Scenario A — a `pending_upload` row that will never be completed

**Detect.** Q1/Q2 with `state = 'pending_upload'` and `created_at` older than 24 h; the staging object has then most likely been collected by the bucket lifecycle (`infra/README.md:63-80`, `:94-96`).

**Diagnose.** Read the row (`GET /v1/artifacts/{a}`) and its background rows (Q4). A `secret_scan` outbox row means the row was already `ready` once and later moved; **no** such row means the producer never completed it. There is no "expired" marker to find: nothing writes one, and `complete` has no expiry branch (`ArtifactCompletionVerifier.cs:231-234`). Logs do not help here — the completion path emits no log line at all (`grep` finds no `LoggerMessage` in `CompleteArtifactUpload.cs` or `ArtifactCompletionVerifier.cs`).

**Act.** A member session cannot perform this repair: a member's tenant carries no project (`src/Evidgate.Api/Tenancy/TenantResolver.cs:68-70`) and `complete` answers 403 `project_required` before any read or storage call (`ArtifactCompletionVerifier.cs:150-156`; `EvidenceErrors.cs:41-48`). The only credential that can call it is a project-bound `ci_producer` token — its permission set carries `artifact:write` (`ExchangeCommands.cs:19-27`; `IngestTokenCommands.cs:22-30`) and its `project_id` claim is the artifact's project (`CiProducerTokens.cs:78-88`, `:150-165`). For an artifact whose run carries a CI reference, only a `github_actions` token bound to that same run passes the binding guard (`ArtifactCompletionVerifier.cs:196-209`); an ingest-token-minted producer carries no run claim (`CiProducerTokens.cs:168-185`) and is refused like an unknown id.

**Confirm with the producer before completing.** `complete` promotes the staged object to the final key after re-verifying size and SHA-256, and the outcome is `ready` — still `scan_state = 'pending'`, so the bytes stay gated until a scan decides (`ArtifactCompletionVerifier.cs:281-315`; `ArtifactReadModel.cs:30-33`, `:47-51`). Completing an upload the producer is still writing, or intends to retry, commits a verdict nobody asked for.

The repair is therefore the producer re-running `complete` once with a fresh `Idempotency-Key` on its own credential:

```console
POST $API/v1/artifacts/$ARTIFACT/complete
Authorization: Bearer <ci_producer token for the artifact's project>
Idempotency-Key: <new key>
```

Rules that decide the outcome: only `pending_upload`/`uploaded` are completable (`ArtifactCompletionVerifier.cs:318-321`); the staging key is derived, never read from the row (`:258-259`); a missing staging object falls through to the final key, and a missing final key is a **committed** 200 — the handler returns `Result.Success(ArtifactCompletionVerdict.Reject(ArtifactFailureCode.ObjectMissing))` (`:306-311`), and a rejection is "a committed 200, never an error response" (`Domain/Artifact.cs:164-167`); a stored outcome replays unchanged (`:215-223`). A service principal is bound to the artifact's run and gets the same 404 as an unknown id when the CI reference differs (`:196-209`).

**Verify.** Read `failure_code` from the **complete response** — the metadata read does not carry it (`CompleteResponse` `EvidenceEndpoints.cs:403-408` has `failure_code`; `ArtifactMetadataView` `:431-466` does not) — or from Q1. `state` `ready` means the object was found and re-verified at the final key (`ArtifactCompletionVerifier.cs:283-300`); `rejected` + `object_missing` means neither key held it (`:304-311`). The audit chain gained one `artifact.complete` row (§11). A second call with the same key must answer the identical body (AC5 shape, `CompleteArtifactUpload.cs:123-135`).

**Rollback.** None exists and none is needed: `rejected` is terminal (`Artifact.cs:310-315`), and `ready` is not reversible either — the operator surface has no transition back to `pending_upload`, and the row can only move on (`ready → pending_delete` from a secret rejection, `ready → rejected` from a parse verdict, §3). Record it, do not try to reverse it; a producer that needs a new attempt mints a new artifact.

## 6. Scenario B — a `ready` row whose object you cannot prove present

**Detect.** A consumer reports a storage 404 on a presigned `GET` for a row whose state is `ready`. Neither the read path nor completion checks presence at that point: `complete` returns the stored outcome before any storage call (`ArtifactCompletionVerifier.cs:211-218`), the download route presigns the gate's `object_key` without a `HEAD` (`IssueArtifactDownloadUrl.cs:242-245`), and no general reconciler exists (§12). The parse handlers and the secret scan read the stored object and throw on an absent one — the artifact parse (`ParseArtifactTask.cs:228-235`), the raw-report parse (`ParseRawReportTask.cs:313-316`), the reprocess (`ReprocessReportTask.cs:163-166`) and the secret scan (`RunArtifactSecretScan.cs:110-114`) — but a `retry`/`dead_letter` row is **not** by itself evidence of object loss: each also retries for reasons that say nothing about the object, the parse on a failed child spawn (`ParseArtifactTask.cs:265-270`) and the scan on a lost write race (`RunArtifactSecretScan.cs:284-288`). Q4 carries `t.last_error`, where the consumer records the exception's type and first message line, masked and capped at the column's 1000 characters (`TaskErrorText.cs:40-55`; column and check `migrations/0008_platform_task_queue.sql:108`, `:127`). Only an attempt whose `last_error` carries the missing-object text — "the artifact's stored object is not readable", "the raw report's stored object is not readable" or "the stored object of artifact … is absent" — indicates the same loss.

**Diagnose.** Q4 shows whether `evidence:artifact:<id>:staging_delete` ran (it did, by design, at completion) and nothing else deletes final keys — no route, no handler. So the default reading of a 404 on a `ready` row is **storage-side loss or a bucket/misconfiguration**, not a lifecycle event. [INFERENCE] — no shipped query distinguishes the two.

**Act.** Do **not** re-`complete` to "refresh" it, and do not delete or rewrite the row: there is no shipped verdict for "ready but absent" (`object_missing` is only produced *before* `ready`, `ArtifactCompletionVerifier.cs:304-311`). Hand off with the artifact id, `object_key`, the consumer's timestamp and request id, and the bucket's configuration read through `scripts/env/bootstrap-bucket.sh <env> verify` (`infra/README.md:82-103`) — that step is a **write**: it signs a `PUT` canary under `bootstrap-verify/`, reads it back and deletes it (`scripts/env/bootstrap-bucket.sh:14-18`, `:215-243`, trap `:249-258`). Never confirm the loss by listing the bucket or `GET`ing the prefix: that bypasses the sensitivity gate (§1). The reconciling sweep is `[OPEN]` with receiver **06.4.3-T1** (`plans/features/06-evidence-artifacts/06.4.3-orphan-sweeper.md:104-111`, `:24-26`).

**Verify / rollback.** This runbook changed no artifact row and no object. If the bootstrap `verify` canary ran, say so in the handoff: it wrote and deleted one object under `bootstrap-verify/`, which is why "nothing was changed" may not be claimed for that step. The alert that would have caught the loss earlier is planned, not emitted: `[OPEN]` **06.4.5-T1.2** (`evidgate_sweep_orphans_open`, `plans/features/06-evidence-artifacts/06.4.5-deletion-verification.md:26`, `:80`).

## 7. Scenario C — a `scan_state = 'pending'` that no worker will decide

**Detect.** Q3, plus Q4 filtered to the `secret_scan` outbox row (`CompleteArtifactUpload.cs:29-42`). A `dead_letter` task there is the cause; a `queued` task that never moves means no worker consumes the type; an outbox row with `dispatched_at is null` means the dispatcher never ran at all.

**Diagnose.** The handler declines work in three named ways and logs each: `absent` (row gone), `already_scanned`, `not_ready` — event **2450** with the reason (`RunArtifactSecretScan.cs:146-168`; the `LoggerMessage` is `:318-322`, event id at `:319`) — and event **2451** when it decides (`:324-328`, event id at `:325`). A replayed attempt that finds the row decided writes nothing (`:203-211`, `:265-288`). The task is type `artifact_secret_scan` on the default queue (`TasksOptions.cs:74-81`).

**Act.** There is **no operator path to a scan today**: the only replay route is the dead-letter one, and its gate denies every real caller until 02.x authentication lands (`docs/runbooks/dead-letter.md:158-173`, `OperatorGate.cs:30-33`). So: keep the row, escalate with the task id and `last_error`, and expect the artifact to stay `pending` — which correctly keeps its bytes gated for anyone without `artifact:read_sensitive` (`ArtifactReadModel.cs:30-33`, `:47-51`). If the project's Q6 shows `reject_on_secret = true`, a later decision moves the row to `pending_delete` with `failure_code = 'secret_rejected'` (`Artifact.cs:203-207`; `RunArtifactSecretScan.cs:232-235`).

**Verify.** `scan_state` moves to `clean`/`flagged` (`migrations/0066_evidence_masking_event.sql:31-35`) and a decided state never changes again (trigger `:92-121`), so a second decision is impossible — this is the one state you can neither redo nor undo.

**Audit.** No audit row exists for a scan. Masking findings are events, not audit: `public.masking_event`, one row per artifact and class, `action` `flagged`/`rejected` (`migrations/0066_evidence_masking_event.sql:45-61`).

## 8. Scenario D — a `pending_delete` row that nothing deletes

**Detect.** Q1/Q2 with `state = 'pending_delete'`, always with `failure_code = 'secret_rejected'` today: the domain transition has exactly one shipped caller, the secret scan that the project's `reject_on_secret` armed (`RunArtifactSecretScan.cs:232-235`); the domain doc names the planned retention sweep as its other intended caller (`Artifact.cs:352-355`), which does not exist (§9). Its bytes are still in the bucket: no shipped handler deletes a final key.

**Diagnose.** Q4 will show no deletion row, because **none exists** — the tasks completion writes are the four in §2, and the Evidence module's only other task type is `ingest.reprocess_report` (`ReprocessReportTask.cs:27`, registered at `ParseRunnerServiceCollectionExtensions.cs:65`), which re-parses stored bytes and deletes nothing — so a missing deletion row is the expected result, not the anomaly. A reader of the same row gets 409, whatever permission it holds (`IssueArtifactDownloadUrl.cs:123-128`), so the bytes are not servable meanwhile.

**Act.** Do not hand-delete the object and do not hand-update the row. Both are unsupported and unaudited; the app role cannot delete the row anyway (`migrations/0028_evidence_artifact.sql:125-130`). Escalate as `[OPEN]` with receiver **06.4.2-T1** (delete + verify job) and cite the exact counts from Q2. Retention settings (`PUT /v1/projects/{p}/retention`, `project:write`, `RetentionEndpoints.cs:36`) change what a future job will do; they delete nothing today, and `expires_at` is null on every row (§3), so even a retention job built on expiry would select nothing.

**Verify / rollback.** Nothing changed. Record the row ids; they are the input the 06.4.x job will need.

## 9. Scenario E — "the retention sweep failed"

**Detect.** It cannot fail, because it does not run. Confirm from the schedule registry rather than from absence of logs: the common Tasks composition registers four schedules (`TasksServiceCollectionExtensions.cs:81-84`) — `idempotency.purge`, `authoring.change.purge`, `task.sweep` and `audit.verify_chain` (`EvidgateSweeps.cs:20-29`; `AuthoringChangePurge.cs:14-21`; `VerifyAuditChainTask.cs:16-20`) — `EvidgateSweeps` defines two of them (`EvidgateSweeps.cs:99-116`). A Worker that composes every module (`WorkerHost.cs:82`, `:93`, `:97`, `:104`) adds six module-owned schedules: the OIDC replay-cache purge, session cleanup and invite expiry (`IdentityModule.cs:158`, `:164`, `:195`), the collection-deadline reconcile (`OrchestratorModule.cs:114`) and the daily and period-close usage roll-ups (`UsageModule.cs:61-62`). None of the ten acts on artifacts: the Evidence module registers task handlers and no schedule. `task.sweep` deletes `public.task` rows that are `done`/`dead_letter` and more than 30 days old by `created_at` (`EvidgateSweeps.cs:119`; `SweepTaskHandlers.cs:27-37`); the other nine schedules purge expired idempotency records, purge authoring changes, verify the audit chain, purge the OIDC replay cache, sweep sessions, expire invites, reconcile collection deadlines, and recompute usage roll-ups (daily and on period close). No scheduled task touches `artifact` or object storage.

**Diagnose.** If a project's `artifact_days` is set (Q5) and nobody sees deletion, the correct answer is "not implemented", not "misconfigured". A zero-row Q1 with old `ready` rows is exactly that state.

**Act.** Record it as `[OPEN]` **06.4.2-T1** and **06.4.3-T1**; do not emulate the job with ad-hoc `DELETE`s. Evidence must not be destroyed by hand: the audit chain and the run's verdict reference the row, and the app role's grants exist to make this impossible (`migrations/0028_evidence_artifact.sql:125-130`).

**Verify.** None; nothing ran.

## 10. Scenario F — objects with no row (the unobservable direction)

**Detect.** Not possible with shipped tooling. The seam has `PresignPut`/`PresignGet`, `HeadAsync`, `ReadPrefixAsync` (a ranged GET of one object), `GetAsync`, `CopyAsync` and `DeleteAsync` — **no bucket listing** (`src/BuildingBlocks/ObjectStorage/IObjectStorage.cs:18-129`). Listing is exactly what 06.4.3 needs — `IObjectStore.ListObjects` by prefix, `pending/<org>/` and `org/<org>/`, paginated at 1000 keys (`plans/features/06-evidence-artifacts/06.4.3-orphan-sweeper.md:68-71`) — and it is not built.

**Diagnose.** Two leftovers are known by design: (1) a rejected or uncompleted upload's staging object, collected by the 24 h lifecycle (`infra/README.md:63-80`), which needs no action; (2) a final-key object written by a copy whose commit failed — `complete` re-verifies at the final key and rejects on mismatch, but a crash between copy and commit leaves the object (`ArtifactCompletionVerifier.cs:281-300`). The next `complete` of that artifact adopts it (`:304-315`), so it is only orphaned if no completion follows.

**Act.** None available. Mark `[OPEN]` **06.4.3-T1** (sweep, grace ≥ 48 h, audited object delete). Never "clean up" a prefix by hand: no shipped audit row can record it, and a wrong prefix deletes evidence.

**Verify / rollback.** None; nothing was done.

## 11. Audit, and what is not recorded

| Action you take | Audit row | Evidence |
|---|---|---|
| `POST /v1/artifacts/{a}/complete` (any committed outcome, `ready` or `rejected`) | `artifact.complete`, object `artifact`/`<id>` | `[AuditedAction("artifact.complete")]` `CompleteArtifactUpload.cs:150`; taxonomy row `docs/security/audit-taxonomy.md:105` |
| Mint / upload-url | `artifact.mint` | `CreateArtifactRecord.cs:85`; `RequestArtifactUploadUrl.cs:122`; taxonomy `:104` |
| `PUT /v1/projects/{p}/retention` | `project.retention.set` | `SetRetentionPolicy.cs:37` — **not** listed in `docs/security/audit-taxonomy.md`, whose table carries `artifact.mint`/`artifact.complete` only (`:104-105`) `[OPEN]` (taxonomy update) |
| A refused sensitive download | `artifact.download_denied`, metadata `outcome=sensitive_forbidden`, `run_id` | `IssueArtifactDownloadUrl.cs:52`, `:150-174`; also absent from the taxonomy table `[OPEN]` |
| Staging delete task | none — the task row is the record | `ArtifactStagingDeleteTask.cs:21-23` |
| Secret scan decision | none (events instead, §7) | `RunArtifactSecretScan.cs:213-235` |
| Any orphan repair by a sweep | none — the sweep does not exist | `[OPEN]` **06.4.3-T1.4** (`artifact.swept_orphan`, `plans/features/06-evidence-artifacts/06.4.3-orphan-sweeper.md:111`) |

Read the trail either through the API (`GET /v1/orgs/{o}/audit?action=artifact.complete`, `audit:read`) or Q7. The audit row is written by the pipeline for the committed command — "the pipeline writes no audit entry for a failing command and rolls the tenant transaction back" (`IssueArtifactDownloadUrl.cs:145-148`) — so a `complete` that returns 4xx writes none.

## 12. Known gaps (each with its receiver)

| Gap | Consequence for this runbook | Receiver |
|---|---|---|
| No orphan sweep, no bucket listing, no audited object delete | scenario F is unobservable; scenario B cannot be confirmed | **06.4.3-T1.1–T1.4** |
| No stale-upload rejection, no `upload_expired` code, no upload-window setting | `pending_upload` rows are only ever closed by a human calling `complete` (§5) | **06.4.3-T1.2** |
| No artifact retention/deletion job; `expires_at` never written | `pending_delete` never becomes `deleted`; retention is a stored promise only | **06.4.2-T1** |
| Operator routes deny every caller (`DenyAllOperatorGate`) | a dead-lettered `artifact_secret_scan`/`artifact_staging_delete` cannot be replayed over HTTP | **02.x** (operator authentication), as recorded in `docs/runbooks/dead-letter.md:173` |
| No orphan, retention or storage-health metric. Two artifact-side counters exist — `evidgate_raw_report_enqueued_total`, a count of completed `raw_report` artifacts scheduled for parsing, tagged `issue` with `resolved` or a producer-lookup issue code (`EvidgateTelemetry.RawReportEnqueue.cs:12-21`, `:43`; emitted `CompleteArtifactUpload.cs:393`), and `evidgate_ingest_cap_exceeded_total`, tagged `cap` and counting ingest refusals including `report_bytes`/`report_cases` (`EvidgateTelemetry.cs:51`, `:129-132`, `:231-241`; emitted from the parse at `ParseArtifactTask.cs:223`, `:255`) — and neither says anything about uploads that never completed, `pending_delete` rows or object presence. The shipped queue signals are `task_dead_letter_total` (untagged), `task_deferred_total` (tagged `task_type`), `task_dispatch_lag_seconds` and `outbox_undispatched` | no signal distinguishes "upload never completed" from "queue busy", and nothing counts a `pending_delete` backlog | **06.4.5-T1.2**; queue metric names `TaskQueueMetrics.cs:27`, `:41`; emission `TaskAlertNotifier.cs:42`, `TaskConsumer.Deferral.cs:41`, `OutboxDispatcher.cs:89-98` |
| No artifact-side alert rule is present in this tree, and no reconciler exists behind one | nothing pages for an upload that never completed or a row whose object is gone | **06.4.5-T1.2** (rules are planned and consume planned metrics: `plans/features/06-evidence-artifacts/06.4.5-deletion-verification.md:79-80`, `:25-26`) |
| Audit taxonomy lacks `artifact.download_denied` and `project.retention.set` | §11 cites code, not the taxonomy | taxonomy owner, `[OPEN]` |
| No staging drill (AC4) | the document is grounded, the drill is not run | **10.7.1-T3.1 drill half** |

## 13. Related runbooks

- [`dead-letter.md`](dead-letter.md) — classification, Q5 by task id, and the replay route that is denied today.
- [`stuck-run.md`](stuck-run.md) — the run-side half: what "stuck" means and when a run may be reconciled.
- [`telemetry.md`](telemetry.md) — resource attributes, sampling and the app-side redaction list, including what is not shipped.
- [`environments.md`](environments.md) — which host and bucket an operator is looking at.
- [`breach.md`](breach.md) — when bytes may have been served that should not have been.

## 14. Evidence table

| # | Claim | Cite |
|---|---|---|
| 1 | Six artifact states, their CHECK, and `pending_upload` as the only mint state | `migrations/0028_evidence_artifact.sql:73`; `Artifact.cs:126-161` |
| 2 | `scan_state` closed list, default `pending` | `migrations/0028_evidence_artifact.sql:59`, `:72` |
| 3 | `expires_at` exists; no writer assigns it | `migrations/0028_evidence_artifact.sql:61`; `Artifact.cs:275`, `:294` |
| 4 | `public.artifact` is FORCE RLS, per-org policy, no operator policy | `migrations/0028_evidence_artifact.sql:103-115` |
| 5 | App role: no DELETE, column-scoped UPDATE plus `sensitivity`; operator has SELECT only | `migrations/0028_evidence_artifact.sql:125-130`; `migrations/0066_evidence_masking_event.sql:124` |
| 6 | Tenant GUC helper | `migrations/0002_foundation_evidgate_schema.sql:30-33` |
| 7 | Artifact routes: upload-url, complete, metadata, download-url | `src/Modules/Evidgate.Modules.Evidence/Endpoints/EvidenceEndpoints.cs:42-58` |
| 8 | `complete` requires `artifact:write` and is audited | `CompleteArtifactUpload.cs:150-151` |
| 8a | A member tenant carries no project, so `complete` refuses it 403 `project_required`; the `ci_producer` token carries `artifact:write` and the project claim | `TenantResolver.cs:68-70`; `ArtifactCompletionVerifier.cs:150-156`; `EvidenceErrors.cs:41-48`; `CiProducerTokens.cs:78-88`; `ExchangeCommands.cs:19-27` |
| 8b | A cookie-authenticated mutation needs `X-XSRF-TOKEN` | `SessionXsrfMiddleware.cs:52-71`; `docs/runbooks/kill-switch.md:115-116` |
| 8c | The metadata read has no `failure_code`; the complete response does | `EvidenceEndpoints.cs:403-408` (complete), `:431-466` (metadata) |
| 9 | Completable states are `pending_upload`/`uploaded` only; no expiry branch | `ArtifactCompletionVerifier.cs:231-234`, `:318-321` |
| 10 | Stored outcome replays unchanged; project-deleting guard | `ArtifactCompletionVerifier.cs:215-228` |
| 11 | Staging HEAD → checksum/size → copy → re-HEAD; missing object is a committed 200 rejection | `ArtifactCompletionVerifier.cs:258-315` |
| 12 | CI-run binding for a service principal | `ArtifactCompletionVerifier.cs:196-209` |
| 13 | Completion enqueues scan + staging delete (+ parse for `raw_report`) | `CompleteArtifactUpload.cs:332-364` |
| 13a | `complete` writes both outcomes: `MarkReady` or `MarkRejected` | `CompleteArtifactUpload.cs:227-228` |
| 13b | `rejected` is also written from `ready` by the parse task (same `MarkRejected`) | `ParseArtifactTask.cs:301`; `Artifact.cs:302-315` |
| 13c | `ready → pending_delete` exists and refuses every other source state | `Artifact.cs:361-372`; caller `RunArtifactSecretScan.cs:232-235` |
| 13d | Completion writes outbox rows keyed `evidence:artifact:<id>:<type>`; the dispatcher derives the task key `'outbox:' \|\| outbox.id` | `CompleteArtifactUpload.cs:332-391`; `OutboxWriter.cs:22-27`; `OutboxDispatchSql.cs:70-87`; `migrations/0006_platform_outbox.sql:46-51`, `:94`; `migrations/0008_platform_task_queue.sql:34-38` |
| 14 | The four evidence task types | `CompleteArtifactUpload.cs:29-49` |
| 15 | The common Tasks composition registers four schedules, two defined by `EvidgateSweeps`; modules add six more and none of the ten touches artifacts; 30-day retention of terminal tasks only | `TasksServiceCollectionExtensions.cs:81-84`; `EvidgateSweeps.cs:99-116`, `:119`; module registrations `IdentityModule.cs:158,164,195`, `OrchestratorModule.cs:114`, `UsageModule.cs:61-62`; delete SQL `SweepTaskHandlers.cs:27-36` |
| 16 | Secret-scan declines, decisions, log events 2450/2451 | `RunArtifactSecretScan.cs:146-168`, `:203-211`, `:230-235`, `:318-322` (id `:319`), `:324-328` (id `:325`) |
| 17 | Scan guard: decided state cannot change | `migrations/0066_evidence_masking_event.sql:31-35`, `:92-121` |
| 18 | `masking_event` shape and action values | `migrations/0066_evidence_masking_event.sql:45-61` |
| 19 | Effective sensitivity gates bytes when scan is not clean | `ArtifactReadModel.cs:30-33`, `:47-51` |
| 20 | Download gate: 409 for `pending_delete`/`deleted`, 409 for non-`ready` | `IssueArtifactDownloadUrl.cs:123-141` |
| 21 | Retention policy table and upsert key; app cannot DELETE | `migrations/0062_evidence_retention_policy.sql:43-60`, `:76-79` |
| 22 | Retention route permissions and defaults | `RetentionEndpoints.cs:32-36`; `RetentionLimits.cs:20-44` |
| 23 | Kind policy carries `reject_on_secret`; read route | `migrations/0028_evidence_artifact.sql:140`; `ArtifactKindPolicyEndpoints.cs:32` |
| 24 | Object storage options and bucket key | `ObjectStorageOptions.cs:20-35` |
| 25 | No bucket listing in the storage seam | `IObjectStorage.cs:18-129` |
| 26 | 24 h `pending/` lifecycle; bootstrap `verify` requires it and **writes** a `bootstrap-verify/` canary | `infra/README.md:63-80`, `:94-103`; `scripts/env/bootstrap-bucket.sh:14-18`, `:215-243` |
| 27 | Task columns and state vocabulary; `idempotency_key` | `migrations/0008_platform_task_queue.sql:94-129` |
| 28 | Dead-letter log events 2306/2320, lease event 2350 | `TaskConsumer.Logging.cs:67-71`; `LoggingTaskAlertSink.cs:42-52`; `TaskClaimer.cs:287-291` |
| 29 | Operator replay exists but denies every caller today | `docs/runbooks/dead-letter.md:158-173` |
| 30 | Shipped queue signals: `task_dead_letter_total` (untagged), `task_deferred_total` (tagged `task_type`), `task_dispatch_lag_seconds`, `outbox_undispatched` | `TaskQueueMetrics.cs:27`, `:41`; emission `TaskAlertNotifier.cs:42`, `TaskConsumer.Deferral.cs:41`, `OutboxDispatcher.cs:89-98` |
| 31 | No orphan/retention/storage-health metric and no artifact-side alert rule ship in this tree (no artifact rule file under `observability/`); the artifact-side counters are `evidgate_raw_report_enqueued_total` (parse scheduling, tagged by the producer lookup's issue code) and `evidgate_ingest_cap_exceeded_total` (tagged `cap`, e.g. `report_bytes`/`report_cases`) | `EvidgateTelemetry.RawReportEnqueue.cs:12-21`, `:43`; emission `CompleteArtifactUpload.cs:393`; `EvidgateTelemetry.cs:51`, `:129-132`, `:231-241`; queue metrics `TaskQueueMetrics.cs:27`, `:41`; `OutboxDispatcher.cs:89-98` |
| 32 | 00 §8.4 defines the state machine and the "never evidence" rule (single paragraph, no reconciler) | `plans/00-overview.md:273-275`; `plans/features/10-operations/10.7.1-operable-by-a-second-person.md:49` |
| 33 | Sweeper design (states, grace, tasks, metrics) is planned, tasks `06.4.3-T1.1`–`T1.4` | `plans/features/06-evidence-artifacts/06.4.3-orphan-sweeper.md:51-54`, `:58-62`, `:68-71`, `:104-111` |
| 34 | Orphan/backlog alert rules are planned, subtask `06.4.5-T1.2`; the backlog metric `evidgate_retention_pending_delete_backlog` is never emitted | `plans/features/06-evidence-artifacts/06.4.5-deletion-verification.md:25-26`, `:31`, `:79-80` |
| 35 | Audit taxonomy rows for `artifact.mint`/`artifact.complete` only | `docs/security/audit-taxonomy.md:104-105` |
| 36 | Task file this runbook satisfies; drill open | `plans/features/10-operations/10.7.1-operable-by-a-second-person.md:112`, `:25`, `:86` |
