// The access paths of the release decision reads as PostgreSQL plans them, explained from the production statements with
// their real parameters on a database whose volume makes the tenant's rows a small share of each table.
using System.Text.Json;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Evidgate.Modules.Policy.Infrastructure.Persistence;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>Plan pins of the decision read, the verdict read and the binding read behind the aggregation.</summary>
[Trait("Category", "Docker")]
public sealed class ReleaseDecisionIndexPlanTests
{
    private const string Empty = "{}";

    private readonly PostgresTenancyFixture _fixture;

    public ReleaseDecisionIndexPlanTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task DecisionRead_UsesTheReleaseCreatedIndexWithoutSortOrSeqScan()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, database: database.Name);
        var release = await SeedVolumeAsync(database.Name, world);

        var plan = await ExplainAsync(database.Name, world, ReleaseDecisionSql.Latest, command =>
        {
            command.Parameters.AddWithValue("org_id", world.Org);
            command.Parameters.AddWithValue("release_id", release);
        });

        plan.Indexes.ShouldContain("release_decision_release_created_idx");
        plan.NodeTypes.ShouldNotContain(type => type.Contains("Sort", StringComparison.Ordinal), "the index order is the read order");
        plan.SeqScans.ShouldNotContain("release_decision");
    }

    [Fact]
    public async Task VerdictRead_ProbesTheEvaluationIndexPerRunWithoutSeqScan()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, database: database.Name);
        await SeedVolumeAsync(database.Name, world);

        var plan = await ExplainAsync(database.Name, world, ReleaseDecisionSql.LiveVerdicts, command =>
        {
            command.Parameters.AddWithValue("org_id", world.Org);
            command.Parameters.AddWithValue("ids", new[] { Guid.CreateVersion7(), Guid.CreateVersion7() });
        });

        plan.Indexes.ShouldContain(name => name.StartsWith("evaluation_", StringComparison.Ordinal));
        plan.SeqScans.ShouldNotContain("evaluation");
    }

    [Fact]
    public async Task BindingRead_UsesTheReleaseCreatedIndexWithoutSortOrSeqScan()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, database: database.Name);
        var release = await SeedVolumeAsync(database.Name, world);

        var plan = await ExplainAsync(database.Name, world, ReleaseAggregationSql.Bindings, command =>
        {
            command.Parameters.AddWithValue("org_id", world.Org);
            command.Parameters.AddWithValue("release_id", release);
            command.Parameters.AddWithValue("limit", 2001);
        });

        plan.Indexes.ShouldContain("release_run_release_created_idx");
        plan.NodeTypes.ShouldNotContain(type => type.Contains("Sort", StringComparison.Ordinal), "the index order is the read order");
        plan.SeqScans.ShouldNotContain("release_run");
    }

    /// <summary>
    /// Bulk rows beside the explained release: triggers and foreign keys are off for the load because the plan only
    /// depends on the row counts and statistics, and the tenant's own rows must be a small share of each table.
    /// </summary>
    private async Task<Guid> SeedVolumeAsync(string database, RunCreationTests.World world)
    {
        var release = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"""
            set session_replication_role = replica;
            insert into public.release_decision
              (id, org_id, release_id, policy_revision_id, component_evaluations, completeness, decision, reasons, inputs_hash, created_at)
            select gen_random_uuid(), '{world.Org}', case when g <= 3 then '{release}'::uuid else gen_random_uuid() end,
                   gen_random_uuid(), '[]'::jsonb, 'incomplete', 'INCONCLUSIVE', '{Empty}',
                   'sha256:' || lpad(to_hex(g), 64, '0'), now() - make_interval(secs => g)
              from generate_series(1, 30000) g;
            insert into public.run (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
                                    collection_deadline_at, late_window_minutes, ref_kind, ref_name)
            select gen_random_uuid(), '{world.Org}', '{world.Project}', '{world.ManifestId}', '{world.SuiteRevision}',
                   '{world.EnvironmentRevision}', now() + interval '1 hour' + (g * interval '1 second'), 1440, 'branch',
                   'bulk-run-' || g
              from generate_series(1, 30000) g;
            insert into public.release_run (id, org_id, release_id, run_id, component_key)
            select gen_random_uuid(), '{world.Org}', case when n.rn <= 3 then '{release}'::uuid else gen_random_uuid() end,
                   n.id, 'web'
              from (select id, row_number() over () as rn from public.run where org_id = '{world.Org}') n;
            insert into public.evaluation
              (id, org_id, run_id, evidence_revision, policy_revision_id, policy_hash, manifest_hash, completeness,
               missing, outcome, decision, reasons, evaluator_version, mode, exceptions_hash, exceptions_as_of)
            select gen_random_uuid(), '{world.Org}', gen_random_uuid(), 1, gen_random_uuid(),
                   'sha256:' || repeat('b', 64), 'sha256:' || repeat('e', 64), 'complete', '{Empty}', 'all_passed', 'PASS',
                   '{Empty}', '1.0.0', 'live', 'sha256:' || repeat('f', 64), now()
              from generate_series(1, 30000) g;
            analyze public.release_decision; analyze public.run; analyze public.release_run; analyze public.evaluation;
            """);

        return release;
    }

    private async Task<Plan> ExplainAsync(string database, RunCreationTests.World world, string sql, Action<NpgsqlCommand> bind)
    {
        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        await using var command = new NpgsqlCommand("explain (format json) " + sql, connection, transaction);
        bind(command);
        var json = (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        using var document = JsonDocument.Parse(json);
        var plan = new Plan();
        Walk(document.RootElement[0].GetProperty("Plan"), plan);

        return plan;
    }

    private static void Walk(JsonElement node, Plan plan)
    {
        var type = node.GetProperty("Node Type").GetString()!;
        plan.NodeTypes.Add(type);
        if (node.TryGetProperty("Index Name", out var index))
        {
            plan.Indexes.Add(index.GetString()!);
        }

        if (type == "Seq Scan" && node.TryGetProperty("Relation Name", out var relation))
        {
            plan.SeqScans.Add(relation.GetString()!);
        }

        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                Walk(child, plan);
            }
        }
    }

    private sealed class Plan
    {
        internal List<string> NodeTypes { get; } = [];

        internal List<string> Indexes { get; } = [];

        internal List<string> SeqScans { get; } = [];
    }
}
