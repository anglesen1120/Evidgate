// 06.5.1-T1/T2 review follow-up (D-S57-Q10/Q11): the guards of the two statements under a forged
// batch (F1), the read's ready-artifact cap (F4), the documented result order (F2) and the
// affected-row count of the resolving UPDATE (F3). The guards are asserted through the application
// role's tenant scope — and once through the owner role, where row level security no longer hides a
// foreign org — so a statement that lost a predicate fails here even though RLS would still hide the
// row from the app role.
#pragma warning disable CA1861
using System.Data.Common;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application.Freeze;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The 06.5.1 SQL against a real PostgreSQL: <see cref="FreezeSql.SelectReadyArtifacts"/> capped by
/// <c>@max_rows</c>, <see cref="FreezeSql.ResolveAttemptIds"/> refusing every row a forged batch names
/// outside its scope, the affected-row count of that statement (D-S57-Q11) and the 06.5.1 §5 result
/// order (F2). Each case fails when the behaviour it names is removed: the forged batch carries a
/// positive control, so a statement that does nothing at all fails too.
/// </summary>
[Trait("Category", "Docker")]
public sealed class EvidenceFreezeGuardTests
{
    private readonly PostgresTenancyFixture _fixture;

    public EvidenceFreezeGuardTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ fixture

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>One migrated database with an org and its first project, created through the real registry routes.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>A second org with a project, through the real routes.</summary>
    private static async Task<(Guid Org, Guid Project)> CreateOtherTenantAsync(World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, slug);
        var project = await CreateProjectAsync(world.Client, user, org, slug + "-project");

