# Code rules for coding agents

These are the binding code rules for every coding agent working in this repository, human or LLM. They also apply to every reviewer. They add to `plans/00-overview.md` §5.4–§5.7 and never override them; on a conflict, 00 wins and this file is fixed.

**Why this file exists.** A full review of `develop` @ `98738803` on 2026-10-29 covered `src/`, `migrations/`, `packages/`, `apps/web` and `tests/`. It found that most defects were not missing knowledge. Several broke rules that 00 §5.5–§5.7 already states:
- C6, rich domain;
- P8, body caps before parsing;
- S2, authorization in the pipeline;
- S8, Data Protection keys in PostgreSQL;
- W4, SignalStore.

The rest were recurring LLM habits:
- copy-paste instead of reuse;
- hand-rolled replacements for framework features;
- comments used as a process log;
- scaffolding left in production;
- invariant checks that nothing can ever trigger.

This matches published data on AI-assisted code. GitClear's 2025 study reports roughly 8× more duplicated blocks in 2024 and, for the first time, more copy/pasted lines than moved (refactored) lines ✅ [devclass](https://www.devclass.com/ai-ml/2025/02/20/ai-is-eroding-code-quality-states-new-in-depth-report/1626250). Hence the rules below. Each rule says how it is enforced, because an instruction file alone is followed "nearly every time", not every time ✅ [Claude Code best practices](https://code.claude.com/docs/en/best-practices).

Legend: ✅ verified with a link · ⚠️ unverified · `[OPEN]` gate not built yet. Rule ids (`U1`, `N3`, …) may be cited in reviews, commits and PRs. They must **never** appear in source-code comments (U1).

---

## 0. Workflow every agent follows

1. **Before writing code**
   - Read the section of this file for each layer you touch.
   - List the AGENTS.md invariants the change can affect.
   - Search for an existing helper (§7 catalogue, then `rg`) before writing one (U2).
2. **While writing**
   - Keep the change to one concern.
   - Prefer extending an existing type over adding a parallel one.
3. **Before saying "done"**
   - Run `scripts/verify.sh --stage <stages for the layers you touched>` (`--list-stages` lists them). A bare `scripts/verify.sh` picks the `docs`, `no-dotnet` or `full` tier from the changed paths (01.2.1-T7) and prints one `== gate scope:` line naming the tier, the reason and the base. It runs the .NET stages whenever the change set holds any path outside the allow-lists — a `*.cs` or build file anywhere, `src/`, `tests/`, `fixtures/`, `migrations/`, `contracts/`, `infra/`, a gate file, `docs/security/audit-and-deletion-rule.md`, a story file the change deletes — and also when a base does not resolve or the classifier fails; `docs/engineering/omp-setup.md` Step 4a lists the exceptions. Run `EVIDGATE_GATE_SCOPE=full` or name the stages when you need the whole gate.
   - Tiers (01.2.1-T7): `docs`, `review-infra` (a diff confined to `infra/review/**` that is not a Dockerfile, `scripts/review/**` and docs; adds the `check-review-infra` stage), `no-dotnet`, `full`.
   - Re-read your diff against [`REVIEW.md`](../../REVIEW.md).
   - In the PR, state what you could not run or verify. "Tests pass" without the command and its output is not evidence.
4. **When a reviewer finds a new class of defect**
   - Add or extend a gate (§8) so it cannot recur.
   - Then add a rule here.
   - A rule without a gate is a last resort.

---

## 1. Universal rules (all languages)

