using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.3-T2.2 through a real PostgreSQL and the real host: <see cref="IBlockedAttemptResolver"/> marks the
/// direct and transitive dependants of a failed setup <c>blocked</c> inside the caller's transaction
/// (FR-05-15, AC4) — a dependant never reads as passed after its setup failed. The Catalog dependency DAG
/// (<c>suite_member</c>/<c>variant</c>/<c>case_dependency</c>) is seeded directly because 03.2.1-T3's routes
/// refuse a cycle; the walk itself must still terminate over one.
/// </summary>
[Trait("Category", "Docker")]
public sealed class BlockedAttemptTests
{
    private readonly PostgresTenancyFixture _fixture;

    public BlockedAttemptTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    private async Task<PlannedExecutionSeeder.SeededRun> SeedPlanAsync(
        World world,
        int count,
        IReadOnlyList<(int Parent, int Child)>? edges = null,
        int? suiteMemberCount = null,
        Guid? org = null,
        IReadOnlyList<string>? targetEnvs = null) =>
        await PlannedExecutionSeeder.SeedPlanAsync(
            _fixture, world.Database, org ?? world.Org, count, edges: edges, suiteMemberCount: suiteMemberCount, targetEnvs: targetEnvs);

    /// <summary>The <c>blocked</c> rows written for <paramref name="runId"/>, ordered by execution key.</summary>
    private Task<IReadOnlyList<string>> BlockedKeysAsync(World world, Guid runId) =>
        _fixture.ColumnAsync<string>(
            world.Database,
            $"select execution_key from public.case_attempt where run_id = '{runId}' and normalized_status = 'blocked' order by execution_key");

    private Task<long> CountRowsAsync(World world, Guid runId) =>
        _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.case_attempt where run_id = '{runId}'");

