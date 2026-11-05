// The row builders and one-shot writers every section shares: each write opens its own transaction as the app
// role unless a case speaks as the owner, and a refused statement always gets one of its own.
using System.Globalization;
using Shouldly;

namespace Tenancy.IntegrationTests;

public sealed partial class UsagePriceSchemaTests
{
    private static DateTimeOffset At(int month) =>
        new(2026, month, 1, 0, 0, 0, TimeSpan.Zero);

    private static (string Sql, (string Name, object Value)[] Parameters) PriceRow(
        Guid id,
        Guid? org,
        string metric = "results",
        decimal unitPrice = 0.0100m,
        string currency = "USD",
        DateTimeOffset? effectiveFrom = null,
        DateTimeOffset? effectiveTo = null,
        string version = "card-1",
        decimal? includedQuota = null)
    {
        const string sql = """
            insert into public.usage_price
                (id, org_id, metric, unit_price, currency, effective_from, effective_to, included_quota,
                 rate_card_version)
            values
                (@id::uuid, @org_id::uuid, @metric::text, @unit_price::numeric, @currency::text,
                 @effective_from::timestamptz, @effective_to::timestamptz, @included_quota::numeric,
                 @rate_card_version::text)
            """;

        (string Name, object Value)[] parameters =
        [
            ("id", id),
            ("org_id", org is { } scope ? (object)scope : DBNull.Value),
            ("metric", metric),
            ("unit_price", unitPrice),
            ("currency", currency),
            ("effective_from", effectiveFrom ?? At(1)),
            ("effective_to", effectiveTo is { } end ? (object)end : DBNull.Value),
            ("included_quota", includedQuota is { } quota ? (object)quota : DBNull.Value),
            ("rate_card_version", version),
        ];

        return (sql, parameters);
    }

    private static (string Sql, (string Name, object Value)[] Parameters) LedgerRow(
        Guid id,
        Guid org,
        Guid? priceRef)
    {
        const string sql = """
            insert into public.usage_ledger_entry
                (id, org_id, metric, quantity, unit, period, billing_key, occurred_at, price_ref)
            values
                (@id::uuid, @org_id::uuid, 'results', 1, 'results', '2026-11', @billing_key::text, now(),
                 @price_ref::uuid)
            """;

        (string Name, object Value)[] parameters =
        [
            ("id", id),
            ("org_id", org),
            ("billing_key", $"ledger-{id:D}"),
            ("price_ref", priceRef is { } price ? price : DBNull.Value),
        ];

        return (sql, parameters);
    }

    private static string DefinerSql(Guid id, string version, DateTimeOffset from, DateTimeOffset? to)
    {
        var end = to is { } closed
            ? "cast('" + closed.ToString("O", CultureInfo.InvariantCulture) + "' as timestamptz), "
            : "cast(null as timestamptz), ";

        return "select evidgate.usage_price_insert_global("
            + "cast('" + id.ToString("D", CultureInfo.InvariantCulture) + "' as uuid), "
            + "'results', 0.0100, 'USD', "
            + "cast('" + from.ToString("O", CultureInfo.InvariantCulture) + "' as timestamptz), "
            + end
            + "cast(null as numeric), '" + version + "')";
    }

    private async Task<Guid> InsertGlobalCardAsync(
        string database,
        string version,
        DateTimeOffset from,
        DateTimeOffset? to,
        Guid? id = null)
    {
        var priceId = id ?? Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database));
        var stored = await scope.ScalarAsync<Guid>(DefinerSql(priceId, version, from, to));
        await scope.CommitAsync();

        return stored;
    }

    private async Task<Guid> InsertTenantPriceAsync(
        string database,
        Guid org,
        string version,
        DateTimeOffset? effectiveFrom = null,
        DateTimeOffset? effectiveTo = null)
    {
        var id = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
        (await ExecuteAsync(
            scope,
            PriceRow(id, org, effectiveFrom: effectiveFrom ?? At(1), effectiveTo: effectiveTo, version: version)))
            .ShouldBe(1);
        await scope.CommitAsync();

        return id;
    }

    private async Task InsertAsync(
        string database,
        Guid? org,
        (string Sql, (string Name, object Value)[] Parameters) statement)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
        (await ExecuteAsync(scope, statement)).ShouldBe(1);
        await scope.CommitAsync();
    }

    private static async Task<int> ExecuteAsync(TenantScope scope, (string Sql, (string Name, object Value)[] Parameters) statement) =>
        await scope.ExecuteAsync(statement.Sql, statement.Parameters);

    /// <summary>
    /// One refusal in its own transaction: a refused statement aborts the transaction it runs in, so a
    /// second statement in the same scope would answer 25P02 instead of the constraint under test.
    /// </summary>
    private async Task AssertRefusedAsync(
        string database,
        Guid? org,
        (string Sql, (string Name, object Value)[] Parameters) statement,
        string sqlState,
        string constraintName)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
        var error = await scope.CaptureErrorAsync(statement.Sql, statement.Parameters);

        error.SqlState.ShouldBe(sqlState, $"'{constraintName}' must answer {sqlState} ({error.MessageText})");
        error.ConstraintName.ShouldBe(constraintName, "the refusal names the constraint it violated");
    }
}
