using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class SuiteExecutionPlanTests
{
    /// <summary>
    /// With sequential and bitmap scans disabled, the page CTE has a member index path. This does not assert which
    /// plan a small table normally selects, or exclude the bounded final sort after the revision's left join.
    /// </summary>
    [Fact]
    public async Task Plan_Page_StatementHasAMemberIndexPath()
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
        explain.Parameters.AddWithValue("token_project_id", world.Project);
        explain.Parameters.AddWithValue("request_project_id", world.Project);
        explain.Parameters.AddWithValue("cursor_ordinal", -1);
        explain.Parameters.AddWithValue("cursor_id", Guid.Empty);
        explain.Parameters.AddWithValue("limit", 3);
        var json = (string)(await explain.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        var (_, indexNames, sequentialScans) = AuthoringTimelineTests.PlanNodes(json);
        indexNames.ShouldContain("suite_member_plan_idx");
        sequentialScans.ShouldNotContain("suite_member");
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(true, null, true)]
    [InlineData(null, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, null, false)]
    [InlineData(null, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task Plan_Statement_RestrictsRevisionByBothProjectScopes(
        bool? tokenMatchesProject, bool? requestMatchesProject, bool allowed)
    {
        await using var world = await StartAsync();
        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        await using var command = new NpgsqlCommand(SuitePlanReadSql.Page, connection, transaction);
        command.Parameters.AddWithValue("org_id", world.Org);
        command.Parameters.AddWithValue("revision_id", world.Revision);
        command.Parameters.Add("token_project_id", NpgsqlDbType.Uuid).Value = tokenMatchesProject switch
        {
            null => DBNull.Value,
            true => world.Project,
            false => Guid.CreateVersion7(),
        };
        command.Parameters.Add("request_project_id", NpgsqlDbType.Uuid).Value = requestMatchesProject switch
        {
            null => DBNull.Value,
            true => world.Project,
            false => Guid.CreateVersion7(),
        };
        command.Parameters.AddWithValue("cursor_ordinal", -1);
        command.Parameters.AddWithValue("cursor_id", Guid.Empty);
        command.Parameters.AddWithValue("limit", 3);

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var count = 0;
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            count++;
            reader.GetGuid(0).ShouldBe(world.Project);
        }

        count.ShouldBe(allowed ? 3 : 0);
    }
}
