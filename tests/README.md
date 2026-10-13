# tests/

Test projects (00 §5.2): `<Project>.UnitTests`, `<Module>.IntegrationTests` (Testcontainers), `Architecture.Tests`, `Contract.Tests`, `Isolation.Tests`. Test projects are created by 01.2.1 (unit/architecture conventions) and 01.6.1 (integration harness), not by the skeleton story.

## Naming convention (01.6.1-T1.1)

| Suffix                      | Meaning                                                                                                                                                                                                                                                  | Docker | Example                      |
| --------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------ | ---------------------------- |
| `<Project>.UnitTests`       | Pure in-process logic. No network, no container, no database. Gets the no-network guard (below).                                                                                                                                                         | no     | `Canonicalization.UnitTests` |
| `<Module>.IntegrationTests` | Real PostgreSQL/Kestrel/S3 through Testcontainers; every Docker-backed test _class_ carries `[Trait("Category","Docker")]` (in-process classes in the same assembly do not, so `EVIDGATE_VERIFY_SKIP_DOCKER=1` skips exactly the container-backed ones). | yes    | `Migrator.IntegrationTests`  |
| `Architecture.Tests`        | Reflection/layout rules over the `Evidgate.*` assemblies (01.2.1).                                                                                                                                                                                       | no     | `Architecture.Tests`         |

Every project under `tests/` deliberately avoids the `Evidgate.` prefix (the `<Project>`/`<Module>` part is free), so the `Architecture.Tests` reflection scan (`Evidgate.*.dll` in the output directory) never analyses a test assembly.

## Hostile-input corpus (06.3.2-T1)

`fixtures/hostile/` is the committed hostile corpus of the 06.3.1 parser profile, indexed by
`fixtures/hostile/manifest.json` (format: `fixtures/hostile/README.md`). `tests/Tenancy.IntegrationTests/HostileFixtureTests`
drives every manifest row through the entry point the row names — `IParsing.ParseXmlReport`/`EnumerateArchive` for
the bytes, the completion's media sniff and size cap for a hostile object at `complete` — and asserts the documented
failure code, with one row asserted ACCEPTED as the positive control; `HostileFixtureMutationSmokeTests` runs the
archive rows against one deliberately weakened `ZipArchiveOptions` cap to prove the guard is load-bearing.

