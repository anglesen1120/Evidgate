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
public sealed class CaseAttemptWriterTests : IClassFixture<TenancyHostPool>
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

    // ------------------------------------------------------------------ AC1

    [Fact]
    public async Task WriteAttempt_NewExecution_RawAndNormalizedStatusStored()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var planned = plan.Executions[0];

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        var outcome = (await scope.WriteAsync(
            plan.RunId, new CaseAttemptInput(planned.ExecutionKey, 1, "timedOut", DurationMs: 1200))).ShouldHaveSingleItem();

        outcome.Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        outcome.IsRefusal.ShouldBeFalse();
        await scope.CommitAsync();

        // The raw value is verbatim, the normalised value is 00 §8.3, and case_id is the planned row's case.
        (await _fixture.ScalarAsync<string>(world.Database, $"select raw_status from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe("timedOut");
        (await _fixture.ScalarAsync<string>(world.Database, $"select normalized_status from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe("timed_out");
        (await _fixture.ScalarAsync<Guid>(world.Database, $"select case_id from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe(planned.CaseId);
        (await _fixture.ScalarAsync<int>(world.Database, $"select attempt_no from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe(1);
    }

    // ------------------------------------------------------------------ AC2 / T50

    [Fact]
    public async Task WriteAttempt_DuplicateDifferentPayload_422_NoRewrite()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var planned = plan.Executions[0];

        await using (var first = await AttemptScope.BeginAsync(world.Api, world.Org))
        {
            (await first.WriteAsync(plan.RunId, new CaseAttemptInput(planned.ExecutionKey, 1, "passed")))
                .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
            await first.CommitAsync();
        }

        var originalId = await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.case_attempt where run_id = '{plan.RunId}'");

        await using var second = await AttemptScope.BeginAsync(world.Api, world.Org);
        var outcome = (await second.WriteAsync(plan.RunId, new CaseAttemptInput(planned.ExecutionKey, 1, "failed")))
            .ShouldHaveSingleItem();

        outcome.Status.ShouldBe(CaseAttemptWriteStatus.DuplicateDifferentPayload);
        outcome.IsRefusal.ShouldBeTrue();

        // The first writer wins: the original row is untouched, and no second row appeared.
        (await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe(originalId);
        (await _fixture.ScalarAsync<string>(world.Database, $"select raw_status from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe("passed");
        (await CountRowsAsync(world, plan.RunId)).ShouldBe(1);
    }

    [Fact]
    public async Task WriteAttempt_DuplicateSamePayload_IsDuplicateNotRefusal()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var planned = plan.Executions[0];
        var started = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var attempt = new CaseAttemptInput(planned.ExecutionKey, 1, "passed", started, started.AddSeconds(2), 2000);

        await using (var first = await AttemptScope.BeginAsync(world.Api, world.Org))
        {
            (await first.WriteAsync(plan.RunId, attempt)).ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
            await first.CommitAsync();
        }

        await using var second = await AttemptScope.BeginAsync(world.Api, world.Org);
        var outcome = (await second.WriteAsync(plan.RunId, attempt)).ShouldHaveSingleItem();

        outcome.Status.ShouldBe(CaseAttemptWriteStatus.Duplicate);
        outcome.IsRefusal.ShouldBeFalse();
        (await CountRowsAsync(world, plan.RunId)).ShouldBe(1);
    }

    [Fact]
    public async Task WriteAttempt_InBatchDuplicate_FirstWins()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var planned = plan.Executions[0];

        // Identical payload twice in one batch: the first is the candidate, the second is a Duplicate.
        await using (var same = await AttemptScope.BeginAsync(world.Api, world.Org))
        {
            var outcomes = await same.WriteAsync(
                plan.RunId,
                new CaseAttemptInput(planned.ExecutionKey, 1, "passed"),
                new CaseAttemptInput(planned.ExecutionKey, 1, "passed"));

            outcomes[0].Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
            outcomes[1].Status.ShouldBe(CaseAttemptWriteStatus.Duplicate);
            await same.CommitAsync();
        }

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(1);

        // A differing payload for the same slot: the pre-pass refuses, so NO statement runs and no row is written.
        await using var different = await AttemptScope.BeginAsync(world.Api, world.Org);
        world.Api.Statements.Clear();
        var refused = await different.WriteAsync(
            plan.RunId,
            new CaseAttemptInput(planned.ExecutionKey, 2, "passed"),
            new CaseAttemptInput(planned.ExecutionKey, 2, "failed"));

        refused[0].Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        refused[1].Status.ShouldBe(CaseAttemptWriteStatus.DuplicateDifferentPayload);
        refused[1].IsRefusal.ShouldBeTrue();
        CaseAttemptStatements(world).ShouldBeEmpty();
        (await CountRowsAsync(world, plan.RunId)).ShouldBe(1);
    }

    [Fact]
    public async Task WriteAttempt_RefusedBatch_KeepsInBatchDuplicate_ZeroStatements()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var planned = plan.Executions[0];

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        world.Api.Statements.Clear();

        // One malformed attempt refuses the whole batch, so no statement runs and nothing is written. The in-batch
        // same-payload replay keeps its Duplicate (its payload equality needed no statement, C1); the two undecided
        // entries stay the advisory Accepted the pre-pass set, because the caller rolls back on the refusal.
        var outcomes = await scope.WriteAsync(
            plan.RunId,
            new CaseAttemptInput(planned.ExecutionKey, 1, "passed"),
            new CaseAttemptInput(planned.ExecutionKey, 1, "passed"),
            new CaseAttemptInput(new string('z', 64), 1, "passed"));

        outcomes[0].Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        outcomes[1].Status.ShouldBe(CaseAttemptWriteStatus.Duplicate);
        outcomes[1].IsRefusal.ShouldBeFalse();
        outcomes[2].Status.ShouldBe(CaseAttemptWriteStatus.Invalid);
        outcomes[2].Reason.ShouldBe("execution_key_invalid");
        outcomes.ShouldContain(outcome => outcome.IsRefusal);

        CaseAttemptStatements(world).ShouldBeEmpty();
        (await CountRowsAsync(world, plan.RunId)).ShouldBe(0);
    }

    [Fact]
    public async Task WriteAttempt_InBatchReplay_OfUnplannedSlot_ReportsUnknownExecution()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);

        // The slot's first occurrence turns out an unknown execution after statement 2, so its in-batch same-payload
        // replay reports the same refusal instead of Duplicate (C2).
        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        var outcomes = await scope.WriteAsync(
            plan.RunId,
            new CaseAttemptInput(UnplannedKey, 1, "passed"),
            new CaseAttemptInput(UnplannedKey, 1, "passed"));

        outcomes[0].Status.ShouldBe(CaseAttemptWriteStatus.UnknownExecution);
        outcomes[1].Status.ShouldBe(CaseAttemptWriteStatus.UnknownExecution);
        outcomes[1].IsRefusal.ShouldBeTrue();
        (await CountRowsAsync(world, plan.RunId)).ShouldBe(0);
    }

    [Fact]
    public async Task WriteAttempt_InBatchReplay_OfStoredDifferentPayload_ReportsDuplicateDifferentPayload()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var planned = plan.Executions[0];

        await using (var first = await AttemptScope.BeginAsync(world.Api, world.Org))
        {
            (await first.WriteAsync(plan.RunId, new CaseAttemptInput(planned.ExecutionKey, 1, "passed")))
                .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
            await first.CommitAsync();
        }

        // The stored row differs from the batch payload, so the slot's first occurrence is DuplicateDifferentPayload
        // after statement 2 and its in-batch same-payload replay reports the same refusal (C2); the stored row is
        // untouched.
        await using var second = await AttemptScope.BeginAsync(world.Api, world.Org);
        var outcomes = await second.WriteAsync(
            plan.RunId,
            new CaseAttemptInput(planned.ExecutionKey, 1, "failed"),
            new CaseAttemptInput(planned.ExecutionKey, 1, "failed"));

        outcomes[0].Status.ShouldBe(CaseAttemptWriteStatus.DuplicateDifferentPayload);
        outcomes[1].Status.ShouldBe(CaseAttemptWriteStatus.DuplicateDifferentPayload);
        outcomes[1].IsRefusal.ShouldBeTrue();
        (await CountRowsAsync(world, plan.RunId)).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database, $"select raw_status from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe("passed");
    }

    // ------------------------------------------------------------------ normalisation

    [Fact]
    public async Task WriteAttempt_UnknownRawStatus_NormalizedError()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var planned = plan.Executions[0];

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(planned.ExecutionKey, 1, "weird-status")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();

        (await _fixture.ScalarAsync<string>(world.Database, $"select raw_status from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe("weird-status");
        (await _fixture.ScalarAsync<string>(world.Database, $"select normalized_status from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe("error");
    }

    [Fact]
    public async Task WriteAttempt_XfailFlipsFailedAndPassed()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 3);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        var outcomes = await scope.WriteAsync(
            plan.RunId,
            new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "failed", Xfail: true),
            new CaseAttemptInput(plan.Executions[1].ExecutionKey, 1, "passed", Xfail: true),
            new CaseAttemptInput(plan.Executions[2].ExecutionKey, 1, "timedOut", Xfail: true));

        outcomes.ShouldAllBe(outcome => outcome.Status == CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();

        (await NormalizedAsync(world, plan.RunId, plan.Executions[0].ExecutionKey)).ShouldBe("passed");
        (await NormalizedAsync(world, plan.RunId, plan.Executions[1].ExecutionKey)).ShouldBe("failed");
        (await NormalizedAsync(world, plan.RunId, plan.Executions[2].ExecutionKey)).ShouldBe("timed_out");
    }

    [Fact]
    public async Task WriteAttempt_BlockedRawStatus_NormalizedError()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var planned = plan.Executions[0];

        // `blocked` is server-assigned only: a producer that sends it gets the unknown-raw mapping (error).
        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(planned.ExecutionKey, 1, "blocked")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();

        (await _fixture.ScalarAsync<string>(world.Database, $"select raw_status from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe("blocked");
        (await NormalizedAsync(world, plan.RunId, planned.ExecutionKey)).ShouldBe("error");
    }

    private Task<string> NormalizedAsync(World world, Guid runId, string executionKey) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            $"select normalized_status from public.case_attempt where run_id = '{runId}' and execution_key = '{executionKey}'");

    // ------------------------------------------------------------------ unknown executions

    [Fact]
    public async Task WriteAttempt_UnplannedExecutionKey_UnknownExecution_NoRow()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        var outcome = (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(UnplannedKey, 1, "passed")))
            .ShouldHaveSingleItem();

        outcome.Status.ShouldBe(CaseAttemptWriteStatus.UnknownExecution);
        outcome.IsRefusal.ShouldBeTrue();
        (await CountRowsAsync(world, plan.RunId)).ShouldBe(0);
    }

    [Fact]
    public async Task WriteAttempt_OtherRunsKey_UnknownExecution()
    {
        await using var world = await StartAsync();
        var runA = await SeedPlanAsync(world);
        var runB = await SeedPlanAsync(world);

        // The key is planned, but in run A: called for run B it is unknown.
        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        var outcome = (await scope.WriteAsync(runB.RunId, new CaseAttemptInput(runA.Executions[0].ExecutionKey, 1, "passed")))
            .ShouldHaveSingleItem();

        outcome.Status.ShouldBe(CaseAttemptWriteStatus.UnknownExecution);
        (await CountRowsAsync(world, runB.RunId)).ShouldBe(0);
    }

    [Fact]
    public async Task WriteAttempt_CrossTenant_UnknownExecution()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);

        // Another org's run: RLS makes it invisible, so the writer reports the same unknown as an unplanned key.
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "rival");
        var otherPlan = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, otherOrg, 1);

        await using (var otherScope = await AttemptScope.BeginAsync(world.Api, otherOrg))
        {
            var outcome = (await otherScope.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "passed")))
                .ShouldHaveSingleItem();
            outcome.Status.ShouldBe(CaseAttemptWriteStatus.UnknownExecution);
        }

        // A project-scoped scope for another project of the same org: the run is not visible either.
        await using (var projectScope = await AttemptScope.BeginAsync(world.Api, world.Org, otherProject))
        {
            var outcome = (await projectScope.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "passed")))
                .ShouldHaveSingleItem();
            outcome.Status.ShouldBe(CaseAttemptWriteStatus.UnknownExecution);
        }

        // The control: under the run's own org the very same call is accepted.
        await using (var own = await AttemptScope.BeginAsync(world.Api, world.Org))
        {
            (await own.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "passed")))
                .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
            await own.CommitAsync();
        }

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(1);
        (await CountRowsAsync(world, otherPlan.RunId)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ validation

    [Theory]
    [InlineData("execution_key_invalid")]
    [InlineData("attempt_no_invalid")]
    [InlineData("raw_status_invalid")]
    [InlineData("duration_invalid")]
    [InlineData("timestamps_invalid")]
    public async Task WriteAttempt_InvalidInputs_Refused_ZeroStatements(string caseName)
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        world.Api.Statements.Clear();
        var outcome = (await scope.WriteAsync(plan.RunId, InvalidInput(caseName, key))).ShouldHaveSingleItem();

        outcome.Status.ShouldBe(CaseAttemptWriteStatus.Invalid);
        outcome.Reason.ShouldBe(caseName);
        outcome.IsRefusal.ShouldBeTrue();
        CaseAttemptStatements(world).ShouldBeEmpty();
        (await CountRowsAsync(world, plan.RunId)).ShouldBe(0);
    }

    private static CaseAttemptInput InvalidInput(string caseName, string key) => caseName switch
    {
        "execution_key_invalid" => new CaseAttemptInput(new string('z', 64), 1, "passed"),
        "attempt_no_invalid" => new CaseAttemptInput(key, 0, "passed"),
        "raw_status_invalid" => new CaseAttemptInput(key, 1, "bad\0status"),
        "duration_invalid" => new CaseAttemptInput(key, 1, "passed", DurationMs: -1),
        _ => new CaseAttemptInput(
            key, 1, "passed",
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero)),
    };

    [Fact]
    public async Task WriteAttempt_RawStatus_65CodePoints_And_LoneSurrogate_Refused()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);

        var tooLong = (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(key, 1, new string('x', 65))))
            .ShouldHaveSingleItem();
        tooLong.Status.ShouldBe(CaseAttemptWriteStatus.Invalid);
        tooLong.Reason.ShouldBe("raw_status_invalid");

        var loneSurrogate = (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(key, 1, "a\ud800b")))
            .ShouldHaveSingleItem();
        loneSurrogate.Status.ShouldBe(CaseAttemptWriteStatus.Invalid);
        loneSurrogate.Reason.ShouldBe("raw_status_invalid");

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(0);
    }

    [Fact]
    public async Task WriteAttempt_RawStatus_64AstralCodePoints_IsAccepted()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var planned = plan.Executions[0];
        var raw = string.Concat(Enumerable.Repeat("😀", 64)); // 64 code points, 128 UTF-16 units

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(planned.ExecutionKey, 1, raw)))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();

        (await _fixture.ScalarAsync<string>(world.Database, $"select raw_status from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe(raw);
        (await NormalizedAsync(world, plan.RunId, planned.ExecutionKey)).ShouldBe("error");
    }

    [Fact]
    public async Task WriteAttempt_EmptyBatch_And_Over500_Throw()
    {
        await using var world = await StartAsync();

        // No tenant scope is open, so an ArgumentException here proves the size guard runs before scopes.Require().
        await using var services = world.Api.Services.CreateAsyncScope();
        var writer = services.ServiceProvider.GetRequiredService<ICaseAttemptWriter>();
        var attempts = Enumerable.Range(0, 501)
            .Select(i => new CaseAttemptInput(UnplannedKey, i + 1, "passed"))
            .ToArray();

        await Should.ThrowAsync<ArgumentException>(
            () => writer.WriteAttemptsAsync(Guid.CreateVersion7(), [], TestContext.Current.CancellationToken).AsTask());
        await Should.ThrowAsync<ArgumentException>(
            () => writer.WriteAttemptsAsync(Guid.CreateVersion7(), attempts, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task WriteAttempt_NullElement_Throws_ZeroStatements()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);

        // No tenant scope is open, so an ArgumentNullException here proves the element guard runs before
        // scopes.Require() like the batch-size guard, and the message names the offending index.
        await using var services = world.Api.Services.CreateAsyncScope();
        var writer = services.ServiceProvider.GetRequiredService<ICaseAttemptWriter>();

        var error = await Should.ThrowAsync<ArgumentNullException>(
            () => writer.WriteAttemptsAsync(
                plan.RunId,
                new CaseAttemptInput[] { null! },
                TestContext.Current.CancellationToken).AsTask());

        error.ParamName.ShouldBe("attempts");
        error.Message.ShouldContain("attempts[0]");
        CaseAttemptStatements(world).ShouldBeEmpty();
    }

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
