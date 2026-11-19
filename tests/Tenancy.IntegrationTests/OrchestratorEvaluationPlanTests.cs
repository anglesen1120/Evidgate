using System.Security.Cryptography;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <see cref="IOrchestratorEvaluationPlan"/> over a real PostgreSQL and the real host, on a caller-owned tenant
/// transaction: the run's header, its manifest's planned count and every planned execution. Every test names the
/// predicate whose removal or narrowing makes it fail.
/// </summary>
[Trait("Category", "Docker")]
public sealed class OrchestratorEvaluationPlanTests
{
    private readonly PostgresTenancyFixture _fixture;

    public OrchestratorEvaluationPlanTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
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

    /// <summary>A second tenant through the real routes, for the isolation cases; the slug is suffixed here.</summary>
    private static async Task<Guid> CreateOtherTenantAsync(World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var unique = Unique.Slug(slug);
        var org = await CreateOrgAsync(world.Client, user, unique);
        await CreateProjectAsync(world.Client, user, org, unique + "-project");

        return org;
    }

    private Task<EvaluationPlanSeeder.Plan> SeedPlanAsync(World world, int executions, Guid? org = null) =>
        EvaluationPlanSeeder.SeedAsync(_fixture, world.Database, org ?? world.Org, executions);

    // ------------------------------------------------------------------ seeds the plan's columns

