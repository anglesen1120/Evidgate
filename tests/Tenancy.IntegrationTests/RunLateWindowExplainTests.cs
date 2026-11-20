// The run-event scope read explained before and after the completion-anchored window: the statement is taken from
// the production const, the old predicate is embedded as a witness, and the two plans must agree node for node.
using System.Text.Json;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <see cref="RunEventScopeReader.ReadScope"/> as PostgreSQL plans it for a completed run: the shared late-window
/// predicate must not cost the read its index path or change the shape of the plan, so the statement with the old
/// collection-deadline-only predicate is explained beside it and the two plans are compared.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RunLateWindowExplainTests : IClassFixture<TenancyHostPool>
{
    // Before/after witness: the expression the statement carried before the completion-anchored window, kept
    // verbatim so the comparison runs the real old text rather than a paraphrase of it.
    private const string OldLateWindowOpenSql =
        "now() <= r.collection_deadline_at + make_interval(mins => r.late_window_minutes)";

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;

    public RunLateWindowExplainTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) =>
        (_fixture, _hosts) = (fixture, hosts);

    [Fact]
    public async Task ReadScope_Plan_BeforeAndAfter_NoSeqScanNoExtraNode()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), hosts: _hosts);
        await world.SetRunStateAsync("complete");
        (await world.ScalarAsync<bool>(
            $"select completed_at is not null from public.run where id = '{world.RunId:D}'"))
            .ShouldBeTrue("the window is measured from a real completion instant, not a seeded one");

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        // The shared-database seed is too small for the planner to prefer the indexes on its own, so this asks the
        // plan for a path that does not depend on table size: a sequential scan here would mean no index fits.
        await using (var settings = new NpgsqlCommand("set local enable_seqscan = off", connection, transaction))
        {
            await settings.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var afterSql = RunEventScopeReader.ReadScope;
        afterSql.ShouldContain(RunAdmissionPolicy.LateWindowOpenSql, Case.Sensitive);
        var beforeSql = afterSql.Replace(RunAdmissionPolicy.LateWindowOpenSql, OldLateWindowOpenSql, StringComparison.Ordinal);
        beforeSql.ShouldNotBe(afterSql, "the old predicate is the only difference between the two statements");

        var beforeJson = await ExplainAsync(connection, transaction, world, beforeSql);
        var afterJson = await ExplainAsync(connection, transaction, world, afterSql);
        Print("BEFORE\n" + Pretty(beforeJson) + "\nAFTER\n" + Pretty(afterJson));

        using var before = JsonDocument.Parse(beforeJson);
        using var after = JsonDocument.Parse(afterJson);
        var beforeNodes = PlanNodes(before.RootElement[0].GetProperty("Plan")).ToList();
        var afterNodes = PlanNodes(after.RootElement[0].GetProperty("Plan")).ToList();
        beforeNodes.ShouldNotBeEmpty("the old statement has a plan to compare");
        afterNodes.ShouldNotBeEmpty("the new statement has a plan to compare");
        beforeNodes.Any(ScanOfRun).ShouldBeFalse("the old predicate must keep the run reachable by index");
        afterNodes.Any(ScanOfRun).ShouldBeFalse("the completion-anchored predicate must keep the run reachable by index");

        // Same multiset of node types: the new predicate must not add or remove a plan node of its own.
        afterNodes.Select(NodeType).Order(StringComparer.Ordinal)
            .ShouldBe(beforeNodes.Select(NodeType).Order(StringComparer.Ordinal));
    }

    /// <summary>Explains one statement with the read's own parameters bound, as the app role in the tenant scope.</summary>
    private static async Task<string> ExplainAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestTestWorld world,
        string sql)
    {
        await using var command = new NpgsqlCommand("explain (format json) " + sql, connection, transaction);
        command.Parameters.AddWithValue("org_id", world.Org);
        command.Parameters.AddWithValue("run_id", world.RunId);
        command.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = world.Plan.Executions.Select(execution => execution.ExecutionKey).ToArray(),
        });

        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>One <c>EXPLAIN (FORMAT JSON)</c> document, indented for the witness file.</summary>
    private static string Pretty(string json)
    {
        using var document = JsonDocument.Parse(json);

        return JsonSerializer.Serialize(document.RootElement, PrettyJson);
    }

    /// <summary>One desired plan node and every node below it, however the plan nests.</summary>
    private static IEnumerable<JsonElement> PlanNodes(JsonElement node)
    {
        yield return node;
        if (!node.TryGetProperty("Plans", out var children))
        {
            yield break;
        }

        foreach (var child in children.EnumerateArray())
        {
            foreach (var descendant in PlanNodes(child))
            {
                yield return descendant;
            }
        }
    }

    private static string NodeType(JsonElement node) => node.GetProperty("Node Type").GetString()!;

    private static bool ScanOfRun(JsonElement node) =>
        NodeType(node) == "Seq Scan"
        && node.TryGetProperty("Relation Name", out var relation)
        && relation.GetString() == "run";

    // A passing test's output never reaches the runner log, so the plans also go to a file the LAN gate copies back.
    private static void Print(string text)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(text);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "scripts", "verify.sh")))
        {
            root = root.Parent;
        }

        if (root is not null)
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, "artifacts", "test-results"));
            File.WriteAllText(Path.Combine(directory.FullName, "s488-readscope-explain.txt"), text + "\n");
        }
    }
}
