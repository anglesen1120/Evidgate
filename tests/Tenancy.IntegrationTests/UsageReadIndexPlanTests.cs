using Evidgate.Modules.Usage.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The organisation-wide usage summary is a keyset page over <c>public.project</c> plus one period of
/// <c>quota_counter</c> and <c>usage_rollup</c>, so its access paths are the three read-order indexes. The plan is
/// read from <see cref="UsageReadStoreSql.Summary"/>, the statement the store serves, with the store's own parameters
/// bound as the app role scoped to the org, so nothing here follows a copy of the query.
/// </summary>
[Trait("Category", "Docker")]
public sealed class UsageReadIndexPlanTests
{
    private const string Period = "2026-11";
    private const int PageLimit = 11;
    private const int PageOrgProjects = 5_000;
    private const int VolumeOrgProjects = 20_000;

    private readonly PostgresTenancyFixture _fixture;

    public UsageReadIndexPlanTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A database of its own, because the seed is a volume the planner reads as statistics: the page's org holds
    /// <see cref="PageOrgProjects"/> projects with a counter and a roll-up in <see cref="Period"/>, twelve closed
    /// periods behind them, and a second org of <see cref="VolumeOrgProjects"/> projects adds the period's other
    /// rows. The three tables are <c>ANALYZE</c>d, so no planner switch stands in for the indexes.
    /// </summary>
    [Fact]
    public async Task Summary_OrgWidePage_UsesThePeriodAndProjectIndexes()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var org = await SeedAsync(database);

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.quota_counter where period = '{Period}' and project_id is not null"))
            .ShouldBe((long)(PageOrgProjects + VolumeOrgProjects));

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", $"{org:D}");
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        var firstPage = await ExplainAsync(connection, transaction, org, cursor: null);
        var cursorPage = await ExplainAsync(
            connection,
            transaction,
            org,
            cursor: (new DateTimeOffset(2025, 1, 2, 12, 0, 0, TimeSpan.Zero), Guid.CreateVersion7()));

        var pages = new (string Label, string Json)[] { ("the first page", firstPage), ("the cursor page", cursorPage) };

        foreach (var (label, json) in pages)
        {
            var (_, indexNames, sequentialScans) = AuthoringTimelineTests.PlanNodes(json);

            indexNames.ShouldContain(
                "quota_counter_org_period_idx", $"{label} must read the period's counters through the period index");
            indexNames.ShouldContain(
                "usage_rollup_org_period_idx", $"{label} must read the period's roll-ups through the period index");
            sequentialScans.ShouldNotContain("quota_counter", $"{label} must not scan the counters");
            sequentialScans.ShouldNotContain("usage_rollup", $"{label} must not scan the roll-ups");
            sequentialScans.ShouldNotContain("project", $"{label} must not scan the projects");
        }

