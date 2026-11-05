using Npgsql;

namespace Persistence.IntegrationTests;

/// <summary>
/// The catalogue rules of 01.4.2-T2.2 (AC5, 00 §5.4.1/§5.4.4/§5.4.6, §7) as one
/// reusable scan over a live connection. The same function runs against the
/// migrated schema and against the deliberately violating fixtures of
/// <see cref="SchemaConventionTests"/>, so the real schema cannot pass a check the
/// fixtures never exercise (the test plan requires both directions).
/// </summary>
internal static class SchemaConventionScan
{
    /// <summary>The composite tenant key every business table declares (00 §5.4.6), read order-insensitively.</summary>
    private static readonly string[] TenantKeyColumns = ["id", "org_id"];

    /// <summary>The <c>created_at</c>/<c>updated_at</c> store type (00 §5.4.4, §7).</summary>
    private const string TimestampType = "timestamp with time zone";

    /// <summary>The <c>version</c> store type (00 §7).</summary>
    private const string VersionType = "integer";

    /// <summary>The one trigger function every <c>version</c> table attaches (0004/0005, M1W3-01).</summary>
    private const string TriggerFunction = "public.set_updated_at_and_version";

    /// <summary>
    /// The explicit allow-list of the scan. Every entry names the table, the rules
    /// it is excused from, and why — an exclusion is a contract, never a silencer.
    /// Its exact contents are pinned by a test, so widening an exemption fails a run
    /// instead of passing review by default.
    /// </summary>
    internal static readonly SchemaExemption[] Exemptions =
    [
        new SchemaExemption(
            "public.schema_migration",
            SchemaRule.All,
            "The DbUp journal (migrations/README.md): written only by the Migrator, carries no org_id and has no RLS."),
        new SchemaExemption(
            "public.outbox",
            SchemaRule.RowLevelSecurity | SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Mechanism table (01.5.1 §3/§4): it carries org_id but holds no RLS policy, because the dispatcher scans every org by design, and it keeps only created_at — no updated_at/version. Its tenant key (outbox_org_id_uq) is NOT exempt: TL T3 lists only schema_migration and the task queue as 01's exceptions to unique (org_id, id)."),
        new SchemaExemption(
            "public.task",
            SchemaRule.TenantKey | SchemaRule.CompositeForeignKey | SchemaRule.RowLevelSecurity,
            "Mechanism table (01.5.1 §3/§4): org_id is nullable for platform tasks, so the tenant key and composite-FK rules cannot hold, and there is no RLS policy because the claim query scans every org by design. Audit columns and the version trigger are NOT exempt: a task row is read-modify-written on every claim, so it carries all three plus the shared trigger."),
        new SchemaExemption(
            "public.audit_log",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Append-only audit trail (02.7.1-T1.1): a row is never updated, so it keeps created_at only — no updated_at/version and no version trigger. Its tenant key and RLS are NOT exempt."),
        new SchemaExemption(
            "public.case_identity_source",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Append-only identity trail (03.1.1-T1.1): a row is never updated (the guard trigger and the revoked UPDATE say so), so it keeps created_at only — no updated_at/version and no version trigger. Its tenant key, composite FK and RLS are NOT exempt."),
        new SchemaExemption(
            "public.case_rename_history",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Append-only move log (03.1.1-T1.1): its timestamp is `detected_at` (when the sync noticed the move), it is never updated, so there is no created_at/updated_at/version and no version trigger. Its tenant key, composite FK and RLS are NOT exempt."),
        new SchemaExemption(
            "public.test_revision",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Append-only test versions (03.1.3-T1.1): a revision is never updated or deleted (trigger and revoked grants), so it keeps created_at only — no updated_at/version and no version trigger. Its tenant key, composite FK and RLS are NOT exempt."),
        new SchemaExemption(
            "public.suite_revision",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "A suite revision is immutable (draft -> published is its only edit, and carries no version): it keeps created_at only (03.2.1-T1.1; migration 0022), so there is no updated_at/version and no version trigger. Its tenant key, composite FKs and RLS are NOT exempt."),
        new SchemaExemption(
            "public.variant",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Content-addressed, shared parameter sets are append-only (03.2.1-T1.1; migration 0022), so there is no updated_at/version and no version trigger. Its tenant key, composite FKs and RLS are NOT exempt."),
        new SchemaExemption(
            "public.suite_member",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "A member row lives and dies with its immutable revision (03.2.1-T1.1; migration 0022), so there is no updated_at/version and no version trigger. Its tenant key, composite FKs and RLS are NOT exempt."),
        new SchemaExemption(
            "public.case_dependency",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "An edge row lives and dies with its immutable revision (03.2.1-T1.1; migration 0022), so there is no updated_at/version and no version trigger. Its tenant key, composite FKs and RLS are NOT exempt."),
        new SchemaExemption(
            "public.manifest",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "A verified manifest is immutable (05.1.1-T3.2; migration 0023): trigger and revoked grants refuse UPDATE/DELETE, so it keeps created_at/verified_at only — no updated_at/version and no version trigger. Its tenant key, composite FKs and RLS are NOT exempt."),
        new SchemaExemption(
            "public.environment_revision",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Append-only environment configuration (05.1.2-T1.1; migration 0024): a revision is never updated or deleted, so it keeps created_at only — no updated_at/version and no version trigger. Its tenant key, composite FKs and RLS are NOT exempt."),
        new SchemaExemption(
            "public.policy_revision",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Append-only policy revisions (07.1.1-T1.1; migration 0025): the trigger and the revoked grants refuse UPDATE/DELETE except the §6 draft -> active -> retired move, so it keeps created_at only — no updated_at, and its `version` is bumped by the 0025 lifecycle trigger on a state transition, never the shared `set_updated_at_and_version`. Its tenant key, composite FK and RLS are NOT exempt."),
        new SchemaExemption(
            "public.adapter_capability",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Append-only first-write facts (04.7.1-T2; migration 0026): a capability row records what an adapter declared the first time it was seen and is never updated (the revoked UPDATE says so), so it keeps `first_seen_at` only — no created_at/updated_at/version and no version trigger. Its tenant key and RLS are NOT exempt."),
        new SchemaExemption(
            "public.planned_execution",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Append-only plan rows (05.2.1-T1; migration 0031): a planned execution is written once inside the run-create transaction and never updated (05.5.1's rerun inserts new rows), so it keeps no created_at/updated_at/version and carries no version trigger. Its tenant key, composite FKs and RLS are NOT exempt."),
        new SchemaExemption(
            "public.case_attempt",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Immutable attempt rows (05.3.3-T1; migration 0032): an attempt is written once inside 04's event-commit transaction and never updated (a correction is a new attempt_no, C6, and the revoked UPDATE says so), so it keeps created_at only — no updated_at/version and no version trigger. Its tenant key, composite FKs and RLS are NOT exempt."),
        new SchemaExemption(
            "public.event",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Append-only event rows (04.1.1-T1; migration 0034): an event is written once inside the ingest transaction and never updated or deleted (the revoked UPDATE/DELETE say so, C6), so it keeps `received_at` only — no created_at/updated_at/version and no version trigger. Its tenant key, composite FKs and RLS are NOT exempt."),
        new SchemaExemption(
            "public.event_batch",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Append-only batch receipts (04.1.1-T1; migration 0034): a batch row is the immutable receipt of the batch that wrote its events (the revoked UPDATE/DELETE say so), so it keeps created_at only — no updated_at/version and no version trigger. Its tenant key, composite FK and RLS are NOT exempt."),
        // `public.manual_case_link` (created_at only) lands with 13.1.1-T4 (migration 0063) and is pinned at the
        // end of this list with the reason its row keeps no updated_at/version.
        new SchemaExemption(
            "public.authoring_version",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Append-only authoring versions (13.1.1-T1.1; migration 0039): a version is never updated (the `authoring_version_immutable` trigger raises 23514 for every role) and only the owner erasure path may delete one, so it keeps `created_at` (clock_timestamp()) only — no updated_at/version and no version trigger. Its tenant key, composite FK and RLS are NOT exempt."),
        new SchemaExemption(
            "public.authoring_code_counter",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "A per-project code allocator (13.1.1-T1.1; migration 0039) carrying no timestamps and no version at all — it is a counter row updated in place (`insert … on conflict do nothing` + `update … returning`), so there is no created_at/updated_at/version and no version trigger. `SchemaRule.TenantKey` is NOT exempt: the plan's natural key `(org_id, project_id)` has no `id`, so 0039 adds the surrogate `id` + `unique (org_id, id)` that `scripts/check-rls.py` and this scan demand (the 0033 `policy_activation` precedent). Its composite FK and RLS are checked like any other business table."),
        new SchemaExemption(
            "public.rls_probe",
            SchemaRule.VersionTrigger,
            "The tenancy template of migration 0003 predates the shared trigger function (0004, M1W3-01) and 0003 is checksum-locked, so the trigger cannot be added there; 0004/0005 are the copy source for module tables."),
        new SchemaExemption(
            "public.role_permission",
            SchemaRule.CompositeForeignKey,
            "Catalogue table (02.2.1-T1.2, 02 §4.5): its only foreign key is `role_permission.role_id -> public.role (id)`, deliberately single-column because a built-in role row carries `org_id IS NULL` and the composite `(org_id, role_id) -> (org_id, id)` reference this rule wants could never match one — the catalogue is org-agnostic by design (TL T3). Its tenant key, RLS flags and audit columns are NOT exempt: 0010 declares `unique (org_id, id)`, ENABLE + FORCE and the shared version trigger."),
        new SchemaExemption(
            "public.usage_ledger_entry",
            SchemaRule.CompositeForeignKey,
            "Usage ledger (migration 0046, key added by 0073): its only non-tenant key is `usage_ledger_entry.price_ref -> public.usage_price (id)`, deliberately single-column because the platform rate card carries `org_id IS NULL` and a composite `(org_id, price_ref) -> (org_id, id)` reference could never match it. The tenant half a foreign key cannot express is the `usage_ledger_entry_price_visible` trigger: a row may name a global price or one of its own org's overrides, never another org's. Its tenant key, RLS flags and audit columns are NOT exempt."),
        new SchemaExemption(
            "public.evidence_revision",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Append-only frozen evidence sets (05.3.2-T5; migration 0049, feature plan 05.3.2 AC8/AC9, C6): a revision is written once when a run's evidence is frozen and is never rewritten — late evidence appends the next `revision_no` — so the app role holds SELECT + INSERT only (the revoked UPDATE/DELETE refuse the rest) and the row keeps created_at only: no updated_at/version and no version trigger. Its tenant key, composite FKs and RLS are NOT exempt."),
        new SchemaExemption(
            "public.evaluation",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "A stored verdict (migration 0059) is evidence, not state: the app role holds SELECT + INSERT only and the `evaluation_immutable` trigger refuses every UPDATE and DELETE, so the row keeps created_at only — no updated_at/version and no version trigger. Its tenant key, composite FKs, closed token CHECKs and RLS are NOT exempt."),
        new SchemaExemption(
            "public.project_ai_settings",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Append-only AI settings versions (13.2.1-T1; migration 0064): the `project_ai_settings_immutable` trigger refuses UPDATE for every role and DELETE for every role but `evidgate_owner`, so the row keeps created_at only — no updated_at/version and no version trigger. Its tenant key, composite FKs and RLS are NOT exempt."),
        new SchemaExemption(
            "public.ai_settings_template",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Read-only platform template catalogue (13.2.1-T1; migration 0064): rows are written only by the migration and are never updated, so they keep created_at only — no updated_at/version and no version trigger. The table carries no org_id, so it is not a business table; its RLS flags are still real (ENABLE + FORCE, the read policy and one denial per write command, plus the owner policy) and it is listed as `platform` in contracts/rls-exceptions.txt."),
        new SchemaExemption(
            "public.manual_case_link",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "A link is insert-or-delete and never edited (13.1.1-T4; migration 0063): the app role holds SELECT, INSERT and DELETE but UPDATE is revoked, so the row keeps created_at only — no updated_at/version and no version trigger. A repeat link is answered by `manual_case_link_uq` rather than by a version bump. Its tenant key, composite FKs, the same-project trigger and RLS are NOT exempt."),
        new SchemaExemption(
            "public.masking_event",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Append-only scan findings (06.2.3-T1; migration 0066): the app role holds SELECT + INSERT and UPDATE/DELETE/TRUNCATE are revoked, so a finding is written once and never rewritten — the row keeps created_at only, with no updated_at/version and no version trigger. A replayed scan is answered by `masking_event_artifact_pattern_key` (on conflict do nothing) rather than by a version bump. Its tenant key, composite FK, closed CHECKs and RLS are NOT exempt."),
        new SchemaExemption(
            "public.report_parser",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "A parser registry row is insert-only (04.4.1-T1; migration 0075): the app role holds SELECT only, and the row is written by the migration and the owner role, so a release is a new (parser_id, version) row rather than an edit — the row keeps created_at only, with no updated_at/version and no version trigger. Its tenant key, closed CHECKs and RLS are NOT exempt."),
        new SchemaExemption(
            "public.release_component",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Immutable component rows (05.6.1-T1; migration 0072): the app role holds SELECT + INSERT, UPDATE and DELETE are revoked, and the `release_component_guard` trigger refuses both for every role (it raises `release_component_immutable`) and refuses INSERT once the release is `active`, so a component is written once at create and keeps created_at only — no updated_at/version and no version trigger. Its tenant key, composite FKs (release, environment_revision), same-project guard and RLS are NOT exempt."),
        new SchemaExemption(
            "public.release_run",
            SchemaRule.AuditColumns | SchemaRule.VersionTrigger,
            "Append-only binding rows (05.6.1-T1 table, T2 writer; migration 0072): the app role holds SELECT + INSERT and UPDATE/DELETE are revoked, and the `release_run_immutable` trigger refuses both for every role, so a binding is written once and keeps created_at only — no updated_at/version and no version trigger. Its tenant key, composite FKs (release, run), uniqueness and RLS are NOT exempt."),
    ];

    /// <summary>
    /// Scans every non-system table the connection can see — the migrated schema on
    /// a plain connection, or the tables a test created inside its own transaction —
    /// and returns the tables it checked plus every violation of the rules.
    /// </summary>
    /// <param name="connection">An open connection (the migration owner or the superuser: this is a catalogue read).</param>
    /// <param name="cancellationToken">The xUnit v3 test token.</param>
    /// <param name="transaction">The transaction the fixture tables live in, when the scan runs inside one.</param>
    internal static async Task<SchemaScan> RunAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var tables = await ReadTablesAsync(connection, transaction, cancellationToken);
        var columns = await ReadColumnsAsync(connection, transaction, cancellationToken);
        var uniqueKeys = await ReadUniqueKeysAsync(connection, transaction, cancellationToken);
        var foreignKeys = await ReadForeignKeysAsync(connection, transaction, cancellationToken);
        var triggers = await ReadVersionTriggersAsync(connection, transaction, cancellationToken);

        // Business tables are the tables that carry `org_id` (00 §5.4.1). The root
        // table `public.organization` is deliberately outside that definition: it is
        // the TL T4 exception that carries no `org_id`, because the column would have
        // to reference the row itself (its own `id` is the tenant key — the Tenancy
        // suite's OrgProjectSchemaTests asserts its shape and policies). Tables the
        // allow-list excuses from every rule are not reported as business tables.
        var businessTables = tables
            .Where(table => table.HasOrgId && !IsExempt(table.Name, SchemaRule.All))
            .Select(table => table.Name)
            .ToHashSet(StringComparer.Ordinal);

        var violations = new List<SchemaViolation>();

        foreach (var table in tables)
        {
            if (businessTables.Contains(table.Name))
            {
                CheckTenantKey(table, uniqueKeys, violations);
                CheckForeignKeys(table, businessTables, foreignKeys, uniqueKeys, violations);
                CheckRowLevelSecurity(table, violations);
                CheckAuditColumns(table, columns, violations);
            }

            CheckVersionTrigger(table, columns, triggers, violations);
        }

        return new SchemaScan(
            tables.Select(table => table.Name).ToList(),
            tables.Where(table => businessTables.Contains(table.Name)).Select(table => table.Name).ToList(),
            violations);
    }

    /// <summary>00 §5.4.6: <c>unique (org_id, id)</c> as a constraint or a unique index — the composite-FK target.</summary>
    private static void CheckTenantKey(
        TableShape table,
        Dictionary<string, List<string[]>> uniqueKeys,
        List<SchemaViolation> violations)
    {
        if (IsExempt(table.Name, SchemaRule.TenantKey))
        {
            return;
        }

        var keys = uniqueKeys.TryGetValue(table.Name, out var found) ? found : [];

        if (!keys.Any(key => key.AsSpan().SequenceEqual(TenantKeyColumns)))
        {
            violations.Add(new SchemaViolation(
                table.Name,
                SchemaRule.TenantKey,
                "has no unique key on (org_id, id) — 00 §5.4.6: it is the only target a composite tenant FK may reference"));
        }
    }

    /// <summary>
    /// 00 §5.4.1/§5.4.6: a foreign key from a business table to another business
    /// table is composite, carries <c>org_id</c>, and references the target's
    /// <c>(org_id, id)</c> key — so no row can be pointed at another tenant's.
    /// A key to a table outside the business set (the <c>organization</c> root) is
    /// not itself a tenant FK and is left to that table's own story.
    /// </summary>
    private static void CheckForeignKeys(
        TableShape table,
        HashSet<string> businessTables,
        IReadOnlyList<ForeignKeyShape> foreignKeys,
        Dictionary<string, List<string[]>> uniqueKeys,
        List<SchemaViolation> violations)
    {
        if (IsExempt(table.Name, SchemaRule.CompositeForeignKey))
        {
            return;
        }

        foreach (var foreignKey in foreignKeys.Where(key => key.DependentTable == table.Name))
        {
            if (!businessTables.Contains(foreignKey.PrincipalTable))
            {
                continue;
            }

            var referenced = new string[foreignKey.PrincipalColumns.Length];
            foreignKey.PrincipalColumns.CopyTo(referenced, 0);
            Array.Sort(referenced, StringComparer.Ordinal);

            // The unique key behind the reference may be declared in either order —
            // PostgreSQL matches a unique index to a referenced column list
            // regardless of the declared order (probed on the pinned postgres:18.6),
            // so the set comparison stays order-insensitive. The *pairing*, though,
            // is positional: a tenant FK is a tenant FK only when org_id pairs with
            // org_id and the target's `id` pairs with the dependent's own row id.
            // `(org_id, project_id) → (id, org_id)` references the same unique key
            // set while pairing org_id with the target's id, so the set comparison
            // alone would bless a key that can never match a real row.
            //
            // A key with MORE columns is also a tenant key when it references a
            // unique key of the principal that includes org_id and the dependent's
            // org_id pairs with the principal's org_id: `case_attempt_planned_execution_fk
            // (org_id, run_id, execution_key) -> planned_execution (org_id, run_id,
            // execution_key)` (0032) is exactly that — the principal's own unique key,
            // so no row can point at another org's planned execution.
            var isCompositeTenantKey = referenced.AsSpan().Contains("org_id", StringComparer.Ordinal)
                && PairsOrgId(foreignKey.Columns, foreignKey.PrincipalColumns)
                && (foreignKey.Columns.Length == 2
                    ? referenced.AsSpan().SequenceEqual(TenantKeyColumns) && PairsTenantKey(foreignKey.Columns, foreignKey.PrincipalColumns)
                    : ReferencesUniqueKey(uniqueKeys, foreignKey));

            if (!isCompositeTenantKey)
            {
                violations.Add(new SchemaViolation(
                    table.Name,
                    SchemaRule.CompositeForeignKey,
                    $"foreign key '{foreignKey.Name}' references {foreignKey.PrincipalTable} as "
                    + $"({string.Join(", ", foreignKey.Columns)}) → ({string.Join(", ", foreignKey.PrincipalColumns)}) — "
                    + "00 §5.4.1/§5.4.6: a tenant FK is (org_id, <id>) referencing (org_id, id)"));
            }
        }
    }

    /// <summary>
    /// The dependent's <c>org_id</c> pairs with the principal's <c>org_id</c>, position
    /// by position — the half of the tenant-key shape every composite tenant key must
    /// have, whatever its width.
    /// </summary>
    private static bool PairsOrgId(string[] columns, string[] principalColumns)
    {
        for (var index = 0; index < columns.Length; index++)
        {
            if (string.Equals(principalColumns[index], "org_id", StringComparison.Ordinal)
                && string.Equals(columns[index], "org_id", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The key's principal columns are exactly a unique key of the principal table
    /// (order-insensitive), so the reference can only ever match a row of the
    /// dependent's own org — the multi-column form of the composite tenant key
    /// (0032's <c>case_attempt_planned_execution_fk</c>).
    /// </summary>
    private static bool ReferencesUniqueKey(
        Dictionary<string, List<string[]>> uniqueKeys,
        ForeignKeyShape foreignKey)
    {
        var referenced = new string[foreignKey.PrincipalColumns.Length];
        foreignKey.PrincipalColumns.CopyTo(referenced, 0);
        Array.Sort(referenced, StringComparer.Ordinal);

        return uniqueKeys.TryGetValue(foreignKey.PrincipalTable, out var keys)
            && keys.Any(key => key.AsSpan().SequenceEqual(referenced));
    }

    /// <summary>
    /// org_id pairs with org_id and the referenced <c>id</c> pairs with the
    /// dependent's own row id, position by position (00 §5.4.1) — the half of the
    /// tenant-FK shape a set comparison cannot see.
    /// </summary>
    private static bool PairsTenantKey(string[] columns, string[] principalColumns)
    {
        var pairsOrgId = false;
        var pairsId = false;

        for (var index = 0; index < columns.Length; index++)
        {
            var dependent = columns[index];
            var principal = principalColumns[index];

            if (string.Equals(principal, "org_id", StringComparison.Ordinal))
            {
                pairsOrgId |= string.Equals(dependent, "org_id", StringComparison.Ordinal);
            }
            else if (string.Equals(principal, "id", StringComparison.Ordinal))
            {
                pairsId |= !string.Equals(dependent, "org_id", StringComparison.Ordinal);
            }
        }

        return pairsOrgId && pairsId;
    }

    /// <summary>DECISIONS F5: ENABLE <b>and</b> FORCE — without FORCE the owner bypasses every policy.</summary>
    private static void CheckRowLevelSecurity(TableShape table, List<SchemaViolation> violations)
    {
        // Both flags, not either: PostgreSQL lets `force row level security` sit on a
        // table whose RLS is not enabled, and that combination applies no policy at
        // all — the strictest-looking migration would be the widest hole.
        if (IsExempt(table.Name, SchemaRule.RowLevelSecurity)
            || (table.RowLevelSecurityEnabled && table.RowLevelSecurityForced))
        {
            return;
        }

        var detail = table.RowLevelSecurityEnabled
            ? "row level security is enabled but not FORCED — the table owner would bypass every policy (DECISIONS F5)"
            : table.RowLevelSecurityForced
                ? "row level security is FORCED but not ENABLED — FORCE on its own applies no policy at all (DECISIONS F5)"
                : "row level security is not enabled (DECISIONS F5)";

        violations.Add(new SchemaViolation(table.Name, SchemaRule.RowLevelSecurity, detail));
    }

    /// <summary>00 §7: <c>created_at</c> and <c>updated_at</c> are UTC <c>timestamptz</c>, <c>version</c> is an integer.</summary>
    private static void CheckAuditColumns(
        TableShape table,
        Dictionary<string, Dictionary<string, ColumnShape>> columns,
        List<SchemaViolation> violations)
    {
        if (IsExempt(table.Name, SchemaRule.AuditColumns))
        {
            return;
        }

        var tableColumns = columns.TryGetValue(table.Name, out var found) ? found : [];

        foreach (var (name, storeType) in new[] { ("created_at", TimestampType), ("updated_at", TimestampType), ("version", VersionType) })
        {
            if (!tableColumns.TryGetValue(name, out var column))
            {
                violations.Add(new SchemaViolation(
                    table.Name,
                    SchemaRule.AuditColumns,
                    $"has no '{name}' column — 00 §7 requires created_at, updated_at and version on every table"));
                continue;
            }

            if (column.StoreType != storeType)
            {
                violations.Add(new SchemaViolation(
                    table.Name,
                    SchemaRule.AuditColumns,
                    $"'{name}' is '{column.StoreType}', expected '{storeType}' (00 §7)"));
            }

            if (!column.NotNull)
            {
                violations.Add(new SchemaViolation(
                    table.Name,
                    SchemaRule.AuditColumns,
                    $"'{name}' is nullable (00 §7)"));
            }
        }
    }

    /// <summary>
    /// 00 §7, M1W3-01: the database owns the <c>version</c> increment and
    /// <c>updated_at</c>, through one BEFORE UPDATE FOR EACH ROW trigger per table
    /// that has a <c>version</c> column. Without it the optimistic-concurrency token
    /// of the column conventions never moves and a lost update goes unnoticed.
    /// </summary>
    private static void CheckVersionTrigger(
        TableShape table,
        Dictionary<string, Dictionary<string, ColumnShape>> columns,
        HashSet<string> triggers,
        List<SchemaViolation> violations)
    {
        if (IsExempt(table.Name, SchemaRule.VersionTrigger))
        {
            return;
        }

        if (!columns.TryGetValue(table.Name, out var tableColumns) || !tableColumns.ContainsKey("version"))
        {
            return;
        }

        if (!triggers.Contains(table.Name))
        {
            violations.Add(new SchemaViolation(
                table.Name,
                SchemaRule.VersionTrigger,
                $"has a 'version' column but no before update trigger calling {TriggerFunction}() — "
                + "the concurrency token would never move (00 §7, M1W3-01)"));
        }
    }

    private static bool IsExempt(string table, SchemaRule rule) =>
        Exemptions.Any(entry =>
            string.Equals(entry.Table, table, StringComparison.Ordinal) && (entry.Rules & rule) == rule);

    private static async Task<IReadOnlyList<TableShape>> ReadTablesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   c.relrowsecurity,
                   c.relforcerowsecurity,
                   exists (select 1
                             from pg_attribute a
                            where a.attrelid = c.oid
                              and a.attname = 'org_id'
                              and a.attnum > 0
                              and not a.attisdropped)
              from pg_class c
              join pg_namespace n on n.oid = c.relnamespace
             where c.relkind in ('r', 'p')
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
             order by 1
            """;

        var tables = new List<TableShape>();
        await using var command = CreateCommand(connection, sql, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(new TableShape(
                await reader.GetFieldValueAsync<string>(0, cancellationToken),
                await reader.GetFieldValueAsync<bool>(1, cancellationToken),
                await reader.GetFieldValueAsync<bool>(2, cancellationToken),
                await reader.GetFieldValueAsync<bool>(3, cancellationToken)));
        }

        return tables;
    }

    private static async Task<Dictionary<string, Dictionary<string, ColumnShape>>> ReadColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   a.attname,
                   pg_catalog.format_type(a.atttypid, a.atttypmod),
                   a.attnotnull
              from pg_attribute a
              join pg_class c on c.oid = a.attrelid
              join pg_namespace n on n.oid = c.relnamespace
             where a.attnum > 0
               and not a.attisdropped
               and c.relkind in ('r', 'p')
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
             order by 1, a.attnum
            """;

        var columns = new Dictionary<string, Dictionary<string, ColumnShape>>(StringComparer.Ordinal);
        await using var command = CreateCommand(connection, sql, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = await reader.GetFieldValueAsync<string>(0, cancellationToken);
            if (!columns.TryGetValue(table, out var tableColumns))
            {
                tableColumns = new Dictionary<string, ColumnShape>(StringComparer.Ordinal);
                columns[table] = tableColumns;
            }

            var name = await reader.GetFieldValueAsync<string>(1, cancellationToken);
            tableColumns[name] = new ColumnShape(
                await reader.GetFieldValueAsync<string>(2, cancellationToken),
                await reader.GetFieldValueAsync<bool>(3, cancellationToken));
        }

        return columns;
    }

    /// <summary>
    /// Every valid, non-partial, non-deferrable unique key as its column list, sorted
    /// (so the check is order-insensitive). The last two conditions are what make the
    /// key a legal composite-FK target (00 §5.4.6).
    /// </summary>
    private static async Task<Dictionary<string, List<string[]>>> ReadUniqueKeysAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   (select array_agg(a.attname::text order by a.attname::text)
                      from generate_series(0, i.indnkeyatts - 1) as s(ord)
                      join pg_attribute a on a.attrelid = i.indrelid and a.attnum = i.indkey[s.ord])
              from pg_index i
              join pg_class c on c.oid = i.indrelid
              join pg_namespace n on n.oid = c.relnamespace
             where i.indisunique
               and i.indisvalid
               and i.indimmediate
               and i.indpred is null
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
             order by 1
            """;

        var keys = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
        await using var command = CreateCommand(connection, sql, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = await reader.GetFieldValueAsync<string>(0, cancellationToken);
            if (!keys.TryGetValue(table, out var tableKeys))
            {
                tableKeys = [];
                keys[table] = tableKeys;
            }

            var columns = await reader.GetFieldValueAsync<string[]>(1, cancellationToken);
            Array.Sort(columns, StringComparer.Ordinal);
            tableKeys.Add(columns);
        }

        return keys;
    }

    private static async Task<IReadOnlyList<ForeignKeyShape>> ReadForeignKeysAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   con.conname,
                   rn.nspname || '.' || rc.relname,
                   (select array_agg(a.attname::text order by k.ord)
                      from unnest(con.conkey) with ordinality as k(attnum, ord)
                      join pg_attribute a on a.attrelid = con.conrelid and a.attnum = k.attnum),
                   (select array_agg(a.attname::text order by k.ord)
                      from unnest(con.confkey) with ordinality as k(attnum, ord)
                      join pg_attribute a on a.attrelid = con.confrelid and a.attnum = k.attnum)
              from pg_constraint con
              join pg_class c on c.oid = con.conrelid
              join pg_namespace n on n.oid = c.relnamespace
              join pg_class rc on rc.oid = con.confrelid
              join pg_namespace rn on rn.oid = rc.relnamespace
             where con.contype = 'f'
             order by 1, 2
            """;

        var foreignKeys = new List<ForeignKeyShape>();
        await using var command = CreateCommand(connection, sql, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            foreignKeys.Add(new ForeignKeyShape(
                await reader.GetFieldValueAsync<string>(0, cancellationToken),
                await reader.GetFieldValueAsync<string>(1, cancellationToken),
                await reader.GetFieldValueAsync<string>(2, cancellationToken),
                await reader.GetFieldValueAsync<string[]>(3, cancellationToken),
                await reader.GetFieldValueAsync<string[]>(4, cancellationToken)));
        }

        return foreignKeys;
    }

    /// <summary>The tables carrying an enabled BEFORE UPDATE FOR EACH ROW trigger over the shared trigger function.</summary>
    private static async Task<HashSet<string>> ReadVersionTriggersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        // tgtype bit 1 = ROW, 2 = BEFORE, 16 = UPDATE. `tgenabled` must be 'O' or 'A':
        // 'D' is a trigger someone disabled again and 'R' fires only under
        // session_replication_role = replica, which this stack never sets. A
        // non-empty `tgattr` is a column-scoped `before update of <column>` trigger,
        // which does not fire for the ordinary update EF issues (00 §7, M1W3-01).
        // All three would leave the token unmoved while glancing at the migration.
        const string sql = """
            select n.nspname || '.' || c.relname
              from pg_trigger t
              join pg_class c on c.oid = t.tgrelid
              join pg_namespace n on n.oid = c.relnamespace
              join pg_proc p on p.oid = t.tgfoid
              join pg_namespace pn on pn.oid = p.pronamespace
             where not t.tgisinternal
               and t.tgenabled in ('O', 'A')
               and t.tgattr = ''::int2vector
               and (t.tgtype & 2) = 2
               and (t.tgtype & 1) = 1
               and (t.tgtype & 16) = 16
               and pn.nspname || '.' || p.proname = @function
             order by 1
            """;

        var triggers = new HashSet<string>(StringComparer.Ordinal);
        await using var command = CreateCommand(connection, sql, transaction);
        command.Parameters.AddWithValue("function", TriggerFunction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            triggers.Add(await reader.GetFieldValueAsync<string>(0, cancellationToken));
        }

        return triggers;
    }

    private static NpgsqlCommand CreateCommand(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private sealed record TableShape(
        string Name,
        bool RowLevelSecurityEnabled,
        bool RowLevelSecurityForced,
        bool HasOrgId);

    private sealed record ColumnShape(string StoreType, bool NotNull);

    private sealed record ForeignKeyShape(
        string DependentTable,
        string Name,
        string PrincipalTable,
        string[] Columns,
        string[] PrincipalColumns);
}

/// <summary>One catalogue rule of 01.4.2-T2.2; the flags form is the allow-list's unit.</summary>
[Flags]
internal enum SchemaRule
{
    /// <summary>No rule.</summary>
    None = 0,

    /// <summary>00 §5.4.6: <c>unique (org_id, id)</c> on every business table.</summary>
    TenantKey = 1,

    /// <summary>00 §5.4.1/§5.4.6: composite <c>(org_id, &lt;id&gt;)</c> foreign keys between business tables.</summary>
    CompositeForeignKey = 2,

    /// <summary>DECISIONS F5: row level security ENABLED and FORCED.</summary>
    RowLevelSecurity = 4,

    /// <summary>00 §7: <c>created_at</c>/<c>updated_at</c> <c>timestamptz</c> and <c>version</c> integer, all NOT NULL.</summary>
    AuditColumns = 8,

    /// <summary>00 §7, M1W3-01: the <c>set_updated_at_and_version</c> BEFORE UPDATE trigger.</summary>
    VersionTrigger = 16,

    /// <summary>Every rule above.</summary>
    All = TenantKey | CompositeForeignKey | RowLevelSecurity | AuditColumns | VersionTrigger,
}

/// <summary>One entry of <see cref="SchemaConventionScan"/>'s allow-list: the table, the rules it is excused from, and why.</summary>
internal sealed record SchemaExemption(string Table, SchemaRule Rules, string Reason);

/// <summary>One broken rule, named so a failure reads as a sentence.</summary>
internal sealed record SchemaViolation(string Table, SchemaRule Rule, string Detail)
{
    /// <inheritdoc />
    public override string ToString() => $"{Table}: {Rule}: {Detail}";
}

/// <summary>What a scan saw and what it rejected.</summary>
/// <param name="Tables">Every non-system table in the database.</param>
/// <param name="BusinessTables">The subset the tenant rules apply to (carries <c>org_id</c>, not allow-listed).</param>
/// <param name="Violations">Every violation, in table-name order.</param>
internal sealed record SchemaScan(
    IReadOnlyList<string> Tables,
    IReadOnlyList<string> BusinessTables,
    IReadOnlyList<SchemaViolation> Violations);
