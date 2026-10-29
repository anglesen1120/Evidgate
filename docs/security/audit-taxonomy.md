# Audit event taxonomy, immutability and chain design

Owner: 11.5.1-T1.1 (task [11.5.1](../../plans/features/11-security-compliance/11.5.1-who-saw-what-provably-acceptance-audit.md), AC1). This page documents what the code on develop `4d1a7aa` actually writes: the row shape, every emitted action, the retention position per data class, the T8 chain mechanics and the enforcement that makes the trail append-only.

Legend: ✅ verified with a file:line or link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Every line number below was read from the worktree at `4d1a7aa`; paths are repository-relative.

**Shipped names (finding G6).** The [11.5.1](../../plans/features/11-security-compliance/11.5.1-who-saw-what-provably-acceptance-audit.md) plan says `audit_event` and `row_hash`; the shipped table is `public.audit_log` (`migrations/0015_identity_audit_log.sql:25`) with chain column `entry_hash` (`migrations/0015_identity_audit_log.sql:40`). `audit_event` = `audit_log`, `row_hash` = `entry_hash`; there is no other audit table. The plan's cron (`0 */6 * * *`, [02.7.1](../../plans/features/02-identity-tenancy/02.7.1-audit-trail.md) §5 and §7) also differs from the shipped `7 */6 * * *` (`src/BuildingBlocks/Persistence/Audit/VerifyAuditChainTask.cs:20`); the plan rows stay uncorrected until their owner edits them, and this page uses the shipped values.

## 1. One writer, one table

| Property | Evidence |
|---|---|
| One insert site in `src/**`, and a second added by F55 in `migrations/**`, extended by F58: `insert into public.audit_log` exists once in the app writers and, since `0050`, also inside the admin-revoke definer (`session_revoke_one`, whose six-argument `0051` version also stamps `request_id` and `trace_id`), which writes its row in its own transaction and computes the chain entry in SQL | `src/BuildingBlocks/Persistence/Audit/AuditTrail.cs:250-256` (`AuditSql.Insert`), executed at `:176-197`; `migrations/0050_identity_session_revoke_audit.sql`; `migrations/0051_identity_session_revoke_audit_ids.sql:85,87-93,300-301` |
| The buffer is opened by the tenant transaction stage and flushed just before commit, so the audit rows and the business change are one transaction | `src/BuildingBlocks/Pipeline/Behaviours/TenantTransactionBehaviour.cs:57` (open), `:70` (flush), `:72` (commit) |
| `[AuditedAction]` handlers are queued by the authorization stage after a successful result | `src/BuildingBlocks/Pipeline/Behaviours/AuthorizationBehaviour.cs:108-112` |
| Three direct (non-attribute) call sites cover five actions; they use the same writer and the same flush | `src/Modules/Evidgate.Modules.Identity/Invites/MembershipBinder.cs:473-482`; `src/Modules/Evidgate.Modules.Identity/Login/Audit/IdentityAuditWriter.cs:89-94`; `src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/TransitionRunStateCommand.cs:155` |
| The verifier and the read API only read | `src/BuildingBlocks/Persistence/Audit/AuditChainVerifier.cs:38-79`; `src/Modules/Evidgate.Modules.Identity/Infrastructure/Persistence/AuditReadSql.cs:13-30` |
| The migration adds a BEFORE INSERT guard trigger, not a writer | `migrations/0015_identity_audit_log.sql:89-122` |

## 2. The row shape

### 2.1 Columns

| Column | Source | Written at |
|---|---|---|
| `id` | `Guid.CreateVersion7()` per row | `AuditTrail.cs:158`; column `migrations/0015_identity_audit_log.sql:26` |
| `org_id` | the open tenant scope's org, or the org the login/link/bind writer opened | `AuditTrail.cs:159,180`; `migrations/0015_identity_audit_log.sql:27` |
| `seq` | chain tip + 1, dense per org | `AuditTrail.cs:141,149,182`; `migrations/0015_identity_audit_log.sql:28,44` |
| `actor_type` | one of `user`, `ci_producer`, `agent`, `platform_operator`, `system` | `AuditModels.cs:4-20`; `AuditTrail.cs:161,183`; CHECK `migrations/0015_identity_audit_log.sql:47` |
| `actor_id` | user id, credential id / `project:{id}` / `unbound`, `operator`, or `system` | `src/BuildingBlocks/Pipeline/Authorization/AuditActorResolver.cs:10-19`; `AuditTrail.cs:162,184`; `migrations/0015_identity_audit_log.sql:30` |
| `action` | the `[AuditedAction]` value (see §2.2) | `src/BuildingBlocks/SharedKernel/Authorization/AuditedActionAttribute.cs:15,19-29`; `AuditTrail.cs:163,185`; `migrations/0015_identity_audit_log.sql:31,49` |
| `object_type`, `object_id` | the handler's `Describe` call; NULL when it never describes | `AuditModels.cs:36-41`; `AuditTrail.cs:164-165,186-187`; `migrations/0015_identity_audit_log.sql:32-33` |
| `before_hash` | `AuditObject.BeforeHash` — **no emitter sets it** (§2.5) | `AuditModels.cs:33,39`; `AuditTrail.cs:166,188`; `migrations/0015_identity_audit_log.sql:34` |
| `after_hash` | `AuditObject.AfterHash` — set by two actions only (§2.5) | `AuditModels.cs:34,40`; `AuditTrail.cs:167,189`; `migrations/0015_identity_audit_log.sql:35` |
| `request_id` | `activity?.GetBaggageItem("evidgate.request_id")`; baggage set by the request-id middleware; **nullable** (worker/system paths have no HTTP request). The `0050` definer row was always null because the database sees no baggage; since `0051` the route passes the same baggage value into the six-argument `session_revoke_one`, which keeps it only when it matches `^[!-~]{1,128}$` (`CorrelationNames.IsSafeRequestId`) and nulls it otherwise | `AuditTrail.cs:46,100,168,190`; `src/Evidgate.Api/Middleware/RequestIdMiddleware.cs:52`; `migrations/0015_identity_audit_log.sql:36`; `migrations/0051_identity_session_revoke_audit_ids.sql:128,300-301` |
| `trace_id` | `activity?.TraceId` (W3C); **nullable** for the same reason — the `0051` definer row carries it when the caller passes a valid id (32 lowercase hex, never the all-zero id) and nulls it otherwise | `AuditTrail.cs:101,169,191`; `migrations/0015_identity_audit_log.sql:37`; `migrations/0051_identity_session_revoke_audit_ids.sql:132-135,300-301` |
| `metadata` | jsonb from `AuditObject.Metadata`, validated against the allowlist (§2.6) | `src/BuildingBlocks/Persistence/Audit/AuditMetadata.cs:13,16,19-41,45-76`; `AuditTrail.cs:71,170,192`; `migrations/0015_identity_audit_log.sql:38` |
| `prev_hash` | the previous row's `entry_hash`, 32 zero bytes for a first row | `src/BuildingBlocks/Persistence/Audit/AuditHashing.cs:17`; `AuditTrail.cs:142,171,193`; `migrations/0015_identity_audit_log.sql:39,48` |
| `entry_hash` | `sha256(prev_hash ‖ RFC 8785 canonical fields)` over 14 fields | `AuditHashing.cs:11,21-44,49-59`; `AuditTrail.cs:174,194`; `migrations/0015_identity_audit_log.sql:40,48` |
| `created_at` | application `UtcNow`, truncated to microseconds and floored at the tip's value; the `0050` definer keeps the same floor from its own clock (`greatest(clock_timestamp(), tip.created_at)`, microsecond precision) | `AuditHashing.cs:61-67`; `AuditTrail.cs:144-156,195`; `migrations/0015_identity_audit_log.sql:41` |
| absent on purpose | no `updated_at`, no `version`; UPDATE/DELETE/TRUNCATE revoked from the app role; append-only guard trigger | `migrations/0015_identity_audit_log.sql:21-23,89-122,124-125` |

