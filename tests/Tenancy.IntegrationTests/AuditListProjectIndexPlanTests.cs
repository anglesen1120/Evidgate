using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The audit page statement, with its real parameters, streams from an index and never sorts: a project-filtered
/// page from the partial project index (a sibling project and untagged history make the org-wide index a poor
/// choice), the unfiltered page from the org-wide one.
/// </summary>
[Trait("Category", "Docker")]
public sealed class AuditListProjectIndexPlanTests
{
    private const string ProjectIndex = "audit_log_org_project_created_id_idx";
    private const string OrgIndex = "audit_log_org_created_id_idx";

    private readonly PostgresTenancyFixture _fixture;

    public AuditListProjectIndexPlanTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AuditPage_ProjectFilter_UsesThePartialIndexWithoutASort_AndTheUnfilteredPageKeepsItsIndex()
    {
        // The case seeds 40 000 audit rows and ANALYZEs public.audit_log, so the statistics are database-wide.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database.Name));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("plan"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("plan-project"));
        await SeedAsync(database.Name, org, project);

        // created_at is seeded explicitly, so this position sits inside the project's range.
        var midRange = DateTimeOffset.Parse("2026-01-01T03:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database.Name));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        await AssertUsesIndexAsync(connection, transaction, org, project, null, ProjectIndex);
        await AssertUsesIndexAsync(connection, transaction, org, project, midRange, ProjectIndex);
        await AssertUsesIndexAsync(connection, transaction, org, null, null, OrgIndex);
        await AssertUsesIndexAsync(connection, transaction, org, null, midRange, OrgIndex);
    }

    /// <summary>
    /// 200 rows of the project among 39 000 of a sibling project and 800 untagged ones. The chain guard is disabled
    /// for the bulk insert only (it checks every row against the chain tip, which a set-based seed does not keep);
    /// the plan depends on the columns and the statistics, not on a valid chain.
    /// </summary>
    private async Task SeedAsync(string database, Guid org, Guid project) =>
        await _fixture.ExecuteAsync(
            database,
            $"""
            alter table public.audit_log disable trigger audit_log_chain_guard;
            insert into public.audit_log
              (id, org_id, seq, actor_type, actor_id, action, prev_hash, entry_hash, created_at, project_id)
            select gen_random_uuid(), '{org:D}', 1000 + g, 'user', 'seed', 'run.create',
                   sha256(convert_to('p' || g, 'UTF8')), sha256(convert_to('e' || g, 'UTF8')),
                   timestamptz '2026-01-01T00:00:00Z' + g * interval '1 second',
                   case when g % 200 = 0 then '{project:D}'::uuid
                        when g % 50 = 1 then null
                        else '{Guid.CreateVersion7():D}'::uuid end
              from generate_series(1, 40000) g;
            alter table public.audit_log enable trigger audit_log_chain_guard;
            analyze public.audit_log;
            """);

    /// <summary>Explains <see cref="AuditReadSql.Page"/> with its real parameters: the named index, no sort, no table scan.</summary>
    private static async Task AssertUsesIndexAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid org,
        Guid? project,
        DateTimeOffset? cursor,
        string expectedIndex)
    {
        await using var command = new NpgsqlCommand("explain (format json) " + AuditReadSql.Page, connection, transaction);
        command.Parameters.AddWithValue("org_id", org);
        command.Parameters.Add(new NpgsqlParameter("actor_type", NpgsqlDbType.Text) { Value = DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("action", NpgsqlDbType.Text) { Value = DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("object_type", NpgsqlDbType.Text) { Value = DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("project_id", NpgsqlDbType.Uuid) { Value = (object?)project ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("cursor_created_at", NpgsqlDbType.TimestampTz) { Value = (object?)cursor ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("cursor_id", NpgsqlDbType.Uuid) { Value = cursor is null ? DBNull.Value : Guid.CreateVersion7() });
        command.Parameters.AddWithValue("limit", 51);

        var plan = (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        var (nodeTypes, indexNames) = ListRunsIndexPlanTests.PlanNodes(plan);

        indexNames.ShouldContain(expectedIndex, plan);
        nodeTypes.ShouldNotContain("Sort", plan);
        nodeTypes.ShouldNotContain("Seq Scan", plan);
    }
}