Both classes need no container, but they are selected by their own trait rather than by `Category=Docker`:
`[Trait("Category","Hostile")]`, which `scripts/verify.sh --stage fixtures-hostile` (and the `fixtures-hostile` CI
job) selects with `dotnet test --filter-trait "Category=Hostile" --ignore-exit-code 8`. That way the corpus runs
as its own gate on every PR while the assembly's Docker suites stay untouched, and
`EVIDGATE_VERIFY_SKIP_DOCKER=1` still skips exactly the container-backed classes. `Category=Hostile` is a
selection trait (the `test` stage's `--filter-not-trait "Category=Docker"` skip path ignores it); the Docker
column above still uses `Category=Docker` alone.

## Fuzz harness (06.3.2-T2)

`HostileFuzzHarness.cs` adds the seeded-mutation fuzzer 06.3.2-T2.1 asks for: it loads every
`fixtures/hostile/manifest.json` seed (the cheap generated `oversized.bin` included; the 10,500-entry
`zipbomb.zip` is deliberately not a seed — a mutated copy costs a central-directory walk with no bound well under
the per-input limit, and the corpus test plus the mutation smoke already carry it), mutates them with a
deterministic PRNG (splitmix64, so a recorded seed replays a run exactly), and drives every mutated input through
`IParsing.ParseXmlReport` and `IParsing.EnumerateArchive`. `HostileFuzzTests` and `FuzzPromotionTests` carry
`[Trait("Category","Fuzz")]`, which `scripts/verify.sh --stage fixtures-fuzz` (and the nightly `fixtures-fuzz` CI
job) selects with `dotnet test --filter-trait "Category=Fuzz" --ignore-exit-code 8`.

**`[DECISION]`** 06.3.2-T2.1 ships the seeded mutation fuzzer instead of `SharpFuzz`: SharpFuzz is only
coverage-guided together with the AFL/libFuzzer native tooling plus an IL-instrumented copy of the parser assembly
in the CI image, and any new dependency needs a licence-scan pass and an ADR-0002 row (00 §5.7 S6).
`scripts/licence-policy.json` and `docs/adr/0002-compatibility-and-licence-matrix.md` have no SharpFuzz entry
(grepped: zero hits), so the story's "or a seeded mutation fuzzer if the licence check fails" arm applies. The run
adds no dependency.

**Oracle.** Only documented outcomes pass: a `Result` success, or a failure carrying one `content` detail whose
reason is in the closed `ParsingFailureReason` set under the code and status that reason is documented with
(`artifact_invalid`/422, or `payload_too_large`/413 for `size_exceeded`). Anything else — an escaped exception, an
input abandoned at the per-input bound, a failure with an undocumented reason, path, status or detail count — is a
finding: its bytes go to the findings directory and the test fails. Mutated bytes are never executed or extracted.

**Budget.** `EVIDGATE_FUZZ_BUDGET_SECONDS` (default 5; the `fixtures-fuzz` stage exports 600 unless the caller
already set one), `EVIDGATE_FUZZ_MAX_ITERATIONS`, `EVIDGATE_FUZZ_PER_INPUT_MS` and `EVIDGATE_FUZZ_FINDINGS_DIR`
(default a temp directory; the stage points it at `artifacts/fuzz-findings`, which the nightly job uploads on
failure and which always receives a `run.json`). `EVIDGATE_FUZZ_SEED` replays a run exactly: the `fixtures-fuzz`
stage sets it from the CI run id (the clock outside CI) and echoes it, so consecutive nights do not repeat one
PRNG stream, while the harness's constant default `20261013` covers the PR smoke, which never sets the variable.
The `test` stage therefore runs only the few-second smoke, never the ten-minute budget; the `fixtures-hostile`
stage selects `Category=Hostile` only and never runs the fuzz harness.

**Promotion.** `FuzzPromotion.Promote` writes a finding into `fixtures/hostile/` as a new committed fixture plus
its `manifest.json` row, so a crash the nightly run finds becomes a permanent case the T1 suite asserts from then
on. It refuses unless the explicit opt-in is passed and `EVIDGATE_FUZZ_PROMOTE=1` is set — which no CI job sets —
so a CI run never modifies the corpus. The promoted row's `expected_failure_code` starts at the conservative
`malformed`; the operator reviewing a real crash tightens it to the reason the fixed profile returns, and the T1
corpus test proves the row. `FuzzPromotionTests` drives the helper against a temp copy of the corpus, never the
committed one.

## Shared build conventions — `tests/Directory.Build.props`

Imported automatically by every project under `tests/`. It imports the repository-root `Directory.Build.props` first, then adds what every test project shares:

- `<OutputType>Exe</OutputType>` (`global.json` pins `test.runner` to Microsoft.Testing.Platform).
- `Microsoft.NET.Test.Sdk`, `xunit.v3`, `xunit.runner.visualstudio`, `Shouldly`, `NSubstitute`.
- The no-network guard source file for projects whose name ends in `.UnitTests` (matched case-insensitively).

Container, ASP.NET, storage and driver packages stay in the individual csproj files. A project must not re-add the shared references.

## No-network default for unit projects (01.6.1-T1.1, 00 §5.6 W9)

`tests/Shared/NoNetworkGuard.cs` is compiled into every `*.UnitTests` assembly (the props inject it; the file also carries `[assembly: AssemblyFixture]`), so it arms before the first test runs:

- The process default proxy (`HttpClient.DefaultProxy`) is set to a black hole on the reserved loopback port 1, and that proxy bypasses nothing — `IsBypassed` is false for a normal host _and_ for `localhost`/`127.0.0.1` (measured, 01.6.1-T1.1). A `HttpClient` on its default handler therefore fails with `HttpRequestException` (`Connection refused (127.0.0.1:1)`) instead of reaching the network or a loopback test server.
- `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` (upper and lower case) are set to the same black hole and `NO_PROXY` is emptied, so a child process that builds its proxy from the environment (Node CLI, curl, git) cannot reach the network either.

It does **not** catch raw `Socket`/`Dns` calls or ADO.NET/Npgsql connections: no managed hook intercepts those, so they remain forbidden by review. Suites that genuinely need sockets are `*.IntegrationTests` and do not get the file.

Each unit assembly also runs `NoNetworkConventionTests`, three I/O-free checks that fail as soon as the assembly fixture stops running (verified by commenting the attribute out): `Guard_Is_Armed` reads the AppContext flag, `Http_Requests_Route_To_The_Blackhole` asserts what `HttpClient.DefaultProxy` answers for a normal host and for loopback, and `Environment_Proxies_Route_To_The_Blackhole` asserts the `*_PROXY`/`NO_PROXY` variables the guard pins. A deliberate network call to a real host is blocked with `HttpRequestException` while the guard is armed, and succeeds without it. What the guard still does not stop: a raw `Socket`/`Dns` call, an ADO.NET/Npgsql connection, or an `HttpClient` built with an explicit `UseProxy = false` handler — those stay review-only.

## Integration harness — PostgreSQL (01.6.1-T2)

`tests/Shared/DatabaseFixture.cs` (+ `RespawnCheckpoint.cs`, `TenantTestScope.cs`, `HarnessTest.cs`) is the shared Testcontainers harness. `tests/Foundation.IntegrationTests` is its own verification suite; every later `*.IntegrationTests` project should link the five files (these four plus `ContainerImages.cs`) and delete its private per-test-database fixture. That migration of the existing projects is a follow-up, not part of 01.6.1-T2: `Persistence.IntegrationTests`, `Tasks.IntegrationTests`, `Tenancy.IntegrationTests`, `Hosts.IntegrationTests`, `Migrator.IntegrationTests` and `ObjectStorage.IntegrationTests` still carry their own fixtures and keep their own counts.

**What it gives a test.** One digest-pinned `postgres:18.6` container per assembly; one database created and migrated to head **once**, by the real `Evidgate.Migrator` running as `evidgate_owner` (superuser bootstrap: `create role evidgate_owner login createrole nosuperuser nocreatedb nobypassrls`, then `create database … owner evidgate_owner`, then `alter role evidgate_app login`); an app-role `NpgsqlDataSource` + `AddEvidgateTenancy` registration the tenant path borrows from; `SeedOrgAsync`/`SeedProjectAsync` (and the raw `SeedAsync`) as the single, clearly marked owner-connection seam; `BeginTenantScopeAsync(orgId, projectId?)` returning a `TenantTestScope`; `JournalHeadAsync`/`JournalCountAsync` read as the app role. Adoption:

- link the five shared files, add `Testcontainers.PostgreSQL`, `Npgsql`, `Respawn` and the `Evidgate.BuildingBlocks.Persistence` + `Evidgate.Migrator` project references (no version attributes — central package management);
- derive each test class from `HarnessTest` (it resets in `IAsyncLifetime.InitializeAsync`, i.e. per test case) and mark it `[Collection(HarnessSuite.Name)]` + `[Trait("Category","Docker")]`; the shared files declare namespace `Evidgate.Testing`, so the test files need `using Evidgate.Testing;`.

**Reset between tests (01.6.1-T2.2, AC2).** Respawn empties every table of every non-system schema and preserves `public.schema_migration`, so the Migrator's journal (and the readiness state built on it) survives. On the pinned Respawn 7 that is one `truncate table "public"."rls_probe","public"."idempotency_record","public"."project","public"."organization","public"."task","public"."outbox" cascade;` (captured from `Respawner.DeleteSql` during this slice — table order is Respawn's, and every table of `migrations/0001`–`0008` is in the list; do not trust the `delete from` literals in the DLL — they come from the other adapters). It connects as **`evidgate_owner`** because TRUNCATE follows table **ownership**: the owner owns every object the migrations created. Row level security plays no part — PostgreSQL never applies RLS to TRUNCATE, and no policy is needed for the reset to work. `evidgate_app` is the wrong role on rights: 0001 grants it DML and never TRUNCATE, and explicitly revokes TRUNCATE on the journal. The container superuser would also work and is deliberately not used: the harness must not need an escape hatch a real deployment does not have, and `evidgate_owner` is the role a deployment already runs the sweeps as (TL T80). The table list is read once, at harness start, from the catalog of the discovered schemas — `Respawn_LeavesNoRowInAnyApplicationTable` is the outcome check for that plan (seeded rows in, reset, then a **superuser** census of every application table), so a table the plan missed cannot hide.

