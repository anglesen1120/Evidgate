using System.Text.Json;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Shouldly;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The plan half of AC12: the covered/uncovered page is served by an index over <c>manual_case_link</c>, so the filter
/// is one bounded statement rather than a scan of every link row. The statement explained is the handler's own, with
/// every parameter bound, so the plan cannot come from a simplified copy of the query.
/// </summary>
public sealed partial class AuthoringCoverageTests
{
    /// <summary>Asserts the coverage probe is index-served and never sequential over the link table.</summary>
    private static async Task AssertIndexServedAsync(
        PostgresTenancyFixture fixture, string database, AuthoringLinkTree tree)
    {
        var archived = await fixture.ColumnAsync<Guid>(
            database,
            "select id from public.test_case"
            + $" where org_id = '{tree.Org}' and project_id = '{tree.Project}' and status = 'archived'");
        var plan = await ExplainPageAsync(fixture, database, tree, archived);
        var nodes = AuthoringTimelineTests.PlanNodes(plan);

        nodes.SequentialScanRelations.ShouldNotContain("manual_case_link", "the coverage probe is index-served");

        // The pair's two indexes are interchangeable for this probe: `manual_case_link_case_idx` carries test_case_id
        // in its leaf pages, while `manual_case_link_uq` orders by it and so serves array_agg(... order by test_case_id)
        // without a sort. Either keeps the read index-only; a sequential scan over the links fails above.
        nodes.IndexNames.ShouldContain(
            index => index == "manual_case_link_case_idx" || index == "manual_case_link_uq",
            "the coverage aggregate reads manual_case_link through one of its indexes");
    }

    /// <summary>Explains the served page statement with the handler's parameter set bound inside an app-role scope.</summary>
    private static async Task<string> ExplainPageAsync(
        PostgresTenancyFixture fixture, string database, AuthoringLinkTree tree, IReadOnlyList<Guid> archived)
    {
        await using var scope = await TenantScope.BeginAsync(
            fixture.AppConnectionString(database), tree.Org, tree.Project);

        return await scope.ScalarAsync<string>(
            "explain (format json) " + AuthoringManualCaseReadSql.CasesFirstPage,
            ("org_id", tree.Org),
            ("project_id", tree.Project),
            ("item_id", DBNull.Value),
            ("node_id", DBNull.Value),
            ("state", DBNull.Value),
            ("priority", DBNull.Value),
            ("covered", false),
            ("archived_case_ids", JsonSerializer.Serialize(archived)),
            ("q", DBNull.Value),
            ("take", 51));
    }
}
