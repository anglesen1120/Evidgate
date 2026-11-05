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
public sealed partial class UsagePriceSchemaTests
{
    private const string Table = "usage_price";
    private const string LedgerTable = "usage_ledger_entry";
    private const string MigrationFile = "0073_usage_price.sql";

    private readonly PostgresTenancyFixture _fixture;

    public UsagePriceSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ (a) the migration and the contract shape

    [Fact]
    public async Task Migration_IsJournalled_AndTheTableDeclaresTheContractShape()
    {
        var database = await _fixture.SharedDatabaseAsync();

        await AssertMigrationIsJournalledAsync(database);
        await AssertContractConstraintsAsync(database);
        await AssertCardIndexesAndPoliciesAsync(database);
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

    /// <summary>The journal row, the table itself and every column of the declared contract shape.</summary>
    private async Task AssertMigrationIsJournalledAsync(string database)
    {
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
    }

    /// <summary>The keys, the composite tenant key, the closed-value checks and the absence of a foreign key.</summary>
    private async Task AssertContractConstraintsAsync(string database)
    {
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
    }

    /// <summary>The card lookup indexes, the forced tenant boundary, its three policies and its two triggers.</summary>
    private async Task AssertCardIndexesAndPoliciesAsync(string database)
    {
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
}
