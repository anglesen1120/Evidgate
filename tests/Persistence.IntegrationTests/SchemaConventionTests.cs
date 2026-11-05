using Npgsql;
using Shouldly;
using Xunit;

namespace Persistence.IntegrationTests;

/// <summary>
/// 01.4.2-T2.2 (AC5): the schema conventions of 00 §5.4.1/§5.4.4/§5.4.6 and §7
/// asserted on the migrated database, not on a document — every business table
/// carries <c>unique (org_id, id)</c>, composite tenant foreign keys, ENABLE +
/// FORCE row level security and the 00 §7 columns, and every table with a
/// <c>version</c> column carries the <c>set_updated_at_and_version</c> trigger
/// (M1W3-01). Each rule is also proven to fail: deliberately violating fixture
/// tables are created inside a test transaction that is rolled back, so nothing
/// ever reaches a migrated database, and the same scan function sees both.
/// </summary>
[Collection(PersistenceSuite.Name)]
[Trait("Category", "Docker")]
public sealed class SchemaConventionTests
{
    private readonly PostgresPersistenceFixture _fixture;

    public SchemaConventionTests(PostgresPersistenceFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The migrated schema (0001–0005, plus whatever later slice migrations the
    /// branch carries) passes every rule. The scan is not vacuous: it must have
    /// seen the real tables, the business set must contain the tables that carry
    /// <c>org_id</c>, and it must exclude the two documented exceptions — the
    /// <c>organization</c> root (TL T4: it has no <c>org_id</c> by design) and the
    /// mechanism tables of the allow-list.
    /// </summary>
    [Fact]
    public async Task Migrated_Schema_Satisfies_Conventions()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await _fixture.OpenSuperuserAsync(database);

        var scan = await SchemaConventionScan.RunAsync(connection, Token);

        scan.Violations.ShouldBeEmpty(string.Join(" | ", scan.Violations));
        scan.Tables.ShouldContain("public.project");
        scan.Tables.ShouldContain("public.rls_probe");
        scan.Tables.ShouldContain("public.organization");
        scan.Tables.ShouldContain("public.schema_migration");
        scan.BusinessTables.ShouldContain("public.project");
        scan.BusinessTables.ShouldContain("public.rls_probe");

        // The org root is not a tenant table (`org_id` would reference the row
        // itself), so the org_id rule keeps it out of the scan. The mechanism tables
        // are exempt only from the rules their design breaks — RLS, the audit columns
        // and the version trigger for outbox, everything for the journal (TL T3's 01
        // exceptions) — and outbox keeps its tenant key checked (TL T3 does not
        // exempt it, 0006 declares outbox_org_id_uq). public.task is in the business
        // set: it carries org_id (nullable for platform tasks), so its AuditColumns
        // and VersionTrigger stay checked while the tenant-key, composite-FK and RLS
        // rules are excused.
        scan.BusinessTables.ShouldNotContain("public.organization");
        scan.BusinessTables.ShouldContain("public.outbox");
        scan.BusinessTables.ShouldContain("public.task");
        scan.BusinessTables.ShouldNotContain("public.schema_migration");
    }

    /// <summary>
    /// The compliant companion table passes: it carries the tenant key, the columns,
    /// the trigger, FORCE row security and a composite <c>(org_id, project_id)</c> FK
    /// to <c>public.project</c>. Without this the violating tests below could pass
    /// because the scan fails every fixture table.
    /// </summary>
    [Fact]
    public async Task Compliant_Fixture_Passes()
    {
        var scan = await ScanFixtureAsync(violatingSql: null);

        FixtureViolations(scan).ShouldBeEmpty(string.Join(" | ", FixtureViolations(scan)));
        scan.BusinessTables.ShouldContain("fixture_conventions.compliant");
    }

    /// <summary>AC5: a business table without <c>unique (org_id, id)</c> fails (00 §5.4.6).</summary>
    [Fact]
    public async Task Missing_Unique_OrgId_Id_Fails()
    {
        var scan = await ScanFixtureAsync(
            """
            create table fixture_conventions.missing_tenant_key (
              org_id uuid not null,
              id uuid not null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              version integer not null default 1,
              constraint missing_tenant_key_pkey primary key (id)
            );
            alter table fixture_conventions.missing_tenant_key enable row level security;
            alter table fixture_conventions.missing_tenant_key force row level security;
            create trigger missing_tenant_key_set_updated_at_and_version
              before update on fixture_conventions.missing_tenant_key
              for each row execute function public.set_updated_at_and_version();
            """);

        var violation = OnlyViolation(scan, "fixture_conventions.missing_tenant_key", SchemaRule.TenantKey);
        violation.Detail.ShouldContain("(org_id, id)");
    }

    /// <summary>
    /// AC5: a foreign key from a business table to another business table that is
    /// not composite — here single-column, so it lacks the org column — fails
    /// (00 §5.4.1/§5.4.6). The key is valid SQL (it references <c>project_pkey</c>);
    /// the scan is what rejects the cross-tenant shape.
    /// </summary>
    [Fact]
    public async Task Non_Composite_FK_Fails()
    {
        var scan = await ScanFixtureAsync(
            """
            create table fixture_conventions.non_composite_fk (
              org_id uuid not null,
              id uuid not null,
              project_id uuid not null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              version integer not null default 1,
              constraint non_composite_fk_pkey primary key (org_id, id),
              constraint non_composite_fk_project_fk
                foreign key (project_id) references public.project (id)
            );
            alter table fixture_conventions.non_composite_fk enable row level security;
            alter table fixture_conventions.non_composite_fk force row level security;
            create trigger non_composite_fk_set_updated_at_and_version
              before update on fixture_conventions.non_composite_fk
              for each row execute function public.set_updated_at_and_version();
            """);

        var violation = OnlyViolation(scan, "fixture_conventions.non_composite_fk", SchemaRule.CompositeForeignKey);
        violation.Detail.ShouldContain("non_composite_fk_project_fk");
        violation.Detail.ShouldContain("org_id");
    }

    /// <summary>DECISIONS F5: ENABLE without FORCE is not enough — the owner would bypass every policy.</summary>
    [Fact]
    public async Task Rls_Must_Be_Forced()
    {
        var scan = await ScanFixtureAsync(
            """
            create table fixture_conventions.rls_not_forced (
              org_id uuid not null,
              id uuid not null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              version integer not null default 1,
              constraint rls_not_forced_pkey primary key (org_id, id)
            );
            alter table fixture_conventions.rls_not_forced enable row level security;
            create trigger rls_not_forced_set_updated_at_and_version
              before update on fixture_conventions.rls_not_forced
              for each row execute function public.set_updated_at_and_version();
            """);

        OnlyViolation(scan, "fixture_conventions.rls_not_forced", SchemaRule.RowLevelSecurity)
            .Detail.ShouldContain("FORCED");
    }

    /// <summary>
    /// DECISIONS F5, the other half: PostgreSQL accepts
    /// <c>force row level security</c> on a table whose RLS is not enabled, and that
    /// table applies no policy at all — the strictest-looking migration is the
    /// widest hole, so FORCE alone fails the gate.
    /// </summary>
    [Fact]
    public async Task Rls_Forced_Without_Enabled_Fails()
    {
        var scan = await ScanFixtureAsync(
            """
            create table fixture_conventions.forced_not_enabled (
              org_id uuid not null,
              id uuid not null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              version integer not null default 1,
              constraint forced_not_enabled_pkey primary key (org_id, id)
            );
            alter table fixture_conventions.forced_not_enabled force row level security;
            create trigger forced_not_enabled_set_updated_at_and_version
              before update on fixture_conventions.forced_not_enabled
              for each row execute function public.set_updated_at_and_version();
            """);

        var violation = OnlyViolation(scan, "fixture_conventions.forced_not_enabled", SchemaRule.RowLevelSecurity);
        violation.Detail.ShouldContain("not ENABLED");
    }

    /// <summary>
    /// M1W3-01 / 00 §7: a table with a <c>version</c> column and no
    /// <c>set_updated_at_and_version</c> trigger fails — the token would never move.
    /// </summary>
    [Fact]
    public async Task Missing_Version_Trigger_Fails()
    {
        var scan = await ScanFixtureAsync(
            """
            create table fixture_conventions.missing_version_trigger (
              org_id uuid not null,
              id uuid not null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              version integer not null default 1,
              constraint missing_version_trigger_pkey primary key (org_id, id)
            );
            alter table fixture_conventions.missing_version_trigger enable row level security;
            alter table fixture_conventions.missing_version_trigger force row level security;
            """);

        OnlyViolation(scan, "fixture_conventions.missing_version_trigger", SchemaRule.VersionTrigger)
            .Detail.ShouldContain("set_updated_at_and_version");
    }

    /// <summary>
    /// M1W3-01 / 00 §7, the trigger's reach: a correct function, event, timing and
    /// granularity is not enough — the trigger must cover every UPDATE. A
    /// column-scoped `before update of version` trigger never fires for the update
    /// EF issues (it does not write the store-generated `version`), so the token
    /// would silently stay put while the migration reads as if it were wired.
    /// </summary>
    [Fact]
    public async Task Column_Scoped_Version_Trigger_Fails()
    {
        var scan = await ScanFixtureAsync(
            """
            create table fixture_conventions.column_scoped_trigger (
              org_id uuid not null,
              id uuid not null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              version integer not null default 1,
              constraint column_scoped_trigger_pkey primary key (org_id, id)
            );
            alter table fixture_conventions.column_scoped_trigger enable row level security;
            alter table fixture_conventions.column_scoped_trigger force row level security;
            create trigger column_scoped_trigger_set_updated_at_and_version
              before update of version on fixture_conventions.column_scoped_trigger
              for each row execute function public.set_updated_at_and_version();
            """);

        OnlyViolation(scan, "fixture_conventions.column_scoped_trigger", SchemaRule.VersionTrigger)
            .Detail.ShouldContain("set_updated_at_and_version");
    }

    /// <summary>
    /// The other way a correct trigger stops working without being dropped: it is
    /// disabled. `alter table … disable trigger` leaves the definition (and a
    /// migration diff) intact while the token never moves, so the rule has to read
    /// `pg_trigger.tgenabled`.
    /// </summary>
    [Fact]
    public async Task Disabled_Version_Trigger_Fails()
    {
        var scan = await ScanFixtureAsync(
            """
            create table fixture_conventions.disabled_trigger (
              org_id uuid not null,
              id uuid not null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              version integer not null default 1,
              constraint disabled_trigger_pkey primary key (org_id, id)
            );
            alter table fixture_conventions.disabled_trigger enable row level security;
            alter table fixture_conventions.disabled_trigger force row level security;
            create trigger disabled_trigger_set_updated_at_and_version
              before update on fixture_conventions.disabled_trigger
              for each row execute function public.set_updated_at_and_version();
            alter table fixture_conventions.disabled_trigger disable trigger all;
            """);

        OnlyViolation(scan, "fixture_conventions.disabled_trigger", SchemaRule.VersionTrigger)
            .Detail.ShouldContain("set_updated_at_and_version");
    }

    /// <summary>
    /// AC5/00 §5.4.1, the pairing half: PostgreSQL matches a referenced column list
    /// to a unique index regardless of the declared order, so
    /// `(org_id, project_id) references project (id, org_id)` is accepted DDL that
    /// pairs org_id with the target's id — a set comparison of the referenced
    /// columns would call it a tenant FK, and the pairing check must not.
    /// </summary>
    [Fact]
    public async Task Mispaired_Composite_FK_Fails()
    {
        var scan = await ScanFixtureAsync(
            """
            create table fixture_conventions.mispaired_fk (
              org_id uuid not null,
              id uuid not null,
              project_id uuid not null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              version integer not null default 1,
              constraint mispaired_fk_pkey primary key (org_id, id),
              constraint mispaired_fk_project_fk
                foreign key (org_id, project_id) references public.project (id, org_id)
            );
            alter table fixture_conventions.mispaired_fk enable row level security;
            alter table fixture_conventions.mispaired_fk force row level security;
            create trigger mispaired_fk_set_updated_at_and_version
              before update on fixture_conventions.mispaired_fk
              for each row execute function public.set_updated_at_and_version();
            """);

        var violation = OnlyViolation(scan, "fixture_conventions.mispaired_fk", SchemaRule.CompositeForeignKey);
        violation.Detail.ShouldContain("mispaired_fk_project_fk");
        violation.Detail.ShouldContain("org_id");
    }

    /// <summary>
    /// AC5/00 §5.4.1, the wide half: a tenant key with more than two columns is a
    /// tenant key when its principal columns include <c>org_id</c>, the dependent's
    /// <c>org_id</c> pairs with the principal's <c>org_id</c>, and the principal
    /// columns are a unique key of the principal — 0032's
    /// <c>case_attempt_planned_execution_fk (org_id, run_id, execution_key) →
    /// planned_execution (org_id, run_id, execution_key)</c> is exactly that shape,
    /// and the migrated-schema test alone would not prove the scan accepts it.
    /// </summary>
    [Fact]
    public async Task Wide_Composite_Tenant_FK_Passes()
    {
        var scan = await ScanFixtureAsync(
            """
            create table fixture_conventions.wide_tenant_fk (
              org_id uuid not null,
              id uuid not null,
              run_id uuid not null,
              execution_key text not null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              version integer not null default 1,
              constraint wide_tenant_fk_pkey primary key (org_id, id),
              constraint wide_tenant_fk_planned_fk
                foreign key (org_id, run_id, execution_key)
                references public.planned_execution (org_id, run_id, execution_key)
            );
            alter table fixture_conventions.wide_tenant_fk enable row level security;
            alter table fixture_conventions.wide_tenant_fk force row level security;
            create trigger wide_tenant_fk_set_updated_at_and_version
              before update on fixture_conventions.wide_tenant_fk
              for each row execute function public.set_updated_at_and_version();
            """);

        FixtureViolations(scan).ShouldBeEmpty(string.Join(" | ", FixtureViolations(scan)));
        scan.BusinessTables.ShouldContain("fixture_conventions.wide_tenant_fk");
    }

    /// <summary>
    /// AC5/00 §5.4.1, the wide half's negative: the relaxation must not become a
    /// blanket exemption for any key with more than two columns. Here the principal
    /// columns are still a unique key of <c>public.planned_execution</c> (PostgreSQL
    /// matches a referenced column list to a unique index regardless of the declared
    /// order, so this is valid DDL), but the dependent's <c>org_id</c> pairs with the
    /// principal's <c>run_id</c> — a key that can never match a row of the
    /// dependent's own org, so it must still be reported.
    /// </summary>
    [Fact]
    public async Task Mispaired_Wide_Composite_FK_Fails()
    {
        var scan = await ScanFixtureAsync(
            """
            create table fixture_conventions.mispaired_wide_fk (
              org_id uuid not null,
              id uuid not null,
              run_id uuid not null,
              execution_key text not null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              version integer not null default 1,
              constraint mispaired_wide_fk_pkey primary key (org_id, id),
              constraint mispaired_wide_fk_planned_fk
                foreign key (org_id, run_id, execution_key)
                references public.planned_execution (run_id, org_id, execution_key)
            );
            alter table fixture_conventions.mispaired_wide_fk enable row level security;
            alter table fixture_conventions.mispaired_wide_fk force row level security;
            create trigger mispaired_wide_fk_set_updated_at_and_version
              before update on fixture_conventions.mispaired_wide_fk
              for each row execute function public.set_updated_at_and_version();
            """);

        var violation = OnlyViolation(scan, "fixture_conventions.mispaired_wide_fk", SchemaRule.CompositeForeignKey);
        violation.Detail.ShouldContain("mispaired_wide_fk_planned_fk");
        violation.Detail.ShouldContain("org_id");
    }

    /// <summary>00 §7: <c>created_at</c>, <c>updated_at</c> and <c>version</c> are missing here, and all three are reported.</summary>
    [Fact]
    public async Task Missing_Audit_Columns_Fail()
    {
        var scan = await ScanFixtureAsync(
            """
            create table fixture_conventions.no_audit_columns (
              org_id uuid not null,
              id uuid not null,
              constraint no_audit_columns_pkey primary key (org_id, id)
            );
            alter table fixture_conventions.no_audit_columns enable row level security;
            alter table fixture_conventions.no_audit_columns force row level security;
            """);

        var violations = FixtureViolations(scan);
        violations.ShouldAllBe(violation => violation.Rule == SchemaRule.AuditColumns);
        violations.Count.ShouldBe(3);
        foreach (var column in new[] { "created_at", "updated_at", "version" })
        {
            violations.ShouldContain(violation => violation.Detail.Contains(column, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Creates the compliant companion table — and, when given, one violating table —
    /// inside one transaction that is rolled back, then runs the same scan the
    /// migrated-schema test runs. The transaction is what keeps these objects out of
    /// the migration history for good (01.4.2 §4: fixtures are never migrated).
    /// </summary>
    private async Task<SchemaScan> ScanFixtureAsync(string? violatingSql)
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await _fixture.OpenSuperuserAsync(database);
        await using var transaction = await connection.BeginTransactionAsync(Token);

        await ExecuteAsync(connection, transaction, CompliantFixtureSql);
        if (violatingSql is not null)
        {
            await ExecuteAsync(connection, transaction, violatingSql);
        }

        var scan = await SchemaConventionScan.RunAsync(connection, Token, transaction);

        // Belt and braces: every test owns its database, and the fixture tables are
        // rolled back so a scan of the migrated schema can never see them.
        await transaction.RollbackAsync(Token);

        return scan;
    }

    /// <summary>
    /// The fixture tables' violations only. Real-schema drift is
    /// <see cref="Migrated_Schema_Satisfies_Conventions"/>'s finding, so a later
    /// slice's migration cannot turn these rule tests red and hide the cause.
    /// </summary>
    private static List<SchemaViolation> FixtureViolations(SchemaScan scan) =>
        scan.Violations.Where(violation => violation.Table.StartsWith("fixture_conventions.", StringComparison.Ordinal)).ToList();

    private static SchemaViolation OnlyViolation(SchemaScan scan, string table, SchemaRule rule)
    {
        // The compliant companion is part of the same scan, so "exactly one" is also
        // the proof that a compliant table yields no false positive.
        var violations = FixtureViolations(scan);
        var violation = violations.ShouldHaveSingleItem(string.Join(" | ", violations));
        violation.Table.ShouldBe(table);
        violation.Rule.ShouldBe(rule);
        return violation;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The compliant table: every rule of the scan satisfied, including the composite tenant FK.</summary>
    private const string CompliantFixtureSql = """
        create schema fixture_conventions;

        create table fixture_conventions.compliant (
          org_id uuid not null,
          id uuid not null,
          project_id uuid,
          created_at timestamptz not null default now(),
          updated_at timestamptz not null default now(),
          version integer not null default 1,
          constraint compliant_pkey primary key (org_id, id),
          constraint compliant_project_fk
            foreign key (org_id, project_id) references public.project (org_id, id)
        );
        alter table fixture_conventions.compliant enable row level security;
        alter table fixture_conventions.compliant force row level security;
        create trigger compliant_set_updated_at_and_version
          before update on fixture_conventions.compliant
          for each row execute function public.set_updated_at_and_version();
        """;
}

/// <summary>
/// The allow-list of <see cref="SchemaConventionScan"/> is the one place where a
/// rule can be excused, so it is asserted offline and exactly: an entry added,
/// widened or dropped fails here rather than silently stopping a rule for a real
/// table. <c>rls_probe</c> is the only non-mechanism entry that predates the shared
/// trigger function (0003 is checksum-locked), and <c>role_permission</c> is the only
/// entry excused from a single rule for a reason a migration cannot fix (a built-in
/// role carries <c>org_id IS NULL</c>, so its composite tenant FK cannot exist).
/// </summary>
public sealed class SchemaConventionAllowListTests
{
    [Fact]
    public void AllowList_Is_Exactly_The_Documented_Exemptions()
    {
        SchemaConventionScan.Exemptions
            .Select(entry => (entry.Table, entry.Rules))
            .ShouldBe(
            [
                ("public.schema_migration", SchemaRule.All),
                ("public.outbox", SchemaRule.RowLevelSecurity | SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.task", SchemaRule.TenantKey | SchemaRule.CompositeForeignKey | SchemaRule.RowLevelSecurity),
                ("public.audit_log", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.case_identity_source", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.case_rename_history", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.test_revision", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.suite_revision", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.variant", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.suite_member", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.case_dependency", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.manifest", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.environment_revision", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.policy_revision", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.adapter_capability", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.planned_execution", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.case_attempt", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.event", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.event_batch", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.authoring_version", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.authoring_code_counter", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.rls_probe", SchemaRule.VersionTrigger),
                ("public.role_permission", SchemaRule.CompositeForeignKey),
                ("public.usage_ledger_entry", SchemaRule.CompositeForeignKey),
                ("public.evidence_revision", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.evaluation", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.project_ai_settings", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.ai_settings_template", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.manual_case_link", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.masking_event", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.report_parser", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.release_component", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.release_run", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.fixture", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
                ("public.fixture_revision", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
            ]);

        // Every exclusion carries its reason: an exemption without one is a silencer,
        // and the next reader cannot tell whether it is still needed.
        SchemaConventionScan.Exemptions.ShouldAllBe(entry => entry.Reason.Length > 0);

        // `outbox` keeps its tenant key: TL T3 lists only `schema_migration` and the
        // task queue as 01's exceptions to `unique (org_id, id)`.
        SchemaConventionScan.Exemptions.ShouldContain(
            entry => entry.Table == "public.outbox" && (entry.Rules & SchemaRule.TenantKey) == 0);

        // `role_permission` is excused from exactly one rule: its tenant key, RLS flags
        // and audit columns are checked like any other business table (02.2.1-T1.2).
        SchemaConventionScan.Exemptions.ShouldContain(
            entry => entry.Table == "public.role_permission" && entry.Rules == SchemaRule.CompositeForeignKey);
    }
}
