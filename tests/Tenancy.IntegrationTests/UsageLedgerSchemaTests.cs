using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 12.3.1-T1.1/T1.2 against a real PostgreSQL: migration 0046's append-only <c>usage_ledger_entry</c>
/// and migration 0047's <c>usage_rollup</c> — the tenant keys, the dedupe unique key (AC1, T43), the
/// composite adjustment self-reference and the reason CHECK (AC2), the app role's missing UPDATE and
/// DELETE (AC3, C6), the plan §4 closed-list CHECKs (metric, period, billing_key) plus the additive
/// quantity and unit bounds the plan does not state, the roll-up's both uniques and
/// its coalesce-index upsert target (AC6, NFR-12-1), FORCE RLS with the tenant policy (T2, T3) and the
/// deploy path onto a database that already holds data.
/// <para>
/// The fixture is this suite's own <see cref="PostgresTenancyFixture"/> — one digest-pinned PostgreSQL
/// 18 container per assembly and one database for the run (tests isolate by tenant), with a private
/// database where a case applies migrations or truncates. Every write runs as <c>evidgate_app</c>
/// inside a tenant-scoped transaction (00 §5.5 C3) unless a case deliberately speaks as the owner (the
/// migration/backfill role) or as the operator.
/// </para>
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class UsageLedgerSchemaTests
{
    /// <summary>The first usage migration; the forward-apply case stops just before it.</summary>
    private const string LedgerMigrationFile = "0046_usage_ledger.sql";

    /// <summary>The roll-up migration, applied by the same forward step.</summary>
    private const string RollupMigrationFile = "0047_usage_rollup.sql";

    /// <summary>The two tables this story's schema owns, in dependency order.</summary>
    private static readonly string[] UsageTables = ["usage_ledger_entry", "usage_rollup"];

    /// <summary>The sentinel the roll-up's unique index substitutes for a NULL <c>project_id</c>.</summary>
    private const string NullProjectSentinel = "00000000-0000-0000-0000-000000000000";

    private readonly PostgresTenancyFixture _fixture;

    public UsageLedgerSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ helpers

    private const string LedgerColumns =
        "(id, org_id, metric, quantity, unit, period, billing_key, occurred_at";

    /// <summary>
    /// One ledger INSERT as the production batch statement shape. Optional columns are appended only
    /// when set, so a NULL never needs a typed parameter (00 §5.7 S1: values stay parameterised).
    /// </summary>
    private static (string Sql, (string Name, object Value)[] Parameters) LedgerInsert(
        Guid id,
        Guid org,
        Guid? project,
        string metric = "results",
        decimal quantity = 1m,
        string unit = "count",
        string period = "2026-10",
        string billingKey = "batch:1:results",
        Guid? adjustmentOf = null,
        string? reason = null,
        Guid? priceRef = null,
        decimal? amount = null,
        string? currency = null)
    {
        var columns = LedgerColumns + (project is null ? string.Empty : ", project_id");
        var values = "values (@id, @org_id, @metric, @quantity, @unit, @period, @billing_key, @occurred_at"
            + (project is null ? string.Empty : ", @project_id");
        var parameters = new List<(string Name, object Value)>
        {
            ("id", id),
            ("org_id", org),
            ("metric", metric),
            ("quantity", quantity),
            ("unit", unit),
            ("period", period),
            ("billing_key", billingKey),
            ("occurred_at", DateTimeOffset.UtcNow),
        };

        if (project is { } projectId)
        {
            parameters.Add(("project_id", projectId));
        }

        if (priceRef is { } price)
        {
            columns += ", price_ref";
            values += ", @price_ref";
            parameters.Add(("price_ref", price));
        }

        if (amount is { } amountValue)
        {
            columns += ", amount";
            values += ", @amount";
            parameters.Add(("amount", amountValue));
        }

        if (currency is { } currencyCode)
        {
            columns += ", currency";
            values += ", @currency";
            parameters.Add(("currency", currencyCode));
        }

        if (adjustmentOf is { } parent)
        {
            columns += ", adjustment_of";
            values += ", @adjustment_of";
            parameters.Add(("adjustment_of", parent));
        }

        if (reason is { } adjustmentReason)
        {
            columns += ", reason";
            values += ", @reason";
            parameters.Add(("reason", adjustmentReason));
        }

        return ($"insert into public.usage_ledger_entry {columns}) {values})", parameters.ToArray());
    }

    /// <summary>One roll-up INSERT; the caller appends the recompute's <c>on conflict</c> clause.</summary>
    private static (string Sql, (string Name, object Value)[] Parameters) RollupInsert(
        Guid id,
        Guid org,
        Guid? projectId,
        decimal totalQuantity,
        int rowCount,
        string period = "2026-10",
        string metric = "results")
    {
        var columns = "(id, org_id, period, metric, total_quantity, row_count, computed_at"
            + (projectId is null ? string.Empty : ", project_id");
        var values = "values (@id, @org_id, @period, @metric, @total_quantity, @row_count, @computed_at"
            + (projectId is null ? string.Empty : ", @project_id");
        var parameters = new List<(string Name, object Value)>
        {
            ("id", id),
            ("org_id", org),
            ("period", period),
            ("metric", metric),
            ("total_quantity", totalQuantity),
            ("row_count", rowCount),
            ("computed_at", DateTimeOffset.UtcNow),
        };

        if (projectId is { } scope)
        {
            parameters.Add(("project_id", scope));
        }

        return ($"insert into public.usage_rollup {columns}) {values})", parameters.ToArray());
    }

    /// <summary>An org and a project in it, written as the migration role.</summary>
    private async Task<(Guid Org, Guid Project)> SeedTenantAsync(string database, string orgSlug, string projectSlug)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var slug = Unique.Slug(orgSlug);

        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await seed.ExecuteAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            ("id", org),
            ("name", orgSlug),
            ("slug", slug));
        await seed.ExecuteAsync(
            "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
            ("id", project),
            ("org_id", org),
            ("name", projectSlug),
            ("slug", projectSlug));
        await seed.CommitAsync();

        return (org, project);
    }

    /// <summary>Runs one INSERT as the app role in a fresh tenant transaction and commits exactly one row.</summary>
    private static async Task ExecuteAsync(
        string connectionString,
        Guid org,
        (string Sql, (string Name, object Value)[] Parameters) insert)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, org);

        (await scope.ExecuteAsync(insert.Sql, insert.Parameters)).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>Runs one statement in a fresh transaction for <paramref name="connectionString"/> and returns the server error.</summary>
    private static async Task<PostgresException> CaptureErrorAsync(
        string connectionString,
        Guid org,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, org);

        return await scope.CaptureErrorAsync(sql, parameters);
    }
}
