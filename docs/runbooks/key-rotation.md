# Key rotation

Status: **10.7.1-T4.1 written; the pepper section is the written half of T4.2.** The staging drill has not run, and a non-author has not executed this runbook. AC1 and AC5 are not met. [OPEN O-F]

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`.

## 1. Scope, roles and rules

This runbook covers the six key classes in 10.7.1-T4.1 and the T5 pepper procedure. It describes current behavior, not a promise of zero downtime. A **secret** is a credential or pepper; a **pepper version** identifies the key used to HMAC a stored token; a **token overlap** is the period in which a rotated token and its successor can both verify. Token overlap and pepper overlap are different windows.

- Never put secret values in this runbook, logs, tickets, terminal history, drill evidence, or screenshots. Use the environment's secret delivery mechanism; do not print responses containing one-time credentials. Record only non-secret identifiers and outcomes.
- Never perform ad-hoc `psql` writes. Use the supported tenant API for token and binding mutations; use the environment's authorized database administration and secret-delivery procedures for credentials. Do not invent an `/internal/operator/*` rotation endpoint: operational routes are absent from the contract (`contracts/openapi.yaml:15-17`), and no audited operator rotation path exists. [OPEN O-E]
- For platform secrets, the V1 solo platform environment owner changes the secret store/deployment environment and restarts the affected processes; managed-store choice remains open. For tenant ingest tokens, the caller needs `ingest_token:write` (`src/Modules/Evidgate.Modules.Identity/Application/Commands/IngestTokenCommands.cs:138,231`). Binding administration uses its own authorized tenant API. Separate duties and operator authorization are receivers of O-E.
- [environments](environments.md) owns how a value is changed, and its §2 hands ingest-token, DB-credential, JWT-key and HMAC-pepper rotation to this runbook (`docs/runbooks/environments.md:165-170`). The [GitHub App private-key custody stub](github-app-key-custody.md) holds the App key procedure documented in §2.6: that is the stub's documented procedure, not yet drilled, and it is not proof of deployed webhook secrets.

## 2. Key classes

### 2.1 Ingest tokens

**Where it lives.** A generated tenant token is returned once; the list API returns metadata only (`src/Modules/Evidgate.Modules.Identity/Endpoints/CiAdminEndpoints.cs:45-55`). Its HMAC uses `EVIDGATE__CI__PEPPER` and its version uses `EVIDGATE__CI__PEPPERVERSION` (`infra/config-matrix.json:157,177`; reader `src/Modules/Evidgate.Modules.Identity/Ci/CiSecrets.cs:23-30,41-61`). Pepper rotation is §3.1.

**Who may rotate.** A tenant principal with `ingest_token:write` (`src/Modules/Evidgate.Modules.Identity/Application/Commands/IngestTokenCommands.cs:138,231`).

**Steps.**

1. List the project's token metadata with `GET /v1/projects/{p}/ingest-tokens`; identify the token and its consumers without exposing its value (`CiAdminEndpoints.cs:49-51`).
2. Call `POST /v1/ingest-tokens/{t}/rotate`. Deliver the returned successor privately to each consumer and switch them before `overlap_ends_at`. The predecessor and successor both verify during the 24-hour overlap; the predecessor then expires, subject also to its existing expiry. A revoked, already rotated or expired token cannot be rotated (`src/Modules/Evidgate.Modules.Identity/Application/Commands/IngestTokenCommands.cs:132-150,174-196,200-220`; `tests/Tenancy.IntegrationTests/CiAuthTests.cs:458-485`).
3. If compromise requires immediate cut-off, call `DELETE /v1/ingest-tokens/{t}` with the required idempotency key. Revocation is immediate; deleting a rotated predecessor ends its remaining overlap immediately. Repeated revocation or deletion after overlap returns success without a new audit event (`IngestTokenCommands.cs:224-267`; `CiAdminEndpoints.cs:149-150`).

**Verify.** Use the existing token list and CI token exchange, without logging either credential, to verify the successor and the intended predecessor state. The exchange verifies the stored pepper version and token status (`src/Modules/Evidgate.Modules.Identity/Application/Commands/ExchangeCommands.cs:197-228`). The existing integration cases cover overlap and immediate revoke (`tests/Tenancy.IntegrationTests/CiAuthTests.cs:436-485`); they are references, not evidence of this deployment's drill.

**Rollback.** During an unrevoked overlap, consumers can temporarily use the predecessor while its existing expiry and overlap allow it. Once revoked or expired it cannot be restored by this route; issue or use a valid successor instead (`IngestTokenCommands.cs:174-180,254-267`). Never assume revocation can be undone.

**Audit event.** `ingest_token.create`, `ingest_token.rotate`, `ingest_token.revoke`, and `ingest_token.exchange` are listed in `docs/security/audit-taxonomy.md:107,121-123`. Pepper changes themselves emit none today [OPEN O-G].

### 2.2 CI OIDC trust

**Where it lives.** Trust is configuration, not a held GitHub signing secret: `EVIDGATE__CI__GITHUBOIDCISSUER`, `EVIDGATE__CI__GITHUBOIDCAUDIENCE`, and `EVIDGATE__CI__GITHUBJWKSURI` (`infra/config-matrix.json:237,257,277`); issued producer tokens have separate `EVIDGATE__CI__ISSUER` and `EVIDGATE__CI__AUDIENCE` (`infra/config-matrix.json:94,114`; reader `src/Modules/Evidgate.Modules.Identity/Options/CiAuthOptions.cs:24-30,50-58`). The validator selects GitHub's published key by `kid`, pins RS256, and retrieves JWKS at runtime (`src/Modules/Evidgate.Modules.Identity/Ci/GitHubOidc.cs:82-95,203-215,225-250`).

**Who may rotate.** GitHub controls its published signing keys. The platform environment owner controls local trust configuration; an authorized tenant administrator manages the project binding.

**Steps.**

1. For upstream key rotation, do not generate or upload a local GitHub signing secret. Allow the existing JWKS provider to fetch the published keys; it has a ten-minute cache and throttles unknown-key refresh to 30 seconds (`GitHubOidc.cs:203-215,225-250`). This is not a locally controlled overlap guarantee.
2. If the binding must be disabled, use `DELETE /v1/ci-bindings/{b}`. To re-enable or update it, use `PATCH /v1/ci-bindings/{b}` with `If-Match`; use the existing project binding list to inspect it (`src/Modules/Evidgate.Modules.Identity/Endpoints/CiAdminEndpoints.cs:30-43`). A disabled binding refuses exchange, and a pending binding needs its challenge (`src/Modules/Evidgate.Modules.Identity/Application/Commands/ExchangeCommands.cs:60-80`). Do not disable a live binding as a routine key-refresh step.
3. If issuer, audience or JWKS configuration must change, have the environment owner coordinate the deployment and restart the affected process, using [environments](environments.md#changing-a-value); preserve the prior configuration for rollback.

**Verify.** Run a real authorized CI exchange through the existing `/v1/ci/token` endpoint without logging the token, and check the intended binding in the list. Existing acceptance and refusal coverage is `tests/Tenancy.IntegrationTests/CiAuthTests.cs:129-170,257-276`. A successful exchange is not proof that all workflows are unaffected.

**Rollback.** Restore the prior trusted configuration and restart, or restore the binding state with the versioned PATCH when authorized; do not restore a known-compromised trust setting.

**Audit event.** `ci_token.exchange`, `ci_binding.create`, `ci_binding.update`, and `ci_binding.disable` (`docs/security/audit-taxonomy.md:106,108-110`); no Evidgate event is emitted for GitHub's upstream key rotation today [OPEN O-G].

### 2.3 JWT signing key

**Where it lives.** `EVIDGATE__CI__SIGNINGKEY` (`infra/config-matrix.json:74`) is resolved by `CiSecrets` (`src/Modules/Evidgate.Modules.Identity/Ci/CiSecrets.cs:23-34,159-172`); `CiProducerTokens` retains a single key and signs `ci_producer` JWTs with HS256 (`src/Modules/Evidgate.Modules.Identity/Ci/CiProducerTokens.cs:48-61,101-109`). Session cookies are opaque and use HMAC peppers, not this JWT key (`migrations/0042_identity_login_resolvers.sql:6-13`); see §3.2.

**Who may rotate.** The platform environment owner through secret delivery and process restart [OPEN O-E].

**Steps.** [OPEN O-A] There is no previous signing-key slot, overlap or live reload. The only current procedure is a disruptive cutover:

1. Schedule a quiet window and arrange for CI consumers to re-exchange their credentials. In-flight producer tokens can fail after the cutover; their configured lifetime is at most 60 minutes (`src/Modules/Evidgate.Modules.Identity/Options/CiAuthOptions.cs:21-34`).
2. Update the signing key in secret delivery and restart affected processes. The key is captured in process (`CiProducerTokens.cs:58-61`); do not claim a zero-downtime or dual-key procedure.
3. Re-exchange CI credentials and verify a newly issued token against the running deployment. Avoid logging either token or key. Boot validation checks only the values that are present (base64 and at least 32 bytes, `src/Modules/Evidgate.Modules.Identity/Ci/CiSecrets.cs:131-148`), so a missing key boots with the exchange switched off rather than failing fast (`CiProducerTokens.cs:65-66`): confirm the exchange answers before declaring the rotation done.

**Verify.** An authorized CI exchange and its intended ingest operation should succeed after restart; the existing exchange test is `tests/Tenancy.IntegrationTests/CiAuthTests.cs:129-165`.

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

**Where it lives.** `EVIDGATE__DATABASE__CONNECTIONSTRING` (app), `EVIDGATE__TASKS__SWEEPCONNECTIONSTRING` (worker sweep), and `EVIDGATE_MIGRATOR__CONNECTIONSTRING` (migrator owner) are secret rows in `infra/config-matrix.json:31,1566,1817`; readers are `src/BuildingBlocks/Persistence/Options/DatabaseOptions.cs:6-11,18-26`, `src/BuildingBlocks/Persistence/Tasks/TasksOptions.cs:103-113`, and `src/Evidgate.Migrator/MigratorOptions.cs:71-85`.

**Who may rotate.** The platform environment owner with the corresponding database-administration and secret-delivery authority [OPEN O-E]. Provisioning and managed-store choice are open [OPEN O-C].

**Steps.** The code binds one credential per role at process startup; it has no credential overlap mechanism. This is not a zero-downtime guarantee.

1. In a coordinated maintenance window, create the replacement credential in the database through its authorized administration mechanism. [INFERENCE] A password change for the same role affects existing connections on reconnect; do not assume a new credential leaves both passwords valid.
2. Update the corresponding connection-string secret in the environment's secret delivery and perform a coordinated rolling restart of affected processes. Restart alone does not guarantee uninterrupted connections during a one-password cutover.
3. Check each affected role's ordinary service operation and connection health without exposing the connection string. Include the worker sweep or deploy-time migrator if their credentials were changed.

**Verify.** Use the existing readiness route `/internal/health/ready` (`docs/runbooks/environments.md:266-269`) and a normal operation that requires the changed role; for the migrator, use its established deployment procedure, not an ad-hoc SQL write. Test reference: `tests/Migrator.IntegrationTests/MigratorSecretResolutionTests.cs:33-71`.

**Rollback.** If the previous credential is still valid and not compromised, restore its secret value and restart affected processes. If the database password was replaced, first restore the corresponding database credential through authorized administration; merely restoring the old secret cannot restore database access.

**Audit event.** None emitted today for database credential or secret-store rotation [OPEN O-G].

### 2.6 Provider keys and GitHub App key

**Where it lives.** `ISecretProvider` is the secret seam; V1 uses `EnvironmentSecretProvider`, with managed-store replacement a future DI swap (`src/BuildingBlocks/SharedKernel/Secrets/ISecretProvider.cs:3-17`; `src/BuildingBlocks/SharedKernel/Secrets/EnvironmentSecretProvider.cs:17-39`). `SecretNames.All` is the registry (`src/BuildingBlocks/SharedKernel/Secrets/SecretNames.cs:14-77`):

| Class | Registered keys and reader |
|---|---|
| Database | The three connection-string keys and readers in §2.5. |
| CI | `EVIDGATE__CI__SIGNINGKEY`, `EVIDGATE__CI__PEPPER`, `EVIDGATE__CI__PREVIOUSPEPPER`; `CiSecrets.cs:159-172` (§2.3 and §3.1). |
| Object store | `EVIDGATE__OBJECT__ACCESSKEY`, `EVIDGATE__OBJECT__SECRETKEY` (`infra/config-matrix.json:1125,1145`); `src/BuildingBlocks/ObjectStorage/ObjectStorageOptionsSecretPostConfigure.cs:13-25`. |
| Human login | `EVIDGATE__OIDC__GOOGLE__CLIENTSECRET`, `EVIDGATE__OAUTH__GITHUB__CLIENTSECRET` (`infra/config-matrix.json:472,707`); `src/Modules/Evidgate.Modules.Identity/Login/Google/GoogleLoginSecrets.cs:13-24` and `src/Modules/Evidgate.Modules.Identity/Login/GitHub/GitHubLoginSecrets.cs:13-24`. |
| Session and invite | `EVIDGATE__SESSION__PEPPER`, `EVIDGATE__SESSION__PREVIOUSPEPPER`, `EVIDGATE__INVITE__PEPPER` (`infra/config-matrix.json:833,919,959`); `src/Modules/Evidgate.Modules.Identity/Sessions/SessionSecrets.cs:115-130` and `src/Modules/Evidgate.Modules.Identity/Invites/InviteSecrets.cs:95-108`. See §3. |
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

## 3. Pepper rotation — T4.2 written half

T5 [DECISION] requires new tokens to use the new pepper version while older versions verify until their last token expires (`plans/features/README.md:49`). This is a requirement, not proof that AC5 has been achieved.

### 3.1 CI and ingest-token pepper

`EVIDGATE__CI__PEPPER`, `EVIDGATE__CI__PEPPERVERSION`, `EVIDGATE__CI__PREVIOUSPEPPER`, and `EVIDGATE__CI__PREVIOUSPEPPERVERSION` are declared at `infra/config-matrix.json:157,177,197,217`. The platform environment owner performs the configuration change. The current pepper also protects binding challenges (`src/Modules/Evidgate.Modules.Identity/Options/CiAuthOptions.cs:36-48`).

1. Preserve the current pepper and version securely as the previous pair; configure a new current pepper and bump its version. Deploy both pairs together and restart affected processes. Copy the previous version **verbatim** from the version that signed the tokens being retired: it defaults to 1 and the validator only rejects a previous version equal to the current one (`src/Modules/Evidgate.Modules.Identity/Options/CiAuthOptions.cs:39-48`; `src/Modules/Evidgate.Modules.Identity/Ci/CiSecrets.cs:115-118`), and nothing cross-checks it against the stored `pepper_version` rows, so a wrong previous version silently verifies nothing. The validator also checks only the values that are present (`CiSecrets.cs:131-148`); it is not a completeness gate for the rotation.
2. New token successors use `HashCurrent`, while exchange uses `HashWithVersion` to choose the configured current or previous pepper (`CiSecrets.cs:41-61`; `src/Modules/Evidgate.Modules.Identity/Application/Commands/ExchangeCommands.cs:215-220`). Use §2.1 to rotate **each** still-needed old-version token, privately distribute its successor, and allow the 24-hour token overlap where the old token has not otherwise expired (`IngestTokenCommands.cs:149-150,183-200`). Keep the previous pepper for every old token still needed during that window. A binding challenge is also stored under the pepper version of the moment it was minted (`ChallengePepperVersion = pepperVersion` from `HashCurrent`, `src/Modules/Evidgate.Modules.Identity/Application/Commands/CiBindingCommands.cs:72-86`), and a pending binding's challenge is matched with `HashWithVersion(version, …)` at exchange (`ExchangeCommands.cs:158-169`) after the pre-tenant endpoint hashes it under every live pepper and passes those hashes to the resolver (`src/Modules/Evidgate.Modules.Identity/Endpoints/CiTokenEndpoints.cs:119-122`). Once the previous pepper is dropped, a challenge minted before the bump matches nothing, so complete or re-establish pending bindings before retiring it.
3. [OPEN O-D] There is no job that re-hashes old-version tokens and no sweep/report by pepper version. Account for all affected tokens and pending binding challenges before retirement; if completeness cannot be established, keep the previous pepper and escalate to 02.4.4 rather than silently cutting consumers off. After all old-version credentials are no longer needed and overlap has ended, remove the previous pepper and restart. A retired version fails like an unknown token (`tests/Tenancy.IntegrationTests/CiAuthTests.cs:487-527`; `ExchangeCommands.cs:215-220`).

**Verify and rollback.** Exercise the existing exchange for an old and a new token during the window without logging either; the integration test above covers current/previous selection. Check successor use and all remaining consumers before retirement. If the new deployment fails, restore the prior configuration and restart, but any tokens already minted under the new version require that version to remain verifiable; do not blindly discard its pepper. No pepper rotation audit event is emitted today [OPEN O-G].

### 3.2 Session pepper

Session cookies are opaque and use a separate HMAC pepper (`migrations/0042_identity_login_resolvers.sql:6-13`). Follow the three-step session pepper procedure in [environments](environments.md) §2, at `docs/runbooks/environments.md:139-145`: retain the old pepper and version as the previous pair, set a new pepper and bumped version, and retire the previous pair only after the oldest affected session's absolute expiry. The session validator rejects an incomplete previous pair, equal versions or equal peppers (`src/Modules/Evidgate.Modules.Identity/Sessions/SessionSecrets.cs:189-242`). A request matching the previous pepper re-hashes the session to the current pepper (`migrations/0045_identity_session_hardening.sql:43-46,425-433`). Restart to apply the configuration. If rolling back, retain every pepper version still needed by sessions; do not discard a newly used version blindly. No session-pepper rotation audit event is emitted today [OPEN O-G].

### 3.3 Invite pepper

`EVIDGATE__INVITE__PEPPER` and `EVIDGATE__INVITE__PEPPERVERSION` (`infra/config-matrix.json:959,979`) have no previous-pepper overlap. The reader accepts only the configured version (`src/Modules/Evidgate.Modules.Identity/Invites/InviteSecrets.cs:50-67,95-108`); a version bump fails pending invites closed, and an administrator must resend them (`migrations/0043_identity_invite_flow.sql:9-12`). The environment owner schedules the change, updates secret and version, restarts, and coordinates resends. Verify with an authorized invite flow without exposing the link. Restoring the old pair may make newly issued invites fail; do not blindly roll back. Receiver: 02.1.2 [OPEN O-D]. No invite-pepper rotation audit event is emitted today [OPEN O-G].

## 4. Open items and receivers

| ID | Gap and receiver |
|---|---|
| O-A | [OPEN] CI producer HS256 key is single-key, without overlap or reload; restart and re-exchange are disruptive. Receiver: 02.4.1-T3. |
| O-B | [OPEN] No inbound webhook receiver or configured key; outbound is planned only. Receivers: 03.4.1-T2 (inbound key name and overlap), 07.6.2 (outbound signing secrets). |
| O-C | [OPEN] Database credentials bind at startup with no code-level overlap or rotation audit. Receivers: O3 (DECISIONS §8, cloud provider/managed store), 10.1.1-T3 (role/secret provisioning); drill: 10.7.1-T4.2. |
| O-D | [OPEN] No sweep/report or re-hash of old-version ingest tokens: rotate each before retiring the previous pepper. Receiver: 02.4.4. Invite pepper has no overlap and a bump fails pending invites closed; receiver: 02.1.2. |
| O-E | [OPEN] `/internal/operator/*` is absent from OpenAPI and no audited operator rotation path exists. Current platform actor is the environment owner; tenant token mutation requires `ingest_token:write`. Receiver: 02.8.1. |
| O-F | [OPEN] Staging does not exist; staging drill and non-author execution have not happened. Receivers: 10.7.1-T4.2 (remains open), 10.1.1-T3 (staging provisioning). |
| O-G | [OPEN] No audit action is emitted today for signing-key, DB credential, pepper or secret-store rotation. Existing token/binding actions are not rotation audit for those secrets. Receiver: 11.5.1. |
| O-H | [OPEN] GitHub App private-key custodian (DECISIONS §8 O11: the role is defined in ADR-0007 and the custody stub, while the holder and the second organization owner are open) and the rotation cadence (stub :109: a proposal of every 12 months or on a custodian change, not decided). Drill and non-author execution: 10.7.1-T4.2. Receiver: 02.4.3. |

## 5. Staging drill — not executed

[OPEN O-F] AC5 requires each class below to rotate in staging with no downtime and no lost acknowledged event, and old pepper versions to verify until their last token expires (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:22,26,113-114`). Staging does not exist; neither this drill nor a non-author execution has been performed. The gaps in §4, especially O-A through O-D and O-H, must be resolved before claiming AC5. Do not treat the following checklist as results.

- [ ] Provision staging through 10.1.1-T3; arrange a non-author executor and synthetic tenant identifiers only.
- [ ] Establish a continuous-ingest probe, record acknowledged event identifiers and timestamps without credentials, and compare acknowledgements with durable outcomes throughout each rotation; show no probe gap and no lost acknowledged event.
- [ ] Rotate an ingest token; check both sides of the supported overlap, successor use, and immediate revoke on a separate synthetic token.
- [ ] Exercise CI OIDC trust/key refresh and binding disable/re-enable with an authorized synthetic binding; check continuous ingest.
- [ ] Rotate the CI producer signing key only after O-A supplies a safe overlap mechanism; check old and new tokens without losing acknowledged events.
- [ ] Rotate inbound and outbound webhook secrets only after O-B delivers the respective implemented paths and overlap; exercise delivery and verification.
- [ ] Rotate each applicable database role credential after O-C provisioning and a safe cutover mechanism exist; verify app, sweep and migrator roles as applicable.
- [ ] Rotate applicable object-store and login provider keys via the provider seam; exercise the corresponding ordinary operation. The GitHub App key follows the custody stub's documented procedure (§2.6), which this drill must exercise; it has not been drilled yet.
- [ ] Rotate the CI/ingest pepper: prove new tokens use the new version, old tokens verify throughout their required lifetime, rotate affected tokens and retire the previous pepper only after they are accounted for. Exercise session and invite pepper procedures separately, including the invite failure boundary.
- [ ] Record non-secret evidence and executor corrections in this runbook; only then evaluate AC1 and AC5. A successful probe cannot by itself prove unexercised consumers safe.