    private Task<string> RawStatusAsync(World world, Guid runId, string executionKey) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            $"select raw_status from public.case_attempt where run_id = '{runId}' and execution_key = '{executionKey}'");

    // ------------------------------------------------------------------ AC4

    [Fact]
    public async Task WriteAttempt_FailedSetup_DependantsMarkedBlocked_NeverPassed()
    {
        await using var world = await StartAsync();

        // A is the setup; B and C depend on A directly, D on B (transitive); E is unrelated.
        var plan = await SeedPlanAsync(world, 5, [(0, 1), (0, 2), (1, 3)]);
        var (a, b, c, d, e) = (plan.Executions[0], plan.Executions[1], plan.Executions[2], plan.Executions[3], plan.Executions[4]);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(a.ExecutionKey, 1, "failed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

        var blocked = await scope.ResolveAsync(plan.RunId, a.ExecutionKey);
        await scope.CommitAsync();

        // Direct and transitive dependants only.
        blocked.OrderBy(key => key, StringComparer.Ordinal)
            .ShouldBe(new[] { b.ExecutionKey, c.ExecutionKey, d.ExecutionKey }.OrderBy(key => key, StringComparer.Ordinal));

        foreach (var execution in new[] { b, c, d })
        {
            (await RawStatusAsync(world, plan.RunId, execution.ExecutionKey)).ShouldBe("blocked");
        }

        // Both statuses are 'blocked', attempt_no is 1, case_id is the planned row's case, and none is passed.
        (await _fixture.ColumnAsync<string>(
            world.Database,
            $"select normalized_status from public.case_attempt where run_id = '{plan.RunId}' and normalized_status <> 'failed'"))
            .ShouldAllBe(status => status == "blocked");
        (await _fixture.ColumnAsync<int>(
            world.Database,
            $"select attempt_no from public.case_attempt where run_id = '{plan.RunId}' and normalized_status = 'blocked'"))
            .ShouldAllBe(attempt => attempt == 1);
        foreach (var execution in new[] { b, c, d })
        {
            (await _fixture.ScalarAsync<Guid>(
                world.Database,
                $"select case_id from public.case_attempt where run_id = '{plan.RunId}' and execution_key = '{execution.ExecutionKey}'"))
                .ShouldBe(execution.CaseId);
        }

        // The unrelated execution gets no row at all.
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.case_attempt where run_id = '{plan.RunId}' and execution_key = '{e.ExecutionKey}'"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task BlockedAttempt_PassedSetup_WritesNothing()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2, [(0, 1)]);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "passed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

        (await scope.ResolveAsync(plan.RunId, plan.Executions[0].ExecutionKey)).ShouldBeEmpty();
        await scope.CommitAsync();

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(1);
        (await BlockedKeysAsync(world, plan.RunId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task BlockedAttempt_UnknownRunOrKey_ReturnsEmpty()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2, [(0, 1)]);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);

        // A run that does not exist, a key that is not planned, and a malformed key: all empty, no statement.
        (await scope.ResolveAsync(Guid.CreateVersion7(), plan.Executions[0].ExecutionKey)).ShouldBeEmpty();
        (await scope.ResolveAsync(plan.RunId, new string('f', 64))).ShouldBeEmpty();
        (await scope.ResolveAsync(plan.RunId, "not-a-key")).ShouldBeEmpty();

        // A planned key with no failing attempt in this run is not a failure either.
        (await scope.ResolveAsync(plan.RunId, plan.Executions[0].ExecutionKey)).ShouldBeEmpty();
        await scope.CommitAsync();

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(0);
    }

    [Fact]
    public async Task BlockedAttempt_DependantNotInRunPlan_GetsNoRow()
    {
        await using var world = await StartAsync();

        // Only execution 0 is planned; the suite has a second member (index 1) with an edge from 0, but that
        // member's execution is not in this run's plan, so the resolver must not write a row for it.
        var plan = await SeedPlanAsync(world, 1, [(0, 1)], suiteMemberCount: 2);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "failed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

        (await scope.ResolveAsync(plan.RunId, plan.Executions[0].ExecutionKey)).ShouldBeEmpty();
        await scope.CommitAsync();

        (await BlockedKeysAsync(world, plan.RunId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task BlockedAttempt_OtherTargetEnv_NotBlocked()
    {
        await using var world = await StartAsync();

        // The setup runs against 'staging'; the dependant's own planned execution is on 'production', so the
        // resolver's target_env filter (statement B) must not block it.
        var plan = await SeedPlanAsync(world, 2, [(0, 1)], targetEnvs: ["staging", "production"]);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "failed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

        (await scope.ResolveAsync(plan.RunId, plan.Executions[0].ExecutionKey)).ShouldBeEmpty();
        await scope.CommitAsync();

        (await BlockedKeysAsync(world, plan.RunId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task BlockedAttempt_DependantAlreadyHasAttempt_Untouched()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2, [(0, 1)]);
        var dependant = plan.Executions[1];

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(dependant.ExecutionKey, 1, "passed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "failed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

        (await scope.ResolveAsync(plan.RunId, plan.Executions[0].ExecutionKey)).ShouldBeEmpty();
        await scope.CommitAsync();

        (await BlockedKeysAsync(world, plan.RunId)).ShouldBeEmpty();
        (await RawStatusAsync(world, plan.RunId, dependant.ExecutionKey)).ShouldBe("passed");
    }

    [Fact]
    public async Task BlockedAttempt_Repeat_IsIdempotent()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 3, [(0, 1), (0, 2)]);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "failed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

        (await scope.ResolveAsync(plan.RunId, plan.Executions[0].ExecutionKey)).Count.ShouldBe(2);
        (await scope.ResolveAsync(plan.RunId, plan.Executions[0].ExecutionKey)).ShouldBeEmpty();
        await scope.CommitAsync();

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(3); // the failed setup plus two blocked rows
    }

    [Fact]
    public async Task BlockedAttempt_CycleInEdges_Terminates()
    {
        await using var world = await StartAsync();

        // A -> B and B -> A, both planted by the seeder while the revision is being created: 03.2.1-T3's routes
        // refuse a cycle, but a UNION walk must still terminate over one.
        var plan = await SeedPlanAsync(world, 2, [(0, 1), (1, 0)]);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "failed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

        var blocked = await scope.ResolveAsync(plan.RunId, plan.Executions[0].ExecutionKey);
        await scope.CommitAsync();

        blocked.ShouldBe([plan.Executions[1].ExecutionKey]);
    }

    [Fact]
    public async Task BlockedAttempt_CommandCounter_Bounded()
    {
        await using var world = await StartAsync();

        // 1 setup, 3 direct dependants, then 60 more dependants of the setup: the statement count is constant.
        var edges = new List<(int Parent, int Child)> { (0, 1), (0, 2), (0, 3) };
        edges.AddRange(Enumerable.Range(4, 60).Select(i => (0, i)));
        var plan = await SeedPlanAsync(world, 64, edges);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "failed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

        world.Api.Statements.Clear();
        var blocked = await scope.ResolveAsync(plan.RunId, plan.Executions[0].ExecutionKey);
        var statements = world.Api.Statements.Snapshot()
            .Count(statement => statement.Contains("public.case_attempt", StringComparison.Ordinal));

        blocked.Count.ShouldBe(63);
        statements.ShouldBeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task BlockedAttempt_CrossTenant_ReturnsEmpty()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2, [(0, 1)]);

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        var otherPlan = await SeedPlanAsync(world, 2, [(0, 1)], org: otherOrg);

        await using var scope = await AttemptScope.BeginAsync(world.Api, otherOrg);
        (await scope.ResolveAsync(plan.RunId, plan.Executions[0].ExecutionKey)).ShouldBeEmpty();
        await scope.CommitAsync();

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(0);
        (await CountRowsAsync(world, otherPlan.RunId)).ShouldBe(0);
    }

    [Fact]
    public async Task BlockedAttempt_RollbackLeavesNoBlockedRows()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2, [(0, 1)]);

        await using (var scope = await AttemptScope.BeginAsync(world.Api, world.Org))
        {
            (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "failed")))
                .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
            (await scope.ResolveAsync(plan.RunId, plan.Executions[0].ExecutionKey)).Count.ShouldBe(1);

            // Disposed without CommitAsync: both the failed attempt and its blocked dependant roll back.
        }

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(0);
    }

    [Fact]
    public async Task BlockedAttempt_LaterProducerAttemptForBlockedSlot_IsDuplicateDifferentPayload()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2, [(0, 1)]);
        var dependant = plan.Executions[1];

        await using (var scope = await AttemptScope.BeginAsync(world.Api, world.Org))
        {
            (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "failed")))
                .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
            (await scope.ResolveAsync(plan.RunId, plan.Executions[0].ExecutionKey)).ShouldBe([dependant.ExecutionKey]);
            await scope.CommitAsync();
        }

        // The producer's own attempt for the already-blocked slot loses to the blocked row (first writer wins).
        await using var later = await AttemptScope.BeginAsync(world.Api, world.Org);
        var outcome = (await later.WriteAsync(plan.RunId, new CaseAttemptInput(dependant.ExecutionKey, 1, "passed")))
            .ShouldHaveSingleItem();

        outcome.Status.ShouldBe(CaseAttemptWriteStatus.DuplicateDifferentPayload);
        outcome.IsRefusal.ShouldBeTrue();
        (await RawStatusAsync(world, plan.RunId, dependant.ExecutionKey)).ShouldBe("blocked");
    }
}