### 2.2 Action naming

- An attribute action must match `^[a-z_]+(\.[a-z_]+)+$` — at least two lower-snake words, one dot; the constructor throws otherwise (`AuditedActionAttribute.cs:15,21-26`).
- The table adds a length bound only: `char_length(action) between 1 and 128` (`migrations/0015_identity_audit_log.sql:49`). The attribute itself does not bound length.
- An `[AuditedAction]` value is not a permission string. The permission is a separate `[Permission]`/`[AuthenticatedMember]` declaration on the same handler; the two are related by convention (`AuditedActionAttribute.cs:5-11`), never derived.
- Four call sites take the action from a variable, and each variable is a closed literal set: `IdentityAuditWriter.cs:89` (`auth.login`, `auth.link`, `auth.link_move`), `AuthorizationBehaviour.cs:109-111` (the attribute values themselves), `MembershipBinder.cs:480` (constant at `:67`), `TransitionRunStateCommand.cs:155` (constant at `RunStateTransition.cs:140`). No other emitter computes a string.

### 2.3 Actor kinds, and which are reachable

| `actor_type` | Declared | Produced today |
|---|---|---|
| `user` | `AuditModels.cs:7` | ✅ member principal — `AuditActorResolver.cs:14`; also the explicit actors in `IdentityAuditWriter.cs:70` and `MembershipBinder.cs:480` |
| `ci_producer` | `AuditModels.cs:10` | ✅ every service principal, including a token minted for the `agent` service role — `AuditActorResolver.cs:15-16` |
| `agent` | `AuditModels.cs:13`, CHECK `migrations/0015_identity_audit_log.sql:47` | ❌ never: the resolver has no `agent` branch (`AuditActorResolver.cs:11-19`). The role exists and holds `artifact:write`/`event:write` (`contracts/permissions.json` roles), but its rows are written as `ci_producer` |
| `platform_operator` | `AuditModels.cs:16` | ❌ never: resolved at `AuditActorResolver.cs:17`, but the only factory that would carry it, `RequestPrincipal.Operator()` (`src/BuildingBlocks/Pipeline/Authorization/IRequestPrincipal.cs:145-146`), has no caller and the evaluator denies the kind outright (`src/Modules/Evidgate.Modules.Identity/Authorization/PermissionEvaluator.cs:63`); 02.8.1 supplies the grant-carrying form |
| `system` | `AuditModels.cs:19` | ✅ a missing principal resolves to `system` (`AuditActorResolver.cs:13,18`); every `run.state_changed` row today is `system` because no caller passes `TransitionActor.Caller` (`src/Modules/Evidgate.Modules.Orchestrator.Contracts/RunStateTransition.cs:99`, `TransitionRunStateCommand.cs:165-168`) |

### 2.4 What never produces a row

- **A failed or denied command.** The authorization stage records only on a successful result (`AuthorizationBehaviour.cs:108-112`) and the tenant stage rolls the transaction back (`TenantTransactionBehaviour.cs:61-66`). Denied reads/writes are therefore not audited in `audit_log` at all ([02.7.1](../../plans/features/02-identity-tenancy/02.7.1-audit-trail.md) `:41` — "denied reads are not audited here"; register entry OI-02-31, [open items 01](../../plans/open-items/01-core-identity.md)). The intended home for denials is `security_event` (§4).
- **An idempotent replay.** `IdempotencyBehaviour` calls `Suppress()` before replaying the stored response (`src/BuildingBlocks/Pipeline/Behaviours/IdempotencyBehaviour.cs:85-94`), and the next `Record` is dropped (`AuditTrail.cs:88-93`). Individual handlers also suppress a no-op success (for example `RemoveMember.cs:83` when the member is already removed).
- **A read-only query.** No unit is opened for `IBaseQuery` messages (`TenantTransactionBehaviour.cs:51,57`).
- **A scheduled task.** No `system` row is written for any sweep today; a fault is carried by a log line, a counter and a dead-lettered task (§6.5).

### 2.5 Hash fields in practice

- `before_hash` is NULL in every row: the column, the record member and the DTO exist (`AuditModels.cs:33,39,66`; `AuditReadSql.cs:17`), but no emitter passes `BeforeHash` — no write path computes a "before" object hash.
- `after_hash` is set by exactly two actions, both with the object's own content address: `policy.activate` (`src/Modules/Evidgate.Modules.Policy/Application/Commands/ActivatePolicy.cs:137`) and `policy_revision.create` (`src/Modules/Evidgate.Modules.Policy/Application/Commands/CreatePolicyRevision.cs:96`). Every other action records no object hash; a manifest or suite-revision hash is returned to the caller, not audited (`UploadManifest.cs:47` row, `SuiteCommands.cs:107` row in §3).

### 2.6 Metadata allowlist

