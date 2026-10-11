using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.3-T2.1 QA (S31): negative, boundary and exploratory coverage the delivery suite
/// (<see cref="CaseAttemptWriterTests"/>) leaves open — the batch-size limit itself, a replay of one slot
/// across two batches inside one transaction, microsecond truncation of the duplicate payload, the
/// validation boundaries, and a mixed batch that writes one row and still refuses.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class CaseAttemptWriterQaTests
{
    /// <summary>A well-formed execution key that matches no plan: 64 lowercase hex.</summary>
    private const string UnplannedKey = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    private readonly PostgresTenancyFixture _fixture;

    public CaseAttemptWriterQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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

    private async Task<PlannedExecutionSeeder.SeededRun> SeedPlanAsync(World world, int count = 1) =>
        await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, count);

    private Task<long> CountRowsAsync(World world, Guid runId) =>
        _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.case_attempt where run_id = '{runId}'");

    private Task<string> RawStatusAsync(World world, Guid runId, string executionKey) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            $"select raw_status from public.case_attempt where run_id = '{runId}' and execution_key = '{executionKey}'");

    /// <summary>Every statement that touched <c>public.case_attempt</c> since the last clear.</summary>
    private static IReadOnlyList<string> CaseAttemptStatements(World world) =>
        [.. world.Api.Statements.Snapshot().Where(statement => statement.Contains("public.case_attempt", StringComparison.Ordinal))];

    // ------------------------------------------------------------------ batch size boundary

    [Fact]
    public async Task WriteAttempt_ExactlyMaxBatchSize_OneStatement_WritesEveryRow()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, ICaseAttemptWriter.MaxBatchSize);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        world.Api.Statements.Clear();
        var batch = plan.Executions
            .Select(execution => new CaseAttemptInput(execution.ExecutionKey, 1, "passed"))
            .ToArray();

        var outcomes = await scope.WriteAsync(plan.RunId, batch);

        outcomes.Count.ShouldBe(ICaseAttemptWriter.MaxBatchSize);
        outcomes.ShouldAllBe(outcome => outcome.Status == CaseAttemptWriteStatus.Accepted);
        CaseAttemptStatements(world).Count.ShouldBe(1);
        await scope.CommitAsync();

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(ICaseAttemptWriter.MaxBatchSize);
    }

    // ------------------------------------------------------------------ replay inside one transaction

    [Fact]
    public async Task WriteAttempt_TwoBatchesSameSlotInOneTransaction_DuplicateThenDifferentPayload()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);

        // Batch 1 writes the slot; batch 2 of the same transaction must see it through statement 2.
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(key, 1, "passed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(key, 1, "passed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Duplicate);
        var different = (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(key, 1, "failed")))
            .ShouldHaveSingleItem();
        different.Status.ShouldBe(CaseAttemptWriteStatus.DuplicateDifferentPayload);
        different.IsRefusal.ShouldBeTrue();

        await scope.CommitAsync();

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(1);
        (await RawStatusAsync(world, plan.RunId, key)).ShouldBe("passed");
    }

    // ------------------------------------------------------------------ microsecond truncation

    [Fact]
    public async Task WriteAttempt_SameSlot_SubMicrosecondTimestampDifference_IsDuplicate()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        await using (var scope = await AttemptScope.BeginAsync(world.Api, world.Org))
        {
            // The first write stores started_at = base, finished_at = base + 1 microsecond (10 ticks).
            (await scope.WriteAsync(
                    plan.RunId,
                    new CaseAttemptInput(key, 1, "passed", baseTime, baseTime.AddTicks(10), 2000)))
                .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);

            // Four ticks (400 ns) later on both instants: PostgreSQL's microsecond column floors them back to the
            // stored value, so the payload is the same row and the replay is a Duplicate, not a refusal.
            (await scope.WriteAsync(
                    plan.RunId,
                    new CaseAttemptInput(key, 1, "passed", baseTime.AddTicks(4), baseTime.AddTicks(14), 2000)))
                .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Duplicate);

            await scope.CommitAsync();
        }

        // One whole microsecond later is a different stored instant: the same slot with a different payload.
        await using (var scope = await AttemptScope.BeginAsync(world.Api, world.Org))
        {
            var different = (await scope.WriteAsync(
                    plan.RunId,
                    new CaseAttemptInput(key, 1, "passed", baseTime.AddTicks(10), baseTime.AddTicks(20), 2000)))
                .ShouldHaveSingleItem();
            different.Status.ShouldBe(CaseAttemptWriteStatus.DuplicateDifferentPayload);
        }

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ duration boundaries

    [Fact]
    public async Task WriteAttempt_DurationBoundaries_ZeroAndIntMax_Accepted()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        var outcomes = await scope.WriteAsync(
            plan.RunId,
            new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "passed", DurationMs: 0),
            new CaseAttemptInput(plan.Executions[1].ExecutionKey, 1, "passed", DurationMs: int.MaxValue));
        outcomes.ShouldAllBe(outcome => outcome.Status == CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();

        (await _fixture.ColumnAsync<int>(
            world.Database,
            $"select duration_ms from public.case_attempt where run_id = '{plan.RunId}' order by duration_ms"))
            .ShouldBe(new[] { 0, int.MaxValue });
    }

    // ------------------------------------------------------------------ timestamp boundaries

    [Fact]
    public async Task WriteAttempt_FinishedEqualToStarted_AndSubMicrosecondInversion_Accepted()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        var outcomes = await scope.WriteAsync(
            plan.RunId,
            // finished == started is not "finished before started".
            new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "passed", baseTime, baseTime),
            // Four ticks (400 ns) earlier than the start, but the same microsecond once floored: accepted.
            new CaseAttemptInput(plan.Executions[1].ExecutionKey, 1, "passed", baseTime.AddTicks(5), baseTime.AddTicks(1)));
        outcomes.ShouldAllBe(outcome => outcome.Status == CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();

        // Both instants are stored at the floored microsecond, so the two columns read equal for both rows.
        (await _fixture.ColumnAsync<bool>(
            world.Database,
            $"select started_at = finished_at from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldAllBe(equal => equal);
    }

    // ------------------------------------------------------------------ attempt_no boundaries

    [Fact]
    public async Task WriteAttempt_AttemptNoBoundaries_NegativeRefused_MaxAccepted()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);

        var negative = (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(key, -1, "passed"))).ShouldHaveSingleItem();
        negative.Status.ShouldBe(CaseAttemptWriteStatus.Invalid);
        negative.Reason.ShouldBe("attempt_no_invalid");

        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(key, int.MaxValue, "passed")))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();

        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select attempt_no from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe(int.MaxValue);
    }

    // ------------------------------------------------------------------ execution_key shape boundaries

    [Theory]
    [InlineData("uppercase")]
    [InlineData("63_chars")]
    [InlineData("65_chars")]
    [InlineData("trailing_newline")]
    [InlineData("leading_space")]
    public async Task WriteAttempt_ExecutionKeyShapeBoundaries_Refused_ZeroStatements(string caseName)
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);

        var key = caseName switch
        {
            "uppercase" => new string('A', 64),
            "63_chars" => new string('a', 63),
            "65_chars" => new string('a', 65),
            "trailing_newline" => new string('a', 64) + "\n",
            _ => " " + new string('a', 64),
        };

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        world.Api.Statements.Clear();
        var outcome = (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(key, 1, "passed"))).ShouldHaveSingleItem();

        outcome.Status.ShouldBe(CaseAttemptWriteStatus.Invalid);
        outcome.Reason.ShouldBe("execution_key_invalid");
        CaseAttemptStatements(world).ShouldBeEmpty();
        (await CountRowsAsync(world, plan.RunId)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ raw status boundaries

    [Fact]
    public async Task WriteAttempt_RawStatus_64AsciiCodePoints_Accepted()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;
        var raw = new string('s', 64);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(key, 1, raw)))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();

        (await RawStatusAsync(world, plan.RunId, key)).ShouldBe(raw);
    }

    [Fact]
    public async Task WriteAttempt_XfailOnSkipped_LeavesNormalisationUnchanged()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(key, 1, "skipped", Xfail: true)))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();

        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select normalized_status from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe("skipped");
    }

    // ------------------------------------------------------------------ mixed batch

    [Fact]
    public async Task WriteAttempt_MixedBatch_ValidAndUnknown_IndexAlignedOutcomes_CallerMustRollBack()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var planned = plan.Executions[0];

        await using (var scope = await AttemptScope.BeginAsync(world.Api, world.Org))
        {
            world.Api.Statements.Clear();
            var outcomes = await scope.WriteAsync(
                plan.RunId,
                new CaseAttemptInput(planned.ExecutionKey, 1, "passed"),
                new CaseAttemptInput(UnplannedKey, 1, "passed"));

            outcomes[0].Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
            outcomes[1].Status.ShouldBe(CaseAttemptWriteStatus.UnknownExecution);
            outcomes[1].IsRefusal.ShouldBeTrue();

            // Two statements: the batch insert (which DID write the valid row) then the classifier.
            CaseAttemptStatements(world).Count.ShouldBe(2);

            // The valid row is already in the caller's transaction: a replay of the same slot now reads as a
            // Duplicate, which a separate connection could never see before the commit.
            (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(planned.ExecutionKey, 1, "passed")))
                .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Duplicate);

            // ...so the contract's IsRefusal duty is what keeps the batch atomic: the caller rolls back.
        }

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(0);
    }
}