        return (org, project);
    }

    // ------------------------------------------------------------------ seeding

    /// <summary>One committed run and its plan for <paramref name="world"/>'s org (or another org).</summary>
    private Task<PlannedExecutionSeeder.SeededRun> SeedPlanAsync(World world, int count = 1, Guid? org = null) =>
        PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, org ?? world.Org, count);

    /// <summary>One committed <c>case_attempt</c> batch for <paramref name="world"/>'s org.</summary>
    private static Task SeedAttemptsAsync(World world, Guid runId, params CaseAttemptInput[] attempts) =>
        SeedAttemptsAsync(world, world.Org, runId, attempts);

    /// <summary>One committed <c>case_attempt</c> batch for <paramref name="org"/>'s run.</summary>
    private static async Task SeedAttemptsAsync(World world, Guid org, Guid runId, params CaseAttemptInput[] attempts)
    {
        await using var scope = await AttemptScope.BeginAsync(world.Api, org);
        var outcomes = await scope.WriteAsync(runId, attempts);

        outcomes.ShouldAllBe(outcome => outcome.Status == CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();
    }

    /// <summary>The persisted <c>case_attempt.id</c> of one slot.</summary>
    private Task<Guid> CaseAttemptIdAsync(World world, Guid runId, string executionKey, int attemptNo) =>
        _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.case_attempt where org_id = '{world.Org}' and run_id = '{runId}' "
            + $"and execution_key = '{executionKey}' and attempt_no = {attemptNo}");

    /// <summary>One committed batch of artifact rows for <paramref name="org"/>'s project, in the order given.</summary>
    private async Task InsertArtifactsAsync(
        World world,
        Guid org,
        Guid project,
        Guid runId,
        IReadOnlyList<ArtifactSeed> seeds)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        foreach (var seed in seeds)
        {
            (await scope.ExecuteAsync(InsertArtifactSql(org, project, runId, seed))).ShouldBe(1);
        }

        await scope.CommitAsync();
    }

    /// <summary>One committed batch of artifact rows for <paramref name="world"/>'s own project.</summary>
    private Task InsertArtifactsAsync(World world, Guid runId, IReadOnlyList<ArtifactSeed> seeds) =>
        InsertArtifactsAsync(world, world.Org, world.Project, runId, seeds);

    /// <summary>
    /// Seeds <paramref name="count"/> <c>ready</c> artifacts of one run in a single
    /// <c>insert … select generate_series</c> statement: row-by-row seeding is far too slow for the
    /// <see cref="FreezeRequest.MaxReadyArtifacts"/> boundary (0028's CHECKs are all satisfiable in the
    /// select). The final key is built from each generated id, so the object-key shape CHECK holds.
    /// </summary>
    private async Task SeedReadyArtifactsBulkAsync(World world, Guid runId, string executionKey, int count)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        var sql = $"""
            insert into public.artifact
              (id, org_id, project_id, run_id, execution_key, attempt_no, attempt_id, kind, object_key,
               media_type, checksum, sensitivity, scan_state, state)
            select gen.id, '{world.Org}', '{world.Project}', '{runId}', '{executionKey}', 1, null,
                   '{ArtifactKind.Trace}',
                   'org/{world.Org}/project/{world.Project}/run/{runId}/attempt/{executionKey}/1/' || gen.id,
                   'application/zip', 'sha256:' || lpad(to_hex(gen.n), 64, '0'),
                   '{ArtifactSensitivity.Normal}', 'clean', '{ArtifactState.Ready}'
              from (select ('00000000-0000-0000-0000-' || lpad(to_hex(n), 12, '0'))::uuid as id, n
                      from generate_series(1, {count}) as n) as gen
            """;

        (await scope.ExecuteAsync(sql)).ShouldBe(count);
        await scope.CommitAsync();
    }

    /// <summary>
    /// One artifact row to insert: identity (<paramref name="Id"/>, <paramref name="ExecutionKey"/>,
    /// <paramref name="AttemptNo"/>, <paramref name="Kind"/>) plus the state, content address, already-resolved
    /// <paramref name="AttemptId"/> and <paramref name="Sensitivity"/> the case under test pins.
    /// </summary>
    private sealed record ArtifactSeed(
        Guid Id,
        string ExecutionKey,
        int AttemptNo,
        string Kind,
        string State = ArtifactState.Ready,
        string? Checksum = null,
        Guid? AttemptId = null,
        string Sensitivity = ArtifactSensitivity.Normal);

    /// <summary>One raw artifact insert as the app role (0028's grants), keyed by the row's own identity.</summary>
    private static string InsertArtifactSql(Guid org, Guid project, Guid runId, ArtifactSeed seed) =>
        "insert into public.artifact "
        + "(id, org_id, project_id, run_id, execution_key, attempt_no, attempt_id, kind, object_key, media_type, checksum, sensitivity, scan_state, state) "
        + $"values ('{seed.Id}', '{org}', '{project}', '{runId}', '{seed.ExecutionKey}', {seed.AttemptNo}, "
        + $"{(seed.AttemptId is { } attemptId ? $"'{attemptId}'" : "null")}, '{seed.Kind}', "
        + $"'{FinalKey(org, project, runId, seed.ExecutionKey, seed.AttemptNo, seed.Id)}', 'application/zip', "
        + $"'{seed.Checksum ?? ChecksumOf(seed.Id)}', '{seed.Sensitivity}', 'clean', '{seed.State}')";

    /// <summary>The 06.1.1 §1/AC2 final object key of one row's own identity (the 0028 shape CHECK).</summary>
    private static string FinalKey(Guid org, Guid project, Guid runId, string executionKey, int attemptNo, Guid id) =>
        $"org/{org}/project/{project}/run/{runId}/attempt/{executionKey}/{attemptNo}/{id}";

    /// <summary>A deterministic, well-formed content address for one artifact id, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    private static string ChecksumOf(Guid id) => "sha256:" + id.ToString("N") + id.ToString("N");

    /// <summary>The resolution batch statement 2 reads: one <c>{id, resolved_attempt_id}</c> element per row.</summary>
    private static string Batch(params (Guid Id, Guid ResolvedAttemptId)[] rows) =>
        "["
        + string.Join(
            ",",
            rows.Select(row => $$"""{"id":"{{row.Id:D}}","resolved_attempt_id":"{{row.ResolvedAttemptId:D}}"}"""))
        + "]";

    // ------------------------------------------------------------------ the caller's transaction

    /// <summary>
    /// The caller's tenant transaction with <see cref="ITenantDb"/> — the path the service itself uses — so a
    /// statement can be driven directly, with parameters the service would never build, inside a real scope.
    /// <para>
    /// Deliberately not <c>async</c> (tests/README.md, "AsyncLocal rule"): the factory publishes the scope into
    /// its ambient slot synchronously, in the caller's execution context.
    /// </para>
    /// </summary>
    private sealed class GuardScope : IAsyncDisposable
    {
        private readonly AsyncServiceScope _services;
        private readonly ITenantScope _scope;

        private GuardScope(AsyncServiceScope services, ITenantScope scope, Guid org)
        {
            _services = services;
            _scope = scope;
            OrgId = org;
            Db = services.ServiceProvider.GetRequiredService<ITenantDb>();
            Accessor = services.ServiceProvider.GetRequiredService<ITenantScopeAccessor>();
            Freeze = services.ServiceProvider.GetRequiredService<IEvidenceFreeze>();
        }

        /// <summary>The tenant Dapper path of this transaction.</summary>
        internal ITenantDb Db { get; }

        /// <summary>The scope accessor, for a service built over a decorated <see cref="ITenantDb"/>.</summary>
        internal ITenantScopeAccessor Accessor { get; }

        /// <summary>The service under test, running in this transaction.</summary>
        internal IEvidenceFreeze Freeze { get; }

        /// <summary>The scope's org.</summary>
        internal Guid OrgId { get; }

        /// <summary>Opens the caller's tenant transaction for <paramref name="org"/> (optionally narrowed to a project).</summary>
        internal static Task<GuardScope> BeginAsync(TenancyApiFactory api, Guid org, Guid? project = null)
        {
            var services = api.Services.CreateAsyncScope();
            var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();

            return scopes
                .BeginAsync(org, project, false, TestContext.Current.CancellationToken)
                .ContinueWith(
                    opened => new GuardScope(services, opened.GetAwaiter().GetResult(), org),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        /// <summary>One freeze of <paramref name="runId"/> with the caller's requirement set.</summary>
        internal Task<FreezeResult> RunAsync(Guid runId, params RequiredArtifactSpec[] required) =>
            Freeze.FreezeAsync(new FreezeRequest(runId, required), TestContext.Current.CancellationToken);

        /// <summary>Runs one statement of <see cref="FreezeSql"/> and returns its affected-row count.</summary>
        internal Task<int> ExecuteAsync(string sql, object? parameters) =>
            Db.ExecuteAsync(sql, parameters, TestContext.Current.CancellationToken);

        /// <summary>Runs a scalar query as this transaction sees it (own uncommitted writes included).</summary>
        internal Task<T?> ScalarAsync<T>(string sql, object? parameters) =>
            Db.ScalarAsync<T>(sql, parameters, TestContext.Current.CancellationToken);

        /// <summary>One row's <c>attempt_id</c>, <c>xmin</c> and <c>state</c> as this transaction sees them.</summary>
        internal Task<string?> RowSnapshotAsync(Guid artifactId) =>
            ScalarAsync<string>(
                "select coalesce(attempt_id::text, 'null') || '|' || xmin::text || '|' || state "
                + "from public.artifact where org_id = @org_id and id = @id",
                new { org_id = OrgId, id = artifactId });

        /// <summary>The C2 commit boundary; the freeze itself never commits.</summary>
        internal Task CommitAsync() => _scope.CommitAsync(TestContext.Current.CancellationToken);

        /// <summary>Releases the transaction scope (a rollback when <see cref="CommitAsync"/> was not called).</summary>
        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    /// <summary>
    /// An <see cref="ITenantDb"/> that changes the world on a second connection immediately before the
    /// resolving statement runs, so the race D-S57-Q11 names — an artifact leaving <c>ready</c> between the read
    /// and the <c>UPDATE</c> — happens deterministically instead of by timing.
    /// </summary>
    private sealed class RacingTenantDb(ITenantDb inner, Func<Task> beforeResolve) : ITenantDb
    {
        public DbConnection Connection => inner.Connection;

        public DbTransaction Transaction => inner.Transaction;

        public Task<IReadOnlyList<T>> QueryAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            inner.QueryAsync<T>(sql, parameters, cancellationToken);

        public Task<T?> QuerySingleOrDefaultAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            inner.QuerySingleOrDefaultAsync<T>(sql, parameters, cancellationToken);

        public Task<int> ExecuteAsync(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            sql == FreezeSql.ResolveAttemptIds
                ? RaceAsync(parameters, cancellationToken)
                : inner.ExecuteAsync(sql, parameters, cancellationToken);

        public Task<T?> ScalarAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            inner.ScalarAsync<T>(sql, parameters, cancellationToken);

        private async Task<int> RaceAsync(object? parameters, CancellationToken cancellationToken)
        {
            await beforeResolve();
            return await inner.ExecuteAsync(FreezeSql.ResolveAttemptIds, parameters, cancellationToken);
        }
    }

    /// <summary>One freeze of its own on a fresh tenant transaction, committed like 05's reconcile would.</summary>
    private static async Task<FreezeResult> FreezeAsync(World world, Guid runId, params RequiredArtifactSpec[] required)
    {
        await using var scope = await GuardScope.BeginAsync(world.Api, world.Org);
        var result = await scope.RunAsync(runId, required);

        await scope.CommitAsync();
        return result;
    }

    /// <summary>Every command one freeze issued (the host's app-role data source is logged by <see cref="TenancyApiFactory.Statements"/>).</summary>
    private static IReadOnlyList<string> Statements(World world) => world.Api.Statements.Snapshot();

    /// <summary>The commands that write (00 §5.7 P2's "no per-artifact write"): a plain <c>update</c> or a CTE one.</summary>
    private static IEnumerable<string> UpdateStatements(IEnumerable<string> statements) =>
        statements.Where(statement => statement.Contains("update ", StringComparison.OrdinalIgnoreCase));

    /// <summary>A committed row's <c>attempt_id</c>, <c>xmin</c> and <c>state</c>, read as the superuser.</summary>
    private Task<string> SnapshotAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            $"select coalesce(attempt_id::text, 'null') || '|' || xmin::text || '|' || state "
            + $"from public.artifact where id = '{artifactId}'");

    /// <summary>The 06.5.1 §5 digest computed independently of the service, over the documented tuple array.</summary>
    private static string ExpectedHash(IEnumerable<(Guid Id, string ObjectKey, string Checksum)> rows) =>
        CanonicalHash.Compute(
            "["
            + string.Join(
                ",",
                rows.Select(row =>
                    $$"""{"artifact_id":"{{row.Id:D}}","object_key":"{{row.ObjectKey}}","checksum":"{{row.Checksum}}"}"""))
            + "]");

    // ------------------------------------------------------------------ F1: the guards of the write statement

    [Fact]
    public async Task Freeze_ResolveSql_Guards_RefuseForgedBatch()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "rival");
        var secondProject = await CreateProjectAsync(world.Client, world.User, world.Org, "second");

        var plan = await SeedPlanAsync(world, 2);
        var key = plan.Executions[0].ExecutionKey;
        var otherRunPlan = await SeedPlanAsync(world);
        var foreignPlan = await SeedPlanAsync(world, 1, otherOrg);

        await SeedAttemptsAsync(
            world,
            plan.RunId,
            new CaseAttemptInput(key, 1, "passed"),
            new CaseAttemptInput(key, 2, "passed"));
        await SeedAttemptsAsync(
            world,
            otherOrg,
            foreignPlan.RunId,
            new CaseAttemptInput(foreignPlan.Executions[0].ExecutionKey, 1, "passed"));

        var boundAttempt = await CaseAttemptIdAsync(world, plan.RunId, key, 1);
        var forgedAttempt = await CaseAttemptIdAsync(world, plan.RunId, key, 2);
        var foreignAttempt = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.case_attempt where org_id = '{otherOrg}' and run_id = '{foreignPlan.RunId}'");

        // Every row a forged batch can name outside the statement's scope, plus one legitimate row.
        var alreadyBound = Guid.CreateVersion7();
        var notReady = Guid.CreateVersion7();
        var otherRun = Guid.CreateVersion7();
        var otherProjectRow = Guid.CreateVersion7();
        var foreignTenant = Guid.CreateVersion7();
        var control = Guid.CreateVersion7();

        await InsertArtifactsAsync(
            world,
            world.Org,
            world.Project,
            plan.RunId,
            [
                // (1) Already resolved to attempt A; the batch claims attempt B. `attempt_id is null` refuses it.
                new ArtifactSeed(alreadyBound, key, 1, ArtifactKind.Trace, AttemptId: boundAttempt),
                // (2) Left the ready set; `state = 'ready'` refuses it even though the batch says otherwise.
                new ArtifactSeed(notReady, key, 1, ArtifactKind.Video, State: ArtifactState.PendingDelete),
                // (6) The positive control: ready, unresolved, same run — the statement MUST touch this one.
                new ArtifactSeed(control, key, 2, ArtifactKind.Trace),
            ]);
        await InsertArtifactsAsync(
            world,
            world.Org,
            world.Project,
            otherRunPlan.RunId,
            // (3) Ready and unresolved, but another run's row: the run predicate refuses it.
            [new ArtifactSeed(otherRun, otherRunPlan.Executions[0].ExecutionKey, 1, ArtifactKind.Trace)]);
        await InsertArtifactsAsync(
            world,
            world.Org,
            secondProject,
            plan.RunId,
            // (4) Another project of the same org, reachable only through a run of the first project.
            [new ArtifactSeed(otherProjectRow, key, 1, ArtifactKind.Trace)]);
        await InsertArtifactsAsync(
            world,
            otherOrg,
            otherProject,
            foreignPlan.RunId,
            // (5) Another org's row, its id forged into the batch: the org predicate (and RLS) refuse it.
            [new ArtifactSeed(foreignTenant, foreignPlan.Executions[0].ExecutionKey, 1, ArtifactKind.Trace)]);

        var alreadyBoundBefore = await SnapshotAsync(world, alreadyBound);
        var notReadyBefore = await SnapshotAsync(world, notReady);
        var otherRunBefore = await SnapshotAsync(world, otherRun);
        var otherProjectBefore = await SnapshotAsync(world, otherProjectRow);
        var foreignTenantBefore = await SnapshotAsync(world, foreignTenant);

        // Project-scoped scope: every guard is in force, so exactly the control is updated.
        await using (var scope = await GuardScope.BeginAsync(world.Api, world.Org, world.Project))
        {
            var affected = await scope.ExecuteAsync(
                FreezeSql.ResolveAttemptIds,
                new
                {
                    org_id = world.Org,
                    project_id = (Guid?)world.Project,
                    run_id = plan.RunId,
                    rows = Batch(
                        (alreadyBound, forgedAttempt),
                        (notReady, boundAttempt),
                        (otherRun, boundAttempt),
                        (otherProjectRow, boundAttempt),
                        (foreignTenant, foreignAttempt),
                        (control, forgedAttempt)),
                });

            affected.ShouldBe(1);

            (await scope.RowSnapshotAsync(control)).ShouldStartWith(forgedAttempt.ToString("D") + "|");
            (await scope.RowSnapshotAsync(alreadyBound)).ShouldStartWith(boundAttempt.ToString("D") + "|");
            (await scope.RowSnapshotAsync(notReady)).ShouldStartWith("null|");
            (await scope.RowSnapshotAsync(otherRun)).ShouldStartWith("null|");
            (await scope.RowSnapshotAsync(otherProjectRow)).ShouldStartWith("null|");

            // A foreign org's row is not even visible to this scope (RLS plus the org predicate).
            (await scope.RowSnapshotAsync(foreignTenant)).ShouldBeNull();

            await scope.CommitAsync();
        }

        (await SnapshotAsync(world, control)).ShouldStartWith(forgedAttempt.ToString("D") + "|");
        (await SnapshotAsync(world, alreadyBound)).ShouldBe(alreadyBoundBefore);
        (await SnapshotAsync(world, notReady)).ShouldBe(notReadyBefore);
        (await SnapshotAsync(world, otherRun)).ShouldBe(otherRunBefore);
        (await SnapshotAsync(world, otherProjectRow)).ShouldBe(otherProjectBefore);
        (await SnapshotAsync(world, foreignTenant)).ShouldBe(foreignTenantBefore);

        // Project-scoped to the OTHER project: the artifact's own project predicate passes, so only the run
        // join (the run belongs to the first project) keeps it out — of the write and of the read alike.
        await using (var scope = await GuardScope.BeginAsync(world.Api, world.Org, secondProject))
        {
            var affected = await scope.ExecuteAsync(
                FreezeSql.ResolveAttemptIds,
                new
                {
                    org_id = world.Org,
                    project_id = (Guid?)secondProject,
                    run_id = plan.RunId,
                    rows = Batch((otherProjectRow, boundAttempt)),
                });

            affected.ShouldBe(0);
            (await scope.ScalarAsync<long>(
                $"select count(*) from ({FreezeSql.SelectReadyArtifacts}) limited",
                new
                {
                    org_id = world.Org,
                    project_id = (Guid?)secondProject,
                    run_id = plan.RunId,
                    max_rows = FreezeRequest.MaxReadyArtifacts + 1,
                })).ShouldBe(0);
        }

        (await SnapshotAsync(world, otherProjectRow)).ShouldBe(otherProjectBefore);

        // Org-wide scope: the artifact predicate allows the second project's row (it is genuinely inside the org
        // and run the caller named), while the other run, the not-ready row and the already-bound row stay out.
        var controlWide = Guid.CreateVersion7();
        await InsertArtifactsAsync(
            world,
            plan.RunId,
            [new ArtifactSeed(controlWide, key, 2, ArtifactKind.Video)]);

        await using (var scope = await GuardScope.BeginAsync(world.Api, world.Org))
        {
            var affected = await scope.ExecuteAsync(
                FreezeSql.ResolveAttemptIds,
                new
                {
                    org_id = world.Org,
                    project_id = (Guid?)null,
                    run_id = plan.RunId,
                    rows = Batch(
                        (alreadyBound, forgedAttempt),
                        (notReady, boundAttempt),
                        (otherRun, boundAttempt),
                        (foreignTenant, foreignAttempt),
                        (otherProjectRow, boundAttempt),
                        (controlWide, forgedAttempt)),
                });

            affected.ShouldBe(2);

            (await scope.RowSnapshotAsync(controlWide)).ShouldStartWith(forgedAttempt.ToString("D") + "|");
            (await scope.RowSnapshotAsync(otherProjectRow)).ShouldStartWith(boundAttempt.ToString("D") + "|");

            await scope.CommitAsync();
        }

        (await SnapshotAsync(world, controlWide)).ShouldStartWith(forgedAttempt.ToString("D") + "|");
        (await SnapshotAsync(world, otherProjectRow)).ShouldStartWith(boundAttempt.ToString("D") + "|");
        (await SnapshotAsync(world, alreadyBound)).ShouldBe(alreadyBoundBefore);
        (await SnapshotAsync(world, notReady)).ShouldBe(notReadyBefore);
        (await SnapshotAsync(world, otherRun)).ShouldBe(otherRunBefore);
        (await SnapshotAsync(world, foreignTenant)).ShouldBe(foreignTenantBefore);

        // The owner role bypasses the org-only RLS policy (artifact_owner_backfill), so the statement's own
        // predicates are what the next two calls exercise: the mixed batch below (matching run, foreign org's
        // artifact) and then the forged cross-org call where only the org predicate can refuse the row.
        var controlOwner = Guid.CreateVersion7();
        await InsertArtifactsAsync(
            world,
            plan.RunId,
            [new ArtifactSeed(controlOwner, key, 1, ArtifactKind.Screenshot)]);

        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var affected = await owner.ExecuteAsync(
                FreezeSql.ResolveAttemptIds,
                ("org_id", world.Org),
                ("project_id", (object)DBNull.Value),
                ("run_id", plan.RunId),
                ("rows", Batch(
                    (alreadyBound, forgedAttempt),
                    (notReady, boundAttempt),
                    (otherRun, boundAttempt),
                    (foreignTenant, foreignAttempt),
                    (controlOwner, boundAttempt))));

            affected.ShouldBe(1);

            (await owner.ScalarAsync<string>(
                "select coalesce(attempt_id::text, 'null') from public.artifact where id = @id",
                ("id", foreignTenant))).ShouldBe("null");
            (await owner.ScalarAsync<string>(
                "select attempt_id::text from public.artifact where id = @id",
                ("id", controlOwner))).ShouldBe(boundAttempt.ToString("D"));

            // The run and the org predicates refuse different rows. This call names ANOTHER org's run with this
            // org's id, so the run and project predicates both pass for that row; only `a.org_id = @org_id` (in the
            // batch and in the update) keeps it out — the mutation that removes the org guard changes 0 to 1 here.
            var crossOrg = await owner.ExecuteAsync(
                FreezeSql.ResolveAttemptIds,
                ("org_id", world.Org),
                ("project_id", (object)DBNull.Value),
                ("run_id", foreignPlan.RunId),
                ("rows", Batch((foreignTenant, foreignAttempt))));

            crossOrg.ShouldBe(0);
            (await owner.ScalarAsync<string>(
                "select coalesce(attempt_id::text, 'null') from public.artifact where id = @id",
                ("id", foreignTenant))).ShouldBe("null");

            await owner.CommitAsync();
        }

        (await SnapshotAsync(world, foreignTenant)).ShouldBe(foreignTenantBefore);
        (await SnapshotAsync(world, controlOwner)).ShouldStartWith(boundAttempt.ToString("D") + "|");
        (await SnapshotAsync(world, alreadyBound)).ShouldBe(alreadyBoundBefore);
        (await SnapshotAsync(world, notReady)).ShouldBe(notReadyBefore);
        (await SnapshotAsync(world, otherRun)).ShouldBe(otherRunBefore);
    }

    // ------------------------------------------------------------------ F1/F4: the read's row cap

    [Fact]
    public async Task Freeze_ReadSql_Limit()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await InsertArtifactsAsync(
            world,
            plan.RunId,
            [
                new ArtifactSeed(Guid.CreateVersion7(), key, 1, ArtifactKind.Trace),
                new ArtifactSeed(Guid.CreateVersion7(), key, 1, ArtifactKind.Video),
                new ArtifactSeed(Guid.CreateVersion7(), key, 1, ArtifactKind.Screenshot),
            ]);

        await using var scope = await GuardScope.BeginAsync(world.Api, world.Org, world.Project);
        var counted = $"select count(*) from ({FreezeSql.SelectReadyArtifacts}) limited";
        object Parameters(int maxRows) => new
        {
            org_id = world.Org,
            project_id = (Guid?)world.Project,
            run_id = plan.RunId,
            max_rows = maxRows,
        };

        // The cap is the bound `@max_rows` of the statement, not a number the service filters in memory.
        (await scope.ScalarAsync<long>(counted, Parameters(2))).ShouldBe(2);
        (await scope.ScalarAsync<long>(counted, Parameters(3))).ShouldBe(3);
        (await scope.ScalarAsync<long>(counted, Parameters(4))).ShouldBe(3);
        (await scope.ScalarAsync<long>(counted, Parameters(0))).ShouldBe(0);
    }

    // ------------------------------------------------------------------ F2: the documented result order

    [Fact]
    public async Task Freeze_Ordering_AttemptNoIsNumeric_AndIdTiebreak_OrderIndependent()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        // Attempts 1..10 recorded, artifacts only at 2 and 10: a text comparison of `attempt_no` would put
        // "10" before "2", the numeric comparison the contract documents puts 2 first.
        await SeedAttemptsAsync(
            world,
            plan.RunId,
            [.. Enumerable.Range(1, 10).Select(attemptNo => new CaseAttemptInput(key, attemptNo, "passed"))]);

        var attempt2 = await CaseAttemptIdAsync(world, plan.RunId, key, 2);
        var attempt10 = await CaseAttemptIdAsync(world, plan.RunId, key, 10);

        // Two ids at the same (execution_key, attempt_no, kind): the id's lower-case "D" text breaks the tie,
        // ordinal. `lower` sorts first by that text; the insertion order is the reverse of the expected order.
        var lower = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
        var higher = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
        var ten = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

        ArtifactSeed[] insertionOrder =
        [
            new(ten, key, 10, ArtifactKind.Trace),
            new(higher, key, 2, ArtifactKind.Trace),
            new(lower, key, 2, ArtifactKind.Trace),
        ];
        await InsertArtifactsAsync(world, plan.RunId, insertionOrder);

        Guid[] expected = [lower, higher, ten];
        expected.ShouldNotBe(insertionOrder.Select(seed => seed.Id));

        var expectedRows = new (Guid Id, string ObjectKey, string Checksum)[]
        {
            (lower, FinalKey(world.Org, world.Project, plan.RunId, key, 2, lower), ChecksumOf(lower)),
            (higher, FinalKey(world.Org, world.Project, plan.RunId, key, 2, higher), ChecksumOf(higher)),
            (ten, FinalKey(world.Org, world.Project, plan.RunId, key, 10, ten), ChecksumOf(ten)),
        };

        var first = await FreezeAsync(world, plan.RunId);

        first.Orphans.ShouldBeEmpty();
        first.Missing.ShouldBeEmpty();
        first.Ready.Select(artifact => artifact.ArtifactId).ShouldBe(expected);
        first.Ready.Select(artifact => artifact.AttemptNo).ShouldBe(new[] { 2, 2, 10 });
        first.Ready[0].AttemptId.ShouldBe(attempt2);
        first.Ready[1].AttemptId.ShouldBe(attempt2);
        first.Ready[2].AttemptId.ShouldBe(attempt10);
        first.ArtifactSetHash.ShouldBe(ExpectedHash(expectedRows));

        // The same rows read again in a fresh transaction: identical order and digest, so neither depends on
        // the physical row order or on the insertion order.
        var again = await FreezeAsync(world, plan.RunId);

        again.Ready.Select(artifact => artifact.ArtifactId).ShouldBe(expected);
        again.Ready.Select(artifact => artifact.AttemptNo).ShouldBe(new[] { 2, 2, 10 });
        again.ArtifactSetHash.ShouldBe(first.ArtifactSetHash);
    }

    // ------------------------------------------------------------------ F3: a short UPDATE is a failure

    [Fact]
    public async Task Freeze_ResolveAffectedRowsShort_ThrowsAndRollsBack()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await SeedAttemptsAsync(world, plan.RunId, new CaseAttemptInput(key, 1, "passed"));
        var attemptId = await CaseAttemptIdAsync(world, plan.RunId, key, 1);

        // Both rows are `ready` and unresolved at the read; `leaving` is flipped out of `ready` on a separate
        // committed connection right before the resolving UPDATE, so one of the two sent rows is skipped.
        var leaving = Guid.CreateVersion7();
        var staying = Guid.CreateVersion7();
        await InsertArtifactsAsync(
            world,
            plan.RunId,
            [
                new ArtifactSeed(leaving, key, 1, ArtifactKind.Trace),
                new ArtifactSeed(staying, key, 1, ArtifactKind.Screenshot),
            ]);

        await using (var scope = await GuardScope.BeginAsync(world.Api, world.Org))
        {
            var raced = false;
            var racing = new RacingTenantDb(scope.Db, async () =>
            {
                await using var other = await TenantScope.BeginAsync(
                    _fixture.AppConnectionString(world.Database),
                    world.Org);
                (await other.ExecuteAsync(
                    "update public.artifact set state = 'pending_delete' where org_id = @org_id and id = @id",
                    ("org_id", world.Org),
                    ("id", leaving))).ShouldBe(1);
                await other.CommitAsync();
                raced = true;
            });
            var freeze = new EvidenceFreeze(racing, scope.Accessor);

            await Should.ThrowAsync<InvalidOperationException>(() => freeze.FreezeAsync(
                new FreezeRequest(plan.RunId, []),
                TestContext.Current.CancellationToken));

            raced.ShouldBeTrue();

            // The exception type and the state below discriminate: without the count check the call returns a result
            // (the throw assertion fails), and a statement that did nothing leaves the still-ready row unresolved.

            // The UPDATE itself ran and resolved the row that was still ready — the shortfall is one skipped
            // row, not a statement that did nothing — and the skipped row keeps its null attempt_id.
            (await scope.RowSnapshotAsync(staying)).ShouldStartWith(attemptId.ToString("D") + "|");

            var skipped = await scope.RowSnapshotAsync(leaving);
            skipped.ShouldStartWith("null|");
            skipped.ShouldEndWith("|" + ArtifactState.PendingDelete);

            // Like 05's reconcile on a refusal: the caller rolls the transaction back, nothing is committed.
        }

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where run_id = '{plan.RunId}' and attempt_id is not null"))
            .ShouldBe(0);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.artifact where id = '{leaving}'"))
            .ShouldBe(ArtifactState.PendingDelete);
    }

    // ------------------------------------------------------------------ F4: fail closed before any write

    [Fact]
    public async Task Freeze_TooManyReadyArtifacts_FailsClosed_BeforeAnyWrite()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        // A recorded attempt, so without the cap the freeze would resolve every row and issue the UPDATE:
        // the "no UPDATE statement ran" assertion is what makes the pre-write validation observable.
        await SeedAttemptsAsync(world, plan.RunId, new CaseAttemptInput(key, 1, "passed"));

        var overflow = FreezeRequest.MaxReadyArtifacts + 1;
        await SeedReadyArtifactsBulkAsync(world, plan.RunId, key, overflow);

        // The caller's transaction is discarded without a commit, like 05's reconcile on a refusal.
        await using (var scope = await GuardScope.BeginAsync(world.Api, world.Org, world.Project))
        {
            world.Api.Statements.Clear();
            await Should.ThrowAsync<InvalidOperationException>(() => scope.RunAsync(plan.RunId));

            var statements = Statements(world);
            UpdateStatements(statements).ShouldBeEmpty();
            statements.Count.ShouldBe(1);
            (await scope.ScalarAsync<long>(
                "select count(*) from public.artifact where org_id = @org_id and run_id = @run_id and state = 'ready'",
                new { org_id = world.Org, run_id = plan.RunId })).ShouldBe(overflow);
            (await scope.ScalarAsync<long>(
                "select count(*) from public.artifact where org_id = @org_id and run_id = @run_id and attempt_id is not null",
                new { org_id = world.Org, run_id = plan.RunId })).ShouldBe(0);
        }

        // Nothing was written: the transaction rolled back and the rows stay unresolved.
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where run_id = '{plan.RunId}' and attempt_id is not null"))
            .ShouldBe(0);
    }
}
