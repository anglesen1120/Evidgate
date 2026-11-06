using System.Globalization;
using Evidgate.Modules.Usage.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The quota counter's access path: both gate statements are explained from their production text
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

        // Both shapes of the tenant-level limit (null project) and the project-scoped one, for the two
        // statements the gate serves: the reservation and the refusal.
        var tenantLevel = await ExplainAsync(
            connection, transaction, UsageQuotaGateSql.Reserve, org, projectId: null, "2026-10", "results", quantity: 1m);
        var projectScoped = await ExplainAsync(
            connection, transaction, UsageQuotaGateSql.Reserve, org, project, "2026-10", "results", quantity: 1m);
        var refusal = await ExplainAsync(
            connection, transaction, UsageQuotaGateSql.StampRefusal, org, projectId: null, "2026-10", "results", quantity: 1m);

        var plans = new List<(string Label, (List<string> NodeTypes, List<string> IndexNames, List<string> SequentialScanRelations) Nodes)>
        {
            ("the tenant-level reservation", tenantLevel),
            ("the project-scoped reservation", projectScoped),
            ("the refusal stamp", refusal),
        };

        foreach (var (label, plan) in plans)
        {
            plan.IndexNames.ShouldNotBeEmpty($"{label} must read through an index, not a scan");
            plan.IndexNames.ShouldContain("quota_counter_scope_key", $"{label} must use the scope index");
            plan.SequentialScanRelations.ShouldNotContain(Table, $"{label} must not scan the counter");
        }
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

    /// <summary>Explains one gate statement with its real parameters bound and returns its plan nodes.</summary>
    private static async Task<(List<string> NodeTypes, List<string> IndexNames, List<string> SequentialScanRelations)> ExplainAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid org,
        Guid? projectId,
        string period,
        string metric,
        decimal? quantity)
    {
        await using var command = new NpgsqlCommand("explain (format json) " + sql, connection, transaction);

        command.Parameters.AddWithValue("org_id", org);

        // An explicit type, so a null project never leaves PostgreSQL guessing at the cast.
        command.Parameters.Add(new NpgsqlParameter("project_id", NpgsqlDbType.Uuid)
        {
            Value = projectId is { } project ? project : DBNull.Value,
        });
        command.Parameters.AddWithValue("period", period);
        command.Parameters.AddWithValue("metric", metric);

        if (quantity is { } value)
        {
            command.Parameters.AddWithValue("quantity", value);
        }

        var plan = (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        return AuthoringTimelineTests.PlanNodes(plan);
    }
}
