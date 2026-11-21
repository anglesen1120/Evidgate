using System.Data.Common;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceFreezeGuardTests
{
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
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>A second org with a project, through the real routes.</summary>
    private static async Task<(Guid Org, Guid Project)> CreateOtherTenantAsync(World world, string slug)
    {
        var unique = Unique.Slug(slug);
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, unique);
        var project = await CreateProjectAsync(world.Client, user, org, unique + "-project");

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
              from (select gen_random_uuid() as id, n
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
}
