using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class SuiteExecutionPlanTests
{
    /// <summary>
    /// The page statement <em>can</em> be served by <c>suite_member_plan_idx</c>: the index is declared with
    /// <c>org_id</c> leading and the statement's <c>(ordinal, id)</c> seek and order match its remaining columns. Six
    /// members would tempt the planner into a scan, so the plan is forced here and this test claims only that an
    /// ordered index path exists for the statement — not that the planner picks it on a small table.
    /// </summary>
    [Fact]
    public async Task Plan_Page_StatementHasAnOrderedIndexPath()
    {
        await using var world = await StartAsync();

        var definition = await _fixture.ScalarAsync<string>(
            world.Database,
            "select indexdef from pg_indexes where schemaname = 'public' and indexname = 'suite_member_plan_idx'");
        definition.ShouldNotBeNull("the plan page index is part of the suite tables' migrations");
        definition.ShouldContain("(org_id, suite_revision_id, ordinal, id)");

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        await using (var forced = new NpgsqlCommand(
            "set local enable_seqscan = off; set local enable_bitmapscan = off", connection, transaction))
        {
            await forced.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var explain = new NpgsqlCommand(
            "explain (format json) " + SuitePlanReadSql.Page, connection, transaction);
        explain.Parameters.AddWithValue("org_id", world.Org);
        explain.Parameters.AddWithValue("revision_id", world.Revision);
        explain.Parameters.AddWithValue("cursor_ordinal", -1);
        explain.Parameters.AddWithValue("cursor_id", Guid.Empty);
        explain.Parameters.AddWithValue("limit", 3);
        var json = (string)(await explain.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        var (nodeTypes, indexNames, sequentialScans) = AuthoringTimelineTests.PlanNodes(json);
        indexNames.ShouldContain("suite_member_plan_idx");
        sequentialScans.ShouldNotContain("suite_member");
        nodeTypes.ShouldNotContain("Sort", "under these settings the page order is the index order");
    }
}
