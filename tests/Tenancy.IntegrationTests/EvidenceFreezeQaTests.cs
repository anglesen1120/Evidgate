// 06.5.1-T1/T2 QA pass (independent of the implementing agent): negative, boundary and exploratory
// cases through the real PostgreSQL and the real host. Helpers are copied from EvidenceFreezeTests
// (its helpers are private to that class). Expectations come from the contract (IEvidenceFreeze.cs),
// 06.5.1 §2-§5 and §8, not from the current implementation.
using System.Globalization;
#pragma warning disable CA1861
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>QA cases for <see cref="IEvidenceFreeze"/> beyond the primary EvidenceFreezeTests suite.</summary>
[Trait("Category", "Docker")]
public sealed class EvidenceFreezeQaTests
{
    private readonly PostgresTenancyFixture _fixture;

    public EvidenceFreezeQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        return new World(api, client, database, user, org, project);
    }

    // ------------------------------------------------------------------ seeding

    private Task<PlannedExecutionSeeder.SeededRun> SeedPlanAsync(World world, int count = 1) =>
        PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, count);

    private static async Task SeedAttemptsAsync(World world, Guid runId, params CaseAttemptInput[] attempts)
    {
        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        var outcomes = await scope.WriteAsync(runId, attempts);

        outcomes.ShouldAllBe(outcome => outcome.Status == CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();
    }

    private Task<Guid> CaseAttemptIdAsync(World world, Guid runId, string executionKey, int attemptNo) =>
        _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.case_attempt where org_id = '{world.Org}' and run_id = '{runId}' "
            + $"and execution_key = '{executionKey}' and attempt_no = {attemptNo}");

    private async Task InsertArtifactAsync(World world, Guid runId, ArtifactSeed seed)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.ExecuteAsync(InsertArtifactSql(world.Org, world.Project, runId, seed))).ShouldBe(1);
        await scope.CommitAsync();
    }

    private sealed record ArtifactSeed(
        Guid Id,
        string ExecutionKey,
        int AttemptNo,
        string Kind,
        string State = ArtifactState.Ready,
        string? Checksum = null,
        Guid? AttemptId = null,
        string Sensitivity = ArtifactSensitivity.Normal);

    private static string InsertArtifactSql(Guid org, Guid project, Guid runId, ArtifactSeed seed) =>
        "insert into public.artifact "
        + "(id, org_id, project_id, run_id, execution_key, attempt_no, attempt_id, kind, object_key, media_type, checksum, sensitivity, scan_state, state) "
        + $"values ('{seed.Id}', '{org}', '{project}', '{runId}', '{seed.ExecutionKey}', {seed.AttemptNo}, "
        + $"{(seed.AttemptId is { } attemptId ? $"'{attemptId}'" : "null")}, '{seed.Kind}', "
        + $"'{FinalKey(org, project, runId, seed.ExecutionKey, seed.AttemptNo, seed.Id)}', 'application/zip', "
        + $"'{seed.Checksum ?? ChecksumOf(seed.Id)}', '{seed.Sensitivity}', 'clean', '{seed.State}')";

    private static string FinalKey(Guid org, Guid project, Guid runId, string executionKey, int attemptNo, Guid id) =>
        $"org/{org}/project/{project}/run/{runId}/attempt/{executionKey}/{attemptNo}/{id}";

    private static string ChecksumOf(Guid id) => "sha256:" + id.ToString("N") + id.ToString("N");

    // ------------------------------------------------------------------ the caller's transaction

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

        internal IEvidenceFreeze Freeze { get; }

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

        internal Task<FreezeResult> RunAsync(Guid runId, params RequiredArtifactSpec[] required) =>
            Freeze.FreezeAsync(new FreezeRequest(runId, required), TestContext.Current.CancellationToken);

        internal async Task<Guid?> AttemptIdAsync(Guid artifactId)
        {
            var value = await ScalarAsync(
                "select attempt_id from public.artifact where org_id = @org_id and id = @id",
                artifactId);

            return value as Guid?;
        }

        internal Task CommitAsync() => _scope.CommitAsync(TestContext.Current.CancellationToken);

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

    private static async Task<FreezeResult> FreezeAsync(World world, Guid runId, params RequiredArtifactSpec[] required)
    {
        await using var scope = await FreezeScope.BeginAsync(world.Api, world.Org);
        var result = await scope.RunAsync(runId, required);

        await scope.CommitAsync();
        return result;
    }

    private static IReadOnlyList<string> Statements(World world) => world.Api.Statements.Snapshot();

    private static IEnumerable<string> UpdateStatements(IEnumerable<string> statements) =>
        statements.Where(statement => statement.Contains("update ", StringComparison.OrdinalIgnoreCase));

    private static (Guid Id, string ObjectKey, string Checksum) TupleOf(World world, Guid runId, ArtifactSeed seed) =>
        (seed.Id,
            FinalKey(world.Org, world.Project, runId, seed.ExecutionKey, seed.AttemptNo, seed.Id),
            seed.Checksum ?? ChecksumOf(seed.Id));

    /// <summary>The 06.5.1 §5 digest over the rows already in D4 order (the caller orders them).</summary>
    private static string ExpectedHash(IEnumerable<(Guid Id, string ObjectKey, string Checksum)> rows) =>
        CanonicalHash.Compute(
            "["
            + string.Join(
                ",",
                rows.Select(row =>
                    $$"""{"artifact_id":"{{row.Id:D}}","object_key":"{{row.ObjectKey}}","checksum":"{{row.Checksum}}"}"""))
            + "]");

    private static RequiredArtifactSpec Spec(string key, string kind, int? attemptNo = null) =>
        new(key, kind, FreezeCodes.ArtifactMissing, attemptNo);

    // ------------------------------------------------------------------ QA: isolation and matching

    [Fact]
    public async Task Freeze_ReadyArtifactOfAnotherRunInSameProject_IsNotReturned()
    {
        await using var world = await StartAsync();
        var own = await SeedPlanAsync(world);
        var other = await SeedPlanAsync(world);
        var ownKey = own.Executions[0].ExecutionKey;
        var otherKey = other.Executions[0].ExecutionKey;

        await SeedAttemptsAsync(world, other.RunId, new CaseAttemptInput(otherKey, 1, "passed"));
        await InsertArtifactAsync(world, other.RunId, new ArtifactSeed(Guid.CreateVersion7(), otherKey, 1, ArtifactKind.Trace));

        var result = await FreezeAsync(world, own.RunId, Spec(ownKey, ArtifactKind.Trace));

        result.Ready.ShouldBeEmpty();
        result.Orphans.ShouldBeEmpty();
        result.Missing.ShouldHaveSingleItem().Source.ShouldBe(MissingSource.Requirement);
        result.ArtifactSetHash.ShouldBe(CanonicalHash.Compute("[]"));
    }

    [Fact]
    public async Task Freeze_AttemptNoMismatch_IsRequirementGap_AndArtifactStillListed()
    {
        // The artifact belongs to attempt 2; the caller requires attempt 1. Attempt 2 is ready and resolved, so it
        // stays in Ready, but it must not satisfy a requirement pinned to attempt 1.
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;
        await SeedAttemptsAsync(
            world,
            plan.RunId,
            new CaseAttemptInput(key, 1, "failed"),
            new CaseAttemptInput(key, 2, "passed"));

        var artifact = Guid.CreateVersion7();
        await InsertArtifactAsync(world, plan.RunId, new ArtifactSeed(artifact, key, 2, ArtifactKind.Trace));

        var result = await FreezeAsync(world, plan.RunId, Spec(key, ArtifactKind.Trace, attemptNo: 1));

        result.Ready.ShouldHaveSingleItem().AttemptNo.ShouldBe(2);
        result.Orphans.ShouldBeEmpty();
        var gap = result.Missing.ShouldHaveSingleItem();
        gap.Source.ShouldBe(MissingSource.Requirement);
        gap.AttemptNo.ShouldBe(1);
        gap.ArtifactId.ShouldBeNull();
    }

    [Fact]
    public async Task Freeze_SameExecutionDifferentKind_IsRequirementGap()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;
        await SeedAttemptsAsync(world, plan.RunId, new CaseAttemptInput(key, 1, "passed"));
        await InsertArtifactAsync(world, plan.RunId, new ArtifactSeed(Guid.CreateVersion7(), key, 1, ArtifactKind.Trace));

        var result = await FreezeAsync(world, plan.RunId, Spec(key, ArtifactKind.Video));

        result.Ready.ShouldHaveSingleItem().Kind.ShouldBe(ArtifactKind.Trace);
        var gap = result.Missing.ShouldHaveSingleItem();
        gap.Source.ShouldBe(MissingSource.Requirement);
        gap.Kind.ShouldBe(ArtifactKind.Video);
    }

    [Fact]
    public async Task Freeze_NullAttemptNo_IsSatisfiedByAnyRecordedAttempt()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;
        await SeedAttemptsAsync(
            world,
            plan.RunId,
            new CaseAttemptInput(key, 1, "failed"),
            new CaseAttemptInput(key, 2, "passed"));
        await InsertArtifactAsync(world, plan.RunId, new ArtifactSeed(Guid.CreateVersion7(), key, 2, ArtifactKind.Trace));

        var result = await FreezeAsync(world, plan.RunId, Spec(key, ArtifactKind.Trace));

        result.Missing.ShouldBeEmpty();
        result.Ready.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Freeze_DuplicateRequirements_ReportedOnce()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        var result = await FreezeAsync(
            world,
            plan.RunId,
            Spec(key, ArtifactKind.Trace),
            Spec(key, ArtifactKind.Trace));

        result.Missing.Count(reason => reason.Source == MissingSource.Requirement).ShouldBe(1);
    }

    // ------------------------------------------------------------------ QA: states (only ready counts)

    [Theory]
    [InlineData(ArtifactState.PendingUpload)]
    [InlineData(ArtifactState.Uploaded)]
    [InlineData(ArtifactState.Rejected)]
    [InlineData(ArtifactState.PendingDelete)]
    [InlineData(ArtifactState.Deleted)]
    public async Task Freeze_NonReadyArtifactState_IsInvisible_NotOrphanNotReady(string state)
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;
        await SeedAttemptsAsync(world, plan.RunId, new CaseAttemptInput(key, 1, "passed"));
        await InsertArtifactAsync(
            world,
            plan.RunId,
            new ArtifactSeed(Guid.CreateVersion7(), key, 1, ArtifactKind.Trace, State: state));

        var result = await FreezeAsync(world, plan.RunId, Spec(key, ArtifactKind.Trace));

        result.Ready.ShouldBeEmpty();
        result.Orphans.ShouldBeEmpty();
        result.Missing.ShouldHaveSingleItem().Source.ShouldBe(MissingSource.Requirement);
        result.ArtifactSetHash.ShouldBe(CanonicalHash.Compute("[]"));
    }

    // ------------------------------------------------------------------ QA: boundaries

    [Fact]
    public async Task Freeze_EmptyRequirementSetAndNoArtifacts_EmptyResultAndEmptySetHash()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);

        var result = await FreezeAsync(world, plan.RunId);

        result.Ready.ShouldBeEmpty();
        result.Orphans.ShouldBeEmpty();
        result.Missing.ShouldBeEmpty();
        result.ArtifactSetHash.ShouldBe(CanonicalHash.Compute("[]"));
    }

    [Fact]
    public async Task Freeze_AtMaxRequired_IsAccepted_AndEveryGapReported()
    {
        // The MaxRequired boundary itself (10_000) must pass validation; each distinct execution key is one gap.
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var specs = Enumerable
            .Range(0, FreezeRequest.MaxRequired)
            .Select(index => Spec(index.ToString("x64", CultureInfo.InvariantCulture), ArtifactKind.Trace))
            .ToArray();

        var result = await FreezeAsync(world, plan.RunId, specs);

        result.Missing.Count.ShouldBe(FreezeRequest.MaxRequired);
        result.Missing.ShouldAllBe(reason => reason.Source == MissingSource.Requirement);
        result.Ready.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ QA: caller transaction and cancellation

    [Fact]
    public async Task Freeze_RolledBackScope_UndoesAttemptIdResolution()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;
        await SeedAttemptsAsync(world, plan.RunId, new CaseAttemptInput(key, 1, "passed"));
        var artifact = Guid.CreateVersion7();
        await InsertArtifactAsync(world, plan.RunId, new ArtifactSeed(artifact, key, 1, ArtifactKind.Trace));

        await using (var scope = await FreezeScope.BeginAsync(world.Api, world.Org))
        {
            var result = await scope.RunAsync(plan.RunId, Spec(key, ArtifactKind.Trace));

            result.Ready.ShouldHaveSingleItem();
            (await scope.AttemptIdAsync(artifact)).ShouldNotBeNull();

            // No CommitAsync: disposing rolls the caller's transaction back.
        }

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where id = '{artifact}' and attempt_id is null"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Freeze_CancelledToken_ThrowsAndIssuesNoUpdate()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await using var scope = await FreezeScope.BeginAsync(world.Api, world.Org);
        world.Api.Statements.Clear();

        await Should.ThrowAsync<OperationCanceledException>(() => scope.Freeze.FreezeAsync(
            new FreezeRequest(plan.RunId, [Spec(key, ArtifactKind.Trace)]),
            cancelled.Token));

        UpdateStatements(Statements(world)).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ QA: late artifact (application layer, D11)

    [Fact]
    public async Task Freeze_LateArtifactBetweenTwoFreezes_ResolvesOnlyNewRow_AndChangesHash()
    {
        // 06.5.1 §6: a second call after a late artifact turned ready resolves only that row; the first row is not
        // rewritten (its xmin is unchanged) and the set hash moves to cover both rows.
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;
        await SeedAttemptsAsync(
            world,
            plan.RunId,
            new CaseAttemptInput(key, 1, "passed"),
            new CaseAttemptInput(key, 2, "passed"),
            new CaseAttemptInput(key, 3, "passed"));

        var first = new ArtifactSeed(Guid.CreateVersion7(), key, 1, ArtifactKind.Trace);
        await InsertArtifactAsync(world, plan.RunId, first);
        var firstFreeze = await FreezeAsync(world, plan.RunId, Spec(key, ArtifactKind.Trace));
        var firstXmin = await _fixture.ScalarAsync<string>(
            world.Database,
            $"select xmin::text from public.artifact where id = '{first.Id}'");

        var late = new ArtifactSeed(Guid.CreateVersion7(), key, 3, ArtifactKind.Video);
        await InsertArtifactAsync(world, plan.RunId, late);

        var secondFreeze = await FreezeAsync(
            world,
            plan.RunId,
            Spec(key, ArtifactKind.Trace),
            Spec(key, ArtifactKind.Video));

        secondFreeze.Ready.Count.ShouldBe(2);
        secondFreeze.Ready[0].ArtifactId.ShouldBe(first.Id);
        secondFreeze.Ready[1].ArtifactId.ShouldBe(late.Id);
        secondFreeze.Ready[1].AttemptId.ShouldBe(await CaseAttemptIdAsync(world, plan.RunId, key, 3));
        secondFreeze.Missing.ShouldBeEmpty();

        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select xmin::text from public.artifact where id = '{first.Id}'"))
            .ShouldBe(firstXmin);

        var expected = ExpectedHash(new[] { TupleOf(world, plan.RunId, first), TupleOf(world, plan.RunId, late) });
        secondFreeze.ArtifactSetHash.ShouldBe(expected);
        secondFreeze.ArtifactSetHash.ShouldNotBe(firstFreeze.ArtifactSetHash);
    }
}
