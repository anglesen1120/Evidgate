// Rows written before 0098 carry a NULL payload_hash. They are never backfilled: the store recomputes the digest from
// the stored billable columns (using original_period when the insert guard re-dated the row) and compares that, so a
// legacy row replays exactly like a new one and still refuses a different payload.
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The legacy half of CR-21: a row with no stored hash is compared by recomputing the hash from what the row bills, and
/// a re-dated row is compared on the period it was metered for, not the open month it was moved to.
/// </summary>
public sealed partial class UsageLedgerPayloadHashTests
{
    [Fact]
    public async Task LegacyRow_NoStoredHash_SamePayload_IsADuplicate_OtherPayload_IsAConflict()
    {
        await using var world = await StartAsync();
        var key = Unique.Slug("legacy");
        var entry = Entry(key, world.Project, quantity: 2.5m);

        await InsertLegacyRowAsync(world, entry);

        (await ColumnAsync(world, key, "payload_hash")).ShouldBeNull("a legacy row keeps its NULL hash, never a backfill");

        var replay = await RecordAsync(world, [entry]);

        replay.Duplicates.ShouldBe([key], "the recomputed digest says the replay bills the same fact");
        replay.Conflicts.ShouldBeEmpty();
        (await LedgerCountAsync(world)).ShouldBe(1);

        var changed = await RecordAsync(world, [entry with { Quantity = 3.5m }]);

        changed.Conflicts.ShouldBe([key], "a legacy row still refuses a key claiming another payload");
        changed.Duplicates.ShouldBeEmpty();
        (await LedgerCountAsync(world)).ShouldBe(1);
        (await ColumnAsync(world, key, "quantity::text")).ShouldBe("2.5");
    }

    /// <summary>
    /// A row the 0094 guard re-dated bills the month it was metered for: the replay names that month, the row stores it
    /// in <c>original_period</c>, and the comparison uses <c>original_period</c> rather than the open month it moved to.
    /// </summary>
    [Fact]
    public async Task LegacyRedatedRow_ComparesOnTheRequestedPeriod()
    {
        await using var world = await StartAsync();
        var key = Unique.Slug("legacy-redated");
        var entry = Entry(key, world.Project, quantity: 4m);

        // An issued statement for the metered month is what makes the guard move the row.
        await IssueStatementAsync(world, entry.Period);
        await InsertLegacyRowAsync(world, entry);

        (await ColumnAsync(world, key, "period")).ShouldBe("2026-11", "the guard bills the next open month");
        (await ColumnAsync(world, key, "original_period")).ShouldBe(entry.Period);
        (await ColumnAsync(world, key, "payload_hash")).ShouldBeNull();

        var replay = await RecordAsync(world, [entry]);

        replay.Duplicates.ShouldBe([key], "the stored original_period is the month the payload claims");
        replay.Conflicts.ShouldBeEmpty();
        (await LedgerCountAsync(world)).ShouldBe(1);

        var changed = await RecordAsync(world, [entry with { Quantity = 5m }]);

        changed.Conflicts.ShouldBe([key]);
        (await ColumnAsync(world, key, "quantity::text")).ShouldBe("4");
    }

    /// <summary>
    /// One ledger row written as the owner with every column the store writes, except <c>payload_hash</c>: the shape of a
    /// row that predates 0098. The owner is the migration/backfill role, and the tenant GUC is set because the table
    /// forces row security on every role.
    /// </summary>
    private async Task InsertLegacyRowAsync(World world, UsageEntryInput entry)
    {
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org);

        (await owner.ExecuteAsync(
            """
            insert into public.usage_ledger_entry
                (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at)
            values (@id, @org_id, @project_id, @metric, @quantity, @unit, @period, @billing_key, @occurred_at)
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", world.Org),
            ("project_id", entry.ProjectId ?? (object)DBNull.Value),
            ("metric", UsageMetricWire.ToWireValue(entry.Metric)),
            ("quantity", entry.Quantity),
            ("unit", entry.Unit),
            ("period", entry.Period),
            ("billing_key", entry.BillingKey),
            ("occurred_at", entry.OccurredAt)))
            .ShouldBe(1, "the legacy row must be insertable without a payload hash");

        await owner.CommitAsync();
    }

    /// <summary>
    /// The one statement row an issue writes for <paramref name="period"/>, committed as the superuser: reaching an
    /// issued period through the issuer's own rules is not what these cases are about, only the guard's reaction to it.
    /// </summary>
    private async Task IssueStatementAsync(World world, string period)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(world.Database);
        await using var command = (NpgsqlCommand)connection.CreateCommand();
        command.CommandText = """
            insert into public.billing_statement
                (id, org_id, period, kind, total_amount, currency, line_count, content_hash, worksheet_json,
                 issued_by, issued_at)
            values (@id, @org_id, @period, 'invoice', 0, 'USD', 0, repeat('a', 64), '{}'::jsonb, @issued_by, now())
            """;
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("org_id", world.Org);
        command.Parameters.AddWithValue("period", period);
        command.Parameters.AddWithValue("issued_by", Guid.CreateVersion7());

        (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(1, $"{period} must be issuable");
    }
}
