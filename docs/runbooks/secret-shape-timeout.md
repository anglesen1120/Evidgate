# Secret-shape timeout runbook

Status: **alert rule specified, not deployed.** No rule file exists: `infra/alerts/` is absent and the rule format and paging route are owned by 10.3.1-T5 (see [incident process](incident-process.md) §1 and [dead-letter](dead-letter.md) §2). Nothing here pages anyone today. No staging drill has run.

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Line pins are this branch's (base `442f1fd1`).

## 1. Purpose, audience and rules

**Purpose.** Tell an operator what a rise of `evidgate_secret_shape_timeout_total` means, what a customer sees while it lasts, and what to check first, without ever handling the value that was being checked.

**Audience.** The on-call engineer who receives the ticket from the rule in [alert rules](../security/alert-rules.md) §6.

**Rules.**

1. Never ask for, copy or paste the checked value, a request body, a URL with identifiers, a token or a header into a ticket. Collect request times and route templates only.
2. The 100 ms budget is a code decision (`src/BuildingBlocks/SharedKernel/Security/SecretShape.cs:96-99` ✅), not an operator setting; do not look for a knob.
3. Incident class: P3-c ([incident process](incident-process.md) §2) only while triage (§5) shows no ordinary value refused because of a timeout, since P3-c requires no customer impact; P2-g when §5 says escalate.

## 2. What the counter means

`evidgate_secret_shape_timeout_total` (catalogue id `secret_shape_timeout`, [`infra/metrics/catalogue.yaml`](../../infra/metrics/catalogue.yaml), meter `Evidgate`) increases by one each time a secret-shape regex runs out of its 100 ms match budget: the catch of `RegexMatchTimeoutException` calls `SecretShapeMetrics.RecordTimeout(running)` and returns true (`src/BuildingBlocks/SharedKernel/Security/SecretShape.cs:118-121` ✅). Its only label is `pattern`, with the emitted strings `jwt`, `url_credentials`, `hex_run`, `random_run`, `uuid` (`src/BuildingBlocks/SharedKernel/Security/SecretShapeMetrics.cs:51-58` ✅). The checked value is never a label, a log line or an exception message.

The patterns are linear, so a timeout means a stalled host (CPU starvation, GC pause, noisy neighbour) rather than a hostile input (`SecretShape.cs:96-98` ✅, the doc comment on `ValueLooksSecret`). It does not mean a secret was found.

## 3. What the caller sees

The check never throws on a timeout: it returns true, "secret-shaped" (`SecretShape.cs:96-98`, `:118-121` ✅). Every call site of `SecretShape.ValueLooksSecret` and `ParameterSecretRule.ValueLooksSecret` read on this base turns a true result into a validation failure; none stores the value, none answers 500. The codebase-memory graph (`trace_path`, inbound) lists the same ten classes: `AiSettingsValidation`, `AiSettingsDocument`, `UrlSafety`, `SuiteRevisionPlanner`, `VariantNormalizer`, `CreateEnvironmentRevisionCommandHandler`, `EnvironmentRevisionRules`, `ManifestVerifier`, `ReleaseRules` and `ParameterSecretRule`.

| Caller (file:line) | What a true result becomes |
|---|---|
| `src/BuildingBlocks/SharedKernel/Security/UrlSafety.cs:35,38,45,53,64,70` | `SecretValueDetected` problem |
| `src/Modules/Evidgate.Modules.Authoring/Application/Commands/AiSettingsValidation.cs:158` | detail `/reason` = `secret_value_detected` |
| `src/Modules/Evidgate.Modules.Authoring/Domain/AiSettingsDocument.cs:190` | detail `secret_value_detected`, value dropped (returns null) |
| `src/Modules/Evidgate.Modules.Catalog/Application/SuiteRevisionPlanner.cs:403,419,429` | `ErrorDetail` `secret_value`; `:453` (key) `secret_key` |
| `src/Modules/Evidgate.Modules.Catalog/Domain/VariantNormalizer.cs:80` and `:236` | `VariantProblem` `secret_key` / `secret_value` |
| `src/Modules/Evidgate.Modules.Orchestrator/Application/Manifests/EnvironmentRevisions.cs:79` | `Result.Failure` `EnvironmentRevisionInvalid`, 422 (`OrchestratorRules.cs:57-59`), detail `/name` = `invalid_value` |
| `src/Modules/Evidgate.Modules.Orchestrator/Domain/EnvironmentRevisionRules.cs:64,77` | `SecretValueDetected` detail |
| `src/Modules/Evidgate.Modules.Orchestrator/Domain/ManifestVerifier.cs:164,169,249` | `SecretValueDetected` detail; `:249` feeds `SecretPositions` |
| `src/Modules/Evidgate.Modules.Orchestrator/Domain/ReleaseRules.cs:65` | `IsName` false, then `ReleaseBody.cs:100,105-112`: 422 `release_invalid`, detail `/name` = `invalid_value` |
| `src/BuildingBlocks/SharedKernel/Security/ParameterSecretRule.cs:60,78` | feeds the callers above |

