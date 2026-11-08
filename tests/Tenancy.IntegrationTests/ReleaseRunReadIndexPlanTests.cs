// The release-binding reads as PostgreSQL plans them, explained from the production statements themselves so the
// assertion follows the served SQL and not a copy of it: one run's bindings must come back in (created_at, id) order
// under the read's own ceiling, and the release side must keep reading its binding key.
using System.Text.Json;
using Evidgate.Modules.Orchestrator.Application.Queries;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The access paths of the two release-binding reads — the run detail's capped bindings column and the release detail's
/// <c>bound_runs</c> list — explained as the app role inside a tenant scope with their real parameters bound, on a
/// dedicated database whose volume makes <c>(org_id, run_id)</c> as selective as a real run's bindings.
/// </summary>
[Trait("Category", "Docker")]
public sealed class ReleaseRunReadIndexPlanTests
{
    /// <summary>Releases of the seeded project: the name lookup's build side, large enough that a nested loop wins.</summary>
    private const int BulkReleases = 50_000;

    /// <summary>Runs other than the explained one, so the table holds many runs the read does not serve.</summary>
    private const int BulkRuns = 2_000;

    /// <summary>Bindings each other run holds, so one run's bindings are a small share of the table.</summary>
    private const int BindingsPerBulkRun = 50;

    /// <summary>Bindings of the explained run, past the read's 500 ceiling so the limit is the plan's business.</summary>
    private const int TargetBindings = 600;

    private readonly PostgresTenancyFixture _fixture;

    public ReleaseRunReadIndexPlanTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The run detail selects one run by id and takes its bindings as a column: a subquery ordered by
    /// <c>(created_at, id)</c> and capped by <c>@bound_release_limit</c>. The plan must reach that cap through
    /// <c>release_run_run_created_idx</c> with no sort under it, since the sort the dropped prefix index forced is the
    /// whole cost the migration removes.
    /// </summary>
    [Fact]
    public async Task RunDetail_BindingsRead_UsesTheRunCreatedIndexWithoutSort()
    {
        ReleaseSql.MaxBoundReleases.ShouldBe(500, "the explained statement binds the production ceiling");

        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, database: database.Name);
        var targetRun = await SeedBindingsVolumeAsync(database.Name, world);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database.Name));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        var plan = await ExplainAsync(
            connection,
            transaction,
            GetRunQueryHandler.RunStateById,
            parameters =>
            {
                parameters.AddWithValue("run_id", targetRun);
                parameters.AddWithValue("bound_release_limit", ReleaseSql.MaxBoundReleases);
                parameters.AddWithValue("producer_slot_limit", ProducerSlotFactsSql.MaxProducerSlots);
            });

        // The cap is the Limit over the bindings subquery; locating it by the table it reads keeps the assertion on the
        // bindings path, whatever sort or aggregate the surrounding statement's column list adds above it.
        var limit = plan.SelfAndDescendants()
            .Where(node => node.NodeType == "Limit"
                && node.SelfAndDescendants().Any(read => read.Relation == "release_run"))
            .ShouldHaveSingleItem("the bindings column is one capped subquery over the run's bindings");
        var capped = limit.SelfAndDescendants().ToList();

        capped.Where(node => node.IndexName is not null)
            .Select(node => node.IndexName)
            .ShouldContain("release_run_run_created_idx", "the bindings are read in the index's own order");
        capped.Count(node => node.NodeType.Contains("Sort", StringComparison.Ordinal))
            .ShouldBe(0, "a sort under the cap is the cost the read-order index removes");
        plan.SelfAndDescendants()
            .Where(node => node.NodeType == "Seq Scan")
            .Select(node => node.Relation)
            .ShouldNotContain("release_run", "a sequential scan would read every binding of every run");
    }

    /// <summary>
    /// The release detail lists a release's bindings by <c>(created_at, id)</c> over <c>(org_id, release_id)</c>, which
    /// the binding key's prefix serves; the explained release holds bindings of several runs, so the plan proves the
    /// read still finds them once the narrower <c>(org_id, run_id)</c> index is gone.
    /// </summary>
    [Fact]
    public async Task ReleaseDetail_BoundRuns_KeepTheBindingKeyPrefix()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, database: database.Name);
        await SeedBindingsVolumeAsync(database.Name, world);
        var releaseId = await MostBoundReleaseAsync(database.Name, world.Org);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database.Name));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        var plan = await ExplainAsync(
            connection,
            transaction,
            ReleaseSql.BoundRuns,
            parameters =>
            {
                parameters.AddWithValue("org_id", world.Org);
                parameters.AddWithValue("release_id", releaseId);
            });

        plan.SelfAndDescendants()
            .Where(node => node.IndexName is not null)
            .Select(node => node.IndexName)
            .ShouldContain("release_run_binding_key", "the release's bindings are the binding key's prefix");
        plan.SelfAndDescendants()
            .Where(node => node.NodeType == "Seq Scan")
            .Select(node => node.Relation)
            .ShouldNotContain("release_run", "a sequential scan would read every run's bindings");
    }

    /// <summary>
    /// The migration's own contract: the read-order index is in the schema with its columns in order, and the
    /// <c>(org_id, run_id)</c> index it replaces is gone, so no later read can keep the sort it forced.
    /// </summary>
    [Fact]
    public async Task ReleaseRun_Indexes_HoldTheReadOrderIndexAndNotItsPrefix()
    {
        var indexes = await _fixture.IndexesAsync(await _fixture.SharedDatabaseAsync(), "release_run");

        indexes.Where(index => index.StartsWith("release_run_run_created_idx:", StringComparison.Ordinal))
            .ShouldHaveSingleItem()
            .ShouldContain("(org_id, run_id, created_at, id)", Case.Sensitive);
        indexes.Where(index => index.StartsWith("release_run_run_idx:", StringComparison.Ordinal))
            .ShouldBeEmpty("the (org_id, run_id) prefix is served by the read-order index that replaces it");
    }

    /// <summary>
    /// The planner's statistics: many releases and runs, a run bound to 600 releases at one-minute steps, and a much
    /// larger body of other runs' bindings, so one run's bindings are the small share a real read sees.
    /// </summary>
    private async Task<Guid> SeedBindingsVolumeAsync(string database, RunCreationTests.World world)
    {
        var targetRun = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(database, ReleasesSql(world));
        await _fixture.ExecuteAsync(database, RunsSql(world, targetRun));
        await _fixture.ExecuteAsync(database, BindingsSql(world, targetRun));
        await _fixture.ExecuteAsync(database, "analyze public.release; analyze public.run; analyze public.release_run");

        return targetRun;
    }

    /// <summary>The project's draft releases: names are unique per project, as the create path would leave them.</summary>
    private static string ReleasesSql(RunCreationTests.World world) => $"""
        insert into public.release (id, org_id, project_id, name, created_by)
        select gen_random_uuid(), '{world.Org:D}', '{world.Project:D}', 'bulk-release-' || g, 'probe'
          from generate_series(1, {BulkReleases}) g
        """;

    /// <summary>The bulk runs, and the explained run under an id this test knows, all on the world's own revisions.</summary>
    private static string RunsSql(RunCreationTests.World world, Guid targetRun) => $"""
        insert into public.run (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
                                collection_deadline_at, late_window_minutes, ref_kind, ref_name)
        select gen_random_uuid(), '{world.Org:D}', '{world.Project:D}', '{world.ManifestId:D}',
               '{world.SuiteRevision:D}', '{world.EnvironmentRevision:D}',
               now() + interval '1 hour' + (g * interval '1 second'), 1440, 'branch', 'bulk-run-' || g
          from generate_series(1, {BulkRuns}) g;
        insert into public.run (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
                                collection_deadline_at, late_window_minutes, ref_kind, ref_name)
        values ('{targetRun:D}', '{world.Org:D}', '{world.Project:D}', '{world.ManifestId:D}',
                '{world.SuiteRevision:D}', '{world.EnvironmentRevision:D}',
                now() + interval '1 hour', 1440, 'branch', 'target-run');
        """;

    /// <summary>
    /// Every bulk run's bindings spread over the releases, and the explained run's 600 on distinct releases at one
    /// minute steps: distinct <c>created_at</c> per row, so the binding order is the seed's, never the wall clock's.
    /// </summary>
    private static string BindingsSql(RunCreationTests.World world, Guid targetRun) => $"""
        with rel as (select id, row_number() over (order by id) as rn from public.release where org_id = '{world.Org:D}'),
             rns as (select id, row_number() over (order by id) as rn
                       from public.run
                      where org_id = '{world.Org:D}' and id <> '{targetRun:D}')
        insert into public.release_run (id, org_id, release_id, run_id, component_key, created_at)
        select gen_random_uuid(), '{world.Org:D}', rel.id, rns.id, 'k' || lpad(m::text, 3, '0'),
               timestamptz '2026-10-01 00:00:00+00' + ((rns.rn * {BindingsPerBulkRun} + m) * interval '1 second')
          from rns
         cross join generate_series(1, {BindingsPerBulkRun}) m
          join rel on rel.rn = ((rns.rn * {BindingsPerBulkRun} + m) % {BulkReleases}) + 1;
        with rel as (select id, row_number() over (order by id) as rn from public.release where org_id = '{world.Org:D}')
        insert into public.release_run (id, org_id, release_id, run_id, component_key, created_at)
        select gen_random_uuid(), '{world.Org:D}', rel.id, '{targetRun:D}', 'k001',
               timestamptz '2026-11-01 00:00:00+00' + ((n - 1) * interval '1 minute')
          from generate_series(1, {TargetBindings}) n
          join rel on rel.rn = n;
        """;

    /// <summary>The release holding the most bindings, so the release-side read is explained over several rows.</summary>
    private async Task<Guid> MostBoundReleaseAsync(string database, Guid org)
    {
        var releases = await _fixture.ColumnAsync<Guid>(
            database,
            $"""
            select release_id
              from public.release_run
             where org_id = '{org:D}'
             group by release_id
             order by count(*) desc, release_id
             limit 1
            """);

        return releases.ShouldHaveSingleItem("the seed binds releases, so the read has a release to plan");
    }

    /// <summary>Explains one production statement with its real parameters bound, as the app role in the tenant scope.</summary>
    private static async Task<PlanNode> ExplainAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Action<NpgsqlParameterCollection> bind)
    {
        await using var command = new NpgsqlCommand("explain (format json) " + sql, connection, transaction);
        bind(command.Parameters);
        var json = (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        return Plan(json);
    }

    /// <summary>The plan tree of one <c>EXPLAIN (FORMAT JSON)</c> document.</summary>
    private static PlanNode Plan(string json)
    {
        using var document = JsonDocument.Parse(json);

        return Node(document.RootElement.EnumerateArray().Single().GetProperty("Plan"));
    }

    /// <summary>One node with the relation and index it names, and its children, whatever the plan nests.</summary>
    private static PlanNode Node(JsonElement element)
    {
        var children = element.TryGetProperty("Plans", out var plans)
            ? plans.EnumerateArray().Select(Node).ToList()
            : new List<PlanNode>();

        return new PlanNode(
            element.GetProperty("Node Type").GetString()!,
            element.TryGetProperty("Relation Name", out var relation) ? relation.GetString() : null,
            element.TryGetProperty("Index Name", out var index) ? index.GetString() : null,
            children);
    }

    /// <summary>One explained plan node: what it does, what it reads, and the nodes under it.</summary>
    private sealed record PlanNode(string NodeType, string? Relation, string? IndexName, IReadOnlyList<PlanNode> Children)
    {
        /// <summary>This node and every node below it, so an assertion holds wherever the read ended up in the plan.</summary>
        public IEnumerable<PlanNode> SelfAndDescendants()
        {
            yield return this;
            foreach (var descendant in Children.SelectMany(child => child.SelfAndDescendants()))
            {
                yield return descendant;
            }
        }
    }
}
