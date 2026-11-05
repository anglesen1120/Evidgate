// The usage ledger's single-column reference to a price: added not valid, refused for a price the writing org
// cannot see, and guarded on update as well as on insert.
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class UsagePriceSchemaTests
{
    [Fact]
    public async Task LedgerPriceReference_IsDeferredAndRefusesAForeignOverride()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var orgA = Guid.CreateVersion7();
        var orgB = Guid.CreateVersion7();

        await AssertLedgerPriceKeyIsDeferredAsync(database);

        var globalId = await InsertGlobalCardAsync(database, "card-global", At(1), null);
        var priceA = await InsertTenantPriceAsync(database, orgA, "card-a");
        var priceB = await InsertTenantPriceAsync(database, orgB, "card-b");

        await AssertUnseenReferencesAreRefusedAsync(database, orgA, priceB);

        // The platform card and the org's own rows are both legal references.
        var ownRow = Guid.CreateVersion7();
        await InsertAsync(database, orgA, LedgerRow(ownRow, orgA, globalId));
        var overrideRow = Guid.CreateVersion7();
        await InsertAsync(database, orgA, LedgerRow(overrideRow, orgA, priceA));
        await InsertAsync(database, orgA, LedgerRow(Guid.CreateVersion7(), orgA, null));

        await AssertRepairsCannotCrossTheBoundaryAsync(database, orgA, orgB, ownRow, overrideRow, priceA, priceB);
    }

    /// <summary>The key exists on <c>price_ref</c> alone and stays NOT VALID until a writer sets it.</summary>
    private async Task AssertLedgerPriceKeyIsDeferredAsync(string database)
    {
        var constraints = await _fixture.ConstraintsAsync(database, LedgerTable);
        constraints.ShouldContain(
            constraint => constraint.StartsWith("f:usage_ledger_entry_price_fk:", StringComparison.Ordinal)
                && constraint.Contains("(price_ref)", StringComparison.Ordinal),
            "the ledger names its price through the single-column key, added not valid");

        (await _fixture.ScalarAsync<bool>(
            database,
            "select convalidated from pg_catalog.pg_constraint where conname = 'usage_ledger_entry_price_fk'"))
            .ShouldBeFalse(
                "the key stays not valid on purpose: no writer sets price_ref yet, and a not valid key "
                + "already refuses a new row, so the validation scan of the whole ledger buys nothing");
    }

    /// <summary>A reference that names no price, and one that names another org's override, both answer 23503.</summary>
    private async Task AssertUnseenReferencesAreRefusedAsync(string database, Guid orgA, Guid priceB)
    {
        // A dangling reference is the foreign key's refusal.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), orgA))
        {
            var row = LedgerRow(Guid.CreateVersion7(), orgA, Guid.CreateVersion7());
            var dangling = await scope.CaptureErrorAsync(row.Sql, row.Parameters);
            dangling.SqlState.ShouldBe("23503", "a price_ref that names no price is refused");
            dangling.ConstraintName.ShouldBe("usage_ledger_entry_price_fk");
        }

        // Another org's override exists, so the foreign key is satisfied; the visibility trigger is what
        // refuses it, and it names the same key because the reference is broken for this caller.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), orgA))
        {
            var row = LedgerRow(Guid.CreateVersion7(), orgA, priceB);
            var foreign = await scope.CaptureErrorAsync(row.Sql, row.Parameters);
            foreign.SqlState.ShouldBe("23503", "a ledger row may not name another org's override");
            foreign.ConstraintName.ShouldBe("usage_ledger_entry_price_fk");
        }
    }

    /// <summary>An update cannot retarget the reference or the tenant; a price the org sees still moves.</summary>
    private async Task AssertRepairsCannotCrossTheBoundaryAsync(
        string database,
        Guid orgA,
        Guid orgB,
        Guid ownRow,
        Guid overrideRow,
        Guid priceA,
        Guid priceB)
    {
        // The guard also runs on an update that rewrites the reference or the tenant, so a repair
        // statement cannot move a row onto a price its org may not see. Each attempt runs in its own
        // transaction, because a refused statement aborts the one it runs in.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), orgA))
        {
            var moved = await owner.CaptureErrorAsync(
                "update public.usage_ledger_entry set price_ref = @price::uuid where id = @id::uuid",
                new (string Name, object Value)[] { ("price", priceB), ("id", ownRow) });
            moved.SqlState.ShouldBe("23503", "an update may not point a ledger row at another org's override");
            moved.ConstraintName.ShouldBe("usage_ledger_entry_price_fk");
        }

        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), orgA))
        {
            var retenanted = await owner.CaptureErrorAsync(
                "update public.usage_ledger_entry set org_id = @org::uuid where id = @id::uuid",
                new (string Name, object Value)[] { ("org", orgB), ("id", overrideRow) });
            retenanted.SqlState.ShouldBe(
                "23503",
                "moving a row to another tenant would leave it naming the old org's price");
            retenanted.ConstraintName.ShouldBe("usage_ledger_entry_price_fk");
        }

        // A visible reference still moves: the guard refuses the reference, never the update itself.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), orgA))
        {
            (await owner.ExecuteAsync(
                "update public.usage_ledger_entry set price_ref = @price::uuid where id = @id::uuid",
                new (string Name, object Value)[] { ("price", priceA), ("id", ownRow) }))
                .ShouldBe(1, "the org's own override is a price its rows may name");
            await owner.CommitAsync();
        }
    }
}
