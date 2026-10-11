using Npgsql;

namespace Isolation.Tests;

/// <summary>The rules 02.9.1-T2 enforces structurally on the live catalogue.</summary>
internal enum TenantFkRule
{
    /// <summary>00 §5.4.6: every tenant table declares <c>unique (org_id, id)</c> — the only composite-FK target.</summary>
    TenantKey,

    /// <summary>00 §5.4.1/§5.4.6: a foreign key between two tenant tables is <c>(org_id, &lt;id&gt;) → (org_id, id)</c>.</summary>
    CompositeForeignKey,

    /// <summary>A tenant table may reference a table outside the tenant set only through its own <c>org_id</c> column.</summary>
    TenantColumnOnly,
}

/// <summary>One broken rule, naming the table and the object, so a failure reads as a sentence.</summary>
internal sealed record TenantFkFinding(string Table, TenantFkRule Rule, string Detail)
{
    /// <inheritdoc />
    public override string ToString() => $"{Table} — {Rule}: {Detail}";
}

/// <summary>
/// What one scan saw: the catalogue, the exception list it subtracted, the tables
/// it treated as tenant tables and those it excluded, and every structural
/// violation of T2's rules.
/// </summary>
/// <param name="Schema">The relation catalogue the scan read.</param>
/// <param name="Exceptions">The parsed <c>contracts/rls-exceptions.txt</c>.</param>
/// <param name="Excluded">The <c>org_id</c>-carrying tables the exception list removed from the tenant set, with their kind.</param>
/// <param name="TenantTables">The tenant set: every <c>org_id</c>-carrying table minus the exception list, in name order.</param>
/// <param name="Findings">Every structural violation, in table order.</param>
internal sealed record TenantFkScanResult(
    TenantSchema Schema,
    IReadOnlyList<RlsException> Exceptions,
    IReadOnlyList<(string Table, string Kind)> Excluded,
    IReadOnlyList<string> TenantTables,
    IReadOnlyList<TenantFkFinding> Findings);

/// <summary>
/// 02.9.1-T2.1: the auto-discovered tenant set and its structural rules. The
/// tenant set is <em>derived</em>, never listed: every table in the catalogue that
/// carries <c>org_id</c>, minus the tables <c>contracts/rls-exceptions.txt</c>
/// names (the one place an exception lives, 02.6.1-T1.4). A new tenant table is
/// therefore covered the moment its migration lands, and one that misses its
/// composite FK fails this scan with its own name in the message.
/// <para>
/// The discovery reads <c>pg_catalog</c> — <c>pg_class</c>, <c>pg_attribute</c>,
/// <c>pg_constraint</c>, <c>pg_index</c> — which is the same catalogue data the
/// story's <c>information_schema</c> wording refers to
/// (<c>information_schema.columns</c> is a view over exactly these relations), and
/// mirrors how <c>SchemaConventionScan</c> of 01.4.2-T2.2 reads it. Every
/// non-system schema is read, not only <c>public</c>: business tables live in
/// <c>public</c> (00 §5.4.1), and reading the rest means a tenant table that someone
/// puts elsewhere is reported instead of silently skipped.
/// </para>
/// <para>
/// The rules, and why they are exactly these:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>TenantKey</b> — every tenant table declares <c>unique (org_id, id)</c>
/// (00 §5.4.6). Without it the table cannot be the target of a composite tenant FK
/// at all, so the other two rules would have nothing to point at.
/// </description></item>
/// <item><description>
/// <b>CompositeForeignKey</b> — every foreign key from a tenant table to a table
/// that <em>carries an <c>org_id</c> column</em> pairs <c>org_id</c> with
/// <c>org_id</c> and the principal's <c>id</c> with the dependent's own id column,
/// referencing the target's <c>(org_id, id)</c> key (00 §5.4.1). A set comparison is
/// not enough: a key declared <c>(org_id, project_id) → (id, org_id)</c> references
/// the same column set while pairing the org with the target's id, and can never
/// match a row. The principal side is scoped by the presence of the <c>org_id</c>
/// column, which is what "org-scoped parent" means in 00 §5.4.6 — not by
/// tenant-set membership. That scoping is what makes the failure message the right
/// instruction: a tenant table pointing at a catalogue row through a plain key is
/// told to use the composite form (plan 02 §4.1 requires exactly that for a future
/// <c>membership.role_id → role (org_id, id)</c>), where the org-column rule would
/// tell it to point at the parent through its own <c>org_id</c> — an instruction
/// that cannot be right for a catalogue row. The <em>dependent</em> side is the
/// exception-subtracted tenant set, so a catalogue table's own outbound key
/// (0010's <c>role_permission.role_id → role (id)</c>, whose built-in rows have
/// <c>org_id</c> NULL and are exempted in
/// <c>Persistence.IntegrationTests.SchemaConventionScan</c>) is that table's own
/// design, not this rule's business.
/// </description></item>
/// <item><description>
/// <b>TenantColumnOnly</b> — every foreign key from a tenant table to a table
/// <em>without</em> an <c>org_id</c> column must name only that table's own
/// <c>org_id</c> column. This is the "how is a table whose only parent is the tenant
/// root handled" rule: <c>public.project</c> references <c>public.organization</c>
/// through its own <c>org_id</c>, and the root's id <em>is</em> the org id, so the
/// org column can only ever name that row's own org (00 §5.4.1, TL T4: the root is
/// the one org-scoped table without an <c>org_id</c> column, and
/// <c>organization.id</c> is globally unique). Every other reference to a table
/// without <c>org_id</c> is a finding, because such a parent can still be
/// org-scoped — the <c>organization</c> root is — so a column other than
/// <c>org_id</c> could name another org's row.
/// </description></item>
/// </list>
/// <para>
/// A table with no foreign key at all (<c>public.rls_probe</c>, the 0003 template)
/// has no parent pointer to corrupt: its only tenant link is its own <c>org_id</c>
/// column, and rule 1 makes it a valid composite-FK target for the tables that come
/// later. It passes vacuously, which the caller records rather than assumes.
/// </para>
/// </summary>
internal static class TenantFkScan
{
    /// <summary>The tenant column every tenant table carries (00 §5.4.1).</summary>
    internal const string TenantColumn = "org_id";

