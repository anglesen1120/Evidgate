#pragma warning disable CA1861
using System.Text.Json;
using Evidgate.Modules.Orchestrator.Application.Queries;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.2-T2.2 AC6 (and 05.2.2 §8 "Performance P2"): every shape of the run list is served by an index and never by
/// a full sort. Ascending with <c>ref_name</c> is a forward range scan of
/// <c>run_project_ref_idx (project_id, ref_name, created_at, id)</c>; descending without it is a forward scan of
/// <c>run_project_created_idx (org_id, project_id, created_at desc, id)</c>, whose key order is the page order.
/// Descending with <c>ref_name</c> reads the ref index backward and settles each <c>created_at</c> tie group with an
/// incremental sort, because the ref index holds <c>id</c> ascending where the page wants it after a descending
/// <c>created_at</c>. The plan is asserted by running <c>EXPLAIN (FORMAT JSON)</c> over
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
    /// planner prefers the index without <c>enable_seqscan = off</c> (which the 07.1.1-T3 contract forbids). The
    /// <c>ref_name</c>-and-<c>pr_number</c> shape, the <c>ref_name</c>-only shape and the project-wide shape are
    /// explained in both orders, first page and cursor page each. Deterministic: it asserts node types and index
    /// names only, never timing.
    /// </summary>
    [Fact]
    public async Task ListRuns_EveryOrderAndFilterShape_UsesAnIndexWithoutAFullSort()
    {
        // The case seeds 40 000 runs and ANALYZEs public.run, so the planner's statistics are database-wide.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, database: database.Name);

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.run
              (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
               created_at, collection_deadline_at, late_window_minutes, ref_kind, ref_name, pr_number)
            select gen_random_uuid(), '{world.Org:D}', '{world.Project:D}', '{world.ManifestId:D}',
                   '{world.SuiteRevision:D}', '{world.EnvironmentRevision:D}',
                   timestamptz '2026-01-01T00:00:00Z' + g * interval '1 second',
                   now() + interval '1 hour' + g * interval '1 second', 1440, 'branch', 'ref-' || g, null
              from generate_series(1, 39800) g;
            insert into public.run
              (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
               created_at, collection_deadline_at, late_window_minutes, ref_kind, ref_name, pr_number)
            select gen_random_uuid(), '{world.Org:D}', '{world.Project:D}', '{world.ManifestId:D}',
                   '{world.SuiteRevision:D}', '{world.EnvironmentRevision:D}',
                   timestamptz '2026-01-01T00:00:00Z' + g * interval '1 second',
                   now() + interval '1 hour' + g * interval '1 second', 1440, 'branch', 'main', 7
              from generate_series(1, 200) g;
            analyze public.run;
            """);

        var refAndPr = new RunListFilters("main", 7, null, null, null);
        var refOnly = new RunListFilters("main", null, null, null, null);
        var project = new RunListFilters(null, null, null, null, null);
        const RunListOrder Desc = RunListOrder.Descending;
        const string RefIndex = "run_project_ref_idx";
        const string CreatedIndex = "run_project_created_idx";

        // created_at is seeded explicitly (the column defaults to now(), one value for the whole insert), so this
        // position sits inside the project's range, with about half of the 200 `main` rows on each side.
        var midRange = DateTimeOffset.Parse("2026-01-01T00:01:40Z", System.Globalization.CultureInfo.InvariantCulture);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        // Ascending with ref_name.
        await AssertUsesIndexAsync(connection, transaction, ListRunsSql.FirstPage(refAndPr), world, refAndPr, null, RefIndex);
        await AssertUsesIndexAsync(connection, transaction, ListRunsSql.NextPage(refAndPr), world, refAndPr, midRange, RefIndex);
        await AssertUsesIndexAsync(connection, transaction, ListRunsSql.FirstPage(refOnly), world, refOnly, null, RefIndex);
        await AssertUsesIndexAsync(connection, transaction, ListRunsSql.NextPage(refOnly), world, refOnly, midRange, RefIndex);

        // Descending with ref_name.
        await AssertUsesIndexAsync(connection, transaction, ListRunsSql.FirstPage(refAndPr, Desc), world, refAndPr, null, RefIndex);
        await AssertUsesIndexAsync(connection, transaction, ListRunsSql.NextPage(refAndPr, Desc), world, refAndPr, midRange, RefIndex);
        await AssertUsesIndexAsync(connection, transaction, ListRunsSql.FirstPage(refOnly, Desc), world, refOnly, null, RefIndex);
        await AssertUsesIndexAsync(connection, transaction, ListRunsSql.NextPage(refOnly, Desc), world, refOnly, midRange, RefIndex);

        // Project-wide, both orders.
        await AssertUsesIndexAsync(connection, transaction, ListRunsSql.FirstPage(project), world, project, null, CreatedIndex);
        await AssertUsesIndexAsync(connection, transaction, ListRunsSql.NextPage(project), world, project, midRange, CreatedIndex);
        await AssertUsesIndexAsync(connection, transaction, ListRunsSql.FirstPage(project, Desc), world, project, null, CreatedIndex);
        await AssertUsesIndexAsync(connection, transaction, ListRunsSql.NextPage(project, Desc), world, project, midRange, CreatedIndex);
    }

    /// <summary>Explains one handler statement with its real parameters; the named index is used, with no full sort and no table scan.</summary>
    private static async Task AssertUsesIndexAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        RunCreationTests.World world,
        RunListFilters filters,
        DateTimeOffset? cursor,
        string expectedIndex)
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

        indexNames.ShouldContain(expectedIndex, plan);
        nodeTypes.ShouldNotContain("Sort", plan);
        nodeTypes.ShouldNotContain("Seq Scan", plan);
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
