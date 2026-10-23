using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.3-T2.2 QA (S31): negative, boundary and exploratory coverage the delivery suite
/// (<see cref="BlockedAttemptTests"/>) leaves open — a diamond DAG, a dependant the run plans twice in
/// two environments, a setup that failed and then passed on a retry, the three normalised failure
/// statuses, a dependant holding only a higher <c>attempt_no</c>, a malformed failed key, and a
/// project-scoped caller that cannot see the run.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class BlockedAttemptQaTests
{
    private readonly PostgresTenancyFixture _fixture;

    public BlockedAttemptQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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
        IReadOnlyList<string>? targetEnvs = null) =>
        await PlannedExecutionSeeder.SeedPlanAsync(
            _fixture, world.Database, world.Org, count, edges: edges, targetEnvs: targetEnvs);

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

    private Task<int> MaxAttemptNoAsync(World world, Guid runId, string executionKey) =>
        _fixture.ScalarAsync<int>(
            world.Database,
            $"select max(attempt_no) from public.case_attempt where run_id = '{runId}' and execution_key = '{executionKey}'");

    /// <summary>Seeds one extra planned execution for a member the run already plans, in another environment.</summary>
    private async Task InsertPlannedExecutionAsync(
        World world, Guid runId, Guid caseRevisionId, string variantHash, string targetEnv, string executionKey)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        await scope.ExecuteAsync(
            """
            insert into public.planned_execution
              (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id)
            values (@id, @org_id, @run_id, @execution_key, @case_revision_id, @variant_hash, @target_env, 'shard-9')
            """,
            ("id", Guid.CreateVersion7()), ("org_id", world.Org), ("run_id", runId),
            ("execution_key", executionKey), ("case_revision_id", caseRevisionId),
            ("variant_hash", variantHash), ("target_env", targetEnv));
        await scope.CommitAsync();
    }

    // ------------------------------------------------------------------ DAG shape

    [Fact]
    public async Task BlockedAttempt_DiamondGraph_BlocksEachDependantOnce()
    {
        await using var world = await StartAsync();

        // A -> B, A -> C, B -> D, C -> D: D is reachable twice but is one dependant.
        var plan = await SeedPlanAsync(world, 4, [(0, 1), (0, 2), (1, 3), (2, 3)]);
        var (a, b, c, d) = (plan.Executions[0], plan.Executions[1], plan.Executions[2], plan.Executions[3]);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(a.ExecutionKey, 1, "failed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

        var blocked = await scope.ResolveAsync(plan.RunId, a.ExecutionKey);
        await scope.CommitAsync();

        blocked.OrderBy(key => key, StringComparer.Ordinal)
            .ShouldBe(new[] { b.ExecutionKey, c.ExecutionKey, d.ExecutionKey }.OrderBy(key => key, StringComparer.Ordinal));
        (await BlockedKeysAsync(world, plan.RunId)).Count.ShouldBe(3);
    }

    // ------------------------------------------------------------------ planned twice, two environments

    [Fact]
    public async Task BlockedAttempt_DependantPlannedInTwoEnvironments_OnlyTheSameEnvIsBlocked()
    {
        await using var world = await StartAsync();

        var plan = await SeedPlanAsync(world, 2, [(0, 1)]);
        var (setup, dependant) = (plan.Executions[0], plan.Executions[1]);
        const string otherEnvKey = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

        // The dependant member is planned twice in this run: once in the setup's 'staging', once in 'production'.
        await InsertPlannedExecutionAsync(
            world, plan.RunId, dependant.CaseRevisionId, dependant.VariantHash, "production", otherEnvKey);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(setup.ExecutionKey, 1, "failed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

        (await scope.ResolveAsync(plan.RunId, setup.ExecutionKey)).ShouldBe([dependant.ExecutionKey]);
        await scope.CommitAsync();

        (await BlockedKeysAsync(world, plan.RunId)).ShouldBe([dependant.ExecutionKey]);
        // The other environment's planned row gets no attempt at all.
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.case_attempt where run_id = '{plan.RunId}' and execution_key = '{otherEnvKey}'"))
            .ShouldBe(0);
    }

    // ------------------------------------------------------------------ retry finality (documented behaviour)

    [Fact]
    public async Task BlockedAttempt_FailedThenPassedRetry_StillBlocks_DocumentedBehaviour()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2, [(0, 1)]);
        var (setup, dependant) = (plan.Executions[0], plan.Executions[1]);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(setup.ExecutionKey, 1, "failed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(setup.ExecutionKey, 2, "passed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

        // Observed behaviour: the resolver looks for ANY failed/timed_out/error attempt of the execution, so a
        // later passing retry does not clear the failure — it blocks the dependants anyway. Which attempt is
        // final is the caller's decision (04.1.1/07.4.2); the resolver is fail-closed by design.
        (await scope.ResolveAsync(plan.RunId, setup.ExecutionKey)).ShouldBe([dependant.ExecutionKey]);
        await scope.CommitAsync();

        (await RawStatusAsync(world, plan.RunId, dependant.ExecutionKey)).ShouldBe("blocked");
        (await MaxAttemptNoAsync(world, plan.RunId, setup.ExecutionKey)).ShouldBe(2);
    }

    // ------------------------------------------------------------------ normalised failure statuses

    [Theory]
    [InlineData("timedOut")]
    [InlineData("interrupted")]
    [InlineData("error")]
    public async Task BlockedAttempt_TimedOutOrErrorSetup_BlocksDependants(string rawStatus)
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2, [(0, 1)]);
        var (setup, dependant) = (plan.Executions[0], plan.Executions[1]);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(setup.ExecutionKey, 1, rawStatus)))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

        (await scope.ResolveAsync(plan.RunId, setup.ExecutionKey)).ShouldBe([dependant.ExecutionKey]);
        await scope.CommitAsync();

        (await RawStatusAsync(world, plan.RunId, dependant.ExecutionKey)).ShouldBe("blocked");
    }

    // ------------------------------------------------------------------ dependant attempt numbering

    [Fact]
    public async Task BlockedAttempt_DependantWithOnlyAHigherAttemptNo_IsNotBlocked()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2, [(0, 1)]);
        var (setup, dependant) = (plan.Executions[0], plan.Executions[1]);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        // The dependant has attempt_no 2 only (no attempt 1): it has an attempt, so the NOT EXISTS guard leaves it.
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(dependant.ExecutionKey, 2, "passed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(setup.ExecutionKey, 1, "failed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

        (await scope.ResolveAsync(plan.RunId, setup.ExecutionKey)).ShouldBeEmpty();
        await scope.CommitAsync();

        (await BlockedKeysAsync(world, plan.RunId)).ShouldBeEmpty();
        (await RawStatusAsync(world, plan.RunId, dependant.ExecutionKey)).ShouldBe("passed");
        (await MaxAttemptNoAsync(world, plan.RunId, dependant.ExecutionKey)).ShouldBe(2);
    }

    // ------------------------------------------------------------------ malformed failed key

    [Fact]
    public async Task BlockedAttempt_UppercaseExecutionKey_ReturnsEmpty()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2, [(0, 1)]);
        var setup = plan.Executions[0];

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(setup.ExecutionKey, 1, "failed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

        // The 00 §7 shape is lowercase hex; an uppercase key is never planned, so nothing is written. The key is
        // built here rather than by upper-casing the seeded one, so the test never depends on the hash's digits.
        (await scope.ResolveAsync(plan.RunId, new string('A', 64))).ShouldBeEmpty();
        await scope.CommitAsync();

        (await BlockedKeysAsync(world, plan.RunId)).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ project-scoped caller

    [Fact]
    public async Task BlockedAttempt_ProjectScopedScopeOfAnotherProject_ReturnsEmpty()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2, [(0, 1)]);
        var setup = plan.Executions[0];
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, "other");

        await using (var orgScope = await AttemptScope.BeginAsync(world.Api, world.Org))
        {
            (await orgScope.WriteAsync(plan.RunId, new CaseAttemptInput(setup.ExecutionKey, 1, "failed")))
                .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
            await orgScope.CommitAsync();
        }

        // A project-scoped scope for another project of the same org cannot see the run: no statement, no row.
        await using (var projectScope = await AttemptScope.BeginAsync(world.Api, world.Org, otherProject))
        {
            (await projectScope.ResolveAsync(plan.RunId, setup.ExecutionKey)).ShouldBeEmpty();
        }

        (await BlockedKeysAsync(world, plan.RunId)).ShouldBeEmpty();

        // The control: the run's own project scope does block the dependant.
        await using (var own = await AttemptScope.BeginAsync(world.Api, world.Org, world.Project))
        {
            (await own.ResolveAsync(plan.RunId, setup.ExecutionKey)).ShouldBe([plan.Executions[1].ExecutionKey]);
            await own.CommitAsync();
        }

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(2);
    }
}
