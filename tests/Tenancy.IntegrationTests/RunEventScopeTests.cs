using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 04.1.1-T1 through a real PostgreSQL and the real host: <see cref="IRunEventScope"/> inside the caller's tenant
/// transaction (rulings T90/T91). One statement reads the run row <c>FOR SHARE</c> — so a concurrent
/// <c>FOR UPDATE</c> on the same run waits for this transaction to end — together with the project's state, the
/// run's state and the subset of the caller's keys that are planned executions of the run.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RunEventScopeTests
{
    /// <summary>A well-formed execution key that matches no plan: 64 lowercase hex.</summary>
    private const string UnplannedKey = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    private readonly PostgresTenancyFixture _fixture;

    public RunEventScopeTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>
    /// One tenant transaction the test opens itself — <see cref="ITenantScopeFactory.BeginAsync"/> over the real
    /// host's app-role data source — with <see cref="IRunEventScope"/> resolved from the same container. The reader
    /// never opens or commits a transaction, so the test owns the commit boundary and holds the <c>FOR SHARE</c>
    /// lock until it disposes the scope.
    /// </summary>
    private sealed class EventScope : IAsyncDisposable
    {
        private readonly AsyncServiceScope _services;
        private readonly ITenantScope _scope;

        private EventScope(AsyncServiceScope services, ITenantScope scope)
        {
            _services = services;
            _scope = scope;
            Reader = services.ServiceProvider.GetRequiredService<IRunEventScope>();
        }

        /// <summary>The reader under test, running in this transaction.</summary>
        internal IRunEventScope Reader { get; }

        /// <summary>
        /// Opens the tenant transaction for <paramref name="org"/>.
        /// <para>
        /// Deliberately <b>not</b> <c>async</c> (tests/README.md, "AsyncLocal rule"): the factory publishes the scope
        /// into its ambient <c>AsyncLocal</c> slot synchronously, in the caller's execution context, and
        /// <c>ExecutionContext</c> flows only downwards. An <c>async</c> wrapper would strand the publication in its
        /// own frame, so the reader — which resolves the scope through that slot — would fail with
        /// <c>TenantScopeMissingException</c> on a scope that is open.
        /// </para>
        /// </summary>
        internal static Task<EventScope> BeginAsync(TenancyApiFactory api, Guid org, Guid? project = null)
        {
            var services = api.Services.CreateAsyncScope();
            var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();

            return scopes
                .BeginAsync(org, project, false, TestContext.Current.CancellationToken)
                .ContinueWith(
                    opened => new EventScope(services, opened.GetAwaiter().GetResult()),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        /// <summary>Reads the run scope in this transaction.</summary>
        internal Task<RunEventScope?> ReadAsync(Guid runId, IReadOnlyCollection<string> keys) =>
            Reader.ReadAsync(runId, keys, TestContext.Current.CancellationToken).AsTask();

        /// <summary>Disposes the transaction scope (a rollback, so a read leaves nothing behind) and the container scope.</summary>
        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _services.DisposeAsync();
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
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        return new World(api, client, database, user, org, project);
    }

    /// <summary>A second org with a project, through the real routes.</summary>
    private static async Task<(Guid Org, Guid Project)> CreateOtherTenantAsync(World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, Unique.Slug(slug));
        var project = await CreateProjectAsync(world.Client, user, org, Unique.Slug(slug + "-project"));

        return (org, project);
    }

    private Task<PlannedExecutionSeeder.SeededRun> SeedPlanAsync(World world, int count) =>
        PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, count);

    /// <summary>A deterministic, well-formed execution key that matches no plan: 64 lowercase hex.</summary>
    private static string UnplannedKeyOf(int index) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"unplanned|{index}")));

    // ------------------------------------------------------------------ visible run

    [Fact]
    public async Task Read_VisibleRun_ReturnsProjectAndStates_AndOnlyPlannedKeys()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 3);
        var keys = plan.Executions.Select(execution => execution.ExecutionKey).ToArray();

        await using var scope = await EventScope.BeginAsync(world.Api, world.Org);

        // An unplanned key rides along: the answer is the planned subset only (T90).
        var answer = await scope.ReadAsync(plan.RunId, [.. keys, UnplannedKey]);

        answer.ShouldNotBeNull();
        answer.ProjectId.ShouldBe(world.Project);
        answer.ProjectState.ShouldBe("active");
        answer.RunState.ShouldBe("created");
        answer.PlannedKeys.ShouldBe(keys, ignoreOrder: true);
    }

    [Fact]
    public async Task Read_UnknownKey_IsAbsentFromPlannedKeys()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);
        var planned = plan.Executions[0].ExecutionKey;

        await using var scope = await EventScope.BeginAsync(world.Api, world.Org);
        var answer = await scope.ReadAsync(plan.RunId, [planned, UnplannedKey]);

        answer.ShouldNotBeNull();
        answer.PlannedKeys.ShouldBe([planned]);
        answer.PlannedKeys.ShouldNotContain(UnplannedKey);
    }

    [Fact]
    public async Task Read_EmptyKeyList_AnswersEmptySet()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);

        await using var scope = await EventScope.BeginAsync(world.Api, world.Org);
        var answer = await scope.ReadAsync(plan.RunId, []);

        answer.ShouldNotBeNull();
        answer.ProjectId.ShouldBe(world.Project);
        answer.PlannedKeys.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ argument guard

    [Fact]
    public async Task Read_FiveHundredKeys_IsAccepted_AndFiveHundredAndOneIsRefused()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var keys = Enumerable.Range(0, 500).Select(UnplannedKeyOf).ToArray();

        await using var scope = await EventScope.BeginAsync(world.Api, world.Org);
        var answer = await scope.ReadAsync(plan.RunId, keys);

        answer.ShouldNotBeNull();
        answer.ProjectId.ShouldBe(world.Project);
        answer.PlannedKeys.ShouldBeEmpty();

        var tooMany = Enumerable.Range(0, 501).Select(UnplannedKeyOf).ToArray();
        await Should.ThrowAsync<ArgumentException>(async () => await scope.ReadAsync(plan.RunId, tooMany));

        // Distinct after de-dup: a repeated key is not a second key and is not refused.
        (await scope.ReadAsync(plan.RunId, [UnplannedKey, UnplannedKey, UnplannedKey])).ShouldNotBeNull();
    }

    [Fact]
    public async Task Read_MalformedKey_IsRefused()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);

        await using var scope = await EventScope.BeginAsync(world.Api, world.Org);

        // Not hex, and one character too long: neither can match a planned execution (0031's execution-key CHECK).
        await Should.ThrowAsync<ArgumentException>(async () => await scope.ReadAsync(plan.RunId, ["not-a-hex-key"]));
        await Should.ThrowAsync<ArgumentException>(async () => await scope.ReadAsync(plan.RunId, [UnplannedKey + "0"]));
    }

    // ------------------------------------------------------------------ other tenant

    [Fact]
    public async Task Read_OtherTenantRun_IsNull_AndTakesNoLock()
    {
        await using var world = await StartAsync();
        var other = await CreateOtherTenantAsync(world, "globex");
        var otherPlan = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, other.Org, 1);

        var scope = await EventScope.BeginAsync(world.Api, world.Org);
        try
        {
            // RLS makes another org's run invisible: the same null as an unknown id.
            (await scope.ReadAsync(otherPlan.RunId, [otherPlan.Executions[0].ExecutionKey])).ShouldBeNull();

            // No row was matched, so no lock was taken: a FOR UPDATE on the other tenant's run succeeds at once.
            await using var contender = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
            await contender.SetAsync("lock_timeout", "500");
            (await contender.ScalarAsync<int>(
                "select 1 from public.run where id = @run_id for update", ("run_id", otherPlan.RunId))).ShouldBe(1);
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ T91: FOR SHARE

    [Fact]
    public async Task Read_TakesForShare_BlockingAConcurrentForUpdate()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);
        var keys = plan.Executions.Select(execution => execution.ExecutionKey).ToArray();

        // The reader holds the run row FOR SHARE until its scope is disposed (rolled back).
        var scope = await EventScope.BeginAsync(world.Api, world.Org);
        (await scope.ReadAsync(plan.RunId, keys)).ShouldNotBeNull();

        await using (var contender = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            await contender.SetAsync("lock_timeout", "500");
            var blocked = await contender.CaptureErrorAsync(
                "select 1 from public.run where id = @run_id for update", ("run_id", plan.RunId));

            // 55P03 lock_not_available: the FOR UPDATE waited past lock_timeout because of the reader's FOR SHARE.
            blocked.SqlState.ShouldBe("55P03");
        }

        // Once the reader's transaction ends the lock is released and the same FOR UPDATE succeeds.
        await scope.DisposeAsync();

        await using var after = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await after.ScalarAsync<int>(
            "select 1 from public.run where id = @run_id for update", ("run_id", plan.RunId))).ShouldBe(1);
    }

    // ------------------------------------------------------------------ one statement

    [Fact]
    public async Task Read_IsOneStatement()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);
        var keys = plan.Executions.Select(execution => execution.ExecutionKey).ToArray();
        world.Api.Statements.Clear();

        await using var scope = await EventScope.BeginAsync(world.Api, world.Org);
        (await scope.ReadAsync(plan.RunId, keys)).ShouldNotBeNull();

        // The membership probe and the run/project read are one statement (00 §5.7 P2/P3, T90): the run's whole
        // planned set is never loaded in a second round trip.
        world.Api.Statements.Snapshot()
            .Where(statement => statement.Contains("public.planned_execution", StringComparison.Ordinal))
            .ShouldHaveSingleItem();
    }
}
