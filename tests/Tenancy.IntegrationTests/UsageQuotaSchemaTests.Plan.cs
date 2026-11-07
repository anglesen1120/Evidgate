using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Evidgate.Modules.Usage.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The quota counter's access path: the gate statements are explained from their production text
/// (<see cref="UsageQuotaGateSql"/>) with their real parameters bound, so the assertion follows the served SQL
/// and not a copy of it.
/// </summary>
public sealed partial class UsageQuotaSchemaTests
{
    [Fact]
    public async Task GateStatements_ReadTheScopeIndex()
    {
        // Dedicated: the seed is a volume for the planner, so it owns the table whole.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        await SeedCounterVolumeAsync(database, org, project);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", org.ToString("D", CultureInfo.InvariantCulture));
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        // Both shapes of the tenant-level limit (null project, the all-zero scope key) and the project-scoped one,
        // for the statements the gate serves: the reservation, the settle and the durable stamp. The last one is the
        // shape a caller that leaves the month open sends: one statement resolves the month and answers it.
        var tenantLevel = await ExplainAsync(
            connection, transaction, UsageQuotaGateSql.Reserve, org, [Guid.Empty], "2026-10", "results", quantity: 1m);
        var projectScoped = await ExplainAsync(
            connection, transaction, UsageQuotaGateSql.Reserve, org, [Guid.Empty, project], "2026-10", "results",
            quantity: 1m);
        var settled = await ExplainAsync(
            connection, transaction, UsageQuotaGateSql.Settle, org, [Guid.Empty], "2026-10", "results", quantity: 1m);
        var stamped = await ExplainAsync(
            connection, transaction, UsageQuotaGateSql.StampBlocked, org, [Guid.Empty, project], "2026-10", "results",
            quantity: 1m);
        var openMonthReservation = await ExplainAsync(
            connection, transaction, UsageQuotaGateSql.Reserve, org, [Guid.Empty, project], null, "results",
            quantity: 1m);

        var plans = new List<(string Label, PlanView Plan)>
        {
            ("the tenant-level reservation", tenantLevel),
            ("the project-scoped reservation", projectScoped),
            ("the settle statement", settled),
            ("the durable refusal stamp", stamped),
            ("the reservation of a caller that names no month", openMonthReservation),
        };

        foreach (var (label, plan) in plans)
        {
            plan.IndexNames.ShouldNotBeEmpty($"{label} must read through an index, not a scan");
            plan.IndexNames.ShouldContain("quota_counter_scope_key", $"{label} must use the scope index");
            plan.SequentialScanRelations.ShouldNotContain(Table, $"{label} must not scan the counter");
        }
    }

    /// <summary>
    /// The advisory read is the gate's other hot path, so its plan is pinned the same way, and the index condition must
    /// carry the tenant equality the statement itself sends, as a bound parameter rather than the row-level policy's
    /// own call: <c>evidgate.current_org_id()</c> names the tenant too, so finding <c>org_id</c> in a plan proves
    /// nothing about the statement. Another tenant holds full counters of the same month, metric and scopes; were the
    /// statement's predicate gone, only the policy would narrow the read, and the last explain below shows its plan
    /// carries no bound equality left, which is what makes the positive assertion fire.
    /// </summary>
    [Fact]
    public async Task PeekStatement_ReadsTheScopeIndexPinnedToTheTenant()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var (foreignOrg, foreignProject) = await SeedTenantAsync(database, "beta", "store");

