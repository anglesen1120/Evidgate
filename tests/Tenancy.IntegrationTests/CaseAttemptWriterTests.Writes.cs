using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class CaseAttemptWriterTests
{
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
}
