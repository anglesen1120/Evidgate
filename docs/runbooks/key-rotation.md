# Key rotation

Status: **10.7.1-T4.1 written; the pepper section (§3) is the written half of T4.2, code-audited 2026-11-09: it records the shipped two-version window and that project API tokens share the CI pepper without a verifier yet.** **Drill-pending:** staging availability, completed drill evidence and non-author execution are not verified by this desk check. AC1 and AC5 are not demonstrated. [OPEN O-F] §2.3 additionally records the producer-token validity window and the signing-key kill step required by owner decision D-S108 §6 row 5 (owner, 2026-10-27; coordinator queue row F51).

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`.

## 1. Scope, roles and rules

This runbook covers the surviving key classes in 10.7.1-T4.1 and the T5 pepper procedure. It describes current behavior, not a promise of zero downtime. A **secret** is a credential or pepper; a **pepper version** identifies the key used to HMAC a stored token; a **token overlap** is the period in which a rotated token and its successor can both verify. Token overlap and pepper overlap are different windows.

- Never put secret values in this runbook, logs, tickets, terminal history, drill evidence, or screenshots. Use the environment's secret delivery mechanism; do not print responses containing one-time credentials. Record only non-secret identifiers and outcomes.
- Never perform ad-hoc `psql` writes. Use the supported tenant API for token mutations; use the environment's authorized database administration and secret-delivery procedures for credentials. An emergency Data Protection reset is explicitly owner-authorized maintenance (§2.7), not a tenant mutation. **Not possible yet:** an audited operator HTTP rotation endpoint; none is implemented. The operator-access receiver is 02.8.1, but no rotation-endpoint task is defined. [OPEN O-E]
- For platform secrets, the V1 solo platform environment owner changes the secret store/deployment environment and restarts the affected processes; managed-store choice remains open. For tenant ingest tokens, the caller needs `ingest_token:write` (`contracts/permissions.json`, `CreateIngestTokenCommand`, `RotateIngestTokenCommand`, `RevokeIngestTokenCommand`). Actual executor grants remain drill-pending. Separate duties and operator authorization are receivers of O-E.
- [environments](environments.md) owns how a value is changed, and its §2 hands ingest-token, DB-credential, JWT-key and HMAC-pepper rotation to this runbook (`docs/runbooks/environments.md`).

## 2. Key classes

### 2.1 Ingest tokens

**Where it lives.** A generated tenant token is returned once; the list API returns metadata only (`CiAdminEndpoints`). Its HMAC uses `EVIDGATE__CI__PEPPER` and its version uses `EVIDGATE__CI__PEPPERVERSION` (`infra/config-matrix.json`; reader `CiSecrets`). Pepper rotation is §3.1.

**Who may rotate.** A tenant principal with `ingest_token:write` (`contracts/permissions.json`; `RotateIngestTokenCommand` and `RevokeIngestTokenCommand`).

**Steps.**

1. List the project's token metadata with `GET /v1/projects/{p}/ingest-tokens`; identify the token and its consumers without exposing its value (`CiAdminEndpoints`).
2. Call `POST /v1/ingest-tokens/{t}/rotate`. Deliver the returned successor privately to each consumer and switch them before `overlap_ends_at`. The predecessor and successor both verify during the 24-hour overlap; the predecessor then expires, subject also to its existing expiry. A revoked, already rotated or expired token cannot be rotated (`RotateIngestTokenCommandHandler`; `CiAuthTests (ingest-token cases)`).
3. If compromise requires immediate cut-off, call `DELETE /v1/ingest-tokens/{t}` with the required idempotency key. Revocation is immediate for new exchanges and for the token itself, but **not** for `ci_producer` tokens already minted from it: those keep working until their `exp`, at most 60 minutes + 30 seconds (§2.3, where the signing-key kill step is the only early cut-off). Deleting a rotated predecessor ends its remaining overlap immediately. Repeated revocation or deletion after overlap returns success without a new audit event (`RevokeIngestTokenCommandHandler`; `CiAdminEndpoints`).

**Verify.** Use the existing token list and CI token exchange, without logging either credential, to verify the successor and the intended predecessor state. The exchange verifies the stored pepper version and token status (`ExchangeIngestTokenCommandHandler`). The existing integration cases cover overlap and immediate revoke (`CiAuthTests (ingest-token cases)`); they are references, not evidence of this deployment's drill.

**Rollback.** During an unrevoked overlap, consumers can temporarily use the predecessor while its existing expiry and overlap allow it. Once revoked or expired it cannot be restored by this route; issue or use a valid successor instead (`RotateIngestTokenCommandHandler`). Never assume revocation can be undone.

**Audit event.** `ingest_token.create`, `ingest_token.rotate`, `ingest_token.revoke`, and `ingest_token.exchange` are listed in `docs/security/audit-taxonomy.md`. Pepper changes themselves emit none today [OPEN O-G].

### 2.2 Customer CI credentials

Use the project ingest-token procedure in §2.1. There is no provider trust configuration or repository binding to rotate.

### 2.3 JWT signing key

**Where it lives.** `EVIDGATE__CI__SIGNINGKEY` (`infra/config-matrix.json`) is resolved by `CiSecrets` (`CiSecrets`); `CiProducerTokens` retains a single key and signs `ci_producer` JWTs with HS256 (`CiProducerTokens`). The name is the registered constant `SecretNames.CiSigningKey` (`SecretNames.All`), read once through `ISecretProvider` at options post-configuration (`CiAuthOptionsSecretPostConfigure.PostConfigure`); there is exactly one signing-key slot, with no previous-key field (`CiSecrets`), and the signing key and the validation parameters are built once and cached per process (`CiProducerTokens`). A changed value therefore takes effect only in a restarted `api`/`worker` process (`infra/config-matrix.json`). Session cookies are opaque and use HMAC peppers, not this JWT key (`migrations/0042_identity_login_resolvers.sql`); see §3.2.

**Who may rotate.** The platform environment owner through secret delivery and process restart [OPEN O-E].

**How long an issued token keeps working.** A `ci_producer` bearer is checked against its signature, issuer, audience and lifetime, not the current state of the parent ingest token. Revoking the parent prevents new exchanges; an already-issued token remains usable until expiry (at most 60 minutes plus the validation skew).

`[DECISION]` D-S108 §6 row 5 (owner, 2026-10-27) accepts this bearer residual on the condition that producer tokens stay bounded by `exp` ≤ 60 minutes and that this runbook gives the signing-key rotation kill step below (`docs/security/ingest-token-review.md`; the condition as recorded in `plans/features/11-security-compliance/11.2.1-a-fork-pull-request-cannot-reach.md`; coordinator queue row F51; the drill receiver stays 10.7.1-T4.2, [OPEN O-F]). Do not describe ingest-token revocation as an immediate producer-token kill.

**Emergency invalidation.** Rotating `EVIDGATE__CI__SIGNINGKEY` invalidates all outstanding producer tokens after every replica restarts. It is disruptive and does not stop minting from an active ingest token.

1. Revoke the affected ingest token (§2.1) to prevent new mints before changing the signing key.
2. Generate a new base64 HS256 key of at least 32 bytes (`CiAuthOptions`; `CiSecrets`) and set it as `EVIDGATE__CI__SIGNINGKEY` in the environment's secret delivery. Do not leave the old value configured as a fallback: there is no previous-key slot to hold it (`CiSecrets`).
3. Restart every `api` and `worker` instance (`infra/config-matrix.json`). The key and validation parameters are captured per process (`CiSecrets`; `CiProducerTokens`) and nothing reloads them, so an instance that has not restarted still accepts tokens signed with the old key. **The kill is complete only after every replica has restarted**; a partially restarted fleet keeps the old key valid on the remaining instances.
4. Expect every token minted under the old key to fail authentication immediately on a restarted instance: the validator trusts only the single configured key and the HS256 algorithm (`CiProducerTokens`). Every ingest-token consumer must re-exchange, and in-flight CI jobs lose their credential mid-run by design. Plan the consumer re-exchange before you rotate; this is not a quiet maintenance step.
5. Verify with an authorized exchange plus the intended ingest call, without logging either token or key; an old token being rejected is the intended result, not a defect, and a revoked/disabled parent must refuse the exchange.
6. Do not "roll back" to the old key to undo this: restoring it and restarting re-validates every token still inside its `exp`, which re-enables the credential the rotation was meant to kill. Treat a restore as a compromise decision, not a routine undo.

This invalidates tokens across all tenants and projects, with no overlap or per-token kill. It emits no rotation audit event [OPEN O-A, O-G]. The issuance-record gap is T3-G5 (F45); the staging drill remains 10.7.1-T4.2 [OPEN O-F].

**Steps.** [OPEN O-A] The general (non-emergency) rotation has no previous signing-key slot, overlap or live reload. The current procedure is a disruptive cutover:

1. Schedule a quiet window and arrange for CI consumers to re-exchange their credentials. In-flight producer tokens can fail after the cutover; their configured lifetime is at most 60 minutes plus the fixed 30-second validation skew (`CiAuthOptions`; `CiProducerTokens`; see the validity window above).
2. Update the signing key in secret delivery and restart affected processes. The key is captured in process (`CiProducerTokens`); do not claim a zero-downtime or dual-key procedure.
3. Re-exchange CI credentials and verify a newly issued token against the running deployment. Avoid logging either token or key. Boot validation checks only the values that are present (base64 and at least 32 bytes, `CiAuthOptionsValidator.Validate`), so a missing key boots with the exchange switched off rather than failing fast (`CiProducerTokens`): confirm the exchange answers before declaring the rotation done.

**Verify.** An authorized CI exchange and its intended ingest operation should succeed after restart; the existing exchange test is `CiAuthTests (CI-exchange cases)`.

**Rollback.** Restore the previous key through secret delivery and restart affected processes; tokens issued under the replacement key can then fail. Re-exchange again. Do not restore a compromised key.

**Audit event.** None emitted today for signing-key or secret-store rotation [OPEN O-G]; `ci_token.exchange` and `ingest_token.exchange` record exchanges, not the key change (`docs/security/audit-taxonomy.md`).

### 2.4 Webhook secrets

**Where it lives.** Only customer-configured outbound webhook signing is in scope (07.6.2). Identify the actual deployed signing and secret-storage path before attempting a drill [OPEN O-B].

**Who may rotate.** No implemented Evidgate rotation role or path exists for this class. The future receiver must define it.

**Steps.** Use only the outbound signing-secret procedure defined by 07.6.2. Do not guess a key name or an overlap window.

**Verify.** No deployed Evidgate secret rotation or receiver is available to verify. The contract alone is not a runnable receiver.

**Rollback.** Not applicable until an implemented mechanism and its rollback are defined.

**Audit event.** None emitted today for webhook-secret rotation [OPEN O-G].

### 2.5 Database credentials

**Where it lives.** `EVIDGATE__DATABASE__CONNECTIONSTRING` (app), `EVIDGATE__TASKS__SWEEPCONNECTIONSTRING` (worker sweep), `EVIDGATE__TASKS__WORKERCONNECTIONSTRING` (worker queue: claim/acknowledge/dispatch/prune on `public.task` and `public.outbox`; the Api does not hold it), and `EVIDGATE_MIGRATOR__CONNECTIONSTRING` (migrator owner) are secret rows in `infra/config-matrix.json`; readers are `src/BuildingBlocks/Persistence/Options/DatabaseOptions.cs`, `src/BuildingBlocks/Persistence/Tasks/TasksOptions.cs`, `src/BuildingBlocks/Persistence/Tasks/TaskWorkerDataSource.cs`, and `src/Evidgate.Migrator/MigratorOptions.cs`. The password of the psql-only `evidgate_platform_operator` role (`EVIDGATE_PLATFORM_OPERATOR_PASSWORD`) is a `dev-stack` secret row with no connection-string key and no reader: it is delivered to the person running the [kill-switch runbook](kill-switch.md) §5.2, so rotating it restarts nothing.

**Who may rotate.** The platform environment owner with the corresponding database-administration and secret-delivery authority [OPEN O-E]. Provisioning and managed-store choice are open [OPEN O-C].

**Steps.** The code binds one credential per role at process startup; it has no credential overlap mechanism. This is not a zero-downtime guarantee.

1. In a coordinated maintenance window, create the replacement credential in the database through its authorized administration mechanism. [INFERENCE] A password change for the same role affects existing connections on reconnect; do not assume a new credential leaves both passwords valid.
2. Update the corresponding connection-string secret in the environment's secret delivery and perform a coordinated rolling restart of affected processes. Restart alone does not guarantee uninterrupted connections during a one-password cutover.
3. Check each affected role's ordinary service operation and connection health without exposing the connection string. Include the worker queue connection, the worker sweep or deploy-time migrator if their credentials were changed. `evidgate_platform_operator` has no process to check: `scripts/env/bootstrap-db.sh <env> credentials` re-grants it and the next operator `psql` session uses the new password.

**Verify.** Use the existing readiness route `/internal/health/ready` (`docs/runbooks/environments.md`) and a normal operation that requires the changed role; for the migrator, use its established deployment procedure, not an ad-hoc SQL write. Test reference: `MigratorSecretResolutionTests`.

**Rollback.** If the previous credential is still valid and not compromised, restore its secret value and restart affected processes. If the database password was replaced, first restore the corresponding database credential through authorized administration; merely restoring the old secret cannot restore database access.

**Audit event.** None emitted today for database credential or secret-store rotation [OPEN O-G].

### 2.6 Provider keys

The GitHub App key and its custody procedure were removed by D-NO-GITHUB; only the provider keys below are in scope.

**Where it lives.** `ISecretProvider` is the secret seam; V1 uses `EnvironmentSecretProvider`, with managed-store replacement a future DI swap (`ISecretProvider`; `EnvironmentSecretProvider.GetSecretAsync`). `SecretNames.All` is the registry (`SecretNames.All`):

| Class | Registered keys and reader |
|---|---|
| Database | The four connection-string keys and readers in §2.5. |
| CI | `EVIDGATE__CI__SIGNINGKEY`, `EVIDGATE__CI__PEPPER`, `EVIDGATE__CI__PREVIOUSPEPPER`; `CiAuthOptionsSecretPostConfigure.PostConfigure` (§2.3 and §3.1). |
| Object store | `EVIDGATE__OBJECT__ACCESSKEY`, `EVIDGATE__OBJECT__SECRETKEY` (`infra/config-matrix.json`); `ObjectStorageOptionsSecretPostConfigure.PostConfigure`. |
| Human login | `EVIDGATE__OIDC__GOOGLE__CLIENTSECRET` (`infra/config-matrix.json`); `GoogleLoginSecrets`. |
| Session and invite | `EVIDGATE__SESSION__PEPPER`, `EVIDGATE__SESSION__PREVIOUSPEPPER`, `EVIDGATE__INVITE__PEPPER` (`infra/config-matrix.json`); `SessionOptionsSecretPostConfigure.PostConfigure` and `InviteSecrets.Verify and InviteOptionsSecretPostConfigure.PostConfigure`. See §3. |
**Who may rotate.** The platform environment owner controls environment secret delivery; the external provider's authorized administrator controls its credentials.

**Steps.**

1. For object-store or OIDC login client credentials, coordinate replacement with the relevant provider using its authorized administration path. No provider-specific overlap is established here.
2. Set the corresponding new values in secret delivery and restart affected processes. Object and login secrets are resolved into options (`ObjectStorageOptionsSecretPostConfigure.PostConfigure`, `GoogleLoginSecrets`); do not assume live reload or zero downtime.
3. Handle CI, session, invite and database keys only by their class-specific procedures above or in §3.

**Verify.** Check a normal object-store operation or the corresponding authorized login flow, without recording credentials; use the readiness route `/internal/health/ready` (`docs/runbooks/environments.md`). Provider registry tests are in `SecretProviderTests`.

**Rollback.** If still valid and not compromised, restore the prior provider credential and secret-delivery value, restart, and verify the same operation. Provider-side invalidation can prevent rollback; do not assume overlap.

**Audit event.** None emitted today for provider-key or secret-store changes [OPEN O-G]. Login or exchange events are not evidence of a credential rotation.

### 2.7 Data Protection key ring

**Where it lives.** `public.data_protection_key` (migration 0100), read and written only through `evidgate_resolve.data_protection_key_list/store`; the Api hosts share one ring through it. It protects the login, link and antiforgery cookies, not sessions (those are peppered HMACs, §3.2).

**Storage encryption (00 §5.7 S8).** Keys are stored as the framework writes them; at-rest protection is the database provider's storage encryption. The operator MUST run PostgreSQL (and its backups and snapshots) with storage encryption, and treat `data_protection_key` as secret-grade: restrict who can read a dump, and never copy the table to a lower environment. Application-level protection (`ProtectKeysWith*`) is an open item until 10.1.1 provisions a certificate or KMS key.

**Rotation.** The framework rolls the ring itself (90-day key lifetime, new key created before expiry). Nothing to do on a schedule. After a suspected leak, use the approved emergency maintenance path as `evidgate_owner`: migration `0100_identity_shared_keys` supplies the owner-only `data_protection_key_owner_backfill` policy and revokes runtime-role table access. Delete only the affected `public.data_protection_key` rows; preserve `public.login_state_replay`. Restart **every** Api and Worker instance together. Each process has its own cached ring: the normal framework refresh interval is approximately 24 hours, not a guaranteed retirement deadline; failed refreshes can extend stale-ring use (`KeyRingProvider.GetCurrentKeyRingCore` in ASP.NET Core). A restart of one replica does not retire the keys on others. Existing pending-login, link and antiforgery cookies then fail once, and users sign in again; sessions are unaffected. The replay set uses database time, so a replica clock cannot shorten or extend another replica's replay record. Storage encryption, authorized owner access and fleet-wide retirement remain drill-pending.

**Fail closed.** With the default `EVIDGATE__ISOLATION__SELFCHECK=Required`, `DataProtectionKeyRingStartupCheck` fails startup if the key table is unreachable. Explicit `Disabled` skips that startup check; it does not introduce an ephemeral or file-ring fallback (`IsolationSelfCheckOptions`, `DataProtectionKeyRepository`). A login callback whose replay write fails answers 503 `unavailable` + `Retry-After` and never calls the provider (`LoginEndpoints.Callback`, `LoginErrors.Unavailable`).

## 3. Pepper rotation — T4.2 written half

T5 [DECISION] requires new tokens to use the new pepper version while older versions verify until their last token expires (`plans/features/README.md`). This is a requirement, not proof that AC5 has been achieved. The shipped code supports **at most two live versions per class**: one current and one previous pepper for CI/ingest (`CiSecrets`) and sessions (`SessionSecrets`); invites have the current pepper only (`InviteSecrets.Verify and InviteOptionsSecretPostConfigure.PostConfigure`). There is no `pepper_vN` ring beyond that, and `HashWithVersion` returns `null` for any version that is neither current nor previous (`CiSecrets.HashWithVersion`), so a second rotation before the first window drains strands the older version. [OPEN O-D] Receiver for a wider CI window: 02.4.2 (no task exists today); sessions' two-candidate limit is [DECISION D-S85-D4] (`plans/features/02-identity-tenancy/02.3.2-sessions-and-revocation.md`), so drain it before rotating again. Project API tokens use the same CI pepper; their verifier is not shipped (§3.4).

### 3.1 CI and ingest-token pepper

`EVIDGATE__CI__PEPPER`, `EVIDGATE__CI__PEPPERVERSION`, `EVIDGATE__CI__PREVIOUSPEPPER`, and `EVIDGATE__CI__PREVIOUSPEPPERVERSION` are declared at `infra/config-matrix.json`. The platform environment owner performs the configuration change. The current pepper also protects project API tokens (§3.4).

1. Preserve the current pepper and version securely as the previous pair; configure a new current pepper and bump its version. Deploy both pairs together and restart affected processes: both pairs are decoded once in the `CiSecrets` constructor (`CiSecrets`). Copy the previous version **verbatim** from the version that signed the tokens being retired: `PreviousPepperVersion` defaults to 1 (`CiAuthOptions`) and the validator refuses a set previous pepper whose version equals the current one (`CiAuthOptionsValidator.Validate`). It never cross-checks the pair against the stored `pepper_version` rows, so a wrong previous version silently matches nothing. Its key checks inspect only values that are present (base64, at least 32 bytes) plus that equality rule: it does not require a previous pepper and is not a completeness gate (`CiAuthOptionsValidator.Validate`).
2. New tokens are hashed with the current pepper and stamped with its version (`CreateIngestTokenCommandHandler and RotateIngestTokenCommandHandler`), while exchange selects the stored version's pepper with `HashWithVersion` (`CiSecrets.HashWithVersion`; `ExchangeIngestTokenCommandHandler`). Use §2.1 to rotate **each** still-needed old-version token, privately distribute its successor, and rely on the 24-hour overlap only while the old row is still otherwise valid: the overlap constant is 24 hours (`RotateIngestTokenCommandHandler`), only an active token rotates (`RotateIngestTokenCommandHandler`), and the old row expires at `min(expires_at, overlap_ends_at)` (`src/Modules/Evidgate.Modules.Identity/Domain/CiBinding.cs`; `RotateIngestTokenCommandHandler`). Keep the previous pepper for every old token still needed during that window.
3. [OPEN O-D] There is no job that re-hashes old-version tokens and no sweep/report by pepper version. Account for all affected ingest and project API tokens before retirement; if completeness cannot be established, keep the previous pepper and escalate to 02.4.2 (a wider CI window has no task today) rather than silently cutting consumers off. After all old-version credentials are no longer needed and overlap has ended, remove the previous pepper and restart. A retired version returns `token_unknown` like an unknown token (`CiAuthTests (ingest-token cases)`; `ExchangeIngestTokenCommandHandler`).

**If the pepper is compromised** — a leaked configuration value, not a routine rotation — the drain ordering of step 3 does not apply. The pepper is key material, not merely a signature: every stored ingest-token and project API-token HMAC (§3.4) is computed under it (`CiSecrets.HashWithVersion`), and exchange compares the HMAC stored beside the token's own `pepper_version` (`ExchangeIngestTokenCommandHandler`), so while the leaked pair stays configured the ingest tokens hashed under it keep verifying, and a project API token's HMAC stays valid for whenever its bearer handler ships (§3.4).

1. Contain exposure first: revoke each affected ingest or project API token through its supported tenant API (§2.1, §3.4). Do **not** issue successors while the compromised pair is still current: `RotateIngestTokenCommandHandler` and `CreateApiTokenCommandHandler` use `CiSecrets.HashCurrent`, so those successors would be stranded by the next step.
2. Configure a fresh current pepper and version without carrying the compromised pair into `EVIDGATE__CI__PREVIOUSPEPPER`; remove any previous pair that names the compromised version and restart every affected instance. This is a deliberate containment interruption. `CiSecrets.HashWithVersion` then returns `null` for the retired version: ingest exchange returns `token_unknown`, and project API tokens under it will be unverifiable once their bearer handler ships (§3.4).
3. Only after every affected instance uses the new current version, create or rotate replacement credentials under that version, distribute them privately and verify the stored version plus successful exchange. A compromised **previous** pair must likewise be removed immediately, not retained for an availability drain.
4. Treat every window in which the compromised pepper still verified as an exposure window: tokens minted in it are suspect, so re-issue them rather than extend their life.

Never restore a compromised pepper, as current or as previous — the rule §2.3 and §2.6 already state for their classes.

**Verify and rollback.** Exercise the existing exchange for an old and a new token during the window without logging either; `CiAuthTests (ingest-token cases)` covers the overlap and its retired-pepper case covers current/previous selection and a retired version. Check successor use and all remaining consumers, including project API tokens (§3.4), before retirement. If the new deployment fails, restore the prior configuration and restart — except after a compromise, where the leaked value is never restored (above) — but any tokens already minted under the new version require that version to remain verifiable; do not blindly discard its pepper. No pepper rotation audit event is emitted today [OPEN O-G].

### 3.2 Session pepper

Session cookies are opaque and use a separate HMAC pepper (migration `0042_identity_login_resolvers`). The keys are `EVIDGATE__SESSION__PEPPER`, `EVIDGATE__SESSION__PEPPERVERSION`, `EVIDGATE__SESSION__PREVIOUSPEPPER`, and `EVIDGATE__SESSION__PREVIOUSPEPPERVERSION` (`SessionOptionsSecretPostConfigure.PostConfigure`, `SessionOptions`). Follow [environments](environments.md#changing-a-value): move today's pepper and version into the previous pair, set a new pepper and bumped version, and restart. **Retirement is a drain, not a date.** Migration `0045_identity_session_hardening` re-hashes an active session presenting a previous-pepper cookie to the current version in the same statement. Revoked, expired or disabled sessions, and sessions that never return, keep their old `pepper_version` until their own `absolute_expires_at`. Remove the previous pair only after no still-needed session depends on it: use the **latest**, never the oldest, affected `absolute_expires_at`, or establish that those rows have drained. `SessionOptionsValidator.Validate` rejects an incomplete previous pair, equal versions, equal peppers, or a previous version below 1; `SessionSecrets.Candidates` offers current then previous. There is one previous slot; drain it before another rotation (D-S85-D4 in `02.3.2-sessions-and-revocation`). No supported query or job reports sessions by pepper version. Migration `0041_identity_user` revokes direct session-table access from `evidgate_app`, `evidgate_operator` and `evidgate_worker_sched`; `0104_platform_worker_role` grants `evidgate_worker` task/outbox access, not session access. Emergency direct inspection requires approved `evidgate_owner` access through `session_owner_backfill` (`0041_identity_user`), not a routine on-call credential. Without that inspection, the configured `EVIDGATE__SESSION__ABSOLUTESECONDS` (default 604800 seconds, `SessionOptions`) is only a conservative lifetime bound, not evidence that old rows have drained [OPEN O-D]. Actual drain and executor authorization remain drill-pending. Restart to apply configuration. On rollback retain every version still needed by sessions; never restore a compromised pepper (§3.1). No session-pepper rotation audit event is emitted today [OPEN O-G].

### 3.3 Invite pepper

`EVIDGATE__INVITE__PEPPER` and `EVIDGATE__INVITE__PEPPERVERSION` (`infra/config-matrix.json`) have no previous-pepper overlap. `InviteOptions` records that exactly one pepper is configured and that bumping the version invalidates every pending invite (`InviteOptions`), and `InviteSecrets.Verify` accepts only the configured version (`InviteSecrets.Verify and InviteOptionsSecretPostConfigure.PostConfigure`); a version bump therefore fails pending invites closed, and an administrator must resend them, which re-mints under the current pepper (`migrations/0043_identity_invite_flow.sql`). The environment owner schedules the change, updates secret and version, restarts, and coordinates resends. Verify with an authorized invite flow without exposing the link. Restoring the old pair may make newly issued invites fail; do not blindly roll back. Receiver: 02.1.2 [OPEN O-D]. No invite-pepper rotation audit event is emitted today [OPEN O-G].

### 3.4 Project API tokens (same CI pepper)

Project API tokens (`evg_<prefix>_<secret>`) are HMACed with the same `EVIDGATE__CI__PEPPER` and stamped with `EVIDGATE__CI__PEPPERVERSION`: `CreateApiToken` calls `CiSecrets.HashCurrent` and stores the returned version (`CreateApiTokenCommandHandler`), and `api_token.pepper_version` is a required column (`ApiToken`; `migrations/0083_identity_api_token.sql`). Those credential columns are immutable, including `pepper_version` (`migrations/0083_identity_api_token.sql`), so a token cannot be re-peppered; revoke it and create a replacement.

No request path verifies these HMACs today: the domain exposes `MatchesHash` (`ApiToken`), but only `tests/Tenancy.IntegrationTests/ApiTokenFormatTests.cs` calls it, and the ingest-token exchange uses `HashWithVersion`; the bearer handler is 02.4.4-T2, not shipped. A pepper rotation therefore does not currently reject an API request, but a token minted under a version dropped from configuration becomes unverifiable once 02.4.4-T2 lands; its AC8 requires the stored `pepper_version` to keep verifying until the last such token expires (`plans/features/02-identity-tenancy/02.4.4-api-tokens-for-automation.md`). [OPEN O-D] Include project API tokens in the completeness count before retiring a previous CI pepper and keep the version until those tokens are revoked or expired. Receivers: 02.4.4-T2 (verifier; AC8) and 02.4.4-T3.2 (retired-version tests). No pepper-rotation audit event exists for this class either [OPEN O-G].

## 4. Open items and receivers

| ID | Gap and receiver |
|---|---|
| O-A | [OPEN] CI producer HS256 key is single-key, without overlap or reload; restart and re-exchange are disruptive. Receiver: 02.4.1-T3. Emergency invalidation of every outstanding producer token is nevertheless supported today: revoke the ingest token to stop new mints, then rotate `EVIDGATE__CI__SIGNINGKEY` and restart every replica (§2.3; owner decision D-S108 §6 row 5, 2026-10-27, coordinator queue row F51). |
| O-B | [OPEN] Verify outbound signing-secret delivery and rotation overlap before drilling it. Receiver: 07.6.2. |
| O-C | [OPEN] Database credentials bind at startup with no code-level overlap or rotation audit. Receivers: O3 (DECISIONS §8, cloud provider/managed store), 10.1.1-T3 (role/secret provisioning); drill: 10.7.1-T4.2. |
| O-D | [OPEN] Pepper windows are two-version at most: CI/ingest and session support one current and one previous pepper, invites current only, and there is no sweep/report by `pepper_version` and no re-hash of old-version tokens (their plaintext is gone, so rotation is the only path). Account for and rotate every ingest token and project API token before retiring a previous CI pepper; a second rotation before the window drains strands the older version. Receivers: 02.4.2 for a wider CI window (no task exists today); 02.4.4-T2 (API-token verifier; AC8) and 02.4.4-T3.2 (retired-version tests). Invite pepper has no overlap and a bump fails pending invites closed; receiver: 02.1.2. |
| O-E | [OPEN] `/internal/operator/*` is absent from OpenAPI and no audited operator rotation path exists. Current platform actor is the environment owner; tenant token mutation requires `ingest_token:write`. Receiver: 02.8.1. |
| O-F | [OPEN] Staging availability and completed drill/non-author evidence are not verified by this desk check; they remain drill-pending. Receivers: 10.7.1-T4.2 (drill evidence), 10.1.1-T3 (environment bootstrap). |
| O-G | [OPEN] No audit action is emitted today for signing-key, DB credential, pepper or secret-store rotation. Existing token actions are not rotation audit for those secrets. Receiver: 11.5.1. |
| O-H | Closed: App key custody, cadence and drill removed by D-NO-GITHUB (owner 2026-11-27). |
| O-I | [OPEN] No targeted producer-token kill: revoke the ingest token to stop new exchanges, then follow §2.3 for fleet-wide invalidation. T3-G2 drill: 10.7.1-T4.2; T3-G5 issuance-record gap: F45. |


> **Not possible yet:** zero-downtime producer signing-key rotation: `CiProducerTokens` has one captured key and no overlap. 02.4.1-T3 explicitly receives the decision between overlap and a supported disruptive cutover; it does not establish that overlap already exists.
>
> **Not possible yet:** webhook-secret rotation and its zero-downtime drill. Outbound signing is 07.6.2-T1.2 and endpoint CRUD is 07.6.2-T2.1. Those receivers must define usable secret delivery and any overlap before this procedure can run.
>
> **Not possible yet:** live API-token authentication checks; 02.4.4-T2 enables the verifier, with retired-version coverage in 02.4.4-T3.2. Do not report a successful API-token rotation drill from token creation alone.
>
> **Not possible yet:** a supported completeness report by pepper version or a third concurrent CI pepper. The 02.4.2 receiver has no defined enabling task; owner planning is required. The session two-candidate limit is D-S85-D4, not a promise to widen it. Approved `evidgate_owner` inspection is an emergency path, not a supported runtime-role report.
>
> **Not possible yet:** targeted early producer-token invalidation, an audited operator HTTP rotation endpoint, or an HTTP Data Protection reset. No enabling task is defined for those mechanisms; T3-G5 tracks the ingest-producer issuance-record gap, 02.8.1 is the operator-access receiver, and 10.7.1-T4.2 is the drill receiver, not an implementation of these mechanisms. The fleet-wide signing-key cutover and approved `evidgate_owner` maintenance described above remain the current emergency paths.

## 5. Staging drill — not executed

[OPEN O-F] AC5 requires each class below to rotate in staging with no downtime and no lost acknowledged event, and old pepper versions to verify until their last token expires (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md`). **Drill-pending:** verify staging availability and obtain actual drill/non-author execution evidence; none is supplied by this desk check. The gaps in §4, especially O-A through O-D, must be resolved before claiming AC5. Do not treat the following checklist as results.

- [ ] Provision staging through 10.1.1-T3; arrange a non-author executor and synthetic tenant identifiers only.
- [ ] Establish a continuous-ingest probe, record acknowledged event identifiers and timestamps without credentials, and compare acknowledgements with durable outcomes throughout each rotation; show no probe gap and no lost acknowledged event.
- [ ] Rotate an ingest token; check both sides of the supported overlap, successor use, and immediate revoke on a separate synthetic token.
- [ ] Rotate the CI producer signing key only after O-A supplies a safe overlap mechanism; check old and new tokens without losing acknowledged events. The emergency invalidation in §2.3 is disruptive by design and does not satisfy this zero-downtime step.
- [ ] Rotate outbound webhook secrets only after O-B delivers the respective implemented paths and overlap; exercise delivery and verification.
- [ ] Rotate each applicable database role credential after O-C provisioning and a safe cutover mechanism exist; verify app, sweep and migrator roles as applicable.
- [ ] Rotate applicable object-store and login provider keys via the provider seam; exercise the corresponding ordinary operation.
- [ ] Rotate the CI/ingest pepper: prove new ingest and project API tokens use the new version, old tokens verify throughout their required lifetime, rotate affected tokens and retire the previous pepper only after they are accounted for; do not start a second rotation before the previous window drains (only one previous slot exists). Exercise session and invite pepper procedures separately, including the invite failure boundary.
- [ ] Record non-secret evidence and executor corrections in this runbook; only then evaluate AC1 and AC5. A successful probe cannot by itself prove unexercised consumers safe.

Desk check: 2026-11-21, wave fd69e03b; not checkable without a live environment: staging and drill/non-author evidence; executor grants; secret-store mappings and fingerprints; all-replica restarts; old/new consumer outcomes and continuous-ingest loss; provider/DB rollback validity; storage and backup encryption.