    /// <summary>
    /// Pins a policy revision to the run as the migration role: <c>run.policy_revision_id</c> is a create-time column
    /// for the app role, so only the owner can write it and the read must report what is stored, not a placeholder.
    /// </summary>
    private async Task<Guid> PinPolicyAsync(World world, Guid runId)
    {
        var policyId = Guid.CreateVersion7();
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            """
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            values (@id, @org_id, @project_id, 1, @hash, '{}'::jsonb, @created_by)
            """,
            ("id", policyId),
            ("org_id", world.Org),
            ("project_id", world.Project),
            ("hash", "sha256:" + new string('b', 64)),
            ("created_by", Guid.CreateVersion7()))).ShouldBe(1);
        (await owner.ExecuteAsync(
            "update public.run set policy_revision_id = @id where org_id = @org_id and id = @run_id",
            ("id", policyId),
            ("org_id", world.Org),
            ("run_id", runId))).ShouldBe(1);
        await owner.CommitAsync();

        return policyId;
    }

    /// <summary>Moves the run to a replay, so its <c>purpose</c> is distinguishable from the default <c>regular</c>.</summary>
    private async Task MarkReplayAsync(World world, Guid runId)
    {
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            """
            update public.run
               set purpose = 'replay',
                   replay_grant_id = @grant
             where org_id = @org_id and id = @run_id
            """,
            ("grant", Guid.CreateVersion7()),
            ("org_id", world.Org),
            ("run_id", runId))).ShouldBe(1);
        await owner.CommitAsync();
    }

    /// <summary>
    /// Repoints the run at a second manifest of the same project and suite that declares <paramref name="plannedCount"/>
    /// executions. A manifest is append-only (a BEFORE UPDATE/DELETE trigger refuses every role), so the seed inserts the
    /// new row instead of editing the original, exactly like the run-creation path's own manifest would have been built.
    /// </summary>
    private async Task SetPlannedCountAsync(World world, Guid runId, int plannedCount)
    {
        var manifestId = Guid.CreateVersion7();
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            """
            insert into public.manifest
              (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id,
               shard_plan, matrix, env_needs, planned_count, document, verified_by)
            select @id, org_id, project_id, @hash, schema_version, suite_revision_id, environment_revision_id,
                   shard_plan, matrix, env_needs, @planned_count, document, verified_by
              from public.manifest
             where org_id = @org_id
               and id = (select manifest_id from public.run where org_id = @org_id and id = @run_id)
            """,
            ("id", manifestId),
            ("hash", "sha256:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()),
            ("planned_count", plannedCount),
            ("org_id", world.Org),
            ("run_id", runId))).ShouldBe(1);
        (await owner.ExecuteAsync(
            "update public.run set manifest_id = @id where org_id = @org_id and id = @run_id",
            ("id", manifestId),
            ("org_id", world.Org),
            ("run_id", runId))).ShouldBe(1);
        await owner.CommitAsync();
    }

    /// <summary>Makes one planned execution optional, so <c>required</c> is distinguishable from its <c>true</c> default.</summary>
    private async Task MakeOptionalAsync(World world, Guid runId, string executionKey)
    {
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            """
            update public.planned_execution
               set required = false
             where org_id = @org_id and run_id = @run_id and execution_key = @execution_key
            """,
            ("org_id", world.Org),
            ("run_id", runId),
            ("execution_key", executionKey))).ShouldBe(1);
        await owner.CommitAsync();
    }

    /// <summary>Empties the run's plan and repoints it at a manifest that declares zero executions.</summary>
    private async Task DropPlanAsync(World world, Guid runId)
    {
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            "delete from public.planned_execution where org_id = @org_id and run_id = @run_id",
            ("org_id", world.Org),
            ("run_id", runId))).ShouldBeGreaterThan(0);
        await owner.CommitAsync();

        await SetPlannedCountAsync(world, runId, 0);
    }

    // ------------------------------------------------------------------ GetEvaluationRunPlanAsync

    [Fact]
    public async Task GetEvaluationRunPlanAsync_ReturnsTheRunsPlanAndEveryPlannedExecution()
    {
        await using var world = await StartAsync();
        var pinned = await SeedPlanAsync(world, 3);
        var unpinned = await SeedPlanAsync(world, 1);
        var policy = await PinPolicyAsync(world, pinned.RunId);
        await MarkReplayAsync(world, pinned.RunId);
        await SetPlannedCountAsync(world, pinned.RunId, 7);
        await MakeOptionalAsync(world, pinned.RunId, pinned.Executions[0].ExecutionKey);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var reader = caller.Resolve<IOrchestratorEvaluationPlan>();

        // The mutations this pins: projecting a constant instead of m.planned_count, r.policy_revision_id or r.purpose;
        // reading case_id instead of case_revision_id; and ignoring pe.required, so the optional row would answer true.
        var plan = await reader.GetEvaluationRunPlanAsync(world.Org, pinned.RunId, TestContext.Current.CancellationToken);

        plan.ShouldNotBeNull();
        plan.PolicyRevisionId.ShouldBe(policy);
        plan.ProjectId.ShouldBe(world.Project);
        plan.Purpose.ShouldBe("replay");
        plan.PlannedCount.ShouldBe(7);
        plan.SuiteRevisionId.ShouldBe(pinned.SuiteRevisionId);
        plan.Executions.Select(row => (row.ExecutionKey, row.CaseRevisionId, row.Required)).ShouldBe(
            [
                (pinned.Executions[0].ExecutionKey, pinned.Executions[0].CaseRevisionId, false),
                (pinned.Executions[1].ExecutionKey, pinned.Executions[1].CaseRevisionId, true),
                (pinned.Executions[2].ExecutionKey, pinned.Executions[2].CaseRevisionId, true),
            ],
            ignoreOrder: true);

        // The seeded run pinned no policy and kept the default purpose: null and `regular`, never a placeholder.
        var plain = await reader.GetEvaluationRunPlanAsync(world.Org, unpinned.RunId, TestContext.Current.CancellationToken);

        plain.ShouldNotBeNull();
        plain.PolicyRevisionId.ShouldBeNull();
        plain.Purpose.ShouldBe("regular");
        plain.PlannedCount.ShouldBe(0);
        plain.Executions.Select(row => row.ExecutionKey).ShouldBe([unpinned.Executions[0].ExecutionKey]);
    }

    [Fact]
    public async Task GetEvaluationRunPlanAsync_ReturnsNothingForAnUnknownRun()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var reader = caller.Resolve<IOrchestratorEvaluationPlan>();

        // The mutation this pins: dropping `r.id = @run_id`, which answers the only run of the tenant for any id.
        (await reader.GetEvaluationRunPlanAsync(world.Org, plan.RunId, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        (await reader.GetEvaluationRunPlanAsync(world.Org, Guid.CreateVersion7(), TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task GetEvaluationRunPlanAsync_ReturnsNothingForAnotherTenantsRun()
    {
        await using var world = await StartAsync();
        var otherOrg = await CreateOtherTenantAsync(world, "rival");
        var mine = await SeedPlanAsync(world, 1);
        var theirs = await SeedPlanAsync(world, 1, otherOrg);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var reader = caller.Resolve<IOrchestratorEvaluationPlan>();

        // The mutations this pins: naming the other tenant's org asks for exactly that tenant's rows, so only row level
        // security can hide them — a read that runs unmasked (owner role or row_security off) answers them. Naming the
        // scope's own org instead is the explicit-predicate direction: it excludes the foreign run by construction.
        (await reader.GetEvaluationRunPlanAsync(world.Org, mine.RunId, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        (await reader.GetEvaluationRunPlanAsync(otherOrg, theirs.RunId, TestContext.Current.CancellationToken))
            .ShouldBeNull();
        (await reader.GetEvaluationRunPlanAsync(world.Org, theirs.RunId, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task GetEvaluationRunPlanAsync_ReturnsNothingWhenTheRequestedOrgIsNotTheScopesOrg()
    {
        await using var world = await StartAsync();
        var otherOrg = await CreateOtherTenantAsync(world, "rival");
        var plan = await SeedPlanAsync(world, 1);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var reader = caller.Resolve<IOrchestratorEvaluationPlan>();

        // The mutation this pins: without `r.org_id = @org_id` the scope's own plan is answered for a foreign org,
        // because row level security alone still admits the scope's own row.
        (await reader.GetEvaluationRunPlanAsync(otherOrg, plan.RunId, TestContext.Current.CancellationToken))
            .ShouldBeNull();
        (await reader.GetEvaluationRunPlanAsync(world.Org, plan.RunId, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task GetEvaluationRunPlanAsync_AnswersTheHeaderForARunThatPlannedNoExecution()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);
        await DropPlanAsync(world, plan.RunId);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var reader = caller.Resolve<IOrchestratorEvaluationPlan>();

        // The mutation this pins: an inner join instead of the left join, which answers null for a planned-zero run and
        // so reports it as an unknown run rather than a run with an empty plan.
        var answer = await reader.GetEvaluationRunPlanAsync(world.Org, plan.RunId, TestContext.Current.CancellationToken);

        answer.ShouldNotBeNull();
        answer.Executions.ShouldBeEmpty();
        answer.PlannedCount.ShouldBe(0);
        answer.SuiteRevisionId.ShouldBe(plan.SuiteRevisionId);
    }

    [Fact]
    public async Task GetEvaluationRunHeaderAsync_ReturnsThePinnedRunHeaderAndUnpinnedDefaults()
    {
        await using var world = await StartAsync();
        var pinned = await SeedPlanAsync(world, 3);
        var unpinned = await SeedPlanAsync(world, 1);
        var policy = await PinPolicyAsync(world, pinned.RunId);
        await MarkReplayAsync(world, pinned.RunId);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var reader = caller.Resolve<IOrchestratorEvaluationPlan>();

        // The mutations this pins: projecting a constant instead of r.policy_revision_id, r.purpose or
        // r.suite_revision_id, which the pinned and unpinned runs below tell apart.
        var header = await reader.GetEvaluationRunHeaderAsync(world.Org, pinned.RunId, TestContext.Current.CancellationToken);

        header.ShouldNotBeNull();
        header.ProjectId.ShouldBe(world.Project);
        header.PolicyRevisionId.ShouldBe(policy);
        header.Purpose.ShouldBe("replay");
        header.IsFork.ShouldBeFalse();
        header.SuiteRevisionId.ShouldBe(pinned.SuiteRevisionId);

        var plain = await reader.GetEvaluationRunHeaderAsync(world.Org, unpinned.RunId, TestContext.Current.CancellationToken);

        plain.ShouldNotBeNull();
        plain.ProjectId.ShouldBe(world.Project);
        plain.PolicyRevisionId.ShouldBeNull();
        plain.Purpose.ShouldBe("regular");
        plain.IsFork.ShouldBeFalse();
        plain.SuiteRevisionId.ShouldBe(unpinned.SuiteRevisionId);
    }

    [Fact]
    public async Task GetEvaluationRunHeaderAsync_ReturnsNothingForAnUnknownRun()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var reader = caller.Resolve<IOrchestratorEvaluationPlan>();

        // The mutation this pins: dropping `r.id = @run_id`, which answers the tenant's only run for any id.
        (await reader.GetEvaluationRunHeaderAsync(world.Org, plan.RunId, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        (await reader.GetEvaluationRunHeaderAsync(world.Org, Guid.CreateVersion7(), TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task GetEvaluationRunHeaderAsync_ReturnsNothingForAnotherTenantsRun()
    {
        await using var world = await StartAsync();
        var otherOrg = await CreateOtherTenantAsync(world, "rival");
        var mine = await SeedPlanAsync(world, 1);
        var theirs = await SeedPlanAsync(world, 1, otherOrg);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var reader = caller.Resolve<IOrchestratorEvaluationPlan>();

        // The mutations this pins: naming the other tenant's org asks for exactly that tenant's rows, so only row level
        // security can hide them; naming the scope's own org with the foreign run id is excluded by the predicate.
        (await reader.GetEvaluationRunHeaderAsync(world.Org, mine.RunId, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        (await reader.GetEvaluationRunHeaderAsync(otherOrg, theirs.RunId, TestContext.Current.CancellationToken))
            .ShouldBeNull();
        (await reader.GetEvaluationRunHeaderAsync(world.Org, theirs.RunId, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task GetEvaluationRunHeaderAsync_ReturnsNothingWhenTheRequestedOrgIsNotTheScopesOrg()
    {
        await using var world = await StartAsync();
        var otherOrg = await CreateOtherTenantAsync(world, "rival");
        var plan = await SeedPlanAsync(world, 1);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var reader = caller.Resolve<IOrchestratorEvaluationPlan>();

        // The mutation this pins: without `r.org_id = @org_id` the scope's own header is answered for a foreign org,
        // because row level security alone still admits the scope's own row.
        (await reader.GetEvaluationRunHeaderAsync(otherOrg, plan.RunId, TestContext.Current.CancellationToken))
            .ShouldBeNull();
        (await reader.GetEvaluationRunHeaderAsync(world.Org, plan.RunId, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task GetEvaluationRunHeaderAsync_ReturnsTheHeaderForARunWithNoPlannedExecutions()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);
        var policy = await PinPolicyAsync(world, plan.RunId);
        await MarkReplayAsync(world, plan.RunId);
        await DropPlanAsync(world, plan.RunId);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var reader = caller.Resolve<IOrchestratorEvaluationPlan>();

        // The mutation this pins: an inner join to the planned executions, which answers null for a run whose plan is
        // empty instead of its header.
        var header = await reader.GetEvaluationRunHeaderAsync(world.Org, plan.RunId, TestContext.Current.CancellationToken);

        header.ShouldNotBeNull();
        header.ProjectId.ShouldBe(world.Project);
        header.PolicyRevisionId.ShouldBe(policy);
        header.Purpose.ShouldBe("replay");
        header.IsFork.ShouldBeFalse();
        header.SuiteRevisionId.ShouldBe(plan.SuiteRevisionId);
    }

    [Fact]
    public async Task GetEvaluationRunHeaderAsync_WithoutTenantScope_RefusesBeforeAnySql()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        await using var services = world.Api.Services.CreateAsyncScope();
        var unscoped = services.ServiceProvider.GetRequiredService<IOrchestratorEvaluationPlan>();

        // The mutation this pins: reading through the connection without demanding the scope, which runs the statement
        // with no evidgate.org_id and answers null (zero rows under RLS) instead of refusing the caller.
        world.Api.Statements.Clear();
        await Should.ThrowAsync<TenantScopeMissingException>(
            () => unscoped.GetEvaluationRunHeaderAsync(world.Org, plan.RunId, TestContext.Current.CancellationToken).AsTask());
        world.Api.Statements.Snapshot().ShouldBeEmpty();

        // The control: the same resolved reader answers once a tenant transaction is published.
        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var header = await caller.Resolve<IOrchestratorEvaluationPlan>()
            .GetEvaluationRunHeaderAsync(world.Org, plan.RunId, TestContext.Current.CancellationToken);

        header.ShouldNotBeNull();
        header.ProjectId.ShouldBe(world.Project);
        world.Api.Statements.Snapshot()
            .Where(row => row.Contains("from public.run r", StringComparison.Ordinal))
            .ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetEvaluationRunHeaderAsync_ReportsIsForkTrueForAForkRunAndFalseForANormalRun()
    {
        await using var world = await StartAsync();
        var fork = await SeedPlanAsync(world, 1);
        var normal = await SeedPlanAsync(world, 1);
        await MarkForkAsync(world, fork.RunId);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var reader = caller.Resolve<IOrchestratorEvaluationPlan>();

        // The mutations this pins: projecting a constant instead of r.is_fork, which one of the two runs tells apart
        // in either direction.
        var forkHeader = await reader.GetEvaluationRunHeaderAsync(world.Org, fork.RunId, TestContext.Current.CancellationToken);
        var normalHeader = await reader.GetEvaluationRunHeaderAsync(world.Org, normal.RunId, TestContext.Current.CancellationToken);

        forkHeader.ShouldNotBeNull();
        forkHeader.IsFork.ShouldBeTrue();
        normalHeader.ShouldNotBeNull();
        normalHeader.IsFork.ShouldBeFalse();
    }

    [Fact]
    public async Task GetEvaluationRunHeaderAsync_WithACancelledToken_ThrowsInsteadOfAnsweringTheHeader()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var reader = caller.Resolve<IOrchestratorEvaluationPlan>();

        // The mutation this pins: not passing the token to the query, which answers the header of a caller that has
        // already given up. The control proves the same call answers with a live token.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(
            () => reader.GetEvaluationRunHeaderAsync(world.Org, plan.RunId, cancelled.Token).AsTask());

        (await reader.GetEvaluationRunHeaderAsync(world.Org, plan.RunId, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
    }

    /// <summary>
    /// Makes the run a fork run as the owner. The identity columns are immutable for every role, so the guard is
    /// disabled for the one transaction and restored before the commit.
    /// </summary>
    private async Task MarkForkAsync(World world, Guid runId)
    {
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        await owner.ExecuteAsync("alter table public.run disable trigger run_identity_immutable");
        (await owner.ExecuteAsync(
            "update public.run set is_fork = true where org_id = @org_id and id = @run_id",
            ("org_id", world.Org),
            ("run_id", runId))).ShouldBe(1);
        await owner.ExecuteAsync("alter table public.run enable trigger run_identity_immutable");
        await owner.CommitAsync();
    }
}
