using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class SuiteExecutionPlanTests
{
    /// <summary>
    /// The page statement is a range over <c>suite_member_plan_idx</c>: migration <c>0111</c> declares the index with
    /// <c>org_id</c> leading (the tenant predicate and the policy both filter it) and the statement's
    /// <c>(ordinal, id)</c> seek and order match its remaining columns, so a page is an index range with no sort and no
    /// scan of the tenant's members.
    /// </summary>
    [Fact]
    public async Task Plan_Page_IsServedByTheSuiteMemberPlanIndex()
    {
        await using var world = await StartAsync();

        var definition = await _fixture.ScalarAsync<string>(
            world.Database,
            "select indexdef from pg_indexes where schemaname = 'public' and indexname = 'suite_member_plan_idx'");
        definition.ShouldNotBeNull("0111 adds the plan page index");
        definition.ShouldContain("(org_id, suite_revision_id, ordinal, id)");

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        // Six members: the planner would rather scan them, so the question is whether the index can serve the seek and
        // the order at all, which is what the declared definition above and this forced plan answer together.
        await using (var forced = new NpgsqlCommand(
            "set local enable_seqscan = off; set local enable_bitmapscan = off", connection, transaction))
        {
            await forced.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var explain = new NpgsqlCommand(
            "explain (format json) " + SuitePlanReadSql.NextPage, connection, transaction);
        explain.Parameters.AddWithValue("org_id", world.Org);
        explain.Parameters.AddWithValue("revision_id", world.Revision);
        explain.Parameters.AddWithValue("cursor_ordinal", 0);
        explain.Parameters.AddWithValue("cursor_id", Guid.Empty);
        explain.Parameters.AddWithValue("limit", 3);
        var json = (string)(await explain.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        var (nodeTypes, indexNames, sequentialScans) = AuthoringTimelineTests.PlanNodes(json);
        indexNames.ShouldContain("suite_member_plan_idx");
        sequentialScans.ShouldNotContain("suite_member");
        nodeTypes.ShouldNotContain("Sort", "the page order is the index order");
    }
}
