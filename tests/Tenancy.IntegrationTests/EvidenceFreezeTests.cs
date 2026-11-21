// IEvidenceFreeze through a real PostgreSQL and the real host — the one cross-module call 05's
// reconciler makes to freeze a run's evidence. Every test opens the caller's tenant transaction itself
// (FreezeScope) and owns the commit boundary, so "runs inside the caller's transaction, never opens or
// commits one" is what the read-back assertions pin. Expectations come from the feature plan's result
// shapes, failure path and test names, not from the current behaviour.
#pragma warning disable CA1861
using System.Globalization;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The freeze through a real PostgreSQL and the real host: <see cref="IEvidenceFreeze"/> inside the
/// caller's tenant transaction. The artifacts of a run are left-joined to <c>case_attempt</c> by
/// <c>(execution_key, attempt_no)</c>, the pairs that resolve are persisted with one idempotent
/// <c>UPDATE … WHERE attempt_id IS NULL</c>, the caller's requirement set is reported as
/// <see cref="MissingSource.Requirement"/> gaps distinct from the unresolved-pair orphans, and the
/// <see cref="FreezeResult.ArtifactSetHash"/> is the canonical digest of the ready set.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class EvidenceFreezeTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;
    public EvidenceFreezeTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

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
        var api = _hosts.RealResolver(database);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
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

    /// <summary>One committed <c>case_attempt</c> batch for one slot of a seeded plan (the production writer path).</summary>
    private static async Task SeedAttemptsAsync(World world, Guid runId, params CaseAttemptInput[] attempts) =>
        await SeedAttemptsAsync(world, world.Org, runId, attempts);

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

    /// <summary>One committed artifact row for <paramref name="world"/>'s org and project.</summary>
    private Task InsertArtifactAsync(World world, Guid runId, ArtifactSeed seed) =>
        InsertArtifactsAsync(world, world.Org, world.Project, runId, [seed]);

    /// <summary>One committed batch of artifact rows, inserted in the order given (physical order under test).</summary>
    private Task InsertArtifactsAsync(World world, Guid runId, IReadOnlyList<ArtifactSeed> seeds) =>
        InsertArtifactsAsync(world, world.Org, world.Project, runId, seeds);

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

    /// <summary>
    /// One raw artifact insert as the app role (0028's grants): the object key is the row's own final key, so
    /// the shape CHECK holds for every state, and <paramref name="seed"/> carries the state, checksum,
    /// resolved <c>attempt_id</c> and sensitivity the case under test needs.
    /// </summary>
    private static string InsertArtifactSql(Guid org, Guid project, Guid runId, ArtifactSeed seed) =>
        "insert into public.artifact "
        + "(id, org_id, project_id, run_id, execution_key, attempt_no, attempt_id, kind, object_key, media_type, checksum, sensitivity, scan_state, state) "
        + $"values ('{seed.Id}', '{org}', '{project}', '{runId}', '{seed.ExecutionKey}', {seed.AttemptNo}, "
        + $"{(seed.AttemptId is { } attemptId ? $"'{attemptId}'" : "null")}, '{seed.Kind}', "
        + $"'{FinalKey(org, project, runId, seed.ExecutionKey, seed.AttemptNo, seed.Id)}', 'application/zip', "
        + $"'{seed.Checksum ?? ChecksumOf(seed.Id)}', '{seed.Sensitivity}', 'clean', '{seed.State}')";

    /// <summary>The final object key of one row's own identity (the artifact shape CHECK).</summary>
    private static string FinalKey(Guid org, Guid project, Guid runId, string executionKey, int attemptNo, Guid id) =>
        $"org/{org}/project/{project}/run/{runId}/attempt/{executionKey}/{attemptNo}/{id}";

    /// <summary>A deterministic, well-formed content address for one artifact id, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    private static string ChecksumOf(Guid id) => "sha256:" + id.ToString("N") + id.ToString("N");

    // ------------------------------------------------------------------ the caller's transaction

    /// <summary>
    /// The caller's tenant transaction of 05's reconcile handler, with <see cref="IEvidenceFreeze"/> resolved
    /// from the same container. The service never opens or commits a transaction, so the test owns the commit
    /// boundary: <see cref="CommitAsync"/> makes the resolution durable, disposing without it rolls it back.
    /// <para>
    /// Deliberately not <c>async</c> (tests/README.md, "AsyncLocal rule"): the factory publishes the scope into
    /// its ambient slot synchronously, in the caller's execution context (the <see cref="AttemptScope"/> rule).
    /// </para>
    /// </summary>
    private sealed class FreezeScope : IAsyncDisposable
    {
        private readonly AsyncServiceScope _services;
        private readonly ITenantScope _scope;

        private FreezeScope(AsyncServiceScope services, ITenantScope scope)
        {
            _services = services;
            _scope = scope;
            Freeze = services.ServiceProvider.GetRequiredService<IEvidenceFreeze>();
        }

        /// <summary>The service under test, running in this transaction.</summary>
        internal IEvidenceFreeze Freeze { get; }

        /// <summary>Opens the caller's tenant transaction for <paramref name="org"/> (optionally narrowed to a project).</summary>
        internal static Task<FreezeScope> BeginAsync(TenancyApiFactory api, Guid org, Guid? project = null)
        {
            var services = api.Services.CreateAsyncScope();
            var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();

            return scopes
                .BeginAsync(org, project, false, TestContext.Current.CancellationToken)
                .ContinueWith(
                    opened => new FreezeScope(services, opened.GetAwaiter().GetResult()),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        /// <summary>One freeze of <paramref name="runId"/> with the caller's requirement set.</summary>
        internal Task<FreezeResult> RunAsync(Guid runId, params RequiredArtifactSpec[] required) =>
            Freeze.FreezeAsync(new FreezeRequest(runId, required), TestContext.Current.CancellationToken);

        /// <summary>The row's <c>attempt_id</c> as this transaction sees it (own uncommitted writes included).</summary>
        internal async Task<Guid?> AttemptIdAsync(Guid artifactId)
        {
            var value = await ScalarAsync(
                "select attempt_id from public.artifact where org_id = @org_id and id = @id",
                artifactId);

            return value as Guid?;
        }

        /// <summary>The row's <c>xmin</c>: the transaction that last wrote it (an UPDATE changes it).</summary>
        internal async Task<string> XminAsync(Guid artifactId)
        {
            var value = await ScalarAsync(
                "select xmin::text from public.artifact where org_id = @org_id and id = @id",
                artifactId);

            return value as string
                ?? throw new InvalidOperationException($"artifact {artifactId} is not visible to this scope");
        }

        /// <summary>The C2 commit boundary; the freeze itself never commits.</summary>
        internal Task CommitAsync() => _scope.CommitAsync(TestContext.Current.CancellationToken);

        /// <summary>Releases the transaction scope (a rollback when <see cref="CommitAsync"/> was not called).</summary>
        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _services.DisposeAsync();
        }

        private async Task<object?> ScalarAsync(string sql, Guid artifactId)
        {
            await using var command = _scope.Connection.CreateCommand();
            command.Transaction = _scope.Transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("org_id", _scope.OrgId);
            command.Parameters.AddWithValue("id", artifactId);

            return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>One freeze of its own on a fresh tenant transaction, committed like 05's reconcile would.</summary>
    private static async Task<FreezeResult> FreezeAsync(World world, Guid runId, params RequiredArtifactSpec[] required)
    {
        await using var scope = await FreezeScope.BeginAsync(world.Api, world.Org);
        var result = await scope.RunAsync(runId, required);

        await scope.CommitAsync();
        return result;
    }

    /// <summary>Every command one freeze issued (the host's app-role data source is logged by <see cref="TenancyApiFactory.Statements"/>).</summary>
    private static IReadOnlyList<string> Statements(World world) => world.Api.Statements.Snapshot();

    /// <summary>The commands that write (the no-per-artifact-write rule): a plain <c>update</c> or a CTE one.</summary>
    private static IEnumerable<string> UpdateStatements(IEnumerable<string> statements) =>
        statements.Where(statement => statement.Contains("update ", StringComparison.OrdinalIgnoreCase));

    /// <summary>The documented order: <c>(execution_key, attempt_no, kind, id)</c>, ordinal, id as lower-case "D".</summary>
    private static List<(string ExecutionKey, int AttemptNo, string Kind, Guid Id)> FreezeOrder(
        IEnumerable<(string ExecutionKey, int AttemptNo, string Kind, Guid Id)> tuples) =>
        tuples
            .OrderBy(tuple => tuple.ExecutionKey, StringComparer.Ordinal)
            .ThenBy(tuple => tuple.AttemptNo)
            .ThenBy(tuple => tuple.Kind, StringComparer.Ordinal)
            .ThenBy(tuple => tuple.Id.ToString("D"), StringComparer.Ordinal)
            .ToList();

    /// <summary>The digest computed independently of the service, over the documented tuple array.</summary>
    private static string ExpectedHash(IEnumerable<(Guid Id, string ObjectKey, string Checksum)> rows) =>
        CanonicalHash.Compute(
            "["
            + string.Join(
                ",",
                rows.Select(row =>
                    $$"""{"artifact_id":"{{row.Id:D}}","object_key":"{{row.ObjectKey}}","checksum":"{{row.Checksum}}"}"""))
            + "]");

    /// <summary>The documented <c>{artifact_id, object_key, checksum}</c> tuple of one inserted seed.</summary>
    private static (Guid Id, string ObjectKey, string Checksum) TupleOf(World world, Guid runId, ArtifactSeed seed) =>
        (seed.Id,
            FinalKey(world.Org, world.Project, runId, seed.ExecutionKey, seed.AttemptNo, seed.Id),
            seed.Checksum ?? ChecksumOf(seed.Id));

    /// <summary>A committed row's <c>xmin</c>, read as the superuser.</summary>
    private Task<string> XminAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<string>(world.Database, $"select xmin::text from public.artifact where id = '{artifactId}'");
}
