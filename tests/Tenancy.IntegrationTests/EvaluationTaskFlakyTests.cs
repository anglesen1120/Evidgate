using Evidgate.Modules.Policy.Application.Evaluation;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// In-run flaky detection through the stored evaluation: the first case fails on attempt 1 and passes on attempt 2, the
/// frozen revision admits both, and the committed row shows what the evaluator made of the retry. The suite's retry
/// allowance comes from the pinned suite revision, so the same attempts count as flaky under one policy and not at all
/// under another.
/// </summary>
public sealed partial class EvaluationTaskTests
{
    private const string FlakyBudgetOfOneHundred = """{"flaky_budget":100}""";

    private const string FlakyBudgetOfZero = """{"flaky_budget":0}""";

    /// <summary>
    /// A critical case that only passed on a retry fails the verdict with the retry reason alone: the latest attempt is a
    /// pass, so no failure reason is stored, and the redelivered evaluation reads the same row back.
    /// </summary>
    [Fact]
    public async Task Evaluate_CriticalCasePassedOnRetry_StoresFailWithFlakyCriticalRetryPass()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts, maxRetries: 2);
        var seed = await SeedRetriedAsync(world, cases => $$"""{"critical_cases":["{{cases.FirstId}}"]}""");

        var token = TestContext.Current.CancellationToken;
        var inserted = await EvaluateAsync(world, seed, EvaluationMode.Live, token);
        var stored = await ReloadAsync(world, seed, "live");
        var winner = await EvaluateAsync(world, seed, EvaluationMode.Live, token);

        stored.Decision.ShouldBe("FAIL");
        stored.Outcome.ShouldBe("all_passed", "the latest attempt of every execution passed");
        stored.Reasons.ShouldBe(["flaky_critical_retry_pass"]);
        DecidingOf(inserted).ShouldBe(stored.Deciding);
        winner.Id.ShouldBe(inserted.Id);
        (await CountAsync(world, seed)).ShouldBe(1);
    }

    /// <summary>A non-critical retried pass inside the policy budget keeps the pass and says why it is not clean.</summary>
    [Fact]
    public async Task Evaluate_NonCriticalRetryWithinBudget_StoresPassWithFlakyWithinBudget()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts, maxRetries: 2);
        var seed = await SeedRetriedAsync(world, _ => FlakyBudgetOfOneHundred);

        await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);
        var stored = await ReloadAsync(world, seed, "live");

        stored.Decision.ShouldBe("PASS");
        stored.Reasons.ShouldBe(["scope_build_policy_pinned", "flaky_within_budget"], ignoreOrder: true);
    }

    /// <summary>A non-critical retried pass beyond the policy budget downgrades the pass to inconclusive.</summary>
    [Fact]
    public async Task Evaluate_NonCriticalRetryOverBudget_StoresInconclusiveWithFlakyBudgetExceeded()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts, maxRetries: 2);
        var seed = await SeedRetriedAsync(world, _ => FlakyBudgetOfZero);

        await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);
        var stored = await ReloadAsync(world, seed, "live");

        stored.Decision.ShouldBe("INCONCLUSIVE");
        stored.Reasons.ShouldBe(["flaky_budget_exceeded"]);
    }

    /// <summary>
    /// A suite that allows no retry still never lets a recovered critical case pass: the same history stays a failure with
    /// the flaky reason, whatever the suite's retry limit says.
    /// </summary>
    [Fact]
    public async Task Evaluate_SuiteWithoutRetries_CriticalRecoveryStillStoresFailWithTheFlakyReason()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seed = await SeedRetriedAsync(world, cases => $$"""{"critical_cases":["{{cases.FirstId}}"]}""");

        await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);
        var stored = await ReloadAsync(world, seed, "live");

        stored.Decision.ShouldBe("FAIL");
        stored.Reasons.ShouldBe(["flaky_critical_retry_pass"]);
    }

    /// <summary>
    /// The matrix run whose first case failed on attempt 1, then attempt 2 passing for every one of its executions, both
    /// admitted by the freeze (the retry is stamped at the revision's own <c>created_at</c>, the inclusive bound).
    /// </summary>
    private async Task<Seed> SeedRetriedAsync(RunCreationTests.World world, Func<Cases, string> body)
    {
        var seed = await SeedMatrixAsync(world, MatrixRow("M1") with { Body = body, FirstCaseStatus = "failed" });
        var first = await FirstPlannedCaseAsync(world, seed.RunId);

        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into public.case_attempt
              (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status, created_at)
            select gen_random_uuid(), pe.org_id, pe.run_id, pe.execution_key, tr.case_id, 2, 'passed', 'passed', er.created_at
              from public.planned_execution pe
              join public.test_revision tr on tr.org_id = pe.org_id and tr.id = pe.case_revision_id
              join public.test_case tc on tc.org_id = tr.org_id and tc.id = tr.case_id
              join public.evidence_revision er on er.org_id = pe.org_id and er.run_id = pe.run_id and er.id = @revision_id
             where pe.org_id = @org_id and pe.run_id = @run_id and tc.name = @first_case
            """;
        command.Parameters.AddWithValue("org_id", world.Org);
        command.Parameters.AddWithValue("run_id", seed.RunId);
        command.Parameters.AddWithValue("revision_id", seed.RevisionId);
        command.Parameters.AddWithValue("first_case", first.FirstName);

        (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBeGreaterThan(
            0, "the first case's executions each get a retry");
        return seed;
    }
}
