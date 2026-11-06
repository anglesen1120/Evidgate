using System.Globalization;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// 02.9.1-T2 (story 02.9.1, area plan 02 §4.5/§7, 00 §5.4.1/§5.4.6): the composite
/// foreign key layer of tenant isolation, asserted against the live catalogue and
/// the real database — not against a list inside the test.
/// <para>
/// The tenant set is auto-discovered: every table in the catalogue carrying
/// <c>org_id</c>, minus the tables <c>contracts/rls-exceptions.txt</c> names
/// (02.6.1-T1.4, the single source of the 02 §4.5 exception list). A table added by
/// a later migration is therefore covered the moment it lands, and one that misses
/// its composite FK fails this suite with its own name in the message — which is
/// the extensibility contract of 02.9.1 §7 ("no manual list to forget").
/// </para>
/// <para>
/// Every insert goes through <c>evidgate_app</c> inside an
/// <c>ITenantScope</c> transaction (00 §5.5 C3), never the owner: with FORCE row
/// level security the owner's policy is permissive, so an owner insert would prove
/// nothing about the path the API uses. The one exception is a table in
/// <see cref="TenantRowFactory.OwnerProvisionedTables"/> (<c>healing_llm_budget</c>,
/// whose NOT NULL caps <c>0054</c> keeps outside the app role's INSERT grant): its
/// rows are generated on the owner connection, because the app role cannot insert
/// them at all. That does not weaken the case — a composite foreign key is a schema
/// constraint, so its rejection is the same statement for either role, and the
/// corrupt row still differs from the valid one only in which org's parent it names.
/// The corrupt insert is filed under org B
/// <em>while the tenant setting is org B</em>, so the row-level-security
/// <c>with check</c> passes (the tenant setting for the app role, the owner backfill
/// policy on the owner path) and the rejection can only be the composite foreign key
/// — asserted by SQLSTATE and by the constraint's own name.
/// <para>
/// That ordering is load-bearing and must not be "simplified": with the tenant
/// setting left at org A and the row filed under org B, the <c>with check</c> half
/// of <c>*_app_tenant</c> fires first (SQLSTATE 42501) and the case would prove the
/// RLS policy, not the composite FK — a tautology as far as this suite is
/// concerned. The tenant setting is switched with the same
/// <c>set_config(…, true)</c> the scope opened with (transaction-local, 00 §5.4.2),
/// so the corrupt row is otherwise the valid org-B row inserted just
/// before it: same shape, same constraints, different parent. When the corrupted
/// parent is not part of one of the row's unique keys, the generator gives one of
/// the row's other same-org parents a second row of its own
/// (<see cref="TenantRowFactory"/>), so the unique key never answers first and the
/// composite foreign key is the rejection.
/// </para>
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class CompositeFkTests : HarnessTest
{
    /// <summary>Two orgs, as in the rest of the isolation suite; the ids are the organizations' own ids (TL T4).</summary>
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-7222-8222-222222222222");

    /// <summary>The tenant setting the parameterised <c>set_config</c> of the tenant scope writes (00 §5.4.2).</summary>
    private const string TenantSetting = "evidgate.org_id";

    /// <summary>
    /// Cross-org guarded catalogue keys whose corrupt row this suite cannot generate. <c>report_parser</c> is the
    /// owner-written catalogue (0075 revokes INSERT from <c>evidgate_app</c>), so the other org's project-owned
    /// release the corrupt <c>raw_report</c> or <c>raw_report_reprocess</c> row must name cannot be created through
    /// the application role this matrix runs as. <c>RawReportSchemaTests</c> exercises that guard: an owner-seeded
    /// org-A release and an org-B insert naming it, refused with 23514 <c>raw_report_parser_invisible</c>.
    /// </summary>
    private static readonly HashSet<(string Table, string Constraint)> GuardedCatalogueNotGeneratable =
        [("public.raw_report", "raw_report_parser_fk"), ("public.raw_report_reprocess", "raw_report_reprocess_parser_fk")];

    private readonly ITestOutputHelper _output;

    /// <summary>Binds the test to the shared harness fixture (container, migrations, Respawn).</summary>
    public CompositeFkTests(DatabaseFixture fixture, ITestOutputHelper output)
        : base(fixture)
    {
        _output = output;
    }

    /// <summary>
    /// AC3 (composite-FK half) and T2.1's "Done when": for every tenant table the
    /// catalogue yields, the suite generates a valid row (creating its parents
    /// recursively) and proves a row filed under another org pointing at this org's
    /// parent is rejected by the composite FK.
    /// <list type="bullet">
    /// <item><description>
    /// A tenant table with a foreign key to a parent that carries <c>org_id</c>
    /// (today <c>public.idempotency_record</c> → <c>public.project</c>) gets the
    /// behavioural case: the same row shape is valid for its own org (both orgs are
    /// inserted first, so nothing but the parent pointer differs) and is then
    /// rejected with SQLSTATE 23503 and the composite constraint's name.
    /// </description></item>
    /// <item><description>
    /// A tenant table with no such parent (<c>public.project</c>, whose only parent
    /// is the <c>organization</c> root; <c>public.rls_probe</c>, whose 0003 template
    /// has no foreign key at all) has no parent pointer that could name another
    /// org's row. Its safety is the structural rule the scan asserts for the whole
    /// tenant set, and the valid rows of both orgs are still inserted so the
    /// generator and the table's own tenant path are exercised.
    /// </description></item>
    /// </list>
    /// The structural rules (tenant key, composite tenant FKs, out-of-set references
    /// through the org column) are asserted for every tenant table in one place:
    /// <c>TenantFkScan.Findings</c> must be empty, and its message names each
    /// violating table.
    /// </summary>
    [Fact]
    public async Task Insert_ForeignOrgId_Rejected()
    {
        var scan = await ScanAsync();

        scan.Exceptions.ShouldNotBeEmpty(
            $"{RlsExceptionList.PathInRepo} listed no table — the suite cannot derive the tenant set without it");
        scan.TenantTables.ShouldNotBeEmpty(
            "the catalogue yielded no tenant table (a table with org_id minus the exception list): the suite would prove nothing");

        var coverage = string.Join(", ", scan.TenantTables);
        scan.Findings.ShouldBeEmpty(
            $"the tenant set [{coverage}] breaks the composite-FK rules:{Environment.NewLine}{TenantFkScan.Describe(scan.Findings)}");

        // The excluded allowance is only documented, not exercised, while its guard exists: dropping the guard
        // while keeping the catalogue key would fail here instead of silently leaving the reference unguarded.
        TenantFkScan.HasCrossOrgGuard("public.raw_report", "raw_report_parser_fk").ShouldBeTrue(
            "raw_report_parser_fk is excluded from the corruption matrix on the strength of raw_report_parser_visible");
        TenantFkScan.HasCrossOrgGuard("public.raw_report_reprocess", "raw_report_reprocess_parser_fk").ShouldBeTrue(
            "raw_report_reprocess_parser_fk is excluded from the corruption matrix on the strength of "
            + "raw_report_reprocess_parser_visible");

        var attempts = 0;
        var structural = new List<string>();
        var rejected = new List<string>();

        foreach (var table in scan.TenantTables)
        {
            // A tenant parent is a key to another table of the tenant set. A catalogue principal
            // (`report_parser`, `usage_price`) carries a nullable `org_id` for its platform rows, so a
            // composite key could never match one; a tenant table's key to it is TenantFkScan's documented
            // catalogue allowance. Only the allowance that names a guard on the referencing table can be
            // exercised here: the corrupt row is built and the guard is what must refuse it — except where the
            // catalogued parent is owner-written, which the exclusion above records.
            var tenantParents = scan.Schema
                .ForeignKeysOf(table)
                .Where(foreignKey => scan.TenantTables.Contains(foreignKey.PrincipalTable)
                    || (TenantFkScan.HasCrossOrgGuard(table, foreignKey.Name)
                        && !GuardedCatalogueNotGeneratable.Contains((table, foreignKey.Name))))
                .ToArray();

            if (tenantParents.Length == 0)
            {
                // No tenant parent: nothing to corrupt. Both orgs' rows are still
                // generated and inserted, which is what proves the generator resolves
                // this table's NOT NULL columns, checks and FK parents at all.
                await using var scope = await TenantScope.BeginAsync(ConnectionFor(table), OrgA);
                var factory = new TenantRowFactory(scan.Schema, (sql, parameters) => scope.ExecuteAsync(sql, parameters));
                await factory.CreateAsync(table, OrgA, Token);
                await scope.SetAsync(TenantSetting, OrgB.ToString("D", CultureInfo.InvariantCulture));
                await factory.CreateAsync(table, OrgB, Token);

                structural.Add(table);
                continue;
            }

            foreach (var tenantParent in tenantParents)
            {
                attempts++;

                // One transaction per attempt: the rejected insert aborts its
                // transaction (25P02), so each corruption gets its own.
                await using var scope = await TenantScope.BeginAsync(ConnectionFor(table), OrgA);
                var factory = new TenantRowFactory(scan.Schema, (sql, parameters) => scope.ExecuteAsync(sql, parameters));

                // A nullable key is left null in a valid row (it may close a cycle, project <-> environment),
                // so its parent is created explicitly for the org the corrupt row will point at.
                if (tenantParent.Columns.Any(column => !scan.Schema.ColumnsOf(table).Single(c => c.Name == column).NotNull))
                {
                    await factory.CreateAsync(tenantParent.PrincipalTable, OrgA, Token);
                }

                // A valid row for each org, so the corrupt row below differs from a
                // row the database accepts only in which org's parent it points at.
                await factory.CreateAsync(table, OrgA, Token);
                await scope.SetAsync(TenantSetting, OrgB.ToString("D", CultureInfo.InvariantCulture));
                await factory.CreateAsync(table, OrgB, Token);

                var corrupted = await factory.PrepareForeignOrgRowAsync(table, OrgB, OrgA, tenantParent.Name, Token);
                var error = await scope.CaptureErrorAsync(corrupted.InsertSql, corrupted.Parameters);

                error.SqlState.ShouldBe(
                    PostgresErrorCodes.ForeignKeyViolation,
                    $"{table}: a row filed under org {OrgB} pointing at org {OrgA}'s {tenantParent.PrincipalTable} row must be "
                    + $"rejected by the composite foreign key '{tenantParent.Name}' (SQLSTATE 23503), got {error.SqlState} "
                    + $"({error.MessageText}) — row level security passed on the seeding path (the tenant setting was org {OrgB} "
                    + "for the app role, the owner backfill policy on the owner path of an owner-provisioned table), "
                    + "so this rejection can only be the foreign key");
                error.ConstraintName.ShouldBeOneOf(
                    ExpectedConstraints(table, tenantParent.Name),
                    $"{table}: the rejection must come from the composite foreign key '{tenantParent.Name}' "
                    + $"({string.Join(", ", tenantParent.Columns)} → {tenantParent.PrincipalTable} "
                    + $"({string.Join(", ", tenantParent.PrincipalColumns)})), not from '{error.ConstraintName}'");

                rejected.Add($"{table} → {tenantParent.PrincipalTable} ({tenantParent.Name})");
            }
        }

        attempts.ShouldBeGreaterThan(
            0,
            $"no tenant table in [{coverage}] has a foreign key to another tenant table, so the behavioural half of T2.1 never ran");

        _output.WriteLine(
            "composite-FK gate: tenant tables [{0}]; rejected cross-org inserts: {1}; structural-only: [{2}]; excluded by {3}: [{4}]",
            coverage,
            string.Join(", ", rejected),
            string.Join(", ", structural),
            RlsExceptionList.PathInRepo,
            string.Join(", ", scan.Excluded.Select(entry => $"{entry.Table} ({entry.Kind})")));
    }

    /// <summary>
    /// The constraint names a corrupt row's rejection may carry. <c>raw_report</c> and <c>raw_report_reprocess</c>
    /// name their run twice — the run foreign key and the <c>(org_id, artifact_id, run_id)</c> pairing — so a row
    /// whose run is moved to the other org breaks both references, and PostgreSQL answers with whichever check it
    /// reaches first. A table with one foreign key to the moved parent has exactly one expected name.
    /// </summary>
    private static string[] ExpectedConstraints(string table, string corrupted)
    {
        var paired = (table, corrupted) switch
        {
            ("public.raw_report", "raw_report_run_fk") => "raw_report_artifact_fk",
            ("public.raw_report_reprocess", "raw_report_reprocess_run_fk") => "raw_report_reprocess_artifact_fk",
            _ => null,
        };

        return paired is null ? [corrupted] : [corrupted, paired];
    }

    /// <summary>
    /// The connection one table's generated rows run on: <c>evidgate_app</c> for every tenant table whose
    /// INSERT grant covers the NOT NULL columns the generator fills, and <c>evidgate_owner</c> for a table
    /// in <see cref="TenantRowFactory.OwnerProvisionedTables"/> — <c>0054</c> keeps
    /// <c>healing_llm_budget</c>'s caps outside the app role's grant, so only the provisioning path can
    /// create its row. The composite FK asserted above is a schema constraint, unaffected by the role.
    /// </summary>
    private string ConnectionFor(string table) =>
        TenantRowFactory.OwnerProvisionedTables.Contains(table)
            ? Fixture.OwnerConnectionString
            : Fixture.AppConnectionString;

    /// <summary>
    /// T2.2, first negative fixture: a scratch tenant table whose foreign key to
    /// another tenant table is plain (single-column, missing <c>org_id</c>) must be
    /// rejected by the suite's own detection routine, with the table named. The
    /// parent is a legal composite-FK target (<c>unique (org_id, id)</c> plus a
    /// separate <c>unique (id)</c>), so the single-column key is valid SQL and only
    /// the tenant rule rejects it.
    /// </summary>
    [Fact]
    public async Task NegativeFixture_NonCompositeForeignKey_IsDetected()
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        await using (var transaction = await connection.BeginTransactionAsync(Token))
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                create table public.composite_fk_fixture_parent (
                  org_id uuid not null,
                  id uuid not null,
                  label text not null,
                  constraint composite_fk_fixture_parent_pkey primary key (org_id, id),
                  constraint composite_fk_fixture_parent_id_key unique (id)
                );
                create table public.composite_fk_fixture_child (
                  org_id uuid not null,
                  id uuid not null,
                  parent_id uuid not null,
                  constraint composite_fk_fixture_child_pkey primary key (org_id, id),
                  constraint composite_fk_fixture_child_plain_fk
                    foreign key (parent_id) references public.composite_fk_fixture_parent (id)
                );
                """);

            var scan = await TenantFkScan.RunAsync(connection, Token, transaction);

            scan.TenantTables.ShouldContain("public.composite_fk_fixture_child");

            var finding = scan.Findings.ShouldHaveSingleItem();
            finding.Table.ShouldBe("public.composite_fk_fixture_child");
            finding.Rule.ShouldBe(TenantFkRule.CompositeForeignKey);
            finding.Detail.ShouldContain("composite_fk_fixture_child_plain_fk");
            finding.Detail.ShouldContain("org_id");

            // The failure the suite would raise names the table, not just the rule.
            TenantFkScan.Describe(scan.Findings).ShouldContain("public.composite_fk_fixture_child");
        }

        // The fixture is rolled back with its transaction: a violating table can never
        // reach the migrated schema, which is what makes the fixture safe to run
        // against the same database the rest of the suite asserts on.
        (await ScalarAsync(connection, "select to_regclass('public.composite_fk_fixture_child')::text")).ShouldBeNull();
    }

    /// <summary>
    /// T2.2, second negative fixture: a scratch tenant table without
    /// <c>unique (org_id, id)</c> cannot be the target of a composite tenant FK at
    /// all, so the same detection routine must reject it and name the table. The
    /// missing key is necessarily detected structurally, on the table that lacks it:
    /// PostgreSQL refuses to declare a composite FK against a keyless target, so
    /// there is no child row to create for that half of the rule.
    /// </summary>
    [Fact]
    public async Task NegativeFixture_MissingTenantKey_IsDetected()
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        await using (var transaction = await connection.BeginTransactionAsync(Token))
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                create table public.composite_fk_fixture_no_key (
                  org_id uuid not null,
                  id uuid not null,
                  label text not null,
                  constraint composite_fk_fixture_no_key_pkey primary key (id)
                );
                """);

            var scan = await TenantFkScan.RunAsync(connection, Token, transaction);

            scan.TenantTables.ShouldContain("public.composite_fk_fixture_no_key");

            var finding = scan.Findings.ShouldHaveSingleItem();
            finding.Table.ShouldBe("public.composite_fk_fixture_no_key");
            finding.Rule.ShouldBe(TenantFkRule.TenantKey);
            finding.Detail.ShouldContain("(org_id, id)");

            TenantFkScan.Describe(scan.Findings).ShouldContain("public.composite_fk_fixture_no_key");
        }

        (await ScalarAsync(connection, "select to_regclass('public.composite_fk_fixture_no_key')::text")).ShouldBeNull();
    }

    /// <summary>
    /// The third negative fixture, pinning the two properties the first two cannot
    /// reach: (a) the exception list scopes the <em>dependent</em> side only, so
    /// <c>public.role_permission</c>'s single-column key to <c>public.role</c> (0010,
    /// deliberately exempted in <c>SchemaConventionScan</c> because built-in rows
    /// have <c>org_id</c> NULL) stays unreported, while a tenant table doing the same
    /// to an org-scoped parent is a finding; and (b) the finding an org-scoped parent
    /// produces is the composite-key rule, whose message names the fix plan 02 §4.1
    /// requires for a future <c>membership.role_id → role (org_id, id)</c> — not the
    /// org-column rule, whose "point at it through your own org_id" wording would be
    /// the wrong instruction for a catalogue parent.
    /// </summary>
    [Fact]
    public async Task NegativeFixture_CatalogueParent_WithoutCompositeKey_IsDetected()
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        await using (var transaction = await connection.BeginTransactionAsync(Token))
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                create table public.composite_fk_fixture_catalogue_child (
                  org_id uuid not null,
                  id uuid not null,
                  role_id uuid not null,
                  constraint composite_fk_fixture_catalogue_child_pkey primary key (org_id, id),
                  constraint composite_fk_fixture_catalogue_child_role_fk
                    foreign key (role_id) references public.role (id)
                );
                """);

            var scan = await TenantFkScan.RunAsync(connection, Token, transaction);

            // The child is a tenant table; the catalogue parent is not — the
            // dependent side is the exception-subtracted tenant set.
            scan.TenantTables.ShouldContain("public.composite_fk_fixture_catalogue_child");
            scan.TenantTables.ShouldNotContain("public.role");
            scan.Excluded.ShouldContain(entry => entry.Table == "public.role" && entry.Kind == "catalogue");

            var finding = scan.Findings.ShouldHaveSingleItem();
            finding.Table.ShouldBe("public.composite_fk_fixture_catalogue_child");
            finding.Rule.ShouldBe(TenantFkRule.CompositeForeignKey);
            finding.Detail.ShouldContain("composite_fk_fixture_catalogue_child_role_fk");
            finding.Detail.ShouldContain("org_id");

            TenantFkScan.Describe(scan.Findings).ShouldContain("public.composite_fk_fixture_catalogue_child");
        }

        (await ScalarAsync(connection, "select to_regclass('public.composite_fk_fixture_catalogue_child')::text"))
            .ShouldBeNull();
    }

    /// <summary>
    /// The fourth negative fixture, pinning the half the first three cannot reach: a two-column key to an
    /// org-scoped parent must pair <c>org_id</c> with the parent's <c>org_id</c> position by position. The
    /// parent's <c>(org_id, id)</c> primary key is a legal target whatever order the columns are named in,
    /// so <c>(org_id, parent_id) → (id, org_id)</c> is valid SQL that references the same column set while
    /// pairing this row's org with the parent's row id — it can never match a row, and it is exactly the
    /// shape the narrow-unique-key allowance of 0076's <c>usage_duplicate_attempt_ledger_fk</c> must not
    /// bless: the allowance adds "references the parent's own unique key" as an alternative to the
    /// <c>(org_id, id)</c> pairing, never as a replacement for the <c>org_id</c> pairing itself.
    /// </summary>
    [Fact]
    public async Task NegativeFixture_MispairedOrgKey_IsDetected()
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        await using (var transaction = await connection.BeginTransactionAsync(Token))
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                create table public.composite_fk_fixture_mispaired_parent (
                  org_id uuid not null,
                  id uuid not null,
                  label text not null,
                  constraint composite_fk_fixture_mispaired_parent_pkey primary key (org_id, id)
                );
                create table public.composite_fk_fixture_mispaired_child (
                  org_id uuid not null,
                  id uuid not null,
                  parent_id uuid not null,
                  constraint composite_fk_fixture_mispaired_child_pkey primary key (org_id, id),
                  constraint composite_fk_fixture_mispaired_child_mispaired_fk
                    foreign key (org_id, parent_id)
                    references public.composite_fk_fixture_mispaired_parent (id, org_id)
                );
                """);

            var scan = await TenantFkScan.RunAsync(connection, Token, transaction);

            scan.TenantTables.ShouldContain("public.composite_fk_fixture_mispaired_child");
            scan.TenantTables.ShouldContain("public.composite_fk_fixture_mispaired_parent");

            // The parent is a legal composite-FK target (its primary key is the tenant key), so only the
            // mispaired columns are a finding — never the parent.
            var finding = scan.Findings.ShouldHaveSingleItem();
            finding.Table.ShouldBe("public.composite_fk_fixture_mispaired_child");
            finding.Rule.ShouldBe(TenantFkRule.CompositeForeignKey);
            finding.Detail.ShouldContain("composite_fk_fixture_mispaired_child_mispaired_fk");
            finding.Detail.ShouldContain("org_id");

            TenantFkScan.Describe(scan.Findings).ShouldContain("public.composite_fk_fixture_mispaired_child");
        }

        (await ScalarAsync(connection, "select to_regclass('public.composite_fk_fixture_mispaired_child')::text"))
            .ShouldBeNull();
    }

    /// <summary>Reads the catalogue through the app role, exactly as the suite's own read path does.</summary>
    private async Task<TenantFkScanResult> ScanAsync()
    {
        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);
        return await TenantFkScan.RunAsync(connection, Token);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>One scalar on the owner connection, outside any transaction (used after a fixture rollback).</summary>
    private static async Task<string?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(Token);
        return value is null or DBNull ? null : (string)value;
    }
}