The HTTP status was traced to the endpoint only for the environment-revision and release routes (422). For the other call sites the statement is: the code turns a true result into a validation failure; the status was not traced. A legitimate caller can therefore see a refusal of a value that is not a secret, only while the condition lasts. A retry after the host recovers can pass the shape check when the timeout was the only refusal reason: the timeout is per call and `ValueLooksSecret` keeps no state, cache or stored verdict (`SecretShape.cs:102-122` ✅ read). It does not guarantee success: a release retry can meet a different refusal, for example 409 `release_name_conflict` (`src/Modules/Evidgate.Modules.Orchestrator/Application/Releases/CreateRelease.cs:124-153`). No test retries across a real stall `[INFERENCE]` ⚠️ (the matcher seam `ValueLooksSecret(value, match)` is exercised in `tests/Tenancy.IntegrationTests/SecretShapeTimeoutTests.cs`).

## 4. Alert rule (specification)

The rule is the row `AR-secret-shape-timeout` in [alert rules](../security/alert-rules.md) §6: default threshold, its basis (a healthy host records zero; no traffic baseline exists, so the windows are defaults to tune `[INFERENCE]` ⚠️), severity `warning`, route `ticket`. It is a specification only; the rule file is owed by 10.3.1-T5.1.

## 5. First checks and escalation

1. Read the `pattern` split. One pattern only points at a value shape; all patterns at once point at the host.
2. Compare with host health in the same window: CPU, `dotnet.gc.last_collection.heap.size`, `dotnet.thread_pool.queue.length` ([telemetry](telemetry.md) §6).
3. Check whether callers report refusals of ordinary values from the same time window (§3).
4. Escalate to P2-g through the [incident process](incident-process.md) when the counter rises in 3 consecutive windows or on every pattern at once (treat it as a host stall), or when a caller confirms that an otherwise acceptable value was refused during a timeout window. A refused real secret is the normal outcome, not an escalation.
5. Customers report refused ordinary values and the host looks healthy: raise it to the secret-shape owner.

## 6. Known gaps

- **Not deployed.** The alert rule file, paging and a dashboard panel are open under 10.3.1-T5; the usage billing-key conflict counter has the same gap (`infra/metrics/catalogue.yaml`, id `usage_billing_key_conflict`). No check validates rule-spec semantics.
- **Untraced statuses.** Catalog, Authoring and manifest-verifier failures were not followed up to an HTTP status (§3).
- **Name callers report `invalid_value`.** `ReleaseRules.IsName` and the environment-revision name check refuse with `invalid_value`, not a secret code: the operator sees an invalid name, not "secret detected".
- **TypeScript twin not read.** `packages/variant/src/secretShape.ts` has a similar check; its timeout behaviour was not examined here.
- **Callers keep iterating after a timeout.** A timeout answers true instead of aborting, so loops such as `SuiteRevisionPlanner.cs:387-433` and `VariantNormalizer.cs:63-98` continue to later members; each timeout costs up to 100 ms and only a sustained host stall reaches this (carried row 'Callers keep iterating after a secret-shape timeout (S502)' in `docs/status-carried.md`; no change made here). There is no request-wide deadline.

## 7. Evidence table

| Cite | What it proves |
|---|---|
| `src/BuildingBlocks/SharedKernel/Security/SecretShape.cs:96-98` | Timeout fails closed; linear patterns mean a stalled host, not hostile input |
| `src/BuildingBlocks/SharedKernel/Security/SecretShape.cs:118-121` | The catch records the timeout and returns true |
| `src/BuildingBlocks/SharedKernel/Security/SecretShapeMetrics.cs:48-58` | One counter, label `pattern` only, emitted strings |
| `infra/metrics/catalogue.yaml` (id `secret_shape_timeout`) | Catalogue entry, source `SecretShapeMetrics.cs` |
| `src/Modules/Evidgate.Modules.Orchestrator/Application/Manifests/OrchestratorRules.cs:57-59` | `EnvironmentRevisionInvalid` is 422 |
| `src/Modules/Evidgate.Modules.Orchestrator/Application/Releases/ReleaseBody.cs:100-112` | A false `IsName` becomes 422 `release_invalid` / `invalid_value` |
| [alert rules](../security/alert-rules.md) §6 | The rule specification |
| [incident process](incident-process.md) §2 (P3-c, P2-g) and §6 | Class and routing |