        var (_, cursorIndexNames, _) = AuthoringTimelineTests.PlanNodes(cursorPage);
        cursorIndexNames.ShouldContain(
            "project_org_created_id_idx", "the cursor page must walk the projects in keyset order through the org index");
    }

    /// <summary>
    /// The catalogue half: the three read-order indexes plus the run read's wider index exist on the tables their
    /// statements read, and the narrower <c>(org_id, run_id)</c> index the wider one covers is gone.
    /// </summary>
    [Fact]
    public async Task ReadIndexes_CreateTheFourIndexesAndDropTheRunPrefixIndex()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

        var indexes = await _fixture.ColumnAsync<string>(
            database,
            """
            select tablename || '.' || indexname
              from pg_indexes
             where schemaname = 'public'
            """);

        indexes.ShouldContain("quota_counter.quota_counter_org_period_idx");
        indexes.ShouldContain("usage_rollup.usage_rollup_org_period_idx");
        indexes.ShouldContain("project.project_org_created_id_idx");
        indexes.ShouldContain("release_run.release_run_run_created_idx");
        indexes.ShouldNotContain("release_run.release_run_run_idx");
    }

    /// <summary>Two orgs with their project volume, then the counters and roll-ups the summary reads, then statistics.</summary>
    private async Task<Guid> SeedAsync(string database)
    {
        var org = (await FixtureSeed.TenantAsync(_fixture, database)).Org;
        var volumeOrg = (await FixtureSeed.TenantAsync(_fixture, database)).Org;

        await SeedProjectsAsync(database, org, volumeOrg);
        await SeedUsageAsync(database, org, volumeOrg);

        return org;
    }

    /// <summary>Projects with a pinned creation instant, so the keyset's order over the page is a fact.</summary>
    private async Task SeedProjectsAsync(string database, Guid org, Guid volumeOrg)
    {
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.project (id, org_id, name, slug, created_at)
            select gen_random_uuid(), '{org:D}', 'p' || g, 'p-' || g,
                   timestamptz '2025-01-01 00:00:00+00' + g * interval '1 minute'
              from generate_series(1, {PageOrgProjects - 1}) g;
            insert into public.project (id, org_id, name, slug, created_at)
            select gen_random_uuid(), '{volumeOrg:D}', 'q' || g, 'q-' || g,
                   timestamptz '2025-01-01 00:00:00+00' + g * interval '1 minute'
              from generate_series(1, {VolumeOrgProjects - 1}) g;
            """);
    }

    /// <summary>
    /// One counter and one roll-up per project in the period, twelve closed periods behind the page's org, and the
    /// tenant-level rows of both orgs: a table whose period rows sit among many the statement must not read.
    /// Split into several commands so each stays well under the driver's command timeout on a loaded host.
    /// </summary>
    private async Task SeedUsageAsync(string database, Guid org, Guid volumeOrg)
    {
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.quota_counter (id, org_id, project_id, period, metric, used, quota_limit)
            select gen_random_uuid(), p.org_id, p.id, '{Period}', 'results', 3, 1000
              from public.project p;
            insert into public.usage_rollup
                (id, org_id, project_id, period, metric, total_quantity, row_count, computed_at)
            select gen_random_uuid(), p.org_id, p.id, '{Period}', 'results', 3, 1, now()
              from public.project p;
            """);
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.quota_counter (id, org_id, project_id, period, metric, used, quota_limit)
            select gen_random_uuid(), p.org_id, p.id,
                   to_char(date '2025-01-01' + (m || ' months')::interval, 'YYYY-MM'), 'results', 3, 1000
              from public.project p
             cross join generate_series(0, 11) m;
            """);
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.usage_rollup
                (id, org_id, project_id, period, metric, total_quantity, row_count, computed_at)
            select gen_random_uuid(), p.org_id, p.id,
                   to_char(date '2025-01-01' + (m || ' months')::interval, 'YYYY-MM'), 'results', 3, 1, now()
              from public.project p
             cross join generate_series(0, 11) m;
            """);
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.quota_counter (id, org_id, project_id, period, metric, used, quota_limit)
            select gen_random_uuid(), o.id, null, '{Period}', metrics.metric, 0, 100000
              from (values ('{org:D}'::uuid), ('{volumeOrg:D}'::uuid)) o (id)
             cross join (values ('results'), ('artifact_gb_month')) metrics (metric);
            insert into public.usage_rollup
                (id, org_id, project_id, period, metric, total_quantity, row_count, computed_at)
            select gen_random_uuid(), o.id, null, '{Period}', metrics.metric, 15000, 5000, now()
              from (values ('{org:D}'::uuid), ('{volumeOrg:D}'::uuid)) o (id)
             cross join (values ('results'), ('artifact_gb_month')) metrics (metric);
            analyze public.project;
            analyze public.quota_counter;
            analyze public.usage_rollup;
            """);
    }

    /// <summary>The production statement explained with the store's parameter set bound and the org's scope open.</summary>
    private static async Task<string> ExplainAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid org,
        (DateTimeOffset CreatedAt, Guid Id)? cursor)
    {
        await using var command = new NpgsqlCommand(
            "explain (format json) " + UsageReadStoreSql.Summary, connection, transaction);

        command.Parameters.AddWithValue("org_id", org);
        command.Parameters.AddWithValue("period", Period);
        command.Parameters.AddWithValue("project_limit", PageLimit);
        command.Parameters.Add(new NpgsqlParameter("project_id", NpgsqlDbType.Uuid) { Value = DBNull.Value });

        if (cursor is { } page)
        {
            command.Parameters.Add(new NpgsqlParameter("cursor_created_at", NpgsqlDbType.TimestampTz) { Value = page.CreatedAt });
            command.Parameters.Add(new NpgsqlParameter("cursor_id", NpgsqlDbType.Uuid) { Value = page.Id });
        }
        else
        {
            command.Parameters.Add(new NpgsqlParameter("cursor_created_at", NpgsqlDbType.TimestampTz) { Value = DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("cursor_id", NpgsqlDbType.Uuid) { Value = DBNull.Value });
        }

        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
