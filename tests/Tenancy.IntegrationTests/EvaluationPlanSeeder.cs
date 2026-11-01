namespace Tenancy.IntegrationTests;

/// <summary>
/// One committed run with a suite revision, its members and planned executions, read back for the evaluation contract
/// tests. <see cref="PlannedExecutionSeeder"/> owns the rows; this helper only answers the catalogue ids an evaluation
/// needs, so both contract test classes seed a plan the same way instead of repeating the lookups.
/// </summary>
internal static class EvaluationPlanSeeder
{
    /// <summary>One seeded run's plan: its suite revision, the suite behind it and the planned executions.</summary>
    /// <param name="RunId">The <c>run.id</c>.</param>
    /// <param name="SuiteId">The <c>suite.id</c> the revision belongs to.</param>
    /// <param name="SuiteRevisionId">The <c>suite_revision.id</c> the run pinned.</param>
    /// <param name="SuiteSlug">The suite's <c>slug</c>.</param>
    /// <param name="Executions">The planned executions, in seed order.</param>
    internal sealed record Plan(
        Guid RunId,
        Guid SuiteId,
        Guid SuiteRevisionId,
        string SuiteSlug,
        IReadOnlyList<PlannedExecutionSeeder.SeededExecution> Executions);

    /// <summary>Seeds one committed plan of <paramref name="executions"/> planned executions for <paramref name="org"/>.</summary>
    internal static async Task<Plan> SeedAsync(
        PostgresTenancyFixture fixture,
        string database,
        Guid org,
        int executions)
    {
        var seeded = await PlannedExecutionSeeder.SeedPlanAsync(fixture, database, org, executions);
        var suiteRevisionId = await fixture.ScalarAsync<Guid>(
            database,
            $"select suite_revision_id from public.run where org_id = '{org}' and id = '{seeded.RunId}'");
        var suiteId = await fixture.ScalarAsync<Guid>(
            database,
            $"select suite_id from public.suite_revision where org_id = '{org}' and id = '{suiteRevisionId}'");
        var slug = await fixture.ScalarAsync<string>(
            database,
            $"select slug from public.suite where org_id = '{org}' and id = '{suiteId}'");

        return new Plan(seeded.RunId, suiteId, suiteRevisionId, slug, seeded.Executions);
    }
}
