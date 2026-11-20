using Evidgate.Modules.Policy.Application.Evaluation;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvaluationTaskTests
{
    /// <summary>
    /// The frozen evidence input is one statement: a revision with no attempts still answers its real header because the
    /// attempt rows are left-joined, so no second revision read and no per-execution attempt read may appear.
    /// </summary>
    [Fact]
    public async Task Evaluate_SeededRun_ReadsFrozenHeaderAndAttemptsInOneStatement()
    {
        var captured = await CaptureEvaluationStatementsAsync(cases: 2);
        var evidence = captured.Statements.Where(statement =>
            statement.Contains("public.evidence_revision", StringComparison.Ordinal)
            || statement.Contains("public.case_attempt", StringComparison.Ordinal)).ToArray();

        evidence.Length.ShouldBe(1, "no separate revision or per-execution attempt read may accompany the frozen input");
        evidence[0].ShouldContain("public.evidence_revision");
        evidence[0].ShouldContain("public.case_attempt");
    }

    /// <summary>
    /// The input stage is a fixed five statements whatever the attempt count: a plan with one case and a plan with twelve
    /// must not make the case or evidence read multiply, because every read is set-based over the whole plan.
    /// </summary>
    [Fact]
    public async Task Evaluate_FewAndManyAttempts_KeepFiveInputLoaderStatements()
    {
        var few = await CaptureEvaluationStatementsAsync(cases: 1);
        var many = await CaptureEvaluationStatementsAsync(cases: 12);

        few.Attempts.ShouldBeGreaterThan(0);
        many.Attempts.ShouldBeGreaterThan(few.Attempts, "the larger seed must exercise more frozen attempt rows");
        var fewInputs = EvaluationInputStatements(few.Statements);
        var manyInputs = EvaluationInputStatements(many.Statements);

        AssertInputStatementBudget(fewInputs);
        AssertInputStatementBudget(manyInputs);
        manyInputs.Length.ShouldBe(fewInputs.Length, "input reads are set-based, not proportional to attempt count");
    }

    private async Task<(long Attempts, IReadOnlyList<string> Statements)> CaptureEvaluationStatementsAsync(int cases)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, cases: cases, hosts: _hosts);
        var seed = await SeedAsync(world);
        var attempts = await _fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.case_attempt where org_id = '{world.Org:D}' and run_id = '{seed.RunId:D}'");
        attempts.ShouldBeGreaterThan(0, "the budget must measure a run with frozen attempts, not an empty plan");

        world.Api.Statements.Clear();
        var evaluated = await OnOwnFlow(token => EvaluateAsync(world, seed, EvaluationMode.Live, token));
        var statements = world.Api.Statements.Snapshot();

        evaluated.Decision.ShouldBe("PASS");
        evaluated.Completeness.ShouldBe("complete");
        (await CountAsync(world, seed)).ShouldBe(1);
        return (attempts, statements);
    }

    private static string[] EvaluationInputStatements(IReadOnlyList<string> statements)
    {
        // The five input reads are named by the tables they touch: plan (run, manifest, planned_execution), suite
        // (suite_revision, suite, suite_member), frozen evidence (evidence_revision, case_attempt), policy
        // (policy_revision) and cases (test_revision, test_case). Table names are the stable identity, unlike select
        // lists; tenant SET LOCAL, the waiver/quarantine snapshot and the evaluation write touch none of them.
        return statements.Where(statement =>
            statement.Contains("public.run ", StringComparison.Ordinal)
            || statement.Contains("public.manifest ", StringComparison.Ordinal)
            || statement.Contains("public.planned_execution ", StringComparison.Ordinal)
            || statement.Contains("public.suite_revision ", StringComparison.Ordinal)
            || statement.Contains("public.suite_member ", StringComparison.Ordinal)
            || statement.Contains("public.suite ", StringComparison.Ordinal)
            || statement.Contains("public.evidence_revision ", StringComparison.Ordinal)
            || statement.Contains("public.case_attempt ", StringComparison.Ordinal)
            || statement.Contains("public.policy_revision ", StringComparison.Ordinal)
            || statement.Contains("public.test_revision ", StringComparison.Ordinal)
            || statement.Contains("public.test_case ", StringComparison.Ordinal)).ToArray();
    }

    private static void AssertInputStatementBudget(string[] statements)
    {
        statements.Length.ShouldBe(5, "one plan, suite, frozen evidence, policy and batched case read are the complete input stage");
        statements.Count(statement => statement.Contains("public.planned_execution ", StringComparison.Ordinal)).ShouldBe(1);
        statements.Count(statement => statement.Contains("public.suite_revision ", StringComparison.Ordinal)).ShouldBe(1);
        statements.Count(statement => statement.Contains("public.evidence_revision ", StringComparison.Ordinal)).ShouldBe(1);
        statements.Count(statement => statement.Contains("public.case_attempt ", StringComparison.Ordinal)).ShouldBe(1);
        statements.Count(statement => statement.Contains("public.policy_revision ", StringComparison.Ordinal)).ShouldBe(1);
        statements.Count(statement => statement.Contains("public.test_case ", StringComparison.Ordinal)).ShouldBe(1);
    }
}