**Collection serialisation rules.** One database per assembly, reset between tests, is only safe if tests of that assembly do not overlap: every harness test class is in the `postgres-harness` collection, which is `DisableParallelization = true`, so within an assembly the harness runs one test at a time even though xUnit's default is collection-level parallelism. Parallelism therefore lives **across** assemblies (each `*.IntegrationTests` project boots its own container, and project-level `dotnet test` may run them concurrently). Consequences for a test: do not start background work that outlives the test, do not hold a connection across the reset, and take every read/write-path assertion through `BeginTenantScopeAsync` — the owner connection is for arranging rows and for harness bookkeeping (counting rows, reading `pg_roles`, the empty-table census) only.

**Timing budget (01.6.1-T2.4, AC3).** `DatabaseFixture.SuiteBudget` is 10 minutes (NFR-01-1). The fixture times the whole assembly — container start, Migrator run, every reset — prints `[evidgate harness] suite wall time …s of a 10 min budget after N Respawn reset(s)` at the end of the run, and fails the assembly from `DisposeAsync` when the budget is exceeded, so the number is a gate and not a note. Measured locally on the reference host (2026-09-26, colima, warm image): 3.3 s wall time for the ten tests of `Foundation.IntegrationTests` — 12 Respawn resets (one per test case plus the two explicit resets inside the census and journal tests), container start included — of which ~2.5 s is container start.

