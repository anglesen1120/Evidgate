using System.Globalization;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The <c>usage_price</c> table as the database sees it: the contract shape, the closed metric and
/// currency vocabularies, the unique card version per scope, the non-overlap rule of one scope and
/// metric, the append-only grant of the application role, the owner-only definer that writes the
/// platform rate card and the tenant boundary itself (ENABLE + FORCE row level security with the
/// catalogue read shape for <c>evidgate_app</c> only), and the unvalidated single-column reference
/// the usage ledger holds to a price.
/// </summary>
/// <remarks>
/// A price is immutable, so the table is written only by the card load: the app role inserts its own
/// overrides, the definer inserts the global card. Both halves are asserted here, together with the
/// trigger that stops a ledger row naming another org's override — the tenant half a single-column
/// foreign key cannot express, which is why the key is single-column in the first place: the global
/// card carries <c>org_id IS NULL</c>.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsagePriceSchemaTests
{
    private const string Table = "usage_price";
    private const string LedgerTable = "usage_ledger_entry";
    private const string MigrationFile = "0073_usage_price.sql";

    private readonly PostgresTenancyFixture _fixture;

    public UsagePriceSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration_IsJournalled_AndTheTableDeclaresTheContractShape()
    {
        var database = await _fixture.SharedDatabaseAsync();

        var journal = await _fixture.ReadJournalAsync(database);
        journal.ShouldContain(
            row => string.Equals(row.Name, MigrationFile, StringComparison.Ordinal),
            $"{MigrationFile} must be applied to the migrated template");

        (await _fixture.TableExistsAsync(database, Table)).ShouldBeTrue();

        var columns = await _fixture.ColumnsAsync(database, Table);
        foreach (var prefix in new[]
        {
            "id:uuid:uuid:NO:",
            "org_id:uuid:uuid:YES:",
            "metric:text:text:NO:",
            "unit_price:numeric:numeric:NO:",
            "currency:text:text:NO:",
            "effective_from:timestamp with time zone:timestamptz:NO:",
            "effective_to:timestamp with time zone:timestamptz:YES:",
            "included_quota:numeric:numeric:YES:",
            "rate_card_version:text:text:NO:",
            "created_at:timestamp with time zone:timestamptz:NO:",
            "updated_at:timestamp with time zone:timestamptz:NO:",
            "version:integer:int4:NO:",
        })
        {
            columns.ShouldContain(
                column => column.StartsWith(prefix, StringComparison.Ordinal),
                $"{Table} needs the contract column shape '{prefix}'");
        }

        // `org_id` nullable is the point of the table, not an accident: a NULL scope is the platform
        // card every org reads, so its nullability is asserted above and the catalogue exception list
        // names it (contracts/rls-exceptions.txt), which the boot self-check reads.
        var constraints = await _fixture.ConstraintsAsync(database, Table);
        constraints.ShouldContain(
            constraint => constraint.StartsWith("p:usage_price_pkey:", StringComparison.Ordinal)
                && constraint.Contains("(id)", StringComparison.Ordinal),
            $"{Table} is addressed by its id");
        constraints.ShouldContain(
            constraint => constraint.StartsWith("u:usage_price_org_id_key:", StringComparison.Ordinal)
                && constraint.Contains("(org_id, id)", StringComparison.Ordinal),
            $"{Table} declares unique (org_id, id) as the composite tenant key");
        foreach (var check in new[]
        {
            "usage_price_metric_check",
            "usage_price_unit_price_check",
            "usage_price_currency_check",
            "usage_price_window_check",
            "usage_price_included_quota_check",
            "usage_price_rate_card_version_check",
            "usage_price_version_check",
        })
        {
            constraints.ShouldContain(
                constraint => constraint.Contains(check, StringComparison.Ordinal),
                $"{Table} closes its values in '{check}'");
        }

        constraints.ShouldNotContain(
            constraint => constraint.StartsWith("f:", StringComparison.Ordinal),
            $"{Table} is a price leaf: it points at no other row");

        var indexes = await _fixture.ScalarAsync<string>(
            database,
            "select coalesce(string_agg(indexname, ',' order by indexname), '') from pg_catalog.pg_indexes "
            + $"where schemaname = 'public' and tablename = '{Table}'");
        indexes.ShouldContain("usage_price_card_key", Case.Sensitive, "one card version per scope is unique");
        indexes.ShouldContain("idx_price_lookup", Case.Sensitive, "the resolver reads by metric and instant");

        var rowSecurity = await _fixture.RowSecurityAsync(database, Table);
        rowSecurity.Enabled.ShouldBeTrue($"{Table} carries ENABLE row level security");
        rowSecurity.Forced.ShouldBeTrue(
            $"{Table} carries FORCE row level security, so its owner cannot bypass the tenant boundary");

        var policies = await _fixture.ScalarAsync<string>(
            database,
            "select coalesce(string_agg(policyname || ':' || cmd || ':' || array_to_string(roles, ','), ',' order by policyname), '') "
            + "from pg_catalog.pg_policies where schemaname = 'public' "
            + $"and tablename = '{Table}'");
        policies.ShouldBe(
            "usage_price_app_read:SELECT:evidgate_app,"
            + "usage_price_app_write:INSERT:evidgate_app,"
            + "usage_price_owner_backfill:ALL:evidgate_owner",
            "the catalogue reads for every org, the org writes only its own rows, and the owner row is the "
            + "definer's path: no policy without a role and no operator policy");

        var triggers = await _fixture.ScalarAsync<string>(
            database,
            "select coalesce(string_agg(tgname, ',' order by tgname), '') from pg_catalog.pg_trigger "
            + $"where tgrelid = 'public.{Table}'::regclass and not tgisinternal");
        triggers.ShouldBe(
            "usage_price_overlap_guard,usage_price_set_updated_at_and_version",
            "the card-version and non-overlap guard runs before every insert and the shared trigger owns "
            + "updated_at and version");
    }

    [Fact]
    public async Task Refusals_AreNamedByTheirOwnConstraint()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var org = Guid.CreateVersion7();

        await InsertAsync(database, org, PriceRow(Guid.CreateVersion7(), org, effectiveTo: At(2)));

        // A negative price, a currency that is not three uppercase letters, an empty window, and a
        // metric outside the closed metering list: each answers 23514 with its own constraint name, so
        // a caller can name what it sent wrong without the message echoing the value. Each refused row
        // takes a free window and its own card version: the guards run before the checks, so an
        // overlapping or repeated row would answer as that first.
        await AssertRefusedAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, unitPrice: -0.01m, effectiveFrom: At(6), effectiveTo: At(7), version: "card-negative"),
            "23514",
            "usage_price_unit_price_check");
        await AssertRefusedAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, currency: "usd", effectiveFrom: At(8), effectiveTo: At(9), version: "card-currency"),
            "23514",
            "usage_price_currency_check");
        await AssertRefusedAsync(
            database,
            org,
            PriceRow(
                Guid.CreateVersion7(),
                org,
                effectiveFrom: At(3),
                effectiveTo: At(2),
                version: "card-window"),
            "23514",
            "usage_price_window_check");
        await AssertRefusedAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, metric: "widgets", effectiveFrom: At(10), effectiveTo: At(11), version: "card-metric"),
            "23514",
            "usage_price_metric_check");
        await AssertRefusedAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, effectiveFrom: At(12), effectiveTo: At(12).AddMonths(1), version: string.Empty),
            "23514",
            "usage_price_rate_card_version_check");
    }

    [Fact]
    public async Task DuplicateCardVersion_IsRefused_ButAnotherScopeOrMetricIsNot()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var orgA = Guid.CreateVersion7();
        var orgB = Guid.CreateVersion7();

        await InsertAsync(database, orgA, PriceRow(Guid.CreateVersion7(), orgA, effectiveFrom: At(1), effectiveTo: At(2)));

        // Same scope, metric and version, in a window that does not overlap: the unique index answers
        // a card reload rather than the caller diffing rows.
        await AssertRefusedAsync(
            database,
            orgA,
            PriceRow(Guid.CreateVersion7(), orgA, effectiveFrom: At(6), effectiveTo: At(7)),
            "23505",
            "usage_price_card_key");

        // The same reload in a window that also overlaps an existing row still answers as a duplicate:
        // the guard's version check runs under the unique index's name before the window rule, so the
        // store's duplicate_version mapping does not depend on which window the retry used.
        await AssertRefusedAsync(
            database,
            orgA,
            PriceRow(Guid.CreateVersion7(), orgA, effectiveFrom: At(1), effectiveTo: At(2)),
            "23505",
            "usage_price_card_key");

        // A different metric and a different tenant are different scopes: both accept the same version.
        await InsertAsync(
            database,
            orgA,
            PriceRow(Guid.CreateVersion7(), orgA, metric: "ai_tokens", version: "card-1"));
        await InsertAsync(database, orgB, PriceRow(Guid.CreateVersion7(), orgB, metric: "ai_tokens", version: "card-1"));
    }

    [Fact]
    public async Task OverlappingWindows_AreRefusedInOneScope_AndAdjacentAndOverridesAreNot()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var org = Guid.CreateVersion7();

        await InsertAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, effectiveFrom: At(1), effectiveTo: At(3)));

        // [Jan, Mar) overlaps [Feb, Apr) on February: one instant may carry one price per scope and
        // metric, or the resolver's answer would depend on row order.
        await AssertRefusedAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, effectiveFrom: At(2), effectiveTo: At(4), version: "card-2"),
            "23514",
            "usage_price_no_overlap");

        // Adjacent windows share only the boundary, which is open: [Jan, Mar) + [Mar, May) is legal.
        await InsertAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, effectiveFrom: At(3), effectiveTo: At(5), version: "card-3"));

        // A NULL end is infinity, so it overlaps everything the same metric prices after its start.
        await InsertAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, metric: "ai_tokens", effectiveFrom: At(6), effectiveTo: null, version: "card-4"));
        await AssertRefusedAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, metric: "ai_tokens", effectiveFrom: At(9), effectiveTo: null, version: "card-5"),
            "23514",
            "usage_price_no_overlap");

        // An override covers the window the platform card covers: override-first is the point of the
        // table, so the guard compares only rows of the same scope. [May, Jun) starts where the org's own
        // [Mar, May) ends and sits inside the global card's open window.
        var globalId = await InsertGlobalCardAsync(database, "card-global", At(1), null);
        (globalId == Guid.Empty).ShouldBeFalse();
        await InsertAsync(
            database,
            org,
            PriceRow(
                Guid.CreateVersion7(),
                org,
                effectiveFrom: At(5),
                effectiveTo: At(6),
                version: "card-override"));
    }

    [Fact]
    public async Task AppRole_IsAppendOnly_AndCannotWriteTheGlobalCard()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var org = Guid.CreateVersion7();
        var id = await InsertTenantPriceAsync(database, org, "card-own");

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "SELECT")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "INSERT")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "UPDATE")).ShouldBeFalse(
            "a price is immutable: a correction is a new row");
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "DELETE")).ShouldBeFalse(
            "a deleted price would orphan the ledger rows that name it");
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_operator", Table, "SELECT")).ShouldBeTrue(
            "the operator holds SELECT and no policy, so the grant is inert until a grant-gated form lands");

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var update = await scope.CaptureErrorAsync(
                "update public.usage_price set unit_price = 0.0200 where id = @id::uuid",
                new (string Name, object Value)[] { ("id", id) });
            update.SqlState.ShouldBe("42501", "the app role holds no UPDATE on a price");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var delete = await scope.CaptureErrorAsync(
                "delete from public.usage_price where id = @id::uuid",
                new (string Name, object Value)[] { ("id", id) });
            delete.SqlState.ShouldBe("42501", "the app role holds no DELETE on a price");
        }

        // A NULL scope is the platform's row: the write policy compares the row's org to the tenant
        // setting, and NULL never equals it, so the app role is refused here rather than by a grant.
        var insert = PriceRow(Guid.CreateVersion7(), null, version: "card-global");
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var global = await scope.CaptureErrorAsync(insert.Sql, insert.Parameters);
            global.SqlState.ShouldBe("42501", "the app role cannot insert a global price directly");
            global.MessageText.ShouldContain("row-level security", Case.Sensitive);
        }
    }

    [Fact]
    public async Task GlobalInsert_DefinerWritesTheCard_AndRefusesATenantTransaction()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var org = Guid.CreateVersion7();

        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_function_privilege('evidgate_app', "
            + "'evidgate.usage_price_insert_global(uuid,text,numeric,text,timestamptz,timestamptz,numeric,text)', 'EXECUTE')"))
            .ShouldBeTrue("the card load runs as the app role through the definer");
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_function_privilege('evidgate_operator', "
            + "'evidgate.usage_price_insert_global(uuid,text,numeric,text,timestamptz,timestamptz,numeric,text)', 'EXECUTE')"))
            .ShouldBeFalse("EXECUTE is the app role's alone among the runtime roles");
        (await _fixture.ScalarAsync<bool>(
            database,
            "select prosecdef from pg_catalog.pg_proc p join pg_catalog.pg_namespace n on n.oid = p.pronamespace "
            + "where n.nspname = 'evidgate' and p.proname = 'usage_price_insert_global'"))
            .ShouldBeTrue("the loader stands in for the migration role");
        // The pinned image spells the empty setting as `search_path=""`; the whole setting list is
        // compared, so an added GUC or a non-empty path fails this check as loudly as a missing one.
        (await _fixture.ScalarAsync<string>(
            database,
            "select array_to_string(proconfig, ',') from pg_catalog.pg_proc p "
            + "join pg_catalog.pg_namespace n on n.oid = p.pronamespace "
            + "where n.nspname = 'evidgate' and p.proname = 'usage_price_insert_global'"))
            .ShouldBe("search_path=\"\"", "the definer pins the empty search_path and nothing else");

        // Outside a tenant transaction the definer stores the platform row and returns its id.
        var id = Guid.CreateVersion7();
        var stored = await InsertGlobalCardAsync(database, "card-1", At(1), null, id);
        stored.ShouldBe(id);
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_price where id = '{id}' and org_id is null")).ShouldBe(
            1,
            "a global price carries no org");

        // Inside one, FORCE row level security cannot tell the definer from the app role, so the
        // function carries the boundary itself and refuses before writing anything.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var refused = await scope.CaptureErrorAsync(
                DefinerSql(Guid.CreateVersion7(), "card-2", At(4), At(5)));
            refused.SqlState.ShouldBe("23514", "a global card write inside a tenant transaction is refused");
            refused.ConstraintName.ShouldBe("usage_price_global_tenant_scope");
        }

        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from public.usage_price where org_id is null")).ShouldBe(
            1,
            "the refused call wrote nothing");
    }

    [Fact]
    public async Task TenantReads_SeeTheGlobalCardAndTheirOwnRows_NeverAnotherOrg()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var orgA = Guid.CreateVersion7();
        var orgB = Guid.CreateVersion7();

        var globalId = await InsertGlobalCardAsync(database, "card-global", At(1), null);
        var priceA = await InsertTenantPriceAsync(database, orgA, "card-a");
        var priceB = await InsertTenantPriceAsync(database, orgB, "card-b");

        await using (var scopeA = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), orgA))
        {
            var visible = await scopeA.ScalarAsync<Guid[]>(
                "select coalesce(array_agg(id order by id), '{}'::uuid[]) from public.usage_price");
            visible.ShouldBe(new[] { globalId, priceA }, ignoreOrder: true);
        }

        await using (var scopeB = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), orgB))
        {
            var visible = await scopeB.ScalarAsync<Guid[]>(
                "select coalesce(array_agg(id order by id), '{}'::uuid[]) from public.usage_price");
            visible.ShouldBe(new[] { globalId, priceB }, ignoreOrder: true);
        }

        // No tenant setting: the catalogue half of the policy still reads the platform card, and the
        // org half matches nothing — that is the fail-closed floor of the catalogue shape.
        await using (var unset = await TenantScope.BeginAsync(_fixture.AppConnectionString(database)))
        {
            var visible = await unset.ScalarAsync<Guid[]>(
                "select coalesce(array_agg(id order by id), '{}'::uuid[]) from public.usage_price");
            visible.ShouldBe(new[] { globalId });
        }
    }

    [Fact]
    public async Task LedgerPriceReference_IsDeferredAndRefusesAForeignOverride()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var orgA = Guid.CreateVersion7();
        var orgB = Guid.CreateVersion7();

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

        var globalId = await InsertGlobalCardAsync(database, "card-global", At(1), null);
        var priceA = await InsertTenantPriceAsync(database, orgA, "card-a");
        var priceB = await InsertTenantPriceAsync(database, orgB, "card-b");

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

        // The platform card and the org's own rows are both legal references.
        var ownRow = Guid.CreateVersion7();
        await InsertAsync(database, orgA, LedgerRow(ownRow, orgA, globalId));
        await InsertAsync(database, orgA, LedgerRow(Guid.CreateVersion7(), orgA, priceA));
        await InsertAsync(database, orgA, LedgerRow(Guid.CreateVersion7(), orgA, null));

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
                new (string Name, object Value)[] { ("org", orgB), ("id", ownRow) });
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

    [Fact]
    public async Task MigrationSql_RunsAgainAsANoOp()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

        var path = Path.Combine(PostgresTenancyFixture.RepoMigrationsPath, MigrationFile);
        var sql = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        // The template already holds the migration, so re-running it must change nothing: the guards,
        // the `if not exists` clauses and the drop-then-create order are what make a re-deploy safe.
        // It runs as the owner role, the role the migrator uses, so ownership and grants stay put.
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await scope.ExecuteAsync(sql);
            await scope.CommitAsync();
        }

        (await _fixture.TableExistsAsync(database, Table)).ShouldBeTrue();
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from information_schema.tables where table_schema = 'public' and table_name = 'usage_price'"))
            .ShouldBe(1);
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from information_schema.columns where table_schema = 'public' and table_name = '{Table}'"))
            .ShouldBe(12);
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_catalog.pg_constraint where conrelid = 'public.usage_ledger_entry'::regclass "
            + "and conname = 'usage_ledger_entry_price_fk'"))
            .ShouldBe(1, "the ledger key is not added twice");
    }

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