        await SeedCounterVolumeAsync(database, org, project);
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.quota_counter (id, org_id, project_id, period, metric, used, quota_limit)
            values (gen_random_uuid(), '{foreignOrg:D}', null, '2026-10', 'results', 100, 100),
                   (gen_random_uuid(), '{foreignOrg:D}', '{foreignProject:D}', '2026-10', 'results', 100, 100);
            analyze public.quota_counter;
            """);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await PinTenantAsync(connection, transaction, org);

        await AssertScopedPlansAsync(connection, transaction, org, project);
        await AssertNeutralisedPlanAsync(connection, transaction, org, project);
    }

    /// <summary>The tenant the plan must see, published for the transaction's lifetime: the policy reads it there.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The transaction whose policy input it is.</param>
    /// <param name="org">The tenant every peek is asked about.</param>
    private static async Task PinTenantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid org)
    {
        await using var set = new NpgsqlCommand(
            "select set_config('evidgate.org_id', @org, true)", connection, transaction);
        set.Parameters.AddWithValue("org", org.ToString("D", CultureInfo.InvariantCulture));
        await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The three shapes of the peek — the tenant scope alone, the project beside it, and a caller that names no month —
    /// each read through the scope index and pinned to the tenant by the statement's own predicate.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The transaction the statements run in.</param>
    /// <param name="org">The tenant every peek is asked about.</param>
    /// <param name="project">The project the scoped shapes name.</param>
    private static async Task AssertScopedPlansAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid org,
        Guid project)
    {
        var plans = new List<(string Label, PlanView Plan)>
        {
            ("the tenant-level peek", await ExplainAsync(
                connection, transaction, UsageQuotaGateSql.Peek, org, [Guid.Empty], "2026-10", "results", quantity: 1m)),
            ("the project-scoped peek", await ExplainAsync(
                connection, transaction, UsageQuotaGateSql.Peek, org, [Guid.Empty, project], "2026-10", "results",
                quantity: 1m)),
            ("the peek of a caller that names no month", await ExplainAsync(
                connection, transaction, UsageQuotaGateSql.Peek, org, [Guid.Empty, project], null, "results",
                quantity: 1m)),
        };

        foreach (var (label, plan) in plans)
        {
            plan.IndexNames.ShouldContain("quota_counter_scope_key", $"{label} must use the scope index");
            plan.SequentialScanRelations.ShouldNotContain(Table, $"{label} must not scan the counter");

            IndexConditions(plan.Json)
                .Where(index => index.Index == "quota_counter_scope_key")
                .Select(index => index.Condition)
                .ShouldContain(
                    condition => BoundTenantEquality.IsMatch(condition),
                    $"{label} must pin the tenant with the uuid its own predicate names in the index condition, not "
                    + "in a filter applied after the read, and not by the policy alone");
        }
    }

    /// <summary>
    /// The control: the same statement with its tenant predicate neutralised, so only the policy names the tenant. Its
    /// plan must not carry the bound equality any more, which is what the assertion above would have seen.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The transaction the statements run in.</param>
    /// <param name="org">The tenant every peek is asked about.</param>
    /// <param name="project">The project the scoped shape names.</param>
    private static async Task AssertNeutralisedPlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid org,
        Guid project)
    {
        const string TenantPredicate = "where c.org_id = @org_id";
        var neutralised = UsageQuotaGateSql.Peek.Replace(
            TenantPredicate, "where @org_id::uuid is not null", StringComparison.Ordinal);
        neutralised.ShouldNotBe(
            UsageQuotaGateSql.Peek, "the control must actually lose the tenant predicate, not silently keep it");
        var withoutTenant = await ExplainAsync(
            connection, transaction, neutralised, org, [Guid.Empty, project], null, "results", quantity: 1m);

        IndexConditions(withoutTenant.Json)
            .Where(index => index.Index == "quota_counter_scope_key")
            .Select(index => index.Condition)
            .ShouldNotContain(
                condition => BoundTenantEquality.IsMatch(condition),
                "without the statement's own predicate only the policy names the tenant, and the policy prints a "
                + "call rather than a literal, so the fingerprint above can only come from the statement");
    }

    /// <summary>
    /// Fills the table with the counters of many periods and every metric for one org, then ANALYZEs it: the
    /// planner prefers the scope index on statistics like that without any planner switch, which is the point.
    /// <para>
    /// The periods stay inside the counter's own year bound (<c>2000-01</c>..<c>2999-12</c>): a volume that
    /// starts earlier is refused by <c>quota_counter_period_check</c>, and the seed must be rows the table
    /// accepts.
    /// </para>
    /// </summary>
    private async Task SeedCounterVolumeAsync(string database, Guid org, Guid project)
    {
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.quota_counter (id, org_id, project_id, period, metric, used, quota_limit)
            select gen_random_uuid(), '{org:D}', null, months.period, metrics.metric, 0, 1000
              from (
                select to_char(make_date(2000 + (n / 12)::int, 1 + (n % 12)::int, 1), 'YYYY-MM') as period
                  from generate_series(0, 1199) as n
              ) as months
              cross join (values ('results'), ('artifact_gb_month'), ('egress_gb'), ('ai_tokens'),
                                 ('support_hours'), ('runner_minutes'), ('device_minutes')) as metrics (metric);
            insert into public.quota_counter (id, org_id, project_id, period, metric, used, quota_limit)
            values (gen_random_uuid(), '{org:D}', '{project:D}', '2026-10', 'results', 40, 100);
            analyze public.quota_counter;
            """);
    }

    /// <summary>
    /// Explains one gate statement with its real parameters bound and returns its plan nodes. A null
    /// <paramref name="period"/> is the shape a caller that leaves the month open sends: a typed null, which the
    /// statement resolves from its own transaction clock.
    /// </summary>
    private static async Task<PlanView> ExplainAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid org,
        Guid[] scopeKeys,
        string? period,
        string metric,
        decimal? quantity)
    {
        await using var command = new NpgsqlCommand("explain (format json) " + sql, connection, transaction);

        command.Parameters.AddWithValue("org_id", org);

        // The scopes are one array: the tenant's all-zero key, then a project's, in the order the statement locks them.
        command.Parameters.Add(new NpgsqlParameter("scope_keys", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            Value = scopeKeys,
        });
        command.Parameters.Add(new NpgsqlParameter("period", NpgsqlDbType.Text)
        {
            Value = (object?)period ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("metric", metric);

        if (quantity is { } value)
        {
            command.Parameters.AddWithValue("quantity", value);
        }

        var plan = (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        var (nodeTypes, indexNames, sequentialScanRelations) = AuthoringTimelineTests.PlanNodes(plan);

        return new PlanView(nodeTypes, indexNames, sequentialScanRelations, plan);
    }

    /// <summary>One explained statement: the node types, the indexes it read, the relations it scanned, and the plan.</summary>
    /// <param name="NodeTypes">Every node type in the plan, however it nests.</param>
    /// <param name="IndexNames">The indexes it read.</param>
    /// <param name="SequentialScanRelations">The relations it scanned sequentially.</param>
    /// <param name="Json">The whole <c>EXPLAIN (FORMAT JSON)</c> document, for assertions on the conditions themselves.</param>
    private sealed record PlanView(
        List<string> NodeTypes,
        List<string> IndexNames,
        List<string> SequentialScanRelations,
        string Json);

    /// <summary>
    /// The tenant equality the explained statement itself sends: the uuid literal the plan macro-expands the parameter
    /// into (or the <c>$n</c> placeholder a prepared statement keeps). The policy's
    /// <c>org_id = evidgate.current_org_id()</c> prints a call and never a value, which is what tells the two apart.
    /// </summary>
    private static readonly Regex BoundTenantEquality = new(
        @"org_id\s*=\s*(?:'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'|\$\d+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Every index the plan uses beside the condition it applies, however the plan nests: the column that pins a
    /// statement's scope must be in the index condition itself, not in a filter applied to the rows it fetched.
    /// </summary>
    private static List<(string Index, string Condition)> IndexConditions(string json)
    {
        var conditions = new List<(string Index, string Condition)>();
        var queue = new Queue<JsonElement>();

        using var document = JsonDocument.Parse(json);

        foreach (var root in document.RootElement.EnumerateArray())
        {
            queue.Enqueue(root.GetProperty("Plan"));
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();

            if (node.TryGetProperty("Index Name", out var index)
                && node.TryGetProperty("Index Cond", out var condition))
            {
                conditions.Add((index.GetString()!, condition.GetString()!));
            }

            if (node.TryGetProperty("Plans", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    queue.Enqueue(child);
                }
            }
        }

        return conditions;
    }
}