- 16 keys maximum, 256 characters per value, and a credential-shaped value is refused when the entry is built (`AuditMetadata.cs:13,16,45-76`) by the regex at `AuditMetadata.cs:79`.
- The allowlist is `email_domain`, `role_code`, `role_id`, `membership_id`, `slug`, `status`, `fields`, `count`, `format`, `range_days`, `reason`, `outcome`, `previous_id`, `mode`, `provider`, `via`, plus `target_user_id` (F55: the revoked session's user on the admin-revoke row, written by the `0050` definer) (`AuditMetadata.cs:19-41`). Adding a key is a review decision (`AuditMetadata.cs:18`).
- `reason` is free text up to 256 characters (only the credential-shape regex applies), which contradicts the no-free-text rule of [11.9.2](../../plans/features/11-security-compliance/11.9.2-data-subject-requests-pdpl.md) AC9/AC10 — recorded as gap G7 in §7.2.

## 3. V1 action catalogue

Emitters on develop `4d1a7aa`: **64 actions / 65 emit sites** (`artifact.mint` has two sites). One row per emitted action; the emit site is the line that decides the action (`[AuditedAction]`, or the `Record`/`WriteAsync` call on a direct path). An object cite written as `:NNN` is a line in the same file as the emit site.

| Module | Actions |
|---|---|
| Policy | 7 |
| Evidence | 2 |
| Identity | 22 |
| Ingestion | 3 |
| Orchestrator | 5 |
| Tenancy | 4 |
| Usage | 1 |
| Catalog | 3 |
| Authoring | 17 |
| **Total** | **64** |

| # | Action | Emit site | Module | Command / handler | Actor | Object (`type` / `id`) | Hash |
|---|---|---|---|---|---|---|---|
| 1 | `quarantine.create` | `src/Modules/Evidgate.Modules.Policy/Application/Quarantine/QuarantineCommands.cs:40` | Policy | `CreateQuarantineEntryCommand` (`:49`) | user | `quarantine` / `id` (`:145`) | — |
| 2 | `quarantine.remove` | `src/Modules/Evidgate.Modules.Policy/Application/Quarantine/QuarantineCommands.cs:211` | Policy | `RemoveQuarantineEntryCommand` (`:218`) | user | `quarantine` / `command.QuarantineId` (`:246`) | — |
| 3 | `waiver.create` | `src/Modules/Evidgate.Modules.Policy/Application/Waivers/WaiverCommands.cs:37` | Policy | `CreateWaiverCommand` (`:46`) | user | `waiver` / `id` (`:92`) | — |
| 4 | `waiver.approve` | `src/Modules/Evidgate.Modules.Policy/Application/Waivers/WaiverCommands.cs:158` | Policy | `ApproveWaiverCommand` (`:165`) | user | `waiver` / `command.WaiverId` (`:204`) | — |
| 5 | `waiver.revoke` | `src/Modules/Evidgate.Modules.Policy/Application/Waivers/WaiverCommands.cs:221` | Policy | `RevokeWaiverCommand` (`:228`) | user | `waiver` / `command.WaiverId` (`:256`) | — |
| 6 | `policy.activate` | `src/Modules/Evidgate.Modules.Policy/Application/Commands/ActivatePolicy.cs:80` | Policy | `ActivatePolicyCommand` (`:87`) | user | `policy_activation` / `outcome.PolicyRevisionId` (`:134-143`); metadata `outcome`, `previous_id`, `mode` | after = `outcome.Hash` without the content-address prefix (`:137`) |
| 7 | `policy_revision.create` | `src/Modules/Evidgate.Modules.Policy/Application/Commands/CreatePolicyRevision.cs:39` | Policy | `CreatePolicyRevisionCommand` (`:48`) | user | `policy_revision` / `row.Id` (`:93-97`); metadata `outcome` (`created`/`unchanged`) | after = `row.Hash` content address (`:96`) |
| 8 | `artifact.mint` | `src/Modules/Evidgate.Modules.Evidence/Application/CreateArtifactRecord.cs:84` and `src/Modules/Evidgate.Modules.Evidence/Application/RequestArtifactUploadUrl.cs:122` | Evidence | `CreateArtifactRecord` (`:90`) / `RequestArtifactUploadUrl` (`:132`) | user, ci | `artifact` / `record.ArtifactId` (`CreateArtifactRecord.cs:139`, `RequestArtifactUploadUrl.cs:258`) | — (bytes checksum is a column, not an audit hash) |
| 9 | `artifact.complete` | `src/Modules/Evidgate.Modules.Evidence/Application/CompleteArtifactUpload.cs:118` | Evidence | `CompleteArtifactUpload` (`:125`) | user, ci | `artifact` / `row.Id` (`:167` replay, `:217` concurrency loser, `:239` normal) | — |
| 10 | `ci_token.exchange` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/ExchangeCommands.cs:37` | Identity | `ExchangeGitHubTokenCommand` (`:45`) | ci (`ci_producer`) | `ci_binding` / `binding.Id` (`:147-150`); metadata `reason=repository_renamed` | — |
| 11 | `ingest_token.exchange` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/ExchangeCommands.cs:182` | Identity | `ExchangeIngestTokenCommand` (`:191`) | ci (`ci_producer`) | `ingest_token` / `token.Id` (`:246`) | — |
| 12 | `ci_binding.create` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/CiBindingCommands.cs:53` | Identity | `CreateCiBindingCommand` (`:61`) | user | `ci_binding` / `binding.Id` (`:109`) | — |
| 13 | `ci_binding.update` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/CiBindingCommands.cs:143` | Identity | `UpdateCiBindingCommand` (`:147`) | user | `ci_binding` / `binding.Id` (`:208-211`); metadata `status` | — |
| 14 | `ci_binding.disable` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/CiBindingCommands.cs:225` | Identity | `DisableCiBindingCommand` (`:229`) | user | `ci_binding` / `binding.Id` (`:263`) | — |
| 15 | `role.create` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/CreateRole.cs:114` | Identity | `CreateRoleCommand` (`:124`) | user | `role` / `granted.Id` (`:213`) | — |
| 16 | `role.update` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/UpdateRole.cs:89` | Identity | `UpdateRoleCommand` (`:99`) | user | `role` / `role.Id` (`:202`) | — |
| 17 | `audit.export` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/ExportAudit.cs:29` | Identity | `ExportAuditCommand` (`:31`) | user | `audit_log` / `orgId` (`:110-118`); metadata `format`, `count`, `range_days` | — |
| 18 | `membership.invite` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/InviteMember.cs:102` | Identity | `InviteMemberCommand` (`:115`) | user | `membership` / `view.Id` (`:277`) | — |
| 19 | `membership.invite_resend` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/InviteLifecycle.cs:43` | Identity | `ResendInviteCommand` (`:54`) | user | `membership` / `view.Id` (`:136`) | — |
| 20 | `membership.invite_cancel` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/InviteLifecycle.cs:161` | Identity | `CancelInviteCommand` (`:167`) | user | `membership` / `membership.Id` (`:217`) | — |
| 21 | `membership.remove` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/RemoveMember.cs:45` | Identity | `RemoveMemberCommand` (`:53`) | user | `membership` / `membership.Id` (`:129`) | — |
| 22 | `membership.role_grant` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/GrantRole.cs:61` | Identity | `GrantRoleCommand` (`:71`) | user | `membership` / `membership.Id` (`:180`); metadata `role_id` | — |
| 23 | `membership.role_revoke` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/RevokeRole.cs:49` | Identity | `RevokeRoleCommand` (`:57`) | user | `membership` / `membership.Id` (`:138`); metadata `role_id` | — |
| 24 | `membership.update` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/UpdateMembership.cs:74` | Identity | `UpdateMembershipCommand` (`:82`) | user | `membership` / `membership.Id` (`:169`); metadata `status` | — |
| 25 | `ingest_token.create` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/IngestTokenCommands.cs:57` | Identity | `CreateIngestTokenCommand` (`:65`) | user | `ingest_token` / `entity.Id` (`:100`) | — (the secret is never in the row) |
| 26 | `ingest_token.rotate` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/IngestTokenCommands.cs:139` | Identity | `RotateIngestTokenCommand` (`:147`) | user | `ingest_token` / `old.Id` (`:218`) | — |
| 27 | `ingest_token.revoke` | `src/Modules/Evidgate.Modules.Identity/Application/Commands/IngestTokenCommands.cs:232` | Identity | `RevokeIngestTokenCommand` (`:237`) | user | `ingest_token` / `token.Id` (`:279`) | — |
| 28 | `membership.bind` | `src/Modules/Evidgate.Modules.Identity/Invites/MembershipBinder.cs:480` | Identity | no command — `MembershipBinder.BindOneAsync` (invite/verified-domain bind during login and link callbacks) | user (explicit actor) | `membership` / `candidate.MembershipId` (`:475-478`); metadata `via` (`:76,79`) | — |
| 29 | `auth.login` | `src/Modules/Evidgate.Modules.Identity/Login/Audit/IdentityAuditWriter.cs:89` | Identity | no pipeline command — `CompleteLoginCommandHandler.HandleAsync` | user (explicit `:70`) | `user` / `auditEvent.UserId` (`:88`, subject built `:71`); metadata `provider` (`:110-126`) | — |
| 30 | `auth.link` | `src/Modules/Evidgate.Modules.Identity/Endpoints/LinkEndpoints.cs:496` | Identity | no command — link callback `LinkEndpoints.AuditAsync` (`:480`) | user | `user` / `userId` (subject `IdentityAuditWriter.cs:71`); metadata `provider` | — |
| 31 | `auth.link_move` | `src/Modules/Evidgate.Modules.Identity/Endpoints/LinkEndpoints.cs:503-504` | Identity | no command — link callback when the identity moved from another user | user | `user` / `userId`; metadata `provider`, `previous_id` (`:509`) | — |
| 32 | `event.batch.ingest` | `src/Modules/Evidgate.Modules.Ingestion/Application/Commands/IngestEventBatch.cs:92` | Ingestion | `IngestEventBatch` (`:106`) | user, ci | `adapter_capability` / `registeredCapability` (`:343`); suppressed for an ordinary batch (`:347`) | — |
| 33 | `adapter_capability.register` | `src/Modules/Evidgate.Modules.Ingestion/Application/Commands/RegisterAdapterCapability.cs:31` | Ingestion | `RegisterAdapterCapability` (`:34`) | user, ci | `adapter_capability` / registered id (`AdapterCapabilityRegistrar.RegisterValidatedAsync:157`) | — |
| 34 | `adapter_capability.register_first_send` | `src/Modules/Evidgate.Modules.Ingestion/Application/Commands/RegisterFirstSendCapability.cs:36` | Ingestion | `RegisterFirstSendCapability` (`:42`) | user, ci | none — no `Describe` call, so `object_type`/`object_id` are NULL | — |
| 35 | `manifest.upload` | `src/Modules/Evidgate.Modules.Orchestrator/Application/Manifests/UploadManifest.cs:47` | Orchestrator | `UploadManifestCommand` (`:57`) | user, ci | `manifest` / `manifestId` (`:191`) | — (the manifest hash is returned, not audited) |
| 36 | `environment_revision.create` | `src/Modules/Evidgate.Modules.Orchestrator/Application/Manifests/EnvironmentRevisions.cs:42` | Orchestrator | `CreateEnvironmentRevisionCommand` (`:51`) | user | `environment_revision` / `existingId` (`:137`) or `revisionId` (`:165`) | — |
| 37 | `run.create` | `src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/CreateRunCommand.cs:156` | Orchestrator | `CreateRunCommand` (`:169`) | user, ci | `run` / `runId` (`:530`) | — |
| 38 | `run.finalize` | `src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/FinalizeShardCommand.cs:142` | Orchestrator | `FinalizeShardCommand` (`:152`) | user, ci | `run` / `command.RunId` (`:336`) | — |
| 39 | `run.state_changed` | `src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/TransitionRunStateCommand.cs:155` | Orchestrator | no command — `TransitionRunStateCommandHandler.TransitionAsync`, called by Orchestrator handlers | system (default); `user`/`ci`/`agent` only if a caller passes `TransitionActor.Caller` | `run` / `command.RunId` (`:154`) | — |
| 40 | `organization.create` | `src/Modules/Evidgate.Modules.Tenancy/Application/Commands/CreateOrganization.cs:61` | Tenancy | `CreateOrganizationCommand` (`:70`) | user | `organization` / `command.OrgId` (`:132`) | — |
| 41 | `organization.update` | `src/Modules/Evidgate.Modules.Tenancy/Application/Commands/UpdateOrganization.cs:83` | Tenancy | `UpdateOrganizationCommand` (`:90`) | user | `organization` / `command.OrgId` (`:170`) | — |
| 42 | `project.create` | `src/Modules/Evidgate.Modules.Tenancy/Application/Commands/CreateProject.cs:51` | Tenancy | `CreateProjectCommand` (`:58`) | user | `project` / `project.Id` (`:132`) | — |
| 43 | `project.update` | `src/Modules/Evidgate.Modules.Tenancy/Application/Commands/UpdateProject.cs:90` | Tenancy | `UpdateProjectCommand` (`:98`) | user | `project` / `command.ProjectId` (`:195`) | — |
| 44 | `usage.record_batch` | `src/Modules/Evidgate.Modules.Usage/Application/Commands/RecordUsageBatch.cs:45` | Usage | `RecordUsageBatch` (`:50`) | user (staff `billing_operator`; see note) | `usage_ledger` / `firstInserted.Id` (`:108-115`); metadata `count`, `outcome=recorded` | — |
| 45 | `suite.create` | `src/Modules/Evidgate.Modules.Catalog/Application/Commands/SuiteCommands.cs:46` | Catalog | `CreateSuiteCommand` (`:54`) | user | `suite` / `id` (`:85`) | — |
| 46 | `suite_revision.create` | `src/Modules/Evidgate.Modules.Catalog/Application/Commands/SuiteCommands.cs:107` | Catalog | `CreateSuiteRevisionCommand` (`:116`) | user | `suite_revision` / `existing.Id` (`:196` replay) or `revisionId` (`:244`) | — (the revision hash is returned, not audited) |
| 47 | `case.update` | `src/Modules/Evidgate.Modules.Catalog/Application/Commands/UpdateCase.cs:90` | Catalog | `UpdateCaseCommand` (`:98`) | user | `test_case` / `command.CaseId` (`:176`) | — |
| 48 | `authoring.checklist_item.create` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/ChecklistItemCreate.cs:42` | Authoring | `CreateChecklistItemCommand` (`:51`) | user | `checklist_item` / `itemId` (`:126`) | — |
| 49 | `authoring.checklist_item.update` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/ChecklistItemUpdate.cs:47` | Authoring | `UpdateChecklistItemCommand` (`:56`) | user | `checklist_item` / `item.ItemId` (`:160`) | — |
| 50 | `authoring.checklist_item.delete` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/ChecklistItemDelete.cs:40` | Authoring | `DeleteChecklistItemCommand` (`:49`) | user | `checklist_item` / `item.ItemId` (`:103`) | — |
| 51 | `authoring.node.create` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/NodeCommands.cs:91` | Authoring | `CreateNodeCommand` (`:100`) | user | `feature_node` / `nodeId` (`:203`) | — |
| 52 | `authoring.node.update` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/NodeCommands.cs:285` | Authoring | `UpdateNodeCommand` (`:294`) | user | `feature_node` / `command.NodeId` (`:430`) | — |
| 53 | `authoring.node.reorder` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/NodeCommands.cs:507` | Authoring | `ReorderNodesCommand` (`:516`) | user | `feature_node` / `command.NodeId` (`:568`) | — |
| 54 | `authoring.node.delete` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/NodeCommands.cs:627` | Authoring | `DeleteNodeCommand` (`:636`) | user | `feature_node` / `command.NodeId` (`:706`) | — |
| 55 | `authoring.manual_case.create` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/ManualCaseCommands.cs:50` | Authoring | `CreateManualCaseCommand` (`:60`) | user | `manual_test_case` / `caseId` (`:165`) | — |
| 56 | `authoring.manual_case.update` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/ManualCaseCommands.cs:192` | Authoring | `UpdateManualCaseCommand` (`:201`) | user | `manual_test_case` / `command.CaseId` (`:317`) | — |
| 57 | `authoring.manual_case.delete` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/ManualCaseCommands.cs:344` | Authoring | `DeleteManualCaseCommand` (`:352`) | user | `manual_test_case` / `command.CaseId` (`:403`) | — |
| 58 | `authoring.manual_case.state` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/ManualCaseCommands.cs:427` | Authoring | `SetManualCaseStateCommand` (`:435`) | user | `manual_test_case` / `command.CaseId` (`:518`) | — |
| 59 | `authoring.manual_case.move` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/ManualCaseCommands.cs:546` | Authoring | `MoveManualTestCaseCommand` (`:554`) | user | `manual_test_case` / `command.CaseId` (`:641`) | — |
| 60 | `authoring.node.restore` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/AuthoringRestoreCommands.cs:103` | Authoring | `RestoreNodeCommand` (`:112`) | user | `feature_node` / `command.NodeId` (`:153`) | — |
| 61 | `authoring.checklist_item.restore` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/AuthoringRestoreCommands.cs:164` | Authoring | `RestoreChecklistItemCommand` (`:173`) | user | `checklist_item` / `command.ItemId` (`:214`) | — |
| 62 | `authoring.manual_case.restore` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/AuthoringRestoreCommands.cs:225` | Authoring | `RestoreManualCaseCommand` (`:234`) | user | `manual_test_case` / `command.CaseId` (`:275`) | — |
| 63 | `authoring.version.restore` | `src/Modules/Evidgate.Modules.Authoring/Application/Commands/AuthoringRestoreCommands.cs:291` | Authoring | `RestoreVersionCommand` (`:300`) | user | kind from `RestoreAudit.ObjectTypeOf(kind)` (`:375-381`) — `feature_node`/`checklist_item`/`manual_test_case`; id `version.EntityId` (`:352`) | — |
| 64 | `authoring.import` | `src/Modules/Evidgate.Modules.Authoring/Application/Import/ImportCommands.cs:74` | Authoring | `ImportAuthoringCommand` (`:83`) | user | `authoring_import` / `batchId` (`:148`) | — |

Notes on the rows that are easy to misread:

- **Row 8** is one action with two emit sites; both handlers are reachable by a service principal. Row 34's contradiction is deliberate: the handler queues the action but never describes an object, so the row carries no object reference.
- **Row 39** is the only `system` row class: `TransitionActor` defaults to `System` (`src/Modules/Evidgate.Modules.Orchestrator.Contracts/RunStateTransition.cs:99`) and no call site passes `Caller` (`TransitionRunStateCommand.cs:165-168`), although `Permissions.Cancel = "run:cancel"` is declared that way (`Permissions.g.cs:249`).
- **Row 44** is emitted code with no live caller: `billing:write` is held only by the staff role `billing_operator` (`contracts/permissions.json` roles) and staff principals are denied by `PermissionEvaluator.cs:63`, so no actor can currently reach the handler.
- **Row 63** is the only dynamic *object* kind (`RestoreAudit.ObjectTypeOf`); the action string itself is constant, like every other row except the four sites listed in §2.2.
- Rows 29–31 are the only actions an unauthenticated-shaped flow produces: each row is written per active org the user belongs to, in its own tenant scope (`IdentityAuditWriter.cs:73-94`).

## 4. Planned, not emitted yet

These are named by a plan or a register entry and have no emitter on develop `4d1a7aa`. When one ships, its row moves into §3.

| Action or class | Status today | Owning task / register |
|---|---|---|
| `security_event` rows (including a broken-chain finding) | The table exists since migration 0053 (11.5.1-T5), but no emitter writes rows yet; a broken chain is still carried by a log line (event 2400), the `audit_chain_verification_failures_total` counter and a dead-lettered task (`VerifyAuditChainTask.cs:52-56,63-64,130-131`) | 11.5.1-T5.1 created the table ([§10](../../plans/features/11-security-compliance/11.5.1-who-saw-what-provably-acceptance-audit.md) `:151`), 11.5.1-T5.2 the read (`:152`); emitting the broken-chain row is G8: receiver 11.1.1-T4.1 adds the type, then 11.5.1-T5.1's emitter work |
| `auth_failure`, `cross_tenant_probe`, `secret_scanner_alert`, `kill_switch_used`, `support_access` | Type baseline defined, no writer | 11.1.1-T4.1 (definition), 11.5.1-T5.2 (read) |
| `notification_channel.create/update/disable/delete/test` | Plan string only; no module implements it | 02.10.2-T1.4 |
| `artifact.object_deleted` | Not a planned action: 10.5.2 says only that *if* the sweeper needs to record a deleting principal it reuses the 02.7.1 writer with this action, so no task currently owns the emitter | 10.5.2 §3 (`plans/features/10-operations/10.5.2-object-storage-delete-protection-and-restore.md:40`); the sweeper itself is 06.4.2-T1 |
| `dsr.opened`, `dsr.identity_verified`, `dsr.executed`, `dsr.closed` | Plan string only; no DSR table yet | 11.9.2-T1.2 |
| Support-grant request/approve/revoke, and operator reads under a grant | No handler or operator principal exists (`IRequestPrincipal.cs:145-146`), so no row can be written | 02.8.1-T1.2, verified by 11.5.1-T2.1 |
| Denied lookup rows | The AC that asks for one `audit_log` row on a cross-tenant read is recorded as not audited; the recommendation is a `security_event` row in a separate write instead | OI-02-31 ([open items 01](../../plans/open-items/01-core-identity.md)); 11.5.1 |
| `idempotency.purge` and other sweep calls | The plan asks for an audited call; today only an Information log line is written, and the handler's own remark records that deviation, so no `system` row exists for any scheduled task (`src/BuildingBlocks/Persistence/Tasks/SweepTaskHandlers.cs:17-20`) | 01.5.1-T6.2 |
| `before_hash` values | Column, record member and DTO exist; no emitter computes a "before" object hash | No task owns populating it today `[OPEN]` |
| Keyed chain (`chain_key_id`, HMAC) | The chain hash is unkeyed today; the keyed variant is the 11.5.2 ADR | 11.5.2-T1.2 (ADR), 11.5.2-T2.3 (key wiring) |

## 5. Retention per class

There is **no retention rule for `audit_log` in force today**: the table has no DELETE grant and no purge path. The register entry that must produce the number is OI-11-11.

| Data class | Rule today | Evidence | Owner to decide |
|---|---|---|---|
| `audit_log` rows | None in force. Rows cannot be deleted by the app role, no cascade exists, and the org itself is only soft-deleted, so every row written stays for the life of the database. The plan text "kept for the life of the org" is the only statement. | `migrations/0015_identity_audit_log.sql:124` (UPDATE/DELETE/TRUNCATE revoked from `evidgate_app`), `:45` (FK NO ACTION), `migrations/0004_identity_organization.sql:119-127` (organization soft delete, app DELETE revoked), [02.7.1](../../plans/features/02-identity-tenancy/02.7.1-audit-trail.md) `:89` ("kept for the life of the org and … never pruned by this story") | OI-11-11 `[OPEN]`: no legal minimum is asserted, counsel review is in 11.8.1; the register's own suggestion is 12 months by default, configurable, with the checkpoint retention set equal to it `[INFERENCE]` — a proposal, not a decision ([open items 04](../../plans/open-items/04-ops-security-commercial.md)). The operational surface that must carry the decided number is the deletion SLA table of 11.9.1-T1.1 (AC1, "log/audit" class) |
| `security_event` | Exists since migration 0053 (11.5.1-T5); no emitter writes rows yet, so no rows and no rule. | `migrations/0053_security_security_event.sql` (`FORCE` RLS, `tenant_isolation` plus the owner-only `security_event_owner_backfill` policy; `revoke update, delete, truncate … from evidgate_app`, 0015/0031/0049 precedent) creates it ([11.5.1](../../plans/features/11-security-compliance/11.5.1-who-saw-what-provably-acceptance-audit.md) §10 T5.1); `security_event:read` keeps its catalogue row and `tenant_admin` grant (`migrations/0010_identity_permission_catalogue.sql:302,351`) | No separate register row found in `plans/open-items/` at `4d1a7aa` `[OPEN]`; 11.5.1-T5.1 must add one (the OI-11-11 entry covers the audit trail, not this table) |
| Artifact objects and evidence metadata | `[DECISION]` artifacts expire after 14 days and their metadata after 90 days by default, per project, enforced by the retention job and bounded against a floor. | [06](../../plans/06-evidence-artifacts.md) FR-06-10 and the "Retention defaults 14 days artifacts / 90 days metadata" decision; `retention_policy` column defaults in §4; bounds (floor/ceiling) in OI-06-10 ([open items 02](../../plans/open-items/02-catalog-ingest-runs-evidence.md) `:77`) | 06.4.1-T1 (policy + floor), 06.4.2-T1 (deletion job). ⚠️ The 14/90 numbers are marked "to confirm with pilots" |
| Erasure and tenant deletion | No hard-delete path exists for any row. An org becomes `status = 'deleted'`; audit rows are kept. A data-subject erasure removes/nulls the personal fields of the user record and keeps the id as a tombstone so the chain stays valid. | `migrations/0004_identity_organization.sql:119-127`; [11.9.2](../../plans/features/11-security-compliance/11.9.2-data-subject-requests-pdpl.md) AC8/AC9/AC10; [11.5.2](../../plans/features/11-security-compliance/11.5.2-audit-chain-integrity-outside-its-own-database.md) AC11 (an `org_deleted` tombstone, never a silent gap) | 11.9.2-T2.1 (writes `docs/security/audit-and-deletion-rule.md`, [11.9.2](../../plans/features/11-security-compliance/11.9.2-data-subject-requests-pdpl.md) AC9, `:147`), 11.9.1-T1 (verified deletion + SLA). ⚠️ The [11.5.1](../../plans/features/11-security-compliance/11.5.1-who-saw-what-provably-acceptance-audit.md) plan still says "tenant deletion removes org rows per deletion SLA"; that wording is unimplementable against `audit_log` today and is recorded as gap G5 |
| `usage_ledger_entry` | No retention or purge rule found for the ledger; [12](../../plans/12-pilot-commercial.md) §4 defines its columns and dedupe key only, and its retention row separates financial/contract records from the artifact retention policy. Not an audit class: ledger rows are billing evidence, not chain rows. | `plans/12-pilot-commercial.md:101` (columns), `:155` (financial records kept per accounting law) | Out of scope of this page `[OPEN]`; the commercial/legal owner of 12 must state it |

Two consequences a reader should not miss:

1. **Nothing can erase an audit row today**, which is why G5's "erasure" half cannot be implemented without either an owner-executed `SECURITY DEFINER` erase after a checkpoint or a documented decision that audit rows are never erased.
2. **The retention rule is `[OPEN]`**, so AC1's "retention rule" is satisfied by stating the position and its owner, not by a number. Any number in this page is marked `[INFERENCE]` or comes with a `[DECISION]` cite.

## 6. Chain design (T8)

### 6.1 Rule

- Ruling T8: the audit hash chain per org is serialised with `pg_advisory_xact_lock(hashtext('audit:' ‖ org_id))` taken as the **last** statement before commit, plus a per-org `seq bigint`; verification walks `seq` ([features README](../../plans/features/README.md), ruling T8). Contract C6 requires immutability "in SQL" ([00-overview](../../plans/00-overview.md) §5.5 C6).
- `seq` is dense per org (`seq >= 1`, `unique (org_id, seq)`): `migrations/0015_identity_audit_log.sql:28,44,46`.
- `prev_hash` is the previous row's `entry_hash`; the first row of an org is `seq = 1` with 32 zero bytes: `AuditHashing.cs:17`, guard trigger `migrations/0015_identity_audit_log.sql:105-109`.
- `entry_hash = sha256(prev_hash ‖ RFC 8785 canonical(fields))` over 14 fields — `action`, `actor_id`, `actor_type`, `after_hash`, `before_hash`, `created_at`, `id`, `metadata`, `object_id`, `object_type`, `org_id`, `request_id`, `seq`, `trace_id` (`AuditHashing.cs:21-44,49-59`). The hash is computed in C#, not SQL, because SQL cannot canonicalise; writer and verifier share the one implementation (`migrations/0015_identity_audit_log.sql:12-19`, `AuditHashing.cs:9-14`). The `0050`/`0051` definer row is the one exception: SQL replicates `AuditHashing.CanonicalFields` over the same 14 fields, with `request_id`/`trace_id` as a JSON string (RFC 8785 escaping) or the literal `null`.
- A BEFORE INSERT guard trigger forces honest extension from the app role: dense `seq`, the tip's `prev_hash`, and `created_at` not earlier than the tip (`migrations/0015_identity_audit_log.sql:89-122`).

### 6.2 Writer order

Per unit of work, inside the tenant transaction:

1. `set_config('lock_timeout', '2000', true)` — a transaction-local 2 s bound (`AuditTrail.cs:127`; SQL `AuditTrail.cs:243`).
2. `pg_advisory_xact_lock(hashtext('audit:' || @org))` — the lock is taken **after** every business statement and immediately before the inserts (`AuditTrail.cs:131`; SQL `AuditTrail.cs:245`). Only this call is wrapped for translation; a `55P03` raised later by the inserts is not (gap G3).
3. Read the chain tip (`AuditTrail.cs:138`; SQL `:247-248`).
4. Per entry: clamp `created_at` (`:150-156`), build the row (`:157-173`), hash it (`:174`), insert (`:176-197`; SQL `:250-256`), carry the hash forward (`:199`).
5. Commit (`TenantTransactionBehaviour.cs:72`), so business change and chain link land atomically.

Lock wait exhaustion surfaces as a retryable 503 with `Retry-After: 1`: `AuditBusyException` is thrown from the lock catch (`AuditTrail.cs:133-136`) and mapped in `src/Evidgate.Api/Middleware/EnvelopeExceptionMiddleware.cs:66-74`.

### 6.3 Lock key, ordering and collisions

- The key is `hashtext('audit:' || org_id)` (32-bit, sign-extended into the single-argument `bigint` advisory space): `AuditTrail.cs:245`.
- Lock inventory relevant to ordering: authoring sibling-group `hashtextextended('authoring_group:…')` taken first (`AuthoringSql.cs:310`, call site `AuthoringVersionWriter.cs:617`); catalog sync `hashtextextended('catalog_sync:'‖project)` (`CaseIdentityService.cs:30`); the two-key pair `hashtext('evidgate.case_prefix'), hashtext(org::text)` (`migrations/0039_authoring_tree.sql:140`, a distinct two-int namespace); the scheduler's `pg_try_advisory_xact_lock(hashtext(schedule))` on its own connection and transaction (`TaskScheduleRunner.cs:110`); and the migrator's **session-level** lock (`MigrationLock.cs:23-27`, documented in `migrations/README.md:111`). No path takes the audit lock and then another advisory lock, so there is no advisory-lock cycle.
- A `hashtext` collision between two orgs costs serialisation between them and nothing else: correctness rests on the guard trigger and `unique (org_id, seq)`, not on the lock (`AuditTrail.cs:245`, `migrations/0015_identity_audit_log.sql:44,105-113`). Probability is about 2⁻³² per org pair `[INFERENCE]` (arithmetic on a 32-bit hash).
- The audited `organization` row is only ever locked `FOR NO KEY UPDATE` (`TenancyReadSql.cs:137-155`, `SeatQuota.cs:119-128`), which does not conflict with the `FOR KEY SHARE` the audit insert's FK check takes. The interaction recorded in STATUS is latency on a login burst, not a deadlock: `FOR UPDATE` was rejected for exactly this reason (`SeatQuota.cs:44-49`, citing `TenancyReadSql.cs:134`), and the accepted PERF2 row describes the 2 s wait (`docs/status-carried.md:200`).

### 6.4 `created_at`

`created_at` is application-clock, truncated to microseconds so a stored row re-hashes identically (`AuditHashing.cs:61-67`), and floored at the current tip's value so `seq` and time never run backwards (`AuditTrail.cs:144-156`). The column default `now()` (`migrations/0015_identity_audit_log.sql:41`) is never used, because the writer always supplies the value. Consequence (gap G9): a leading node with a clock ahead shifts every later row — and the list cursor — forward; the clamp hides the skew instead of rejecting it.

### 6.5 Verification, and what it cannot see

- `AuditChainVerifier` walks one org's chain from `seq = 1` and genesis in pages of 1000, recomputing every hash, and returns the first fault as `Gap`, `BrokenLink` or `HashMismatch` (`AuditChainVerifier.cs:8-18,33,38-79`).
- The scheduled task is `audit.verify_chain` on the maintenance queue, cron `7 */6 * * *`, 100 orgs per page (`VerifyAuditChainTask.cs:17-35`). It lists orgs through the owner-owned definer function `evidgate.audit_org_ids` (`migrations/0015_identity_audit_log.sql:132-147`), verifies each org inside that org's own read-only tenant scope (`VerifyAuditChainTask.cs:86-88`), logs at error level (event 2400) and increments `audit_chain_verification_failures_total` (`:63-64,130-131`), then throws so the task retries and finally dead-letters (`:107-110`).
- It detects an interior gap (also a reorder or renumber), a broken `prev_hash` link, and content tampering by recomputing the hash.
- It **cannot** detect: deletion of the newest N rows, because it stops when a page is short and has no external anchor (G1); a fully emptied chain, because org enumeration today requires at least one `audit_log` row (G2); a forged but self-consistent history, because the hash is unkeyed: the owner role or a superuser can rewrite rows outright, and an app-role holder can append entries of its own — with no key and no checkpoint there is nothing outside the table to contradict either (OI-02-32, [open items 01](../../plans/open-items/01-core-identity.md); 11.5.2 is the keyed/checkpointed design); a malformed `metadata` value, which aborts the walk instead of faulting at that `seq` (G4); and it produces no `security_event` row (G8).

### 6.6 The measured check

The measurement T8 asks for is **10.6.1 AC2**: the load generator's run captures, per batch, the single `INSERT … SELECT … FROM unnest(...) ON CONFLICT ...` statement and the per-org `pg_advisory_xact_lock(hashtext('audit:' ‖ org_id))` wait, with ingest p95 per batch under 1 s ([10.6.1](../../plans/features/10-operations/10.6.1-proving-the-pilot-load.md) AC2).

That check **has not been run**: `STATUS.md` §2 (Done) contains no 10.6.1 row at `4d1a7aa`, and the story is sized at 5 d with milestone M2/M3/M4 ([10.6.1](../../plans/features/10-operations/10.6.1-proving-the-pilot-load.md) header). The [11.5.1](../../plans/features/11-security-compliance/11.5.1-who-saw-what-provably-acceptance-audit.md) plan itself defers the cost to that load test (`:98`, "chain lock is per-org and last-before-commit (T8, measured in 10's load test)"). The chain design in §6.2–§6.4 is therefore verified by code and review only; its cost is measured when 10.6.1 runs, and the residual `55P03`→500 window (G3) is part of what that run will expose.

## 7. Immutability enforcement

### 7.1 What the database grants

| Who | Privilege / policy | Evidence |
|---|---|---|
| `evidgate_app` | SELECT + INSERT; `revoke update, delete, truncate`. The DML comes from 0001's default privileges, which this revoke subtracts. | `migrations/0015_identity_audit_log.sql:124`; `migrations/0001_foundation_roles.sql:90-92` |
| `evidgate_app` policy | `for all … using (org_id = evidgate.current_org_id()) with check (…)` — a foreign-org insert is refused, and with the GUC unset the helper is NULL so no row matches | `migrations/0015_identity_audit_log.sql:59-64`; helper `migrations/0002_foundation_evidgate_schema.sql:26-32` (NULL-safe) |
| `evidgate_worker_sched` | `revoke all` on the table; `EXECUTE` only on `evidgate.audit_org_ids` | `migrations/0015_identity_audit_log.sql:125,146-147` |
| `evidgate_operator` | SELECT grant, but no policy, so it reads zero rows until 02.8.1 adds a grant-gated policy | `migrations/0015_identity_audit_log.sql:126`; policy deliberately absent (`:21-23`) |
| `evidgate_owner` (the migration role) | SELECT and INSERT policies only; `ENABLE` + `FORCE ROW LEVEL SECURITY`, so UPDATE/DELETE match zero rows even for the owner. TRUNCATE is retained, and that is the documented limit. | `migrations/0015_identity_audit_log.sql:56-57,69-79,81-83`; migrations run as the owner (`DECISIONS.md:63`) |
| Guard trigger | BEFORE INSERT, `evidgate.audit_log_chain_guard`, runs as the invoker so it reads the tip under the caller's own tenant policy | `migrations/0015_identity_audit_log.sql:89-122` |
| No cascade | The only FK on the table is NO ACTION, so no parent delete can remove audit rows | `migrations/0015_identity_audit_log.sql:45` |

Deliberate non-enforcement: there is no UPDATE/DELETE blocking trigger. Privileges plus the absence of an UPDATE/DELETE policy are the control (`migrations/0015_identity_audit_log.sql:66-68`).

### 7.2 Known gaps

Each gap has a receiver and a row in [STATUS](../../STATUS.md) §6 (added by the STATUS writer for this wave). The row titles are quoted so the rows can be found by title.

| id | Gap | Evidence | Receiver | STATUS §6 row |
|---|---|---|---|---|
| G1 | Tail truncation is undetectable: the walk ends on a short page and there is no anchor outside the table | `AuditChainVerifier.cs:38-79`; OI-02-32 ([open items 01](../../plans/open-items/01-core-identity.md)) | 11.5.2-T2, 11.5.2-T3 (signed checkpoints, `truncated_tail`) | "Audit chain verifier blind spots (S105 review: G1, G2, G4)" |
| G2 | A fully emptied chain is invisible to the sweep, because the org list itself is derived from `audit_log` presence | `migrations/0015_identity_audit_log.sql:132-144`; `VerifyAuditChainTask.cs:113-128` | 11.5.2-T3 (enumerate from `organization`/checkpoint, require a tombstone) | same row |
| G3 | A `55P03` raised by the inserts answers 500, not 503: only the lock call is wrapped, while the 2 s `lock_timeout` stays in force | `AuditTrail.cs:127,131-136,176-197`; `EnvelopeExceptionMiddleware.cs:66-83` | 02.7.1-T1.2 (wrap the flush or state the limit); the latency interaction is the accepted PERF1/PERF2 rows (`docs/status-carried.md:199`) | "Audit emission and metadata hygiene gaps (S105 review: G3, G6, G7, G8, G9)" |
| G4 | A non-flat `metadata` value aborts verification instead of reporting a fault at that `seq` | `AuditChainVerifier.cs:130`; `AuditHashing.cs:69-83`; `migrations/0015_identity_audit_log.sql:38` (unconstrained jsonb) | 02.7.1-T3.2 | "Audit chain verifier blind spots (S105 review: G1, G2, G4)" |
| G5 | No retention or erasure rule and no purge path exist, and the plan's "tenant deletion removes org rows" wording is unimplementable against `audit_log` | `migrations/0015_identity_audit_log.sql:124`, `:45`; `migrations/0004_identity_organization.sql:119-127`; [11.5.1](../../plans/features/11-security-compliance/11.5.1-who-saw-what-provably-acceptance-audit.md) `:99` ("tenant deletion removes org rows per deletion SLA") | 11.5.1-T1.1 (states the rule as `[OPEN]`), 11.9.2-T2 and 11.9.1-T1 (implement) | "Audit retention and erasure rule missing (S105 review: G5)" |
| G6 | Plan and code disagree on names and cadence: `audit_event`/`row_hash` vs `audit_log`/`entry_hash`, `0 */6 * * *` vs `7 */6 * * *` | `migrations/0015_identity_audit_log.sql:25,40`; `VerifyAuditChainTask.cs:20`; [02.7.1](../../plans/features/02-identity-tenancy/02.7.1-audit-trail.md) §5/§7 | 11.5.1-T1.1 (this page records the mapping; the plan rows stay with their owner) | "Audit emission and metadata hygiene gaps (S105 review: G3, G6, G7, G8, G9)" |
| G7 | `reason` is free text up to 256 characters, contradicting the no-free-text rule | `AuditMetadata.cs:31,79`; [11.9.2](../../plans/features/11-security-compliance/11.9.2-data-subject-requests-pdpl.md) AC9/AC10 | 02.7.1-T2.2 (drop the key or close its vocabulary) | same row |
| G8 | AC6's `security_event` half is not emitted; the interim channel is log + counter + dead letter | `VerifyAuditChainTask.cs:52-56,63-64,107-110`; [02.7.1](../../plans/features/02-identity-tenancy/02.7.1-audit-trail.md) §3 (recorded deviation) | 11.5.1-T5.1 | same row |
| G9 | `created_at` is application-clock and monotonic only by clamp, so a leading node skew shifts later rows and the list cursor forward | `AuditTrail.cs:144-156`; `AuditHashing.cs:61-67`; `migrations/0015_identity_audit_log.sql:41` | 11.5.1-T1.1 (stated here; the fix belongs to the writer owner 02.7.1) | same row |

## 8. Keeping the catalogue current

1. **A new mutation cannot ship unaudited.** `Every_Mutation_Declares_An_Audited_Action` fails the build when a real-module mutating handler lacks `[AuditedAction]`; it also proves non-vacuity on fixture handlers (`tests/Architecture.Tests/PipelineTests.cs:80-95`).
2. **The action string shape is pinned twice.** At construction by the regex (`AuditedActionAttribute.cs:15,21-26`) and at the table by `char_length(action) between 1 and 128` (`migrations/0015_identity_audit_log.sql:49`). Use `object.verb` / `object.verb_qualifier` (`membership.invite`, `authoring.manual_case.restore`).
3. **A new action gets a row here**, in the same pull request: action, module, emit site, actor kinds, object kind, hash source. The row order follows the module groups of §3.
4. **A new direct call site is a design decision, not a convenience.** Only four variable-action sites exist (§2.2); a fifth must be justified, because the attribute path is the one the architecture test protects.
5. **A new metadata key is a review decision** (`AuditMetadata.cs:18`) and must not introduce free text ([11.9.2](../../plans/features/11-security-compliance/11.9.2-data-subject-requests-pdpl.md) AC10).
6. **A planned action moves from §4 to §3** when its emitter lands; until then it must not appear in §3, because the count there is a claim about code that exists.
7. **A change to the count is a change to this page.** The introductory line of §3 states the number; any PR that adds or removes an emitter updates it.

## 9. How the claims were checked

- Codebase-memory was used to locate the writer, the verifier, the actor resolver and the attribute (`search_graph`, `search_code`). `check_index_coverage` reports `src/Modules/**` as `not_tracked`, so the module emitters, their attributes and their `Describe` lines were enumerated with `grep` on the worktree and then read line by line; `migrations/0015` and `migrations/0001` are `parse_partial` in the index and every line cited from them was read from the file.
- Every emit site in §3 was read at its cited line: 59 rows (60 sites, `artifact.mint` twice) are attribute-driven, so the cited line carries `[AuditedAction]` and its literal; the 5 direct-path rows (three write paths: `IdentityAuditWriter`, `MembershipBinder`, `TransitionRunStateCommand`; `IdentityAuditWriter` alone covers `auth.login`, `auth.link`, `auth.link_move`) were read as ranged code, because the deciding value is a constant or a variable a line or two away — that is why those rows cite a range or the constant's own line as well as the emit line. `artifact.mint`'s second site is `RequestArtifactUploadUrl.cs:122`.
- No build, test, lint or formatter was run for this page; it is a documentation artefact. The statements about runtime behaviour are statements about the code at `4d1a7aa`, and §6.6 names the one measurement that is still missing.

## Additions after the `4d1a7aa` snapshot

The counts above are the snapshot's. Rows added later are listed here so no cite above shifts.

| # | Action | Emit site | Module | Command / handler | Actor | Object (`type` / `id`) | Hash |
|---|---|---|---|---|---|---|---|
| 65 | `auth.session_revoke_admin` | `migrations/0050_identity_session_revoke_audit.sql`, re-created six-argument by `migrations/0051_identity_session_revoke_audit_ids.sql:85,87-93` (`evidgate_resolve.session_revoke_one`, the other-member branch) | Identity | no command — inserted inside the definer's own transaction, admin path only (`X-Evidgate-Org` + `membership:write`); F48 (S118) first wrote it from `SessionEndpoints.RevokeOneAsync` after the call returned, F55 (S120) moved it into the definer so the revoke and the row commit or roll back together, and F58 (S122, `0051`) made the definer take `p_request_id`/`p_trace_id` so those columns are stamped (validated: `^[!-~]{1,128}$`; 32 lowercase hex, not all zero) instead of always null | user (the admin) | `session` / the revoked session id; metadata `target_user_id` (the revoked session's user), no cookie material — the definer builds that jsonb itself, so `AuditMetadata.Validate` (the §2.6 gate) does not run on this row | the definer computes the `sha256` chain entry in SQL from the same RFC 8785 canonical fields `AuditHashing.CanonicalFields` builds, under the writer's advisory lock key — the only row hashed outside `AuditTrail` |
