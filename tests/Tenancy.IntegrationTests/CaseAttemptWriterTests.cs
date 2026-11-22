using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.3-T2.1 through a real PostgreSQL and the real host: <see cref="ICaseAttemptWriter"/> inside the
/// caller's tenant transaction (FR-05-15, rulings T3/T50). Every test opens the transaction itself
/// (<see cref="AttemptScope"/>) and owns the commit, so the writer's "never opens, never commits"
/// contract is what AC5's visibility checks pin.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class CaseAttemptWriterTests : IClassFixture<TenancyHostPool>
{
    /// <summary>A well-formed execution key that matches no plan: 64 lowercase hex.</summary>
    private const string UnplannedKey = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;
    public CaseAttemptWriterTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

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
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        return new World(api, client, database, user, org, project);
    }

    /// <summary>A second org with a project, through the real routes.</summary>
    private static async Task<(Guid Org, Guid Project)> CreateOtherTenantAsync(World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var otherSlug = Unique.Slug(slug);
        var org = await CreateOrgAsync(world.Client, user, otherSlug);
        var project = await CreateProjectAsync(world.Client, user, org, Unique.Slug($"{otherSlug}-project"));

        return (org, project);
    }

    private async Task<PlannedExecutionSeeder.SeededRun> SeedPlanAsync(World world, int count = 1) =>
        await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, count);

    /// <summary>Every statement that touched <c>public.case_attempt</c> since the last clear.</summary>
    private static IReadOnlyList<string> CaseAttemptStatements(World world) =>
        [.. world.Api.Statements.Snapshot().Where(statement => statement.Contains("public.case_attempt", StringComparison.Ordinal))];

    private Task<long> CountRowsAsync(World world, Guid runId) =>
        _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.case_attempt where run_id = '{runId}'");

    // ------------------------------------------------------------------ AC5 visibility

    [Fact]
    public async Task CaseAttempt_NotVisibleUntilBatchCommits()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        var outcomes = await scope.WriteAsync(
            plan.RunId,
            new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "passed"),
            new CaseAttemptInput(plan.Executions[1].ExecutionKey, 1, "failed"));
        outcomes.ShouldAllBe(outcome => outcome.Status == CaseAttemptWriteStatus.Accepted);

        // A SECOND connection as the app role under the same org: the uncommitted rows are invisible.
        await using (var other = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await other.ScalarAsync<long>("select count(*) from public.case_attempt")).ShouldBe(0);
        }

        await scope.CommitAsync();

        await using (var other = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await other.ScalarAsync<long>("select count(*) from public.case_attempt")).ShouldBe(2);
        }

        // Disposing a fresh scope without commit leaves zero rows.
        await using (var rolledBack = await AttemptScope.BeginAsync(world.Api, world.Org))
        {
            (await rolledBack.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 2, "passed")))
                .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        }

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(2);
    }

    [Fact]
    public async Task WriteAttempt_CallerRollbackLeavesNoRows()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);

        await using (var scope = await AttemptScope.BeginAsync(world.Api, world.Org))
        {
            (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "passed")))
                .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

            // The scope is disposed without CommitAsync: the writer's rows roll back with the caller's transaction.
        }

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ statement budget

    [Fact]
    public async Task WriteAttempt_BatchInsert_OneStatementPerBatch()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 200);
        var planned = plan.Executions[0];

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);

        world.Api.Statements.Clear();
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(planned.ExecutionKey, 1, "passed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        CaseAttemptStatements(world).Count.ShouldBe(1);

        world.Api.Statements.Clear();
        var batch = plan.Executions
            .Skip(1) // the first slot was already written above, in this same transaction
            .Select(execution => new CaseAttemptInput(execution.ExecutionKey, 1, "passed"))
            .ToArray();
        var outcomes = await scope.WriteAsync(plan.RunId, batch);
        outcomes.ShouldAllBe(outcome => outcome.Status == CaseAttemptWriteStatus.Accepted);
        CaseAttemptStatements(world).Count.ShouldBe(1);

        // A duplicate forces the second bounded statement that classifies the not-inserted slot.
        world.Api.Statements.Clear();
        var mixed = await scope.WriteAsync(
            plan.RunId,
            new CaseAttemptInput(planned.ExecutionKey, 2, "passed"),
            new CaseAttemptInput(planned.ExecutionKey, 1, "passed"));
        mixed[1].Status.ShouldBe(CaseAttemptWriteStatus.Duplicate);
        CaseAttemptStatements(world).Count.ShouldBe(2);
    }

    // ------------------------------------------------------------------ scope guard

    [Fact]
    public async Task WriteAttempt_WithoutScope_Throws()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);

        await using var services = world.Api.Services.CreateAsyncScope();
        var writer = services.ServiceProvider.GetRequiredService<ICaseAttemptWriter>();

        await Should.ThrowAsync<TenantScopeMissingException>(
            () => writer.WriteAttemptsAsync(
                plan.RunId,
                [new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "passed")],
                TestContext.Current.CancellationToken).AsTask());

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ verbatim storage

    [Fact]
    public async Task WriteAttempt_ControlCharsInRawStatus_StoredVerbatim()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var planned = plan.Executions[0];
        var raw = "passed\twith\nnewline";

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(planned.ExecutionKey, 1, raw)))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();

        (await _fixture.ScalarAsync<string>(world.Database, $"select raw_status from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe(raw);
    }

    [Fact]
    public async Task WriteAttempt_JobId_IsStoredWithoutFk()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var planned = plan.Executions[0];
        var jobId = Guid.CreateVersion7();

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(planned.ExecutionKey, 1, "passed", JobId: jobId)))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();

        // No `job` table exists yet (05.7.2-T1), so any uuid is accepted and stored.
        (await _fixture.ScalarAsync<Guid>(world.Database, $"select job_id from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe(jobId);
    }
}
