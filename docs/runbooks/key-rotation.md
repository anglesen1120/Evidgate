# Key rotation

Status: **10.7.1-T4.1 written; the pepper section (§3) is the written half of T4.2, code-audited 2026-11-09: it records the shipped two-version window and that project API tokens share the CI pepper without a verifier yet.** The staging drill has not run, and a non-author has not executed this runbook. AC1 and AC5 are not met. [OPEN O-F] §2.3 additionally records the producer-token validity window and the signing-key kill step required by owner decision D-S108 §6 row 5 (owner, 2026-10-27; coordinator queue row F51).

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`.

## 1. Scope, roles and rules

This runbook covers the six key classes in 10.7.1-T4.1 and the T5 pepper procedure. It describes current behavior, not a promise of zero downtime. A **secret** is a credential or pepper; a **pepper version** identifies the key used to HMAC a stored token; a **token overlap** is the period in which a rotated token and its successor can both verify. Token overlap and pepper overlap are different windows.

- Never put secret values in this runbook, logs, tickets, terminal history, drill evidence, or screenshots. Use the environment's secret delivery mechanism; do not print responses containing one-time credentials. Record only non-secret identifiers and outcomes.
- Never perform ad-hoc `psql` writes. Use the supported tenant API for token and binding mutations; use the environment's authorized database administration and secret-delivery procedures for credentials. Do not invent an `/internal/operator/*` rotation endpoint: operational routes are absent from the contract (`contracts/openapi.yaml:15-17`), and no audited operator rotation path exists. [OPEN O-E]
- For platform secrets, the V1 solo platform environment owner changes the secret store/deployment environment and restarts the affected processes; managed-store choice remains open. For tenant ingest tokens, the caller needs `ingest_token:write` (`src/Modules/Evidgate.Modules.Identity/Application/Commands/IngestTokenCommands.cs:138,231`). Binding administration uses its own authorized tenant API. Separate duties and operator authorization are receivers of O-E.
- [environments](environments.md) owns how a value is changed, and its §2 hands ingest-token, DB-credential, JWT-key and HMAC-pepper rotation to this runbook (`docs/runbooks/environments.md:165-170`). The [GitHub App private-key custody stub](github-app-key-custody.md) holds the App key procedure documented in §2.6: that is the stub's documented procedure, not yet drilled, and it is not proof of deployed webhook secrets.

## 2. Key classes

### 2.1 Ingest tokens

**Where it lives.** A generated tenant token is returned once; the list API returns metadata only (`src/Modules/Evidgate.Modules.Identity/Endpoints/CiAdminEndpoints.cs:45-55`). Its HMAC uses `EVIDGATE__CI__PEPPER` and its version uses `EVIDGATE__CI__PEPPERVERSION` (`infra/config-matrix.json:200,220`; reader `src/Modules/Evidgate.Modules.Identity/Ci/CiSecrets.cs:25-33,43-63`). Pepper rotation is §3.1.

**Who may rotate.** A tenant principal with `ingest_token:write` (`src/Modules/Evidgate.Modules.Identity/Application/Commands/IngestTokenCommands.cs:60,142,235`).

**Steps.**

1. List the project's token metadata with `GET /v1/projects/{p}/ingest-tokens`; identify the token and its consumers without exposing its value (`CiAdminEndpoints.cs:49-51`).
2. Call `POST /v1/ingest-tokens/{t}/rotate`. Deliver the returned successor privately to each consumer and switch them before `overlap_ends_at`. The predecessor and successor both verify during the 24-hour overlap; the predecessor then expires, subject also to its existing expiry. A revoked, already rotated or expired token cannot be rotated (`src/Modules/Evidgate.Modules.Identity/Application/Commands/IngestTokenCommands.cs:132-150,174-196,200-220`; `tests/Tenancy.IntegrationTests/CiAuthTests.IngestTokens.cs:85-133`).
3. If compromise requires immediate cut-off, call `DELETE /v1/ingest-tokens/{t}` with the required idempotency key. Revocation is immediate for new exchanges and for the token itself, but **not** for `ci_producer` tokens already minted from it: those keep working until their `exp`, at most 60 minutes + 30 seconds (§2.3, where the signing-key kill step is the only early cut-off). Deleting a rotated predecessor ends its remaining overlap immediately. Repeated revocation or deletion after overlap returns success without a new audit event (`IngestTokenCommands.cs:224-267`; `CiAdminEndpoints.cs:149-150`).

**Verify.** Use the existing token list and CI token exchange, without logging either credential, to verify the successor and the intended predecessor state. The exchange verifies the stored pepper version and token status (`src/Modules/Evidgate.Modules.Identity/Application/Commands/ExchangeCommands.cs:214-233`). The existing integration cases cover overlap and immediate revoke (`tests/Tenancy.IntegrationTests/CiAuthTests.IngestTokens.cs:85-133`); they are references, not evidence of this deployment's drill.

**Rollback.** During an unrevoked overlap, consumers can temporarily use the predecessor while its existing expiry and overlap allow it. Once revoked or expired it cannot be restored by this route; issue or use a valid successor instead (`IngestTokenCommands.cs:174-180,254-267`). Never assume revocation can be undone.

**Audit event.** `ingest_token.create`, `ingest_token.rotate`, `ingest_token.revoke`, and `ingest_token.exchange` are listed in `docs/security/audit-taxonomy.md:107,121-123`. Pepper changes themselves emit none today [OPEN O-G].

### 2.2 CI OIDC trust

**Where it lives.** Trust is configuration, not a held GitHub signing secret: `EVIDGATE__CI__GITHUBOIDCISSUER`, `EVIDGATE__CI__GITHUBOIDCAUDIENCE`, and `EVIDGATE__CI__GITHUBJWKSURI` (`infra/config-matrix.json:237,257,277`); issued producer tokens have separate `EVIDGATE__CI__ISSUER` and `EVIDGATE__CI__AUDIENCE` (`infra/config-matrix.json:94,114`; reader `src/Modules/Evidgate.Modules.Identity/Options/CiAuthOptions.cs:24-30,50-58`). The validator selects GitHub's published key by `kid`, pins RS256, and retrieves JWKS at runtime (`src/Modules/Evidgate.Modules.Identity/Ci/GitHubOidc.cs:82-95,203-215,225-250`).

**Who may rotate.** GitHub controls its published signing keys. The platform environment owner controls local trust configuration; an authorized tenant administrator manages the project binding.

**Steps.**

1. For upstream key rotation, do not generate or upload a local GitHub signing secret. Allow the existing JWKS provider to fetch the published keys; it has a ten-minute cache and throttles unknown-key refresh to 30 seconds (`GitHubOidc.cs:203-215,225-250`). This is not a locally controlled overlap guarantee.
2. If the binding must be disabled, use `DELETE /v1/ci-bindings/{b}`. To re-enable or update it, use `PATCH /v1/ci-bindings/{b}` with `If-Match`; use the existing project binding list to inspect it (`src/Modules/Evidgate.Modules.Identity/Endpoints/CiAdminEndpoints.cs:30-43`). A disabled binding refuses exchange, and a pending binding needs its challenge (`src/Modules/Evidgate.Modules.Identity/Application/Commands/ExchangeCommands.cs:60-80`); `ci_producer` tokens minted before the disable keep working until their `exp` (at most 60 minutes + 30 seconds, §2.3). Do not disable a live binding as a routine key-refresh step.
3. If issuer, audience or JWKS configuration must change, have the environment owner coordinate the deployment and restart the affected process, using [environments](environments.md#changing-a-value); preserve the prior configuration for rollback.

**Verify.** Run a real authorized CI exchange through the existing `/v1/ci/token` endpoint without logging the token, and check the intended binding in the list. Existing acceptance and refusal coverage is `tests/Tenancy.IntegrationTests/CiAuthTests.Exchange.cs:17-58,86-140,232-286`. A successful exchange is not proof that all workflows are unaffected.

**Rollback.** Restore the prior trusted configuration and restart, or restore the binding state with the versioned PATCH when authorized; do not restore a known-compromised trust setting.

**Audit event.** `ci_token.exchange`, `ci_binding.create`, `ci_binding.update`, and `ci_binding.disable` (`docs/security/audit-taxonomy.md:106,108-110`); no Evidgate event is emitted for GitHub's upstream key rotation today [OPEN O-G].

### 2.3 JWT signing key

**Where it lives.** `EVIDGATE__CI__SIGNINGKEY` (`infra/config-matrix.json:74`) is resolved by `CiSecrets` (`src/Modules/Evidgate.Modules.Identity/Ci/CiSecrets.cs:25-33,199-214`); `CiProducerTokens` retains a single key and signs `ci_producer` JWTs with HS256 (`src/Modules/Evidgate.Modules.Identity/Ci/CiProducerTokens.cs:48-61,101-112`). The name is the registered constant `SecretNames.CiSigningKey` (`src/BuildingBlocks/SharedKernel/Secrets/SecretNames.cs:22-23`), read once through `ISecretProvider` at options post-configuration (`CiSecrets.cs:199-214`); there is exactly one signing-key slot, with no previous-key field (`CiSecrets.cs:21-23`), and the signing key and the validation parameters are built once and cached per process (`CiProducerTokens.cs:60-63,115-129`). A changed value therefore takes effect only in a restarted `api`/`worker` process (`infra/config-matrix.json:74-76`). Session cookies are opaque and use HMAC peppers, not this JWT key (`migrations/0042_identity_login_resolvers.sql:6-13`); see §3.2.

**Who may rotate.** The platform environment owner through secret delivery and process restart [OPEN O-E].

**How long an issued token keeps working.** A `ci_producer` token is a bearer token: validation checks only the signature against the one configured key, the pinned algorithm, issuer, audience, `nbf`/`exp` and the token's own claims, and performs no database, ingest-token-status or binding-status lookup (`src/Modules/Evidgate.Modules.Identity/Ci/CiProducerTokens.cs:115-129,136-166`; its only caller is `CiProducerAuthenticator.AuthenticateAsync`, `CiProducerTokens.cs:256-272`, which takes the org, project and permission set from the claims and from nowhere else). **Revoking the parent ingest token or disabling its binding therefore stops only new exchanges** (`src/Modules/Evidgate.Modules.Identity/Application/Commands/ExchangeCommands.cs:60-80,223-229` refuse a disabled binding and a revoked or expired token at exchange time); a token minted before the change keeps working until its `exp`. Its lifetime is `EVIDGATE__CI__TOKENLIFETIMEMINUTES` (1-60, default 60; `src/Modules/Evidgate.Modules.Identity/Options/CiAuthOptions.cs:32-34`; `infra/config-matrix.json:134`), stamped as `exp` at mint time (`CiProducerTokens.cs:75-76,101-112`) and never refreshed, so a job that outlives it must exchange again (`CiProducerTokens.cs:44-48`). Validation accepts a token up to `exp` plus a fixed 30-second clock skew (`CiProducerTokens.cs:126,276-305`): **the worst case is 60 minutes + 30 seconds after minting, and a revoke or binding disable never shortens that window.** `EVIDGATE__CI__MAXSKEWSECONDS` (default 60; `CiAuthOptions.cs:62-64`; `infra/config-matrix.json:297`) applies to the inbound GitHub OIDC token, not to this one.

`[DECISION]` D-S108 §6 row 5 (owner, 2026-10-27) accepts this bearer residual on the condition that producer tokens stay bounded by `exp` ≤ 60 minutes and that this runbook gives the signing-key rotation kill step below (`docs/security/ingest-token-review.md:169,190`; the condition as recorded in `plans/features/11-security-compliance/11.2.1-a-fork-pull-request-cannot-reach.md:134`; coordinator queue row F51; the drill receiver stays 10.7.1-T4.2, [OPEN O-F]). Do not describe revocation or binding disable as an immediate producer-token kill.

**Emergency invalidation — the signing-key kill step.** Rotating `EVIDGATE__CI__SIGNINGKEY` is the supported way to invalidate **every** outstanding producer token at once. It is a deliberate, disruptive emergency cutover, not the planned rotation under Steps below, and the key change alone does not stop re-minting: both exchange paths mint after validating the parent (`src/Modules/Evidgate.Modules.Identity/Application/Commands/ExchangeCommands.cs:99,231`), so a still-active binding or an unrevoked ingest token would receive a fresh token signed with the new key seconds later.

1. Stop the issuing side first, or the rotation leaks: revoke the affected ingest token (`DELETE /v1/ingest-tokens/{t}`, §2.1) and/or disable the binding (`DELETE /v1/ci-bindings/{b}`, §2.2). Neither ends an already-minted producer token, but both stop new mints (`ExchangeCommands.cs:60-80,223-229`), which is what makes the key rotation final.
2. Generate a new base64 HS256 key of at least 32 bytes (`CiAuthOptions.cs:21-22`; `src/Modules/Evidgate.Modules.Identity/Ci/CiSecrets.cs:17,85-102,171-189`) and set it as `EVIDGATE__CI__SIGNINGKEY` in the environment's secret delivery. Do not leave the old value configured as a fallback: there is no previous-key slot to hold it (`CiSecrets.cs:21-23`).
3. Restart every `api` and `worker` instance (`infra/config-matrix.json:74-76`). The key and validation parameters are captured per process (`CiSecrets.cs:25-33`; `CiProducerTokens.cs:60-63,115-129`) and nothing reloads them, so an instance that has not restarted still accepts tokens signed with the old key. **The kill is complete only after every replica has restarted**; a partially restarted fleet keeps the old key valid on the remaining instances.
4. Expect every token minted under the old key to fail authentication immediately on a restarted instance: the validator trusts only the single configured key and the HS256 algorithm (`CiProducerTokens.cs:119-123,138-148`). Every binding and ingest-token consumer must re-exchange, and in-flight CI jobs lose their credential mid-run by design. Plan the consumer re-exchange before you rotate; this is not a quiet maintenance step.
5. Verify with an authorized exchange plus the intended ingest call, without logging either token or key; an old token being rejected is the intended result, not a defect, and a revoked/disabled parent must refuse the exchange.
6. Do not "roll back" to the old key to undo this: restoring it and restarting re-validates every token still inside its `exp`, which re-enables the credential the rotation was meant to kill. Treat a restore as a compromise decision, not a routine undo.

Limits of the step: it is all-or-nothing across tenants, projects and bindings, has no overlap window, and emits no audit event ([OPEN O-A], [OPEN O-G]). It also cannot be scoped to one binding or one token, because there is no per-token issuance or revocation record for `ingest_token`-minted producers (`ci_token_issue` records only GitHub OIDC exchanges and has no revocation column, `migrations/0017_identity_ci_token_issue.sql:3-8,10-33`): the **T3-G5** gap (no issuance record, carried by coordinator queue row **F45**) is why no narrower kill exists, and the optional `ci_producer` status check that would allow one has no task or queue row yet. The **T3-G2** runbook half is recorded in this section; the drill half stays with 10.7.1-T4.2 [OPEN O-F] (`docs/security/ingest-token-review.md:190,193`).

**Steps.** [OPEN O-A] The general (non-emergency) rotation has no previous signing-key slot, overlap or live reload. The current procedure is a disruptive cutover:

1. Schedule a quiet window and arrange for CI consumers to re-exchange their credentials. In-flight producer tokens can fail after the cutover; their configured lifetime is at most 60 minutes plus the fixed 30-second validation skew (`src/Modules/Evidgate.Modules.Identity/Options/CiAuthOptions.cs:21-34`; `CiProducerTokens.cs:126`; see the validity window above).
2. Update the signing key in secret delivery and restart affected processes. The key is captured in process (`CiProducerTokens.cs:58-61`); do not claim a zero-downtime or dual-key procedure.
3. Re-exchange CI credentials and verify a newly issued token against the running deployment. Avoid logging either token or key. Boot validation checks only the values that are present (base64 and at least 32 bytes, `src/Modules/Evidgate.Modules.Identity/Ci/CiSecrets.cs:171-189`), so a missing key boots with the exchange switched off rather than failing fast (`CiProducerTokens.cs:65-66`): confirm the exchange answers before declaring the rotation done.

**Verify.** An authorized CI exchange and its intended ingest operation should succeed after restart; the existing exchange test is `tests/Tenancy.IntegrationTests/CiAuthTests.Exchange.cs:17-58`.

**Rollback.** Restore the previous key through secret delivery and restart affected processes; tokens issued under the replacement key can then fail. Re-exchange again. Do not restore a compromised key.

**Audit event.** None emitted today for signing-key or secret-store rotation [OPEN O-G]; `ci_token.exchange` and `ingest_token.exchange` record exchanges, not the key change (`docs/security/audit-taxonomy.md:106-107`).

### 2.4 Webhook secrets

**Where it lives.** The contract describes an inbound GitHub webhook signature (`contracts/openapi.yaml:3813-3828`), but no inbound server receiver or configured secret key exists today. Outbound webhook signing is planned, not implemented. There is no current config-matrix key or reader to cite. [OPEN O-B]

**Who may rotate.** No implemented Evidgate rotation role or path exists for this class. The future receiver must define it.

**Steps.** None today. Do not create a guessed environment key, overlap window, or procedure from the contract or the GitHub App custody stub. 03.4.1-T2 must define the inbound key name and overlap; 07.6.2 owns outbound signing secrets.

**Verify.** No deployed Evidgate secret rotation or receiver is available to verify. The contract alone is not a runnable receiver.

**Rollback.** Not applicable until an implemented mechanism and its rollback are defined.

**Audit event.** None emitted today for webhook-secret rotation [OPEN O-G].

### 2.5 Database credentials

**Where it lives.** `EVIDGATE__DATABASE__CONNECTIONSTRING` (app), `EVIDGATE__TASKS__SWEEPCONNECTIONSTRING` (worker sweep), `EVIDGATE__TASKS__WORKERCONNECTIONSTRING` (worker queue: claim/acknowledge/dispatch/prune on `public.task` and `public.outbox`; the Api does not hold it), and `EVIDGATE_MIGRATOR__CONNECTIONSTRING` (migrator owner) are secret rows in `infra/config-matrix.json`; readers are `src/BuildingBlocks/Persistence/Options/DatabaseOptions.cs`, `src/BuildingBlocks/Persistence/Tasks/TasksOptions.cs`, `src/BuildingBlocks/Persistence/Tasks/TaskWorkerDataSource.cs`, and `src/Evidgate.Migrator/MigratorOptions.cs`.

**Who may rotate.** The platform environment owner with the corresponding database-administration and secret-delivery authority [OPEN O-E]. Provisioning and managed-store choice are open [OPEN O-C].

**Steps.** The code binds one credential per role at process startup; it has no credential overlap mechanism. This is not a zero-downtime guarantee.

1. In a coordinated maintenance window, create the replacement credential in the database through its authorized administration mechanism. [INFERENCE] A password change for the same role affects existing connections on reconnect; do not assume a new credential leaves both passwords valid.
2. Update the corresponding connection-string secret in the environment's secret delivery and perform a coordinated rolling restart of affected processes. Restart alone does not guarantee uninterrupted connections during a one-password cutover.
3. Check each affected role's ordinary service operation and connection health without exposing the connection string. Include the worker queue connection, the worker sweep or deploy-time migrator if their credentials were changed.

**Verify.** Use the existing readiness route `/internal/health/ready` (`docs/runbooks/environments.md:266-269`) and a normal operation that requires the changed role; for the migrator, use its established deployment procedure, not an ad-hoc SQL write. Test reference: `tests/Migrator.IntegrationTests/MigratorSecretResolutionTests.cs:33-71`.

**Rollback.** If the previous credential is still valid and not compromised, restore its secret value and restart affected processes. If the database password was replaced, first restore the corresponding database credential through authorized administration; merely restoring the old secret cannot restore database access.

**Audit event.** None emitted today for database credential or secret-store rotation [OPEN O-G].

### 2.6 Provider keys and GitHub App key

**Where it lives.** `ISecretProvider` is the secret seam; V1 uses `EnvironmentSecretProvider`, with managed-store replacement a future DI swap (`src/BuildingBlocks/SharedKernel/Secrets/ISecretProvider.cs:3-17`; `src/BuildingBlocks/SharedKernel/Secrets/EnvironmentSecretProvider.cs:17-39`). `SecretNames.All` is the registry (`src/BuildingBlocks/SharedKernel/Secrets/SecretNames.cs:14-77`):

| Class | Registered keys and reader |
|---|---|
| Database | The four connection-string keys and readers in §2.5. |
| CI | `EVIDGATE__CI__SIGNINGKEY`, `EVIDGATE__CI__PEPPER`, `EVIDGATE__CI__PREVIOUSPEPPER`; `CiSecrets.cs:199-214` (§2.3 and §3.1). |
| Object store | `EVIDGATE__OBJECT__ACCESSKEY`, `EVIDGATE__OBJECT__SECRETKEY` (`infra/config-matrix.json:1125,1145`); `src/BuildingBlocks/ObjectStorage/ObjectStorageOptionsSecretPostConfigure.cs:13-25`. |
| Human login | `EVIDGATE__OIDC__GOOGLE__CLIENTSECRET`, `EVIDGATE__OAUTH__GITHUB__CLIENTSECRET` (`infra/config-matrix.json:472,707`); `src/Modules/Evidgate.Modules.Identity/Login/Google/GoogleLoginSecrets.cs:13-24` and `src/Modules/Evidgate.Modules.Identity/Login/GitHub/GitHubLoginSecrets.cs:13-24`. |
| Session and invite | `EVIDGATE__SESSION__PEPPER`, `EVIDGATE__SESSION__PREVIOUSPEPPER`, `EVIDGATE__INVITE__PEPPER` (`infra/config-matrix.json:896,982,1022`); `src/Modules/Evidgate.Modules.Identity/Sessions/SessionSecrets.cs:115-130` and `src/Modules/Evidgate.Modules.Identity/Invites/InviteSecrets.cs:95-108`. See §3. |
| GitHub App private key / client secret | Not a registered `SecretNames.All` key; the [custody stub](github-app-key-custody.md) documents the procedure, summarised below. |

**GitHub App private key / client secret.** This class is not registered in `SecretNames.All`; the
[custody stub](github-app-key-custody.md) owns it, and the bullets below summarise its documented
procedure instead of copying it. That is the stub's documented procedure, not yet drilled [OPEN O-H].

- **Where and who.** The key and the client secret are versioned secret-store names
  (`github_app_private_key_vN`, `github_app_client_secret_vN`) resolved only through
  `ISecretProvider` (stub §1 and §3); the custodian role is defined there and in ADR-0007, while
  who holds it and the second organization owner stay open (stub §2; DECISIONS §8 O11).
- **Steps.** Follow custody stub §4 steps 1-8 (`docs/runbooks/github-app-key-custody.md:113-128`);
  the client secret follows the same shape (stub `:130-132`).
- **Verify.** An installation-token mint must succeed and
  `github_token_mint_total{result="error"}` must stay flat (stub §4 step 4 and §6,
  `docs/runbooks/github-app-key-custody.md:117-118,180`).
- **Rollback.** Keep the old key at GitHub and the superseded secret version in the store until
  that mint verifies: the deletions are stub §4 steps 6-7 (`:123-127`), and GitHub refuses to
  delete an App's only key — "you must regenerate a new key before you can delete the existing
  key" (stub `:123-126`).
- **Audit event.** None emitted today [OPEN O-G]; the stub §7 log is an operator record, not an
  Evidgate audit event (stub `:186-190`).

**Who may rotate.** The platform environment owner controls environment secret delivery; the external provider's authorized administrator controls its credentials. GitHub App private-key custodian and rotation cadence are open [OPEN O-H] (§2.6).

**Steps.**

1. For object-store or Google/GitHub login client credentials, coordinate replacement with the relevant provider using its authorized administration path. No provider-specific overlap is established here.
2. Set the corresponding new values in secret delivery and restart affected processes. Object and login secrets are resolved into options (`ObjectStorageOptionsSecretPostConfigure.cs:20-25`, `GoogleLoginSecrets.cs:19-24`, `GitHubLoginSecrets.cs:19-24`); do not assume live reload or zero downtime.
3. Handle CI, session, invite and database keys only by their class-specific procedures above or in §3. For the GitHub App key and client secret, follow the custody stub's documented procedure at §2.6; it has not been drilled yet.

**Verify.** Check a normal object-store operation or the corresponding authorized login flow, without recording credentials; use the readiness route `/internal/health/ready` (`docs/runbooks/environments.md:266-269`). Provider registry tests are in `tests/Architecture.Tests/SecretProviderTests.cs:78-87`.

**Rollback.** If still valid and not compromised, restore the prior provider credential and secret-delivery value, restart, and verify the same operation. Provider-side invalidation can prevent rollback; do not assume overlap.

**Audit event.** None emitted today for provider-key or secret-store changes [OPEN O-G]. Login or exchange events are not evidence of a credential rotation.

### 2.7 Data Protection key ring

**Where it lives.** `public.data_protection_key` (migration 0100), read and written only through `evidgate_resolve.data_protection_key_list/store`; the Api hosts share one ring through it. It protects the login, link and antiforgery cookies, not sessions (those are peppered HMACs, §3.2).

**Storage encryption (00 §5.7 S8).** Keys are stored as the framework writes them; at-rest protection is the database provider's storage encryption. The operator MUST run PostgreSQL (and its backups and snapshots) with storage encryption, and treat `data_protection_key` as secret-grade: restrict who can read a dump, and never copy the table to a lower environment. Application-level protection (`ProtectKeysWith*`) is an open item until 10.1.1 provisions a certificate or KMS key.

**Rotation.** The framework rolls the ring itself (90-day key lifetime, new key created before expiry). Nothing to do on a schedule. After a suspected leak, delete the rows as the owner role and restart **every** Api and Worker instance together: each process caches the ring it read, so restarting one replica leaves the retired keys in use on the others (they can hold the old ring for up to 24 hours). Existing pending-login, link and antiforgery cookies then fail once — including on a replica that never saw the cookie — and users sign in again; sessions are unaffected. The login replay set shares the same table family but is judged entirely on database time, so replicas need no clock sync for it: a replica whose clock is off can neither shorten nor extend another replica's record.

**Fail closed.** A host whose key table is unreachable at start refuses to start; it never falls back to an ephemeral or file ring. A login callback whose replay write fails answers 503 `unavailable` + `Retry-After` and never calls the provider.

## 3. Pepper rotation — T4.2 written half

T5 [DECISION] requires new tokens to use the new pepper version while older versions verify until their last token expires (`plans/features/README.md:49`). This is a requirement, not proof that AC5 has been achieved. The shipped code supports **at most two live versions per class**: one current and one previous pepper for CI/ingest (`src/Modules/Evidgate.Modules.Identity/Ci/CiSecrets.cs:21-23,53-63`) and sessions (`src/Modules/Evidgate.Modules.Identity/Sessions/SessionSecrets.cs:22-23,64-85`); invites have the current pepper only (`src/Modules/Evidgate.Modules.Identity/Invites/InviteSecrets.cs:24,56-68`). There is no `pepper_vN` ring beyond that, and `HashWithVersion` returns `null` for any version that is neither current nor previous (`CiSecrets.cs:53-63`), so a second rotation before the first window drains strands the older version. [OPEN O-D] Receiver for a wider CI window: 02.4.2 (no task exists today); sessions' two-candidate limit is [DECISION D-S85-D4] (`plans/features/02-identity-tenancy/02.3.2-sessions-and-revocation.md:130`), so drain it before rotating again. Project API tokens use the same CI pepper; their verifier is not shipped (§3.4).

### 3.1 CI and ingest-token pepper

`EVIDGATE__CI__PEPPER`, `EVIDGATE__CI__PEPPERVERSION`, `EVIDGATE__CI__PREVIOUSPEPPER`, and `EVIDGATE__CI__PREVIOUSPEPPERVERSION` are declared at `infra/config-matrix.json:200,220,240,260`. The platform environment owner performs the configuration change. The current pepper also protects binding challenges (`src/Modules/Evidgate.Modules.Identity/Options/CiAuthOptions.cs:36-48`; `src/Modules/Evidgate.Modules.Identity/Application/Commands/CiBindingCommands.cs:72-86`) and project API tokens (§3.4).

1. Preserve the current pepper and version securely as the previous pair; configure a new current pepper and bump its version. Deploy both pairs together and restart affected processes: both pairs are decoded once in the `CiSecrets` constructor (`src/Modules/Evidgate.Modules.Identity/Ci/CiSecrets.cs:25-33`). Copy the previous version **verbatim** from the version that signed the tokens being retired: `PreviousPepperVersion` defaults to 1 (`src/Modules/Evidgate.Modules.Identity/Options/CiAuthOptions.cs:46-48`) and the validator refuses a set previous pepper whose version equals the current one (`CiSecrets.cs:113-120`). It never cross-checks the pair against the stored `pepper_version` rows, so a wrong previous version silently matches nothing. Its key checks inspect only values that are present (base64, at least 32 bytes) plus that equality rule: it does not require a previous pepper and is not a completeness gate (`CiSecrets.cs:171-189`).
2. New tokens are hashed with the current pepper and stamped with its version (`src/Modules/Evidgate.Modules.Identity/Application/Commands/IngestTokenCommands.cs:127-128,189-198`), while exchange selects the stored version's pepper with `HashWithVersion` (`CiSecrets.cs:53-63`; `src/Modules/Evidgate.Modules.Identity/Application/Commands/ExchangeCommands.cs:221-225`). Use §2.1 to rotate **each** still-needed old-version token, privately distribute its successor, and rely on the 24-hour overlap only while the old row is still otherwise valid: the overlap constant is 24 hours (`IngestTokenCommands.cs:153-154`), only an active token rotates (`IngestTokenCommands.cs:178-185`), and the old row expires at `min(expires_at, overlap_ends_at)` (`src/Modules/Evidgate.Modules.Identity/Domain/CiBinding.cs:128-132`; `IngestTokenCommands.cs:204-214`). Keep the previous pepper for every old token still needed during that window. A binding challenge is also stored under the pepper version of the moment it was minted (`ChallengePepperVersion = pepperVersion` from `HashCurrent`, `src/Modules/Evidgate.Modules.Identity/Application/Commands/CiBindingCommands.cs:72-86`), and a pending binding's challenge is matched with `HashWithVersion(version, …)` at exchange (`ExchangeCommands.cs:162-174`) after the pre-tenant endpoint hashes it under every live pepper and passes those hashes to the resolver (`src/Modules/Evidgate.Modules.Identity/Endpoints/CiTokenEndpoints.cs:120-121`). Once the previous pepper is dropped, a challenge minted before the bump matches nothing, so complete or re-establish pending bindings before retiring it.
3. [OPEN O-D] There is no job that re-hashes old-version tokens and no sweep/report by pepper version. Account for all affected ingest and project API tokens and pending binding challenges before retirement; if completeness cannot be established, keep the previous pepper and escalate to 02.4.2 (a wider CI window has no task today) rather than silently cutting consumers off. After all old-version credentials are no longer needed and overlap has ended, remove the previous pepper and restart. A retired version returns `token_unknown` like an unknown token (`tests/Tenancy.IntegrationTests/CiAuthTests.IngestTokens.cs:136-175`; `ExchangeCommands.cs:221-225`).

**If the pepper is compromised** — a leaked configuration value, not a routine rotation — the drain ordering of step 3 does not apply. The pepper is key material, not merely a signature: every stored ingest-token, binding-challenge and project API-token HMAC (§3.4) is computed under it (`CiSecrets.cs:53-63`), and exchange compares the HMAC stored beside the token's own `pepper_version` (`ExchangeCommands.cs:221-225`), so while the leaked pair stays configured the ingest tokens and binding challenges hashed under it keep verifying, and a project API token's HMAC stays valid for whenever its bearer handler ships (§3.4).

1. Rotate or revoke **each** affected credential first — every ingest token (`DELETE /v1/ingest-tokens/{t}`, §2.1) and the project API tokens of §3.4 — and distribute successors privately. The routine overlap keeps old-version tokens alive through the previous slot; on a compromise that is the leak still working, so re-issue before cutting anyone off.
2. Move to the new pepper and version **without** carrying the compromised value into `EVIDGATE__CI__PREVIOUSPEPPER`, and drop any previous pair that still names the old version promptly. Dropping it is itself a cutoff, not just hygiene: once no current or previous pair matches the stored version, `HashWithVersion` returns `null`, so every still-un-rotated old-version ingest token fails as `token_unknown` (`tests/Tenancy.IntegrationTests/CiAuthTests.IngestTokens.cs:136-175`; `ExchangeCommands.cs:221-225`), any pending binding challenge minted under it fails too, because challenge matching resolves the stored `challenge_pepper_version` through the same lookup (`ExchangeCommands.cs:162-174`), and a project API token minted under it becomes unverifiable once its bearer handler ships (§3.4).
3. If the compromised value is the **previous** pepper, remove that pair immediately instead of waiting for the window to drain: the drain rule protects availability, and a leaked key outranks it.
4. Treat every window in which the compromised pepper still verified as an exposure window: tokens minted in it are suspect, so re-issue them rather than extend their life.

Never restore a compromised pepper, as current or as previous — the rule §2.2, §2.3 and §2.6 already state for their classes.

**Verify and rollback.** Exercise the existing exchange for an old and a new token during the window without logging either; `tests/Tenancy.IntegrationTests/CiAuthTests.IngestTokens.cs:107-133` covers the overlap and `136-175` covers current/previous selection and a retired version. Check successor use and all remaining consumers, including project API tokens (§3.4), before retirement. If the new deployment fails, restore the prior configuration and restart — except after a compromise, where the leaked value is never restored (above) — but any tokens already minted under the new version require that version to remain verifiable; do not blindly discard its pepper. No pepper rotation audit event is emitted today [OPEN O-G].

### 3.2 Session pepper

Session cookies are opaque and use a separate HMAC pepper (`migrations/0042_identity_login_resolvers.sql:6-13`). The keys are `EVIDGATE__SESSION__PEPPER`, `EVIDGATE__SESSION__PEPPERVERSION`, `EVIDGATE__SESSION__PREVIOUSPEPPER`, and `EVIDGATE__SESSION__PREVIOUSPEPPERVERSION` (`infra/config-matrix.json:896,916,982,1002`). The three-step configuration change is [environments](environments.md) §2 (`docs/runbooks/environments.md:163-174`): move today's pepper and version into the previous pair, set a new pepper and a bumped version, restart. **Retirement is a drain, not a date.** An **active** session that presents a cookie hashed with the previous pepper is re-hashed to the current pepper in the same statement (`migrations/0045_identity_session_hardening.sql:43-46`, `:389-396`, `:425-433`), but a revoked, expired or disabled session — and any session that simply never returns — re-hashes nothing and keeps the old `pepper_version` until its own `absolute_expires_at`. Remove the previous pair only when **no** session still carries it: wait for the **latest** `absolute_expires_at` among the sessions the old pepper signed, or for those rows to drain. The "oldest affected session's absolute expiry" form (the sentence at `docs/runbooks/environments.md:168-172`) is too early, and it logs out every session that never came back. The session validator rejects an incomplete previous pair, equal versions, equal peppers, or a previous version below 1 (`src/Modules/Evidgate.Modules.Identity/Sessions/SessionSecrets.cs:189-243`); the lookup offers the current hash and then the previous one (`SessionSecrets.cs:64-85`). There is only one previous slot, so drain it before rotating again ([DECISION D-S85-D4], `plans/features/02-identity-tenancy/02.3.2-sessions-and-revocation.md:130`). No query or job reports sessions by `pepper_version` — `evidgate_app` holds nothing on `public.session` (`migrations/0041_identity_user.sql:224`) and the migration role is not an on-call credential — so the configured absolute lifetime, `EVIDGATE__SESSION__ABSOLUTESECONDS` (default 604 800 s, `docs/runbooks/environments.md:161-162`), is the only bound you can compute; treat the drain as the hard requirement [OPEN O-D]. Restart to apply the configuration. If rolling back, retain every pepper version still needed by sessions and do not discard a newly used version blindly — but never reinstate a compromised pepper (§3.1). No session-pepper rotation audit event is emitted today [OPEN O-G].

### 3.3 Invite pepper

`EVIDGATE__INVITE__PEPPER` and `EVIDGATE__INVITE__PEPPERVERSION` (`infra/config-matrix.json:1022,1042`) have no previous-pepper overlap. `InviteOptions` records that exactly one pepper is configured and that bumping the version invalidates every pending invite (`src/Modules/Evidgate.Modules.Identity/Invites/InviteOptions.cs:16-23`), and `InviteSecrets.Verify` accepts only the configured version (`src/Modules/Evidgate.Modules.Identity/Invites/InviteSecrets.cs:41-46,56-68`); a version bump therefore fails pending invites closed, and an administrator must resend them, which re-mints under the current pepper (`migrations/0043_identity_invite_flow.sql:9-13`). The environment owner schedules the change, updates secret and version, restarts, and coordinates resends. Verify with an authorized invite flow without exposing the link. Restoring the old pair may make newly issued invites fail; do not blindly roll back. Receiver: 02.1.2 [OPEN O-D]. No invite-pepper rotation audit event is emitted today [OPEN O-G].

### 3.4 Project API tokens (same CI pepper)

Project API tokens (`evg_<prefix>_<secret>`) are HMACed with the same `EVIDGATE__CI__PEPPER` and stamped with `EVIDGATE__CI__PEPPERVERSION`: `CreateApiToken` calls `CiSecrets.HashCurrent` and stores the returned version (`src/Modules/Evidgate.Modules.Identity/Application/Commands/CreateApiToken.cs:111-121`), and `api_token.pepper_version` is a required column (`src/Modules/Evidgate.Modules.Identity/Domain/ApiToken.cs:30-32`; `migrations/0083_identity_api_token.sql:64-71,89`). Those credential columns are immutable, including `pepper_version` (`migrations/0083_identity_api_token.sql:113-131`), so a token cannot be re-peppered; revoke it and create a replacement.

No request path verifies these HMACs today: the domain exposes `MatchesHash` (`src/Modules/Evidgate.Modules.Identity/Domain/ApiToken.cs:57-63`), but only `tests/Tenancy.IntegrationTests/ApiTokenFormatTests.cs` calls it, and the only production `HashWithVersion` calls are ingest-token exchange and binding matching (`src/Modules/Evidgate.Modules.Identity/Application/Commands/ExchangeCommands.cs:171,221`); the bearer handler is 02.4.4-T2, not shipped. A pepper rotation therefore does not currently reject an API request, but a token minted under a version dropped from configuration becomes unverifiable once 02.4.4-T2 lands; its AC8 requires the stored `pepper_version` to keep verifying until the last such token expires (`plans/features/02-identity-tenancy/02.4.4-api-tokens-for-automation.md:30,116-120`). [OPEN O-D] Include project API tokens in the completeness count before retiring a previous CI pepper and keep the version until those tokens are revoked or expired. Receivers: 02.4.4-T2 (verifier; AC8) and 02.4.4-T3.2 (retired-version tests). No pepper-rotation audit event exists for this class either [OPEN O-G].

## 4. Open items and receivers

| ID | Gap and receiver |
|---|---|
| O-A | [OPEN] CI producer HS256 key is single-key, without overlap or reload; restart and re-exchange are disruptive. Receiver: 02.4.1-T3. Emergency invalidation of every outstanding producer token is nevertheless supported today: revoke the ingest token and/or disable the binding to stop new mints, then rotate `EVIDGATE__CI__SIGNINGKEY` and restart every replica (§2.3; owner decision D-S108 §6 row 5, 2026-10-27, coordinator queue row F51). |
| O-B | [OPEN] No inbound webhook receiver or configured key; outbound is planned only. Receivers: 03.4.1-T2 (inbound key name and overlap), 07.6.2 (outbound signing secrets). |
| O-C | [OPEN] Database credentials bind at startup with no code-level overlap or rotation audit. Receivers: O3 (DECISIONS §8, cloud provider/managed store), 10.1.1-T3 (role/secret provisioning); drill: 10.7.1-T4.2. |
| O-D | [OPEN] Pepper windows are two-version at most: CI/ingest and session support one current and one previous pepper, invites current only, and there is no sweep/report by `pepper_version` and no re-hash of old-version tokens (their plaintext is gone, so rotation is the only path). Account for and rotate every ingest token and project API token before retiring a previous CI pepper; a second rotation before the window drains strands the older version. Receivers: 02.4.2 for a wider CI window (no task exists today); 02.4.4-T2 (API-token verifier; AC8) and 02.4.4-T3.2 (retired-version tests). Invite pepper has no overlap and a bump fails pending invites closed; receiver: 02.1.2. |
| O-E | [OPEN] `/internal/operator/*` is absent from OpenAPI and no audited operator rotation path exists. Current platform actor is the environment owner; tenant token mutation requires `ingest_token:write`. Receiver: 02.8.1. |
| O-F | [OPEN] Staging does not exist; staging drill and non-author execution have not happened. Receivers: 10.7.1-T4.2 (remains open), 10.1.1-T3 (staging provisioning). |
| O-G | [OPEN] No audit action is emitted today for signing-key, DB credential, pepper or secret-store rotation. Existing token/binding actions are not rotation audit for those secrets. Receiver: 11.5.1. |
| O-H | [OPEN] GitHub App private-key custodian (DECISIONS §8 O11: the role is defined in ADR-0007 and the custody stub, while the holder and the second organization owner are open) and the rotation cadence (stub :109: a proposal of every 12 months or on a custodian change, not decided). Drill and non-author execution: 10.7.1-T4.2. Receiver: 02.4.3. |
| O-I | [OPEN] No targeted producer-token kill: revoke and binding disable reach only new exchanges (§2.3), because the authentication path does no status lookup (`src/Modules/Evidgate.Modules.Identity/Ci/CiProducerTokens.cs:136-166`) and `ci_token_issue` records only GitHub OIDC exchanges and has no revocation column (`migrations/0017_identity_ci_token_issue.sql:3-8,10-33`). Fleet-wide invalidation is the revoke/disable-then-rotate cutover in §2.3 (owner decision D-S108 §6 row 5, 2026-10-27). Receivers: T3-G2 runbook half → coordinator queue row F51 (recorded in §2.3); T3-G2 drill half → 10.7.1-T4.2; T3-G5 (no issuance record for ingest-minted producers, so the window cannot be scoped) → coordinator queue row F45; the optional `ci_producer` status check → no task or queue row yet (`docs/security/ingest-token-review.md:190,193`). |

## 5. Staging drill — not executed

[OPEN O-F] AC5 requires each class below to rotate in staging with no downtime and no lost acknowledged event, and old pepper versions to verify until their last token expires (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:22,26,118-119`). Staging does not exist; neither this drill nor a non-author execution has been performed. The gaps in §4, especially O-A through O-D and O-H, must be resolved before claiming AC5. Do not treat the following checklist as results.

- [ ] Provision staging through 10.1.1-T3; arrange a non-author executor and synthetic tenant identifiers only.
- [ ] Establish a continuous-ingest probe, record acknowledged event identifiers and timestamps without credentials, and compare acknowledgements with durable outcomes throughout each rotation; show no probe gap and no lost acknowledged event.
- [ ] Rotate an ingest token; check both sides of the supported overlap, successor use, and immediate revoke on a separate synthetic token.
- [ ] Exercise CI OIDC trust/key refresh and binding disable/re-enable with an authorized synthetic binding; check continuous ingest.
- [ ] Rotate the CI producer signing key only after O-A supplies a safe overlap mechanism; check old and new tokens without losing acknowledged events. The emergency invalidation in §2.3 is disruptive by design and does not satisfy this zero-downtime step.
- [ ] Rotate inbound and outbound webhook secrets only after O-B delivers the respective implemented paths and overlap; exercise delivery and verification.
- [ ] Rotate each applicable database role credential after O-C provisioning and a safe cutover mechanism exist; verify app, sweep and migrator roles as applicable.
- [ ] Rotate applicable object-store and login provider keys via the provider seam; exercise the corresponding ordinary operation. The GitHub App key follows the custody stub's documented procedure (§2.6), which this drill must exercise; it has not been drilled yet.
- [ ] Rotate the CI/ingest pepper: prove new ingest and project API tokens use the new version, old tokens verify throughout their required lifetime, rotate affected tokens and retire the previous pepper only after they are accounted for; do not start a second rotation before the previous window drains (only one previous slot exists). Exercise session and invite pepper procedures separately, including the invite failure boundary.
- [ ] Record non-secret evidence and executor corrections in this runbook; only then evaluate AC1 and AC5. A successful probe cannot by itself prove unexercised consumers safe.
