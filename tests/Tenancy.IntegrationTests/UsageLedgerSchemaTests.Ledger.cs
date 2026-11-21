using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class UsageLedgerSchemaTests
{
    // ------------------------------------------------------------------ (d) AC1: the dedupe key

    [Fact]
    public async Task Ledger_DuplicateBillingKey_RaisesUniqueViolation()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);

        var first = LedgerInsert(Guid.CreateVersion7(), org, project, billingKey: "batch:7f6a:results");
        (await app.ExecuteAsync(first.Sql, first.Parameters)).ShouldBe(1);

        // A plain INSERT of the same source event is the caller's bug, and the database says so
        // rather than billing twice; the batch path below is the tolerant form.
        var second = LedgerInsert(Guid.CreateVersion7(), org, project, billingKey: "batch:7f6a:results");
        var error = await app.CaptureErrorAsync(second.Sql, second.Parameters);

        error.SqlState.ShouldBe("23505");
        error.ConstraintName.ShouldBe("usage_ledger_entry_billing_key_key");
    }

    [Fact]
    public async Task Ledger_DuplicateBillingKey_ConcurrentOnConflictInserts_LeaveOneRow()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        const int Writers = 8;
        const string BillingKey = "batch:race:results";

        // AC1 through the production statement shape: one `insert … on conflict (org_id, billing_key)
        // do nothing returning billing_key` per batch, all racing the same source event. Exactly one
        // connection may insert; every other one reports nothing back, and the table holds one row.
        var inserted = await Task.WhenAll(Enumerable.Range(0, Writers).Select(async _ =>
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
            var row = LedgerInsert(Guid.CreateVersion7(), org, project, billingKey: BillingKey);
            var affected = await scope.ExecuteAsync(
                row.Sql + " on conflict (org_id, billing_key) do nothing",
                row.Parameters);
            await scope.CommitAsync();

            return affected;
        }));

        inserted.Sum().ShouldBe(1, $"{Writers} concurrent batches with one billing_key must insert exactly one row");
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_ledger_entry where org_id = '{org}' and billing_key = '{BillingKey}'"))
            .ShouldBe(1);
    }

    // ------------------------------------------------------------------ (e) AC2: adjustments

    [Fact]
    public async Task Ledger_Adjustment_LeavesTheOriginalIdentical_AndBothRowsAppear()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var originalId = Guid.CreateVersion7();

        await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);

        var original = LedgerInsert(originalId, org, project, quantity: 120m, billingKey: "batch:ac2:results");
        (await app.ExecuteAsync(original.Sql, original.Parameters)).ShouldBe(1);

        var before = await app.ScalarAsync<string>(
            $"select row_to_json(t)::text from public.usage_ledger_entry t where id = '{originalId}'");

        var adjustment = LedgerInsert(
            Guid.CreateVersion7(),
            org,
            project,
            quantity: -20m,
            billingKey: "adjustment:ac2:results",
            adjustmentOf: originalId,
            reason: "24 events were duplicates of batch 6f1c");
        (await app.ExecuteAsync(adjustment.Sql, adjustment.Parameters))
            .ShouldBe(1, "a same-org adjustment is a new row pointing at the corrected one");

        var after = await app.ScalarAsync<string>(
            $"select row_to_json(t)::text from public.usage_ledger_entry t where id = '{originalId}'");

        after.ShouldBe(before, "AC2: the corrected row is byte-identical after the adjustment is inserted");

        // Both rows are in the period statement: the sum is what the invoice reconciles to.
        (await app.ScalarAsync<long>(
            $"select count(*) from public.usage_ledger_entry where org_id = '{org}' "
            + "and period = '2026-10' and metric = 'results'"))
            .ShouldBe(2);
        (await app.ScalarAsync<decimal>(
            $"select sum(quantity) from public.usage_ledger_entry where org_id = '{org}' "
            + "and period = '2026-10' and metric = 'results'"))
            .ShouldBe(100m);
    }

    [Fact]
    public async Task Ledger_Adjustment_CrossTenantParent_Rejected()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var (otherOrg, otherProject) = await SeedTenantAsync(database, "beta", "store");
        var foreignRow = Guid.CreateVersion7();

        await using (var other = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), otherOrg))
        {
            var row = LedgerInsert(foreignRow, otherOrg, otherProject, billingKey: "batch:other:results");
            (await other.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1);
            await other.CommitAsync();
        }

        await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);

        // (org_id, adjustment_of) is a composite key, so naming another tenant's row cannot even be
        // expressed as a valid reference: the FK answers 23503 for the whole key.
        var corrupt = LedgerInsert(
            Guid.CreateVersion7(),
            org,
            project,
            billingKey: "adjustment:corrupt:results",
            adjustmentOf: foreignRow,
            reason: "points at another tenant");
        var error = await app.CaptureErrorAsync(corrupt.Sql, corrupt.Parameters);

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("usage_ledger_entry_adjustment_fk");
    }

    [Fact]
    public async Task Ledger_Adjustment_RequiresAReason()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var originalId = Guid.CreateVersion7();

        var original = LedgerInsert(originalId, org, project, billingKey: "batch:reason:results");
        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            // A plain ledger row needs no reason: `reason` is only constrained on an adjustment.
            (await app.ExecuteAsync(original.Sql, original.Parameters)).ShouldBe(1);
            await app.CommitAsync();
        }

        foreach (var (label, reason) in new[]
        {
            ("missing", (string?)null),
            ("empty", string.Empty),
            ("over-long", new string('r', 2001)),
        })
        {
            await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
            var adjustment = LedgerInsert(
                Guid.CreateVersion7(),
                org,
                project,
                billingKey: $"adjustment:{label}:results",
                adjustmentOf: originalId,
                reason: reason);
            var error = await app.CaptureErrorAsync(adjustment.Sql, adjustment.Parameters);

            error.SqlState.ShouldBe("23514", $"an adjustment with a {label} reason must be refused");
            error.ConstraintName.ShouldBe("usage_ledger_entry_adjustment_reason_check");
        }
    }

    // ------------------------------------------------------------------ (f) the closed lists and bounds

    [Fact]
    public async Task Ledger_CheckBoundaries_RejectUnknownMetricPeriodAndBillingKey()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        var cases = new (string Label, (string Sql, (string Name, object Value)[] Parameters) Insert, string Constraint)[]
        {
            ("unknown metric",
                LedgerInsert(Guid.CreateVersion7(), org, project, metric: "storage_gb"),
                "usage_ledger_entry_metric_check"),
            ("month 13",
                LedgerInsert(Guid.CreateVersion7(), org, project, period: "2026-13"),
                "usage_ledger_entry_period_check"),
            ("unpadded month",
                LedgerInsert(Guid.CreateVersion7(), org, project, period: "2026-1"),
                "usage_ledger_entry_period_check"),
            ("empty billing key",
                LedgerInsert(Guid.CreateVersion7(), org, project, billingKey: string.Empty),
                "usage_ledger_entry_billing_key_check"),
            ("201-character billing key",
                LedgerInsert(Guid.CreateVersion7(), org, project, billingKey: new string('k', 201)),
                "usage_ledger_entry_billing_key_check"),
        };

        foreach (var (label, insert, constraint) in cases)
        {
            await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
            var error = await app.CaptureErrorAsync(insert.Sql, insert.Parameters);

            error.SqlState.ShouldBe("23514", $"{label} must be refused by {constraint}");
            error.ConstraintName.ShouldBe(constraint);
        }

        // The boundaries themselves are inside the contract: January, and a 200-character key.
        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var edge = LedgerInsert(
                Guid.CreateVersion7(),
                org,
                project,
                period: "2026-01",
                billingKey: new string('k', 200));
            (await app.ExecuteAsync(edge.Sql, edge.Parameters)).ShouldBe(1, "the inclusive bounds are legal rows");
            await app.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_ledger_entry where org_id = '{org}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Ledger_NegativeQuantity_OnlyOnAnAdjustmentRow()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        // F-B: a plain metering row cannot carry a negative quantity. The domain rule refuses it with
        // 422 first; this CHECK is the backstop for every writer that reaches SQL directly.
        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var negative = LedgerInsert(
                Guid.CreateVersion7(),
                org,
                project,
                quantity: -1m,
                billingKey: "batch:negative:results");
            var error = await app.CaptureErrorAsync(negative.Sql, negative.Parameters);

            error.SqlState.ShouldBe("23514", "a negative quantity on a plain ledger row must be refused");
            error.ConstraintName.ShouldBe("usage_ledger_entry_quantity_check");
        }

        var originalId = Guid.CreateVersion7();
        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var original = LedgerInsert(originalId, org, project, quantity: 40m, billingKey: "batch:correction:results");
            (await app.ExecuteAsync(original.Sql, original.Parameters)).ShouldBe(1);

            // AC2: a correction subtracts, so a negative quantity is legal exactly here.
            var correction = LedgerInsert(
                Guid.CreateVersion7(),
                org,
                project,
                quantity: -40m,
                billingKey: "adjustment:correction:results",
                adjustmentOf: originalId,
                reason: "the batch was rejected after it had been billed");
            (await app.ExecuteAsync(correction.Sql, correction.Parameters))
                .ShouldBe(1, "a negative quantity is the point of an adjustment row");

            // The CHECK bounds the plain rows only: a positive adjustment is equally legal.
            var topUp = LedgerInsert(
                Guid.CreateVersion7(),
                org,
                project,
                quantity: 5m,
                billingKey: "adjustment:topup:results",
                adjustmentOf: originalId,
                reason: "five accepted events were missing from the batch");
            (await app.ExecuteAsync(topUp.Sql, topUp.Parameters)).ShouldBe(1);
            await app.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_ledger_entry where org_id = '{org}'"))
            .ShouldBe(3, "the refused negative row wrote nothing; the original and both adjustments did");
        (await _fixture.ScalarAsync<decimal>(
            database,
            $"select sum(quantity) from public.usage_ledger_entry where org_id = '{org}'"))
            .ShouldBe(5m, "40 - 40 + 5 is what the period statement reconciles to");
    }

    [Fact]
    public async Task Ledger_Unit_BoundedToOneTo32Characters()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        var refused = new (string Label, string Unit)[]
        {
            ("empty", string.Empty),
            ("33 ASCII characters", new string('u', 33)),
            // char_length counts characters, not bytes: 33 two-byte characters are still 33.
            ("33 two-byte characters", new string('\u00e9', 33)),
        };

        foreach (var (label, unit) in refused)
        {
            await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
            var row = LedgerInsert(
                Guid.CreateVersion7(),
                org,
                project,
                unit: unit,
                billingKey: $"batch:unit:{label}");
            var error = await app.CaptureErrorAsync(row.Sql, row.Parameters);

            error.SqlState.ShouldBe("23514", $"a {label} unit must be refused");
            error.ConstraintName.ShouldBe("usage_ledger_entry_unit_check");
        }

        // The inclusive bounds are legal rows, including a multibyte unit at the limit.
        foreach (var (label, unit) in new[]
        {
            ("one", "u"),
            ("32 ASCII characters", new string('u', 32)),
            ("32 two-byte characters", new string('\u00e9', 32)),
        })
        {
            await ExecuteAsync(
                _fixture.AppConnectionString(database),
                org,
                LedgerInsert(Guid.CreateVersion7(), org, project, unit: unit, billingKey: $"batch:unit:ok:{label}"));
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_ledger_entry where org_id = '{org}'"))
            .ShouldBe(3, "only the three in-bound rows exist; every refused unit wrote nothing");
    }
}
