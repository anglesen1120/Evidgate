using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <see cref="IFrozenEvidence"/> over a real PostgreSQL and the real host, on a caller-owned tenant transaction: the
/// frozen revision row, and the latest attempt each execution key had at or before that revision's own
/// <c>created_at</c>. Every test names the predicate whose removal or narrowing makes it fail.
/// </summary>
[Trait("Category", "Docker")]
public sealed class FrozenEvidenceTests
{
    /// <summary>A fixed clock base so the freeze bounds are exact instead of "a few milliseconds ago".</summary>
    private static readonly DateTimeOffset Base = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresTenancyFixture _fixture;

    public FrozenEvidenceTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>A second tenant through the real routes, for the isolation cases.</summary>
    private static async Task<(Guid Org, Guid Project)> CreateOtherTenantAsync(World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, slug);
        var project = await CreateProjectAsync(world.Client, user, org, slug + "-project");

        return (org, project);
    }

    private Task<EvaluationPlanSeeder.Plan> SeedPlanAsync(World world, int executions, Guid? org = null) =>
        EvaluationPlanSeeder.SeedAsync(_fixture, world.Database, org ?? world.Org, executions);

    /// <summary>
    /// One committed <c>evidence_revision</c> written as the migration role, because the revision's <c>created_at</c> is
    /// the freeze bound under test and 05's own writer takes it from the clock.
    /// </summary>
    private async Task<Guid> SeedRevisionAsync(
        World world,
        Guid runId,
        int revisionNo,
        DateTimeOffset createdAt,
        string completeness = "incomplete",
        string[]? missing = null,
        Guid? org = null,
        string? manifestHash = null)
    {
        var id = Guid.CreateVersion7();
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            """
            insert into public.evidence_revision
              (id, org_id, run_id, revision_no, completeness, missing, manifest_hash, event_watermark, created_at)
            values (@id, @org_id, @run_id, @revision_no, @completeness, @missing::text[], @manifest_hash, 7, @created_at)
            """,
            ("id", id),
            ("org_id", org ?? world.Org),
            ("run_id", runId),
            ("revision_no", revisionNo),
            ("completeness", completeness),
            ("missing", missing ?? ["missing_result"]),
            ("manifest_hash", manifestHash ?? "sha256:" + new string('a', 64)),
            ("created_at", createdAt))).ShouldBe(1);
        await owner.CommitAsync();

        return id;
    }

    /// <summary>One committed <c>case_attempt</c> at an explicit instant, so the freeze bound decides which attempt wins.</summary>
    private async Task SeedAttemptAsync(
        World world,
        Guid runId,
        PlannedExecutionSeeder.SeededExecution execution,
        int attemptNo,
        string status,
        DateTimeOffset createdAt,
        Guid? org = null)
    {
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            """
            insert into public.case_attempt
              (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status, created_at)
            values (@id, @org_id, @run_id, @execution_key, @case_id, @attempt_no, @raw_status, @normalized_status, @created_at)
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", org ?? world.Org),
            ("run_id", runId),
            ("execution_key", execution.ExecutionKey),
            ("case_id", execution.CaseId),
            ("attempt_no", attemptNo),
            ("raw_status", status),
            ("normalized_status", status),
            ("created_at", createdAt))).ShouldBe(1);
        await owner.CommitAsync();
    }

    // ------------------------------------------------------------------ GetRevisionAsync

    [Fact]
    public async Task GetRevisionAsync_ReturnsTheRevisionTheRunFroze()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var other = await SeedPlanAsync(world, 1);
        var frozen = Base.AddMinutes(10);
        await SeedRevisionAsync(world, plan.RunId, 1, frozen, missing: ["missing_result", "artifact_missing"],
            manifestHash: "sha256:" + new string('b', 64));
        await SeedRevisionAsync(world, plan.RunId, 2, frozen.AddMinutes(10), "complete", []);
        await SeedRevisionAsync(world, other.RunId, 1, frozen, missing: ["count_mismatch"]);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var evidence = caller.Resolve<IFrozenEvidence>();

        // The mutation this pins: dropping `run_id` or `revision_no` makes the statement answer more than one row.
        var first = await evidence.GetRevisionAsync(world.Org, plan.RunId, 1, TestContext.Current.CancellationToken);
        first.ShouldNotBeNull();
        first.RevisionNo.ShouldBe(1);
        first.Completeness.ShouldBe("incomplete");
        first.Missing.ShouldBe(["missing_result", "artifact_missing"]);
        first.ManifestHash.ShouldBe("sha256:" + new string('b', 64));
        first.CreatedAt.ShouldBe(frozen, TimeSpan.FromMilliseconds(1));

        var second = await evidence.GetRevisionAsync(world.Org, plan.RunId, 2, TestContext.Current.CancellationToken);
        second.ShouldNotBeNull();
        second.Completeness.ShouldBe("complete");
        second.Missing.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetRevisionAsync_ReturnsNothingForARevisionNumberTheRunNeverFroze()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        await SeedRevisionAsync(world, plan.RunId, 1, Base.AddMinutes(10));

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var evidence = caller.Resolve<IFrozenEvidence>();

        (await evidence.GetRevisionAsync(world.Org, plan.RunId, 1, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await evidence.GetRevisionAsync(world.Org, plan.RunId, 2, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task GetRevisionAsync_ReturnsNothingForAnotherTenantsRun()
    {
        await using var world = await StartAsync();
        var (otherOrg, _) = await CreateOtherTenantAsync(world, Unique.Slug("rival"));
        var mine = await SeedPlanAsync(world, 1);
        var theirs = await SeedPlanAsync(world, 1, otherOrg);
        await SeedRevisionAsync(world, mine.RunId, 1, Base.AddMinutes(10));
        await SeedRevisionAsync(world, theirs.RunId, 1, Base.AddMinutes(10), org: otherOrg);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var evidence = caller.Resolve<IFrozenEvidence>();

        (await evidence.GetRevisionAsync(world.Org, mine.RunId, 1, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await evidence.GetRevisionAsync(world.Org, theirs.RunId, 1, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task GetRevisionAsync_ReturnsNothingWhenTheRequestedOrgIsNotTheScopesOrg()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var (otherOrg, _) = await CreateOtherTenantAsync(world, Unique.Slug("rival"));
        await SeedRevisionAsync(world, plan.RunId, 1, Base.AddMinutes(10));

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var evidence = caller.Resolve<IFrozenEvidence>();

        // The mutation this pins: without `er.org_id = @org_id` the scope's own revision is answered for a foreign org,
        // because row level security alone still admits it.
        (await evidence.GetRevisionAsync(otherOrg, plan.RunId, 1, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await evidence.GetRevisionAsync(world.Org, plan.RunId, 1, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    // ------------------------------------------------------------------ GetLatestAttemptStatusesAsync

    [Fact]
    public async Task GetLatestAttemptStatuses_ReturnsTheLatestAttemptTheFreezeAdmits()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);
        var freeze = Base.AddMinutes(10);
        await SeedRevisionAsync(world, plan.RunId, 1, freeze);
        await SeedAttemptAsync(world, plan.RunId, plan.Executions[0], 1, "failed", Base);
        await SeedAttemptAsync(world, plan.RunId, plan.Executions[0], 2, "passed", Base.AddMinutes(5));
        await SeedAttemptAsync(world, plan.RunId, plan.Executions[1], 1, "blocked", Base.AddMinutes(1));

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var evidence = caller.Resolve<IFrozenEvidence>();

        // The mutation this pins: without `order by attempt_no desc` the lower attempt wins and exec 0 reads `failed`.
        var statuses = await evidence.GetLatestAttemptStatusesAsync(
            world.Org, plan.RunId, 1, TestContext.Current.CancellationToken);

        statuses.Select(row => (row.ExecutionKey, row.Status)).ShouldBe(
            [
                (plan.Executions[0].ExecutionKey, "passed"),
                (plan.Executions[1].ExecutionKey, "blocked"),
            ],
            ignoreOrder: true);
    }

    [Fact]
    public async Task GetLatestAttemptStatuses_ExcludesAttemptsCreatedAfterTheFreezeStamp()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var freeze = Base.AddMinutes(10);
        await SeedRevisionAsync(world, plan.RunId, 1, freeze);
        await SeedAttemptAsync(world, plan.RunId, plan.Executions[0], 1, "failed", Base);
        await SeedAttemptAsync(world, plan.RunId, plan.Executions[0], 2, "passed", Base.AddMinutes(20));

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var evidence = caller.Resolve<IFrozenEvidence>();

        // The mutation this pins: dropping `a.created_at <= er.created_at` lets the retry decide, answering `passed`.
        var statuses = await evidence.GetLatestAttemptStatusesAsync(
            world.Org, plan.RunId, 1, TestContext.Current.CancellationToken);

        statuses.Select(row => row.Status).ShouldBe(["failed"]);
    }

    [Fact]
    public async Task GetLatestAttemptStatuses_AdmitsAnAttemptCreatedAtTheFreezeStamp()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var freeze = Base.AddMinutes(10);
        await SeedRevisionAsync(world, plan.RunId, 1, freeze);
        await SeedAttemptAsync(world, plan.RunId, plan.Executions[0], 1, "failed", Base);
        await SeedAttemptAsync(world, plan.RunId, plan.Executions[0], 2, "passed", freeze);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var evidence = caller.Resolve<IFrozenEvidence>();

        // The mutation this pins: narrowing the bound to `a.created_at < er.created_at` answers `failed`.
        var statuses = await evidence.GetLatestAttemptStatusesAsync(
            world.Org, plan.RunId, 1, TestContext.Current.CancellationToken);

        statuses.Select(row => row.Status).ShouldBe(["passed"]);
    }

    [Fact]
    public async Task GetLatestAttemptStatuses_LeavesOutAnExecutionWhoseAttemptsAllPostdateTheFreeze()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);
        var freeze = Base.AddMinutes(10);
        await SeedRevisionAsync(world, plan.RunId, 1, freeze);
        await SeedAttemptAsync(world, plan.RunId, plan.Executions[0], 1, "passed", Base);
        await SeedAttemptAsync(world, plan.RunId, plan.Executions[1], 1, "failed", Base.AddMinutes(30));

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var evidence = caller.Resolve<IFrozenEvidence>();

        var statuses = await evidence.GetLatestAttemptStatusesAsync(
            world.Org, plan.RunId, 1, TestContext.Current.CancellationToken);

        statuses.Select(row => row.ExecutionKey).ShouldBe([plan.Executions[0].ExecutionKey]);
    }

    [Fact]
    public async Task GetLatestAttemptStatuses_ReturnsNothingForAnotherTenantsRun()
    {
        await using var world = await StartAsync();
        var (otherOrg, _) = await CreateOtherTenantAsync(world, Unique.Slug("rival"));
        var mine = await SeedPlanAsync(world, 1);
        var theirs = await SeedPlanAsync(world, 1, otherOrg);
        var freeze = Base.AddMinutes(10);
        await SeedRevisionAsync(world, mine.RunId, 1, freeze);
        await SeedRevisionAsync(world, theirs.RunId, 1, freeze, org: otherOrg);
        await SeedAttemptAsync(world, mine.RunId, mine.Executions[0], 1, "passed", Base);
        await SeedAttemptAsync(world, theirs.RunId, theirs.Executions[0], 1, "failed", Base, otherOrg);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var evidence = caller.Resolve<IFrozenEvidence>();

        // Positive control: the scope's own run answers its attempt.
        (await evidence.GetLatestAttemptStatusesAsync(world.Org, mine.RunId, 1, TestContext.Current.CancellationToken))
            .Count.ShouldBe(1);

        (await evidence.GetLatestAttemptStatusesAsync(world.Org, theirs.RunId, 1, TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task GetLatestAttemptStatuses_ReturnsNothingWhenTheRequestedOrgIsNotTheScopesOrg()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var (otherOrg, _) = await CreateOtherTenantAsync(world, Unique.Slug("rival"));
        await SeedRevisionAsync(world, plan.RunId, 1, Base.AddMinutes(10));
        await SeedAttemptAsync(world, plan.RunId, plan.Executions[0], 1, "passed", Base);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var evidence = caller.Resolve<IFrozenEvidence>();

        // The mutation this pins: dropping `a.org_id = @org_id` answers the scope's own attempt for a foreign org.
        (await evidence.GetLatestAttemptStatusesAsync(otherOrg, plan.RunId, 1, TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
        (await evidence.GetLatestAttemptStatusesAsync(world.Org, plan.RunId, 1, TestContext.Current.CancellationToken))
            .Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetLatestAttemptStatuses_ReadsAttemptsWrittenInTheCallersTransaction()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);

        // A revision stamped in the future admits an attempt written now; the writer takes created_at from the clock.
        await SeedRevisionAsync(world, plan.RunId, 1, DateTimeOffset.UtcNow.AddHours(1));

        var execution = plan.Executions[0];
        await using (var caller = await ContractScope.BeginAsync(world.Api, world.Org))
        {
            var writer = caller.Resolve<ICaseAttemptWriter>();
            var outcomes = await writer.WriteAttemptsAsync(
                plan.RunId,
                [new CaseAttemptInput(execution.ExecutionKey, 1, "passed")],
                TestContext.Current.CancellationToken);
            outcomes.ShouldAllBe(outcome => outcome.Status == CaseAttemptWriteStatus.Accepted);

            var evidence = caller.Resolve<IFrozenEvidence>();
            var seen = await evidence.GetLatestAttemptStatusesAsync(
                world.Org, plan.RunId, 1, TestContext.Current.CancellationToken);

            // The mutation this pins: a second connection (or an inner commit) would not see the uncommitted row.
            seen.Select(row => row.Status).ShouldBe(["passed"]);
            caller.Committed.ShouldBeFalse();
        }

        await using var fresh = await ContractScope.BeginAsync(world.Api, world.Org);
        (await fresh.Resolve<IFrozenEvidence>().GetLatestAttemptStatusesAsync(
            world.Org, plan.RunId, 1, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
}
