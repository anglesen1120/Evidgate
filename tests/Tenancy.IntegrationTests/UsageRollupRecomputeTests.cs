// The roll-up recompute against a real PostgreSQL: the set-based aggregate of the ledger into `usage_rollup`, the
// conditional update that keeps a closed period byte-identical across fires, the coalesce-project upsert that makes an
// org-level row singular, the gap flag that follows the ledger rows disappearing and returning, and two workers that
// reach one org at the same time.
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Infrastructure.Persistence;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The recompute of plan §7 (AC6/AC7): one aggregate per (project-or-org-level, period, metric), derived from the ledger
/// rows of the tenant, never appended twice, and never rewritten when nothing changed. The scope is the production
/// app-role factory, so row security and the column-scoped UPDATE grant of 0047 are part of what these tests prove.
/// </summary>
[Trait("Category", "Docker")]
public sealed class UsageRollupRecomputeTests
{
    private const string ClosedPeriod = "2026-01";
    private const string OpenPeriod = "2026-02";

    private readonly PostgresTenancyFixture _fixture;

    public UsageRollupRecomputeTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>One dedicated database, one org, one project, and the app-role scope factory over it.</summary>
    private sealed record World(
        string Database,
        Guid Org,
        Guid Project,
        NpgsqlDataSource App,
        NpgsqlTenantScopeFactory Scopes) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDedicatedDatabaseAsync();
        var name = database.Name;
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var slug = org.ToString("N")[..8];

        await _fixture.ExecuteAsync(
            name,
            $"""
            insert into public.organization (id, name, slug) values ('{org:D}', 'acme', 'acme-{slug}');
            insert into public.project (id, org_id, name, slug) values ('{project:D}', '{org:D}', 'pp', 'pp-{slug}');
            """);

