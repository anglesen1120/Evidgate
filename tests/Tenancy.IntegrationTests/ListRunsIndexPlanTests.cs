#pragma warning disable CA1861
using System.Text.Json;
using Evidgate.Modules.Orchestrator.Application.Queries;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.2-T2.2 AC6 (and 05.2.2 §8 "Performance P2"): the run list's <c>ref_name</c> filter is served by
/// <c>run_project_ref_idx (project_id, ref_name, created_at, id)</c> — a short forward range scan in
/// <c>(created_at, id)</c> order with no sort step. The plan is asserted by running <c>EXPLAIN (FORMAT JSON)</c> over
/// the handler's own statements (<see cref="ListRunsSql"/>, reached through <c>InternalsVisibleTo</c>) with the
/// handler's parameters bound, so the assertion follows the served query and not a copy of it.
/// </summary>
[Trait("Category", "Docker")]
public sealed class ListRunsIndexPlanTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ListRunsIndexPlanTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The target project is seeded sparsely for the filtered ref (<c>main</c>: 200 of 40 000 runs, the rest one row
    /// each under 39 800 distinct ref names) with a distinct <c>created_at</c> per row and <c>ANALYZE</c>d, so the
    /// planner prefers the index without <c>enable_seqscan = off</c> (which the 07.1.1-T3 contract forbids). Both the
    /// <c>ref_name</c>-and-<c>pr_number</c> shape and the <c>ref_name</c>-only shape are explained, first page and
    /// cursor page each. Deterministic: it asserts node types and index names only, never timing.
    /// </summary>
    [Fact]
    public async Task ListRuns_FilterByRefAndPr_UsesIndex()
    {
        // The case seeds 40 000 runs and ANALYZEs public.run, so the planner's statistics are database-wide.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, database: database.Name);

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.run
              (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
               collection_deadline_at, late_window_minutes, ref_kind, ref_name, pr_number)
            select gen_random_uuid(), '{world.Org:D}', '{world.Project:D}', '{world.ManifestId:D}',
                   '{world.SuiteRevision:D}', '{world.EnvironmentRevision:D}',
                   now() + interval '1 hour' + g * interval '1 second', 1440, 'branch', 'ref-' || g, null
              from generate_series(1, 39800) g;
            insert into public.run
              (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
               collection_deadline_at, late_window_minutes, ref_kind, ref_name, pr_number)
            select gen_random_uuid(), '{world.Org:D}', '{world.Project:D}', '{world.ManifestId:D}',
                   '{world.SuiteRevision:D}', '{world.EnvironmentRevision:D}',
                   now() + interval '1 hour' + g * interval '1 second', 1440, 'branch', 'main', 7
              from generate_series(1, 200) g;
            analyze public.run;
            """);

        var refAndPr = new RunListFilters("main", 7, null, null, null);
        var refOnly = new RunListFilters("main", null, null, null, null);

        var firstPage = ListRunsSql.FirstPage(refAndPr);
        var nextPage = ListRunsSql.NextPage(refAndPr);
        var refOnlyFirstPage = ListRunsSql.FirstPage(refOnly);
        var refOnlyNextPage = ListRunsSql.NextPage(refOnly);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        await AssertUsesRefIndexAsync(connection, transaction, firstPage, world, refAndPr, cursor: null);
        await AssertUsesRefIndexAsync(connection, transaction, nextPage, world, refAndPr, cursor: DateTimeOffset.UtcNow);
        await AssertUsesRefIndexAsync(connection, transaction, refOnlyFirstPage, world, refOnly, cursor: null);
        await AssertUsesRefIndexAsync(connection, transaction, refOnlyNextPage, world, refOnly, cursor: DateTimeOffset.UtcNow);
    }

    /// <summary>Explains one handler statement with its real parameters and asserts the ref index is used with no sort step.</summary>
    private static async Task AssertUsesRefIndexAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        RunCreationTests.World world,
        RunListFilters filters,
        DateTimeOffset? cursor)
    {
        await using var command = new NpgsqlCommand("explain (format json) " + sql, connection, transaction);
        command.Parameters.AddWithValue("org_id", world.Org);
        command.Parameters.AddWithValue("project_id", world.Project);
        if (filters.RefName is { } refName)
        {
            command.Parameters.AddWithValue("ref_name", refName);
        }

        if (filters.PrNumber is { } prNumber)
        {
            command.Parameters.AddWithValue("pr_number", prNumber);
        }

        if (cursor is not null)
        {
            command.Parameters.AddWithValue("cursor_created_at", cursor.Value);
            command.Parameters.AddWithValue("cursor_id", Guid.CreateVersion7());
        }

        command.Parameters.AddWithValue("limit", 51);

        var plan = (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        var (nodeTypes, indexNames) = PlanNodes(plan);

        indexNames.ShouldContain("run_project_ref_idx");
        nodeTypes.ShouldNotContain("Sort");
    }

    /// <summary>Every node type and index name in an <c>EXPLAIN (FORMAT JSON)</c> document, however the plan nests.</summary>
    private static (List<string> NodeTypes, List<string> IndexNames) PlanNodes(string json)
    {
        var nodeTypes = new List<string>();
        var indexNames = new List<string>();

        using var document = JsonDocument.Parse(json);
        foreach (var root in document.RootElement.EnumerateArray())
        {
            Walk(root.GetProperty("Plan"));
        }

        return (nodeTypes, indexNames);

        void Walk(JsonElement node)
        {
            if (node.TryGetProperty("Node Type", out var type))
            {
                nodeTypes.Add(type.GetString()!);
            }

            if (node.TryGetProperty("Index Name", out var index))
            {
                indexNames.Add(index.GetString()!);
            }

            if (node.TryGetProperty("Plans", out var plans))
            {
                foreach (var child in plans.EnumerateArray())
                {
                    Walk(child);
                }
            }
        }
    }
}