    /// <summary>The row identity column a composite tenant key pairs <c>org_id</c> with (00 §5.4.6).</summary>
    internal const string TenantKeyColumn = "id";

    /// <summary>The composite tenant key, sorted ordinal — <c>"id"</c> sorts before <c>"org_id"</c>.</summary>
    private static readonly string[] TenantKeyColumns = [TenantKeyColumn, TenantColumn];

    /// <summary>An out-of-set reference on the tenant column itself (rule 3).</summary>
    private static readonly string[] TenantColumnOnly = [TenantColumn];

    /// <summary>Reads the catalogue and the exception list, derives the tenant set, and checks the three rules.</summary>
    internal static async Task<TenantFkScanResult> RunAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        var schema = await TenantSchema.ReadAsync(connection, transaction, cancellationToken);
        var exceptions = RlsExceptionList.Read();

        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in exceptions)
        {
            listed[entry.Table] = entry.Kind;
        }

        var orgIdTables = schema.Tables.Where(table => table.HasOrgId).Select(table => table.Name).ToArray();
        var tenantTables = orgIdTables.Where(table => !listed.ContainsKey(table)).ToArray();
        var excluded = orgIdTables.Where(listed.ContainsKey).Select(table => (table, listed[table])).ToArray();

        var findings = new List<TenantFkFinding>();
        foreach (var table in tenantTables)
        {
            CheckTenantKey(schema, table, findings);
            CheckForeignKeys(schema, table, findings);
        }

        return new TenantFkScanResult(schema, exceptions, excluded, tenantTables, findings);
    }

    /// <summary>The failure text of a finding list: one line per finding, each starting with the table name.</summary>
    internal static string Describe(IReadOnlyList<TenantFkFinding> findings) =>
        string.Join(Environment.NewLine, findings.Select(finding => finding.ToString()));

    /// <summary>00 §5.4.6: <c>unique (org_id, id)</c> as a constraint or a unique index.</summary>
    private static void CheckTenantKey(TenantSchema schema, string table, List<TenantFkFinding> findings)
    {
        var keys = schema.UniqueKeysOf(table);
        if (!keys.Any(key => key.Columns.AsSpan().SequenceEqual(TenantKeyColumns)))
        {
            findings.Add(new TenantFkFinding(
                table,
                TenantFkRule.TenantKey,
                "has no unique key on (org_id, id) — 00 §5.4.6: it is the only target a composite tenant FK may reference"));
        }
    }

    /// <summary>00 §5.4.1/§5.4.6: a parent with <c>org_id</c> takes the composite key (rule 2); one without takes the org column (rule 3).</summary>
    private static void CheckForeignKeys(
        TenantSchema schema,
        string table,
        List<TenantFkFinding> findings)
    {
        foreach (var foreignKey in schema.ForeignKeysOf(table))
        {
            var pair = $"({string.Join(", ", foreignKey.Columns)}) → {foreignKey.PrincipalTable} "
                + $"({string.Join(", ", foreignKey.PrincipalColumns)})";

            if (!schema.HasOrgId(foreignKey.PrincipalTable))
            {
                if (!foreignKey.Columns.AsSpan().SequenceEqual(TenantColumnOnly))
                {
                    findings.Add(new TenantFkFinding(
                        table,
                        TenantFkRule.TenantColumnOnly,
                        $"foreign key '{foreignKey.Name}' references {pair} — a tenant table may only point at a table without "
                        + "org_id through its own org_id column (the tenant-root reference, whose target id is the org id); any "
                        + "other column could name another org's row in an org-scoped parent"));
                }

                continue;
            }

            var referenced = (string[])foreignKey.PrincipalColumns.Clone();
            Array.Sort(referenced, StringComparer.Ordinal);

            // A composite tenant key is (org_id, <id>) referencing (org_id, id) — the 00 §5.4.6
            // shape. A key with MORE columns is also a tenant key when it references a unique key of
            // the principal that includes org_id and the dependent's org_id pairs with the
            // principal's org_id: `case_attempt_planned_execution_fk (org_id, run_id, execution_key)
            // -> planned_execution (org_id, run_id, execution_key)` (0032) is exactly that — the
            // principal's own unique key, so no row can point at another org's planned execution.
            var isTenantKey = referenced.AsSpan().Contains(TenantColumn, StringComparer.Ordinal)
                && PairsOrgId(foreignKey.Columns, foreignKey.PrincipalColumns)
                && (foreignKey.Columns.Length == 2
                    ? referenced.AsSpan().SequenceEqual(TenantKeyColumns) && PairsTenantKey(foreignKey.Columns, foreignKey.PrincipalColumns)
                    : ReferencesUniqueKey(schema, foreignKey));

            if (!isTenantKey)
            {
                findings.Add(new TenantFkFinding(
                    table,
                    TenantFkRule.CompositeForeignKey,
                    $"foreign key '{foreignKey.Name}' references {pair} — 00 §5.4.1/§5.4.6: a key to an org-scoped parent is "
                    + "(org_id, <id>) referencing (org_id, id), so a child row can never point at another org's parent"));
            }
        }
    }

    /// <summary>
    /// <c>org_id</c> pairs with <c>org_id</c> and the referenced <c>id</c> pairs
    /// with the dependent's own row id, position by position (00 §5.4.1) — the half
    /// of the tenant-FK shape that a column-set comparison cannot see.
    /// </summary>
    private static bool PairsTenantKey(string[] columns, string[] principalColumns)
    {
        var pairsOrgId = false;
        var pairsId = false;

        for (var index = 0; index < columns.Length; index++)
        {
            var dependent = columns[index];
            var principal = principalColumns[index];

            if (string.Equals(principal, TenantColumn, StringComparison.Ordinal))
            {
                pairsOrgId |= string.Equals(dependent, TenantColumn, StringComparison.Ordinal);
            }
            else if (string.Equals(principal, TenantKeyColumn, StringComparison.Ordinal))
            {
                pairsId |= !string.Equals(dependent, TenantColumn, StringComparison.Ordinal);
            }
        }

        return pairsOrgId && pairsId;
    }

    /// <summary>
    /// The dependent's <c>org_id</c> pairs with the principal's <c>org_id</c>, position by position —
    /// the half of the tenant-key shape every composite tenant key must have, whatever its width.
    /// </summary>
    private static bool PairsOrgId(string[] columns, string[] principalColumns)
    {
        for (var index = 0; index < columns.Length; index++)
        {
            if (string.Equals(principalColumns[index], TenantColumn, StringComparison.Ordinal)
                && string.Equals(columns[index], TenantColumn, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The key's principal columns are exactly a unique key of the principal table (order-insensitive),
    /// so the reference can only ever match a row of the dependent's own org — the multi-column form of
    /// the composite tenant key (0032's <c>case_attempt_planned_execution_fk</c>).
    /// </summary>
    private static bool ReferencesUniqueKey(TenantSchema schema, ScannedForeignKey foreignKey)
    {
        var referenced = (string[])foreignKey.PrincipalColumns.Clone();
        Array.Sort(referenced, StringComparer.Ordinal);

        return schema.UniqueKeysOf(foreignKey.PrincipalTable)
            .Any(key => key.Columns.AsSpan().SequenceEqual(referenced));
    }
}