        var app = NpgsqlDataSource.Create(_fixture.AppConnectionString(name));
        return new World(name, org, project, app, new NpgsqlTenantScopeFactory(app));
    }

    /// <summary>One ledger row; <paramref name="projectId"/> null writes the org-level scope.</summary>
    private readonly record struct LedgerRow(
        Guid? ProjectId,
        string Metric,
        string Period,
        decimal Quantity,
        DateTimeOffset OccurredAt,
        decimal? Amount = null,
        string? Currency = null);

    private static Task SeedAsync(World world, params LedgerRow[] rows) => SeedAsync(world, "seed", rows);

    /// <summary>Writes ledger rows through an app-role tenant scope, the way the metering path does.</summary>
    private static async Task SeedAsync(World world, string keyPrefix, params LedgerRow[] rows)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = await world.Scopes.BeginAsync(world.Org, null, false, token);

        foreach (var row in rows)
        {
            var billingKey = $"test:{keyPrefix}:{Guid.CreateVersion7():N}";
            await ExecuteAsync(
                scope,
                """
                insert into public.usage_ledger_entry
                    (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at, amount, currency)
                values (@id, @org_id, @project_id, @metric, @quantity, 'batch', @period, @billing_key, @occurred_at,
                        @amount, @currency)
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", world.Org),
                ("project_id", (object?)row.ProjectId ?? DBNull.Value),
                ("metric", row.Metric),
                ("quantity", row.Quantity),
                ("period", row.Period),
                ("billing_key", billingKey),
                ("occurred_at", row.OccurredAt.UtcDateTime),
                ("amount", (object?)row.Amount ?? DBNull.Value),
                ("currency", (object?)row.Currency ?? DBNull.Value));
        }

        await scope.CommitAsync(token);
    }

    /// <summary>Runs the recompute the way the task does: its own tenant scope, committed per org.</summary>
    private static async Task RecomputeAsync(World world)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = await world.Scopes.BeginAsync(world.Org, null, false, token);
        await UsageRollupStore.RecomputeAsync(scope, token);
        await scope.CommitAsync(token);
    }

    /// <summary>
    /// Every roll-up row of the world's org as <c>scope|period|metric|qty|amount|currency|rows|gap|computed_at|version</c>,
    /// read as the superuser so the assertion sees the tenant's whole set.
    /// </summary>
    private Task<IReadOnlyList<string>> RowsAsync(World world) =>
        _fixture.ColumnAsync<string>(
            world.Database,
            $"""
            select coalesce(project_id::text, 'org-level') || '|' || period || '|' || metric || '|'
                || total_quantity::text || '|' || coalesce(total_amount::text, 'null') || '|'
                || coalesce(currency, 'null') || '|' || row_count::text || '|' || gap_flag::text || '|'
                || computed_at::text || '|' || version::text
            from public.usage_rollup
            where org_id = '{world.Org:D}'
            order by period, coalesce(project_id::text, 'org-level'), metric
            """);

    // ------------------------------------------------------------------ AC6: the aggregate itself

    [Fact]
    public async Task Recompute_GroupsByProjectPeriodAndMetric_InOneRowEach()
    {
        await using var world = await StartAsync();
        var at = new DateTimeOffset(2026, 2, 10, 9, 0, 0, TimeSpan.Zero);

        await SeedAsync(
            world,
            new LedgerRow(world.Project, "results", OpenPeriod, 2m, at, 4m, "USD"),
            new LedgerRow(world.Project, "results", OpenPeriod, 3m, at, 6m, "USD"),
            new LedgerRow(world.Project, "ai_tokens", OpenPeriod, 100m, at),
            new LedgerRow(world.Project, "results", ClosedPeriod, 1m, at));

        await RecomputeAsync(world);

        var rows = await RowsAsync(world);
        rows.Count.ShouldBe(3, "one aggregate per (project, period, metric)");

        var open = rows.Single(row => row.StartsWith(world.Project.ToString("D"), StringComparison.Ordinal)
            && row.Contains($"|{OpenPeriod}|results|", StringComparison.Ordinal));
        open.ShouldContain("|results|5|10|USD|2|false|", Case.Sensitive);

        rows.ShouldContain(row => row.Contains($"|{OpenPeriod}|ai_tokens|100|null|null|1|false|", StringComparison.Ordinal));
        rows.ShouldContain(row => row.Contains($"|{ClosedPeriod}|results|1|null|null|1|false|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recompute_Twice_WritesIdenticalRowsIncludingComputedAt()
    {
        await using var world = await StartAsync();
        await SeedAsync(world, new LedgerRow(world.Project, "results", ClosedPeriod, 3m, DateTimeOffset.UtcNow));

        await RecomputeAsync(world);
        var first = await RowsAsync(world);

        await RecomputeAsync(world);
        var second = await RowsAsync(world);

        second.ShouldBe(first, "an unchanged aggregate keeps its computed_at and its version");
    }

    [Fact]
    public async Task Recompute_LateRowForAClosedPeriod_RecomputesThatPeriodOnly()
    {
        await using var world = await StartAsync();
        var at = new DateTimeOffset(2026, 2, 3, 8, 0, 0, TimeSpan.Zero);
        await SeedAsync(
            world,
            new LedgerRow(world.Project, "results", ClosedPeriod, 5m, at),
            new LedgerRow(world.Project, "results", OpenPeriod, 7m, at));

        await RecomputeAsync(world);
        var before = await RowsAsync(world);

        // The late row belongs to a month that already closed: the next fire recomputes it without a clock window.
        await SeedAsync(world, new LedgerRow(world.Project, "results", ClosedPeriod, 4m, at));
        await RecomputeAsync(world);
        var after = await RowsAsync(world);

        after.Single(row => row.Contains($"|{ClosedPeriod}|", StringComparison.Ordinal))
            .ShouldContain("|results|9|null|null|2|false|", Case.Sensitive);
        after.Single(row => row.Contains($"|{OpenPeriod}|", StringComparison.Ordinal))
            .ShouldBe(before.Single(row => row.Contains($"|{OpenPeriod}|", StringComparison.Ordinal)),
                "a period whose rows did not change is not rewritten by the fire");
    }

    [Fact]
    public async Task Recompute_OrgLevelScope_UpsertsToOneRowAcrossFires()
    {
        await using var world = await StartAsync();
        var at = new DateTimeOffset(2026, 2, 3, 8, 0, 0, TimeSpan.Zero);
        await SeedAsync(
            world,
            new LedgerRow(null, "runner_minutes", OpenPeriod, 2m, at),
            new LedgerRow(null, "runner_minutes", OpenPeriod, 3m, at));

        await RecomputeAsync(world);
        await RecomputeAsync(world);

        var rows = await RowsAsync(world);
        rows.Count.ShouldBe(1, "the coalesce-project arbiter keeps the org-level aggregate singular");
        rows[0].ShouldStartWith("org-level|", Case.Sensitive);
        rows[0].ShouldContain("|runner_minutes|5|null|null|2|false|", Case.Sensitive);
    }

    [Fact]
    public async Task Recompute_MixedCurrency_LabelsTheAggregateUnknown()
    {
        await using var world = await StartAsync();
        var at = new DateTimeOffset(2026, 2, 3, 8, 0, 0, TimeSpan.Zero);
        await SeedAsync(
            world,
            new LedgerRow(world.Project, "results", OpenPeriod, 1m, at, 2m, "USD"),
            new LedgerRow(world.Project, "results", OpenPeriod, 1m, at, 3m, "EUR"));

        await RecomputeAsync(world);

        var row = (await RowsAsync(world)).Single();
        row.ShouldContain("|results|2|null|null|2|false|", Case.Sensitive);
    }

    [Fact]
    public async Task Recompute_PartiallyPricedScope_LeavesAmountAndCurrencyUnknown()
    {
        await using var world = await StartAsync();
        var at = new DateTimeOffset(2026, 2, 3, 8, 0, 0, TimeSpan.Zero);
        await SeedAsync(
            world,
            new LedgerRow(world.Project, "results", OpenPeriod, 2m, at, 4m, "USD"),
            new LedgerRow(world.Project, "results", OpenPeriod, 3m, at));

        await RecomputeAsync(world);

        var row = (await RowsAsync(world)).Single();
        row.ShouldContain(
            "|results|5|null|null|2|false|",
            Case.Sensitive,
            "a partial sum would understate the scope without saying so");
    }

    // ------------------------------------------------------------------ AC7: the gap flag

    [Fact]
    public async Task Recompute_ScopeWhoseLedgerRowsDisappear_IsFlagged_AndClearedWhenTheyReturn()
    {
        await using var world = await StartAsync();
        var at = new DateTimeOffset(2026, 2, 3, 8, 0, 0, TimeSpan.Zero);
        await SeedAsync(world, new LedgerRow(world.Project, "results", ClosedPeriod, 5m, at));

        await RecomputeAsync(world);
        (await RowsAsync(world)).Single().ShouldContain("|false|", Case.Sensitive);

        // The rows of a stored aggregate vanish (an erasure, a corrected import): the recompute cannot insert such a
        // row, so the flag is what keeps the divergence visible.
        await _fixture.ExecuteAsync(
            world.Database,
            $"delete from public.usage_ledger_entry where org_id = '{world.Org:D}'");
        await RecomputeAsync(world);

        (await RowsAsync(world)).Single().ShouldContain("|true|", Case.Sensitive);

        // A repeated fire changes nothing: the flag is set only while it is not already set.
        await RecomputeAsync(world);
        (await RowsAsync(world)).Single().ShouldContain("|true|", Case.Sensitive);

        await SeedAsync(world, new LedgerRow(world.Project, "results", ClosedPeriod, 5m, at));
        await RecomputeAsync(world);

        (await RowsAsync(world)).Single().ShouldContain("|false|", Case.Sensitive);
    }

    [Fact]
    public async Task Recompute_OneFlagDoesNotTouchAnotherScope()
    {
        await using var world = await StartAsync();
        var at = new DateTimeOffset(2026, 2, 3, 8, 0, 0, TimeSpan.Zero);
        await SeedAsync(
            world,
            new LedgerRow(world.Project, "results", OpenPeriod, 5m, at),
            new LedgerRow(world.Project, "ai_tokens", OpenPeriod, 50m, at));

        await RecomputeAsync(world);
        await _fixture.ExecuteAsync(
            world.Database,
            $"delete from public.usage_ledger_entry where org_id = '{world.Org:D}' and metric = 'ai_tokens'");
        await RecomputeAsync(world);

        var rows = await RowsAsync(world);
        rows.Single(row => row.Contains("|ai_tokens|", StringComparison.Ordinal)).ShouldContain("|true|", Case.Sensitive);
        rows.Single(row => row.Contains("|results|", StringComparison.Ordinal)).ShouldContain("|false|", Case.Sensitive);
    }

    // ------------------------------------------------------------------ AC6: two workers, one org

    [Fact]
    public async Task Recompute_TwoWorkersOnOneOrg_EndWithOneRowAndNoError()
    {
        await using var world = await StartAsync();
        await SeedAsync(world, new LedgerRow(world.Project, "results", OpenPeriod, 5m, DateTimeOffset.UtcNow));

        // The advisory lock is what serialises them; the upsert then sees one aggregate either way.
        await Task.WhenAll(RecomputeAsync(world), RecomputeAsync(world));
        await RecomputeAsync(world);

        var rows = await RowsAsync(world);
        rows.Count.ShouldBe(1);
        rows[0].ShouldContain("|results|5|null|null|1|false|", Case.Sensitive);
    }

    [Fact]
    public async Task Recompute_OtherOrgRowsAreInvisible()
    {
        await using var world = await StartAsync();
        var otherOrg = Guid.CreateVersion7();
        var otherProject = Guid.CreateVersion7();
        var slug = otherOrg.ToString("N")[..8];
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.organization (id, name, slug) values ('{otherOrg:D}', 'other', 'other-{slug}');
            insert into public.project (id, org_id, name, slug) values ('{otherProject:D}', '{otherOrg:D}', 'op', 'op-{slug}');
            insert into public.usage_ledger_entry
                (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at)
            values ('{Guid.CreateVersion7():D}', '{otherOrg:D}', '{otherProject:D}', 'results', 9, 'batch',
                    '{OpenPeriod}', 'other:{Guid.CreateVersion7():N}', now());
            """);

        await SeedAsync(world, new LedgerRow(world.Project, "results", OpenPeriod, 1m, DateTimeOffset.UtcNow));
        await RecomputeAsync(world);

        var rows = await RowsAsync(world);
        rows.Count.ShouldBe(1, "the recompute only derives the scope's own tenant");
        rows[0].ShouldContain("|results|1|null|null|1|false|", Case.Sensitive);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.usage_rollup where org_id = '{otherOrg:D}'"))
            .ShouldBe(0, "another org's roll-up is not this scope's to write");
    }

    private static async Task ExecuteAsync(ITenantScope scope, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, scope.Connection, scope.Transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