| Id | Rule | Evidence from the 2026-10-29 review |
|---|---|---|
| **U1** | **Comments explain *why*, briefly.** <br>• Never put plan, task, ticket, review or session ids (`01.4.2-T1.3`, `W7-SEC-02`, `S123`, `§8.5`, `T76`) in source comments. Traceability belongs in the commit message, the PR and the feature plan. <br>• No comment restates the code. <br>• Keep a comment to about 3 lines; anything longer is an ADR or feature-plan paragraph that the comment links to. <br>• A behaviour change updates or deletes the comments and READMEs it falsifies, in the same commit. | Comments made up 25–35 % of lines, with about 1,300 plan ids in BuildingBlocks, 1,839 in six modules and 580 in `apps/web`. Several were already false (Angular stores, `adapter-playwright` README). |
| **U2** | **Reuse before you write.** <br>• Search §7 and the code base first. <br>• A second copy of security, protocol or wire-format logic is a defect: extract it on the second occurrence. <br>• Other code is extracted on the third. <br>• Never fork a helper to tweak it; add a parameter. | Bounded body reader ×8, cursor codec ×7 (despite `KeysetCursor`), `Throttle` ×4, `Base64Url` ×5, sha256 regex ×5, two spool implementations in TS, retry timer ×12 in Angular. |
| **U3** | **Use the framework first.** <br>• §2 and §5 list the required primitives. <br>• A hand-rolled replacement for a framework or platform feature needs an ADR (`docs/adr/`) saying why the standard one does not fit. | Hand-written HTTP server, SQL predicate parser at boot, log redactor (×2), gzip/CRC32, rejection sampling, hand-rolled dialogs and stores. |
| **U4** | **No scaffolding in production.** <br>• No `InMemory*`, `Mock*`, `Fake*`, `Stub*`, `Slice*` or demo type may be registered by `Evidgate.Api`/`Evidgate.Worker` composition outside `IsDevelopment()`. <br>• When the real feature lands, the skeleton is deleted in the same PR. | `/v1/slice/run` was mapped unconditionally onto unbounded singleton dictionaries; `SliceRunConsumer` ran on every Worker boot; `IHealingLlmProvider` resolved to `MockHealingLlmProvider`. |
| **U5** | **Security checks fail closed whatever the shape of the call.** <br>• A check may never be skipped because of a return type, a missing attribute, a null or an unknown value. <br>• Unknown means deny. | The pipeline skipped tenant, authorization and idempotency when `TResponse` was not `Result`, so a handler with `[Permission]` that returned a DTO ran unchecked. |
| **U6** | **Every invariant ships with its producer and a test that makes it fire.** <br>• Adding a reason code, a state, a guard or a flag (`stale`, `blocked`, `missing_*`) requires: <br>  (a) the code that sets it, and <br>  (b) a test where it is set and changes the outcome. <br>• A check nothing can trigger is not done. It must not be merged as "scaffolding for later". | `event.stale` was never written, so `stale_epoch` could never fire. Completeness ignored planned executions. Late evidence never produced a revision. |
| **U7** | **Size limits.** <br>• File ≤ 500 lines (generated code excluded). <br>• Method or function ≤ 60 lines. <br>• Cognitive complexity ≤ 15 (the Sonar S3776 default ⚠️). <br>• Split before you exceed a limit, by responsibility, not by line number. | 26 module files over 500 lines; `AuthoringVersionWriter.cs` has 2,434 lines; 72 methods over 60 lines; Angular stores of 700+ lines. |
| **U8** | **Do not bend code to satisfy a gate.** <br>• No `as unknown as` and no `!` to silence a type. <br>• No logging a secret so a test can check masking. <br>• No `--ignore-exit-code`, `Assert.Skip` or relaxed threshold to make CI green. <br>• No wrapper whose only purpose is to dodge an analyzer. <br>• If a gate is wrong, change the gate in its own PR with a reason. | `onEnd` typed `void` via `as unknown as void`; the API logged its connection string at Information for a test; `verify.sh` used `--ignore-exit-code 8` on the hostile and fuzz stages. |
| **U9** | **Claims must be true.** <br>• Never write "idempotent", "safe", "atomic", "re-runnable" or "fail closed" in a comment, doc or PR unless a test proves it. <br>• Name tests for what actually causes the failure. | Migrations 0013 and 0053 said "idempotent" and were not; `CrashAfterCommit_TaskStillDispatched` crashed *before* commit. |
| **U10** | **Small, single-concern changes.** <br>• Aim for ≤ ~400 changed non-generated lines per PR ⚠️ (heuristic). <br>• Refactors and behaviour changes go in separate commits. <br>• A task too big for that is split in its feature plan first. | Large single-pass features produced the god classes and duplicated paths (for example the importer's second write path beside `AuthoringVersionWriter`). |

---

## 2. C# / .NET (`src/`)

| Id | Rule |
|---|---|
| **N1** | Every Mediator handler returns `Result`/`Result<T>` (00 §5.5 C8). A handler with `[Permission]`, `[CredentialAuthenticated]` or `[AuthenticatedMember]` and a non-`Result` response fails `Architecture.Tests` `[OPEN]`. Pipeline behaviours throw on an unexpected shape; they never pass through (U5). |
| **N2** | **Thin endpoints.** An endpoint binds, sends one Mediator message and maps the `Result`. <br>• JSON bodies are validated by the contract filter (`ValidateContractBody`, 00 §5.5 Edge). Hand-written `EnumerateObject`/`TryGetProperty` validation in handlers is not allowed. <br>• `Application/` does not use `HttpContext`, `StatusCodes`, `IResult` or raw body bytes. |
| **N3** | **Layering inside a module.** <br>• `Domain` references nothing but SharedKernel. <br>• `Application` depends on interfaces; SQL text and EF/Dapper types live in `Infrastructure`. <br>• `Application` never imports an `Endpoints` namespace. |
| **N4** | **Module data is private.** <br>• A module's SQL reads and writes only its own tables. <br>• Another module's data is reached through that module's `.Contracts` interface. <br>• Raw `public.project`/`public.membership`/`public.organization` reads from another module are not allowed. <br>• No `InternalsVisibleTo` from a module to another module or to a test assembly of a different module. |
| **N5** | **Rich domain for anything with a state machine** (00 §5.5 C6). <br>• Aggregates expose transition methods (`binding.Activate(now)`) that enforce the 00 §8 transitions. <br>• No public setters on aggregate state. <br>• Statuses are enums or value objects; string literals are allowed only at the SQL boundary. <br>• Immutable things (manifests, revisions, evaluations) are records with `init`-only members. |
| **N6** | **Options.** <br>• Every options class is bound with `ValidateOnStart()`. <br>• Nested objects and collections are validated with `[ValidateObjectMembers]`/`[ValidateEnumeratedItems]` and the options source generator ✅ [Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.options.validateenumerateditemsattribute.-ctor?view=net-8.0). `ValidateDataAnnotations` alone does not recurse into list items. |
| **N7** | **Background work.** <br>• A task handler's run time is bounded by its lease: a linked token with `CancelAfter(lease − margin)`, or a heartbeat that extends the lease. <br>• Cancellation caused by host shutdown releases the task; it never marks it failed. <br>• Polling loops use one shared helper with exponential backoff and a "recovered" log. Don't write another. |
| **N8** | **Exceptions and logging.** <br>• Catch the narrowest type. Never map a server-side `JsonException` (or any exception you did not cause from client input) to 400. <br>• Log an unhandled exception once, at `Error`. <br>• Use `[LoggerMessage]` with unique event ids per module range. <br>• Secrets, connection strings, tokens and cookies are never logged, at any level, for any reason (U8). |
| **N9** | **Edge limits before parsing** (restates 00 §5.7 P8). <br>• Per-route `RequestSizeLimit`/`MaxRequestBodySize`. <br>• Checking `Length` after `JsonDocument.Parse` is not a limit. <br>• `/metrics` and detailed readiness are not served unauthenticated on the public port. |
| **N10** | **Multi-instance safe by default.** <br>• Security state is never process-local: Data Protection keys go to PostgreSQL (00 §5.7 S8); rate-limit windows and replay caches go to a shared store. ✅ [Microsoft Learn: web farm](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/web-farm) — the default key ring is per node and other nodes cannot decrypt it. <br>• Rate limits and client IPs come from `UseForwardedHeaders` with explicit `KnownProxies`/`KnownNetworks`. Since .NET 8.0.17/9.0.6, headers from unlisted proxies are ignored ✅ [Microsoft Learn](https://learn.microsoft.com/aspnet/core/breaking-changes/8/forwarded-headers-unknown-proxies). <br>• Production start-up fails if no trusted proxy is configured. <br>• A bounded in-memory structure evicts; it never refuses every new key once full. |
| **N11** | **Regex.** <br>• Full-string validation uses `\A…\z`, never `^…$`: in .NET `$` also matches before a final `\n` ✅ [Microsoft Learn: anchors](https://github.com/dotnet/docs/blob/master/docs/standard/base-types/anchors-in-regular-expressions.md). <br>• Use `[GeneratedRegex]` with a timeout. <br>• Shared shapes (slug, sha256 hex, uuid, secret reference) live in one `Shapes` class and must equal the SQL `CHECK` they mirror. |
| **N12** | **Framework primitives (U3).** Use these and do not hand-roll them: <br>• `MapGroup` for shared endpoint filters; <br>• `MapHealthChecks`/Kestrel for probes; <br>• `IMeterFactory` with one meter-name constant; <br>• `IHttpClientFactory` with resilience handlers (00 §5.7 P6); <br>• `GZipStream`/`System.IO.Hashing`; <br>• `RandomNumberGenerator.GetString`/`GetHexString` for random tokens; <br>• `CryptographicOperations.FixedTimeEquals` for secret comparison; <br>• `TimeProvider` for time (no `DateTimeOffset.UtcNow` in handlers); <br>• `Microsoft.Extensions.Compliance.Redaction` before writing a redactor. |
| **N13** | **DI.** <br>• No service resolution from the root `IServiceProvider` inside behaviours or singletons (no service locator). <br>• No `DbContext` created only to borrow its connection: use `ITenantDb`. <br>• No global Dapper type handler whose `SetValue` throws. |
| **N14** | **One unit of work for an action and its audit.** <br>• A state change and its audit row commit in the same transaction. <br>• No network call (S3, GitHub, LLM) inside a DB transaction (00 §5.7 P5). <br>• Cost or usage records of an external call are written in their own unit of work so a rollback cannot erase them. |
| **N15** | **Privilege checks are not limited to the self case.** <br>• A grant or invite checks that the caller holds every permission it hands out, whoever the target is, unless an ADR records the delegation as intended. <br>• Separation-of-duty rules (requester ≠ approver) are checked on roles, not only on user ids. |

---

## 3. PostgreSQL migrations (`migrations/`)

| Id | Rule |
|---|---|
| **D1** | **Every policy names its role.** Write `create policy … to evidgate_app` (or the one role it is for). Without `TO` a policy applies to all roles ✅ [PostgreSQL 18: CREATE POLICY](https://www.postgresql.org/docs/18/sql-createpolicy.html), and `evidgate_operator` gets `SELECT` through the 0001 default privileges. The operator's grant on a new table is decided explicitly: revoke it or add the T76 policy. `scripts/check-rls.py` fails on a policy without `TO`. |
| **D2** | **State columns have transition guards.** <br>• Any column the app may update that encodes a state machine (`state`, `closed_at`, `last_sequence`, …) gets a trigger that refuses illegal transitions, following `waiver_guard`/`policy_revision_immutable`. <br>• Append-only tables revoke `UPDATE, DELETE, TRUNCATE` from the app role. |
| **D3** | **References are foreign keys.** <br>• Every id column that points at a row has an FK, including role ids, project ids and actor ids where the target is tenant data. <br>• Same-project integrity uses 3-column composite FKs `(org_id, project_id, id)`, not triggers. <br>• `ON DELETE` is `NO ACTION` unless an ADR says otherwise; erasure goes through one owner function. |
| **D4** | **Follow the 0003 template.** <br>• Schema-qualified names. <br>• Named constraints. <br>• Guarded DDL (`if not exists` / `drop … if exists`) so a re-run is safe. <br>• `CHECK` on every enum-like text column. <br>• A size bound on free text and `jsonb` columns. <br>• `timestamptz`, `uuid` and `version` default 1. <br>• Policy names `<table>_app_tenant` / `<table>_owner_backfill`. |
| **D5** | **Locking.** <br>• Every migration sets `lock_timeout` (and `statement_timeout` for data moves). <br>• `NOT VALID` and `VALIDATE` go in separate migrations; otherwise there is no lock benefit. <br>• Follow [`docs/migrations-expand-contract.md`](../migrations-expand-contract.md). |
| **D6** | **Dedupe compares payloads.** <br>• Natural-key dedupe stores a payload hash. <br>• A reused key with a different hash is a conflict, never a silent `on conflict do nothing` (AGENTS.md invariant). |
| **D7** | **Definer functions** (restates 00 §5.4.8). <br>• `search_path = ''`, schema-qualified, audited. <br>• Time comes from `now()` inside the function, not a caller parameter. <br>• A function that changes platform-wide state (kill switches) is granted to a dedicated role, not `evidgate_app`. |
| **D8** | **Advisory locks** use the two-key form `pg_advisory_xact_lock(<namespace int>, hashtext(<id>))` with a namespace constant per purpose. |

---

## 4. TypeScript / Node (`packages/`, `scripts/*.mjs`)

| Id | Rule |
|---|---|
| **T1** | **One implementation per format.** <br>• On-disk and wire formats (spool, chunk names, open flags, path containment, artifact-kind tables) live in one shared module that the other packages import. <br>• A second implementation is a defect (U2). |
| **T2** | **One API call path.** <br>• Every HTTP call goes through one helper that owns the timeout, retry with backoff for retryable statuses (429, 503, transport errors when an `Idempotency-Key` is present), 401 handling and error mapping `[OPEN]`. <br>• Commands do not each re-implement it. <br>• Response bodies are always consumed or cancelled. |
| **T3** | **Packages are self-contained.** <br>• Never read repository files (`contracts/`, `fixtures/`) at runtime. Embed what is needed at build time and guard it with a parity test. <br>• `package.json` declares `type`, `engines` (the repo's Node major), `exports` and `files`. |
| **T4** | **Types are honest.** <br>• No `any`, no `as unknown as` and no non-null `!` outside generated code. <br>• Declare the real type (for example `Promise<…>` from a reporter's `onEnd`) and `await` it in tests. |
| **T5** | **Tests live with their subject and are named by behaviour.** <br>• No `*.qa3.test.ts`, `qa-s87.test.ts` or other review-round names. <br>• Tests for `apps/web` lint rules live in `apps/web`. |
| **T6** | **Scripts.** <br>• Shell: `set -euo pipefail` on line 2, quoted expansions, `trap` cleanup. <br>• Python: `def main() -> int` + `if __name__ == "__main__"`, `argparse`, type hints, `ruff format`. No one-line statements joined by `;`. |
| **T7** | **Containers.** <br>• Runtime images keep application files owned by root and read-only to the app uid (no `COPY --chown=$APP_UID`; follow `infra/docker/Dockerfile.worker`). <br>• Base images are pinned by digest. <br>• `USER` is non-root. |

---

## 5. Angular (`apps/web`), in addition to 00 §5.6 W1–W9

| Id | Rule |
|---|---|
| **A1** | **State.** <br>• Feature state is an `@ngrx/signals` `signalStore` (W4). No hand-rolled `@Injectable` stores. <br>• Shared behaviour is a store feature (`withCursorPaging()`, `withRetryAfter()`) `[OPEN]`, not a copied block. |
| **A2** | **Effects are for leaving the signal graph only** (DOM, storage, logging). <br>• Derived values use `computed()`. <br>• Values that are derived but also user-writable use `linkedSignal()`. <br>• Async loads keyed on signals use `resource()`/`rxResource()`. <br>• Using an effect to propagate state is discouraged by the framework ✅ [angular.dev: effects](https://angular.dev/guide/signals/effect). <br>• Generation counters for stale async results are a smell that a resource is missing. |
| **A3** | **Route data.** Route params and query params reach components through `withComponentInputBinding()` + `input()` or `toSignal(paramMap)`. Do not read them once from `route.snapshot`, and do not subscribe manually. |
| **A4** | **Shared UI primitives.** <br>• Dialogs use CDK `Dialog`, with one focus-trap mechanism. <br>• Lists over 200 rows use `app-data-table`. <br>• Heavy panels use `@defer` (W2). <br>• Don't write `standalone: true`; it is the default. |
| **A5** | **Permissions in the UI.** <br>• Permission codes come from generated TS constants (from `contracts/permissions.json`), never string literals. <br>• Guards that redirect to sign-in always pass the full original `returnUrl`. |
| **A6** | **Specs test behaviour.** <br>• Use CDK component harnesses (W9). <br>• No `componentInstance['private']()` and no casting to reach private members. |

---

## 6. Tests (`tests/`, `*.test.ts`, `*.spec.ts`)

| Id | Rule |
|---|---|
| **X1** | **Layout follows modules.** <br>• Write `<Module>.IntegrationTests` for code that needs PostgreSQL and `<Module>.UnitTests` for pure logic (parsers, rules, resolvers, policies). <br>• Never put another module's tests into `Tenancy.IntegrationTests`. |
| **X2** | **Use the shared harness** (`tests/Shared`: one container per assembly, reset between tests). <br>• Do not add a new `PostgreSqlBuilder` fixture. <br>• Do not create a fresh database per test. <br>• Reference data seeded by migrations (permission catalogue) survives resets. |
| **X3** | **No sleeps for synchronisation.** <br>• Wait on an observable condition: `pg_locks`/`pg_stat_activity` wait, `TaskCompletionSource`, or polling with a deadline. <br>• Time comes from `FakeTimeProvider`/`TimeProvider`, never `DateTimeOffset.UtcNow` in assertions. |
| **X4** | **A test must fail for the reason in its name.** <br>• Every deny/refuse test has a positive control that succeeds with only the variable under test changed. <br>• Tests that discover their inputs (tables, routes, types) assert the discovered set is non-empty and has a minimum size. |
| **X5** | **No silent skips.** <br>• A test guarding a security property cannot `Skip` in CI. <br>• Give it the CI environment it needs (for example `--network none`), or make the skip fail when `CI=true`. |
| **X6** | **Naming.** `Subject_Condition_ExpectedResult`. One class per subject. No `*QaTests`/`*QaExtraTests` siblings: fold them into the subject's class. |
| **X7** | **Architecture rules cover the real graph.** <br>• BuildingBlocks ↛ Modules. <br>• Module ↛ another module's non-Contracts project. <br>• Domain ↛ Application/Infrastructure. <br>• `InternalsVisibleTo` only to the module's own test projects. <br>• Each rule asserts its input is non-empty. |

---

## 7. Shared helper catalogue (search here first, U2)

| Need | Use | Location |
|---|---|---|
| Keyset cursor encode/decode | `KeysetCursor` | `src/BuildingBlocks/SharedKernel/Api/KeysetCursor.cs` |
| Error envelope | `ErrorEnvelope`, `Result`, `ResultFactory` | `src/BuildingBlocks/SharedKernel/{Api,Results}/` |
| Canonical JSON and hash (RFC 8785) | `JsonCanonicalizer`, `CanonicalHash` | `src/BuildingBlocks/Canonicalization/` |
| Secret-reference shape | `SecretShape` | `src/BuildingBlocks/SharedKernel/Security/SecretShape.cs` |
| Identifier shape (first/rest/last character sets, length bounds, no regex timeout) | `AsciiShape` | `src/BuildingBlocks/SharedKernel/Security/AsciiShape.cs` |
| Secrets | `ISecretProvider`, `SecretNames` | `src/BuildingBlocks/SharedKernel/Secrets/` |
| Log redaction | `SecretRedactor` (the copy in `src/Evidgate.Migrator/SecretRedactor.cs` is to be removed) | `src/BuildingBlocks/Observability/Redaction/` |
| Tenant-scoped Dapper | `ITenantDb` | `src/BuildingBlocks/Persistence/Tenancy/ITenantDb.cs` |
| Permissions | `Permissions` (generated), `[Permission]` | `src/BuildingBlocks/SharedKernel/Authorization/` |
| Audit | `[AuditedAction]`, the audit trail writer | `src/BuildingBlocks/SharedKernel/Authorization/`, `src/BuildingBlocks/Persistence/Audit/` |
| Keyset page step of a cursor walk (drop rows already shown, refuse a repeated cursor or a page without new rows; the run list's automatic top-up walk is not this step: it throws a plain `Error` naming the cursor, appends every row as served, never refuses an empty page, and caps itself at 5 pages / 1 000 rows) | `takeKeysetPage`, `KeysetStep` | `apps/web/src/app/shared/keyset-walk.ts` |
| Timer-delay ceiling and clamp for a `Retry-After`-style wait | `MAX_TIMER_DELAY_MS`, `clampTimerDelay` | `apps/web/src/app/shared/time/timer-delay.ts` |
| Bounded body reader, `Base64Url`, client-IP throttle key, regex `Shapes`, pepper ring, polling loop | **to be extracted** `[OPEN]`: until then, the PR that adds a third copy must extract it instead | — |

Add a row whenever a shared helper is created.

---

## 8. Enforcement map

An instruction is followed most of the time; a gate is followed every time. Anything that must hold with zero exceptions belongs in a gate, a hook or CI ✅ [Claude Code best practices](https://code.claude.com/docs/en/best-practices).

| Rules | Gate | Status |
|---|---|---|
| U1 (plan ids in comments) | `scripts/check-review-blockers.py --u1-ratchet` in the `check-review-blockers` stage: the regex for plan/task/review ids in `:(top,glob)src/**/*.cs`, `:(top,glob)apps/web/src/**/*.ts`, `:(top,glob)packages/*/src/**/*.ts`, counted per comment line over the head tree and ratcheted against `scripts/check-review-blockers.baseline.json` (above it is a BLOCK; below it only prints the number to adopt — `--u1-count <ref>` prints it, so the baseline follows the count down); its fixtures are `scripts/tests/test_check_review_blockers*.py` plus the `review_blockers_fixtures.py` helper, and the entry, the `scripts/review_blockers` package that holds the rules, those fixtures and the baseline are each a `WARN gate-file` on every edit | `check-review-blockers` |
| U2, T1 | Copy-paste detector (`jscpd`) in CI with a duplication threshold and ratchet ⚠️ (C# support to confirm with `jscpd --list`) | `[OPEN]` |
| U7 | .NET: SonarAnalyzer.CSharp S3776 (cognitive complexity) and S138 (method length) as errors ⚠️. TS: ESLint `max-lines`, `max-lines-per-function`, `complexity`. Baseline file for existing violations. | `[OPEN]` |
| U3, N12, X3 | `Microsoft.CodeAnalysis.BannedApiAnalyzers` with `BannedSymbols.txt` (RS0030), for example `T:System.Net.Sockets.TcpListener`, `P:System.DateTimeOffset.UtcNow` in handlers, `M:System.Threading.Tasks.Task.Delay(...)` in test projects ✅ [SSW rule](https://www.ssw.com.au/rules/use-banned-api-analyzers) | `[OPEN]` |
| U4, U5, N1, N3, N4, X7 | `tests/Architecture.Tests` rules | partly existing; new rules `[OPEN]` |
| D1, D2, D4 | `scripts/check-rls.py` extended: policy has `TO`, `WITH CHECK` present, guarded DDL. The diff level of D1 is wired: the `check-review-blockers` stage blocks a migration the change set touches whose `CREATE POLICY`, or `ALTER POLICY` that restates its roles, has no `TO <role>` or lists `public`, quoted or not; the whole-tree check in `check-rls.py` also inspects policies built by `EXECUTE` inside `DO $$ … $$` | `check-review-blockers` for T6's D1 half; `check-rls.py` checks |
| U6, X4 | Mutation testing on Orchestrator, Policy, Identity rules (Stryker.NET, `--break-at` threshold, ratchet) ⚠️ | `[OPEN]` |
| U8, X5 | `verify.sh` refuses `--ignore-exit-code`; CI fails on skipped tests tagged security. The diff level is wired: the `check-review-blockers` stage blocks an added `--ignore-exit-code` outside a line that tests for the flag, a new test skip and `as unknown as`; the `verify.sh` refusal is still owed (CR-06) | `check-review-blockers` for the diff level; `verify.sh` refusal `[OPEN]` |
| T7 | Dockerfile lint (`hadolint` plus a grep for `--chown=$APP_UID`) ⚠️ | `[OPEN]` |
| §0 step 3 | Agent harness hook: a `Stop` hook (Claude Code) or a pre-commit hook (other agents) runs the fast `verify.sh` stages and blocks "done" while they fail ✅ [Claude Code best practices](https://code.claude.com/docs/en/best-practices) | `[OPEN]` |
| gate scope (§0 step 3) | `scripts/gate_scope.py`, run by the `check-gate-scope` stage and consumed by a bare `scripts/verify.sh`: the tier (`docs`, `no-dotnet`, `full`) comes from the change set's paths, allow-lists only, so an unknown path, an unresolvable base, a failing git call, an empty judged set or an unreadable `EVIDGATE_GATE_SCOPE` is `full`; the docs tier is widened to `full` by `docs/security/audit-and-deletion-rule.md` and by a deleted or renamed-away `plans/features/` story file, and to `no-dotnet` by `docs/adr/**`; any .NET file anywhere, a build or tool configuration, a gate file and a committed `.omp/` path are `full`; `fixtures/web-api/**` is not safe; `EVIDGATE_REVIEW_BASE`, when set, adds a base and the change set is the union, so it widens and never narrows; a requested scope widens a tier and never narrows it; an explicit `--stage` list is never filtered; CI sets `EVIDGATE_GATE_SCOPE=full`. Fixtures: `scripts/tests/test_gate_scope.py` (allow-lists, exceptions, git plumbing, base failures, request handling, the `ALL_STAGES` mirror) and `scripts/tests/test_verify_scope.py` (the runner's tier selection and its printed lines) | `check-gate-scope` |
| everything | Independent review against [`REVIEW.md`](../../REVIEW.md), by a different model or a human than the author | process |

When a gate lands, change its row to the stage name in `scripts/verify.sh`. New gates start in ratchet mode: existing violations sit in a baseline file and only new ones fail.

---

## Sources (retrieved 2026-10-29)

- ✅ Claude Code, *Best practices*: instructions vs deterministic hooks; keep instruction files short. https://code.claude.com/docs/en/best-practices
- ✅ Claude Code, *Customize Code Review*: `REVIEW.md` for review-only rules. https://code.claude.com/docs/en/code-review-customize
- ✅ GitClear 2025 report, via DevClass. https://www.devclass.com/ai-ml/2025/02/20/ai-is-eroding-code-quality-states-new-in-depth-report/1626250
- ✅ PostgreSQL 18, `CREATE POLICY`. https://www.postgresql.org/docs/18/sql-createpolicy.html
- ✅ ASP.NET Core forwarded headers and unknown proxies. https://learn.microsoft.com/aspnet/core/breaking-changes/8/forwarded-headers-unknown-proxies
- ✅ ASP.NET Core web farm and Data Protection. https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/web-farm
- ✅ .NET regex anchors. https://github.com/dotnet/docs/blob/master/docs/standard/base-types/anchors-in-regular-expressions.md
- ✅ `ValidateEnumeratedItemsAttribute`. https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.options.validateenumerateditemsattribute.-ctor?view=net-8.0
- ✅ Angular, effects guidance. https://angular.dev/guide/signals/effect
- ✅ BannedApiAnalyzers usage. https://www.ssw.com.au/rules/use-banned-api-analyzers
- ⚠️ Sonar S3776 default threshold 15 and S138 rule key: from community posts; confirm on rules.sonarsource.com before configuring.
- ⚠️ jscpd C# support and threshold semantics: confirm with `jscpd --list` and the installed version's docs.
- ⚠️ Stryker.NET `--break-at`: confirm at https://stryker-mutator.io/docs/stryker-net/configuration.