**AsyncLocal rule (do not wrap `Scopes.BeginAsync`).** `NpgsqlTenantScopeFactory.BeginAsync` publishes the ambient scope into an `AsyncLocal` synchronously, in the caller's execution context, and `ExecutionContext` flows only downwards. A test that calls it through an `async` helper therefore gets a scope the Dapper path (`ITenantDb`, and the EF interceptor) cannot see — the failure looks like `TenantScopeMissingException` on a scope that is open. `DatabaseFixture.BeginTenantScopeAsync` is non-`async` for that reason (it continues the factory's task instead), and a harness test must do the same: `await Fixture.Scopes.BeginAsync(...)` directly in the test method, or use `BeginTenantScopeAsync`.

**What the suite proves (01.6.1-T2).** `Fixture_MigratesToHead` (journal head equals the baked `ExpectedMigrationHead`, every file applied), `Harness_UsesAppRole` (AC3: `current_user` is `evidgate_app` on the data source and on a scope), `AppRole_HasNoBypassRls` (AC3: catalog flags of the four roles, plus a row of another tenant staying invisible to the app role), `Respawn_TwoTestsSeeEmptyDb_InsertsARow` / `_ObservesAnEmptyTable` (AC2; both halves assert the table is empty before writing, so the pair fails whichever half runs second when the reset is off — verified by disabling the reset in both execution orders: `Failed: 1` each time, on the half that ran second), `Respawn_LeavesNoRowInAnyApplicationTable` (AC2: superuser census of every application table after a reset), `Respawn_PreservesMigrationJournal` (AC2: the journal survives), `TenantScope_MissingOrgContext_Throws` (AC4/C3: the Dapper accessor, the scope factory and the EF Core `TenantCommandInterceptor` all refuse an un-scoped command, and the same probe context succeeds on the scope's connection and transaction), `SeedOrgAndProject_RoundTrip` (AC4), `TenantScope_ReadOnly_RejectsWrite` (C1/T95). A real module context (EF read/write over RLS) is still 01.6.1-T4's isolation suite; the module-level interceptor coverage of 01.4.2 stays where the module lives.
