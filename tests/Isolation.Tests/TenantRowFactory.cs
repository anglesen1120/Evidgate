using System.Globalization;
using Npgsql;

namespace Isolation.Tests;

/// <summary>Runs one generated INSERT on the scope's connection and transaction, and returns the row count.</summary>
internal delegate Task<int> TenantInsertExecutor(string sql, (string Name, object Value)[] parameters);

/// <summary>One generated row: the values, the INSERT statement that writes it, and its bound parameters.</summary>
internal sealed class GeneratedRow
{
    private GeneratedRow(IReadOnlyDictionary<string, object> values, string insertSql, (string Name, object Value)[] parameters)
    {
        Values = values;
        InsertSql = insertSql;
        Parameters = parameters;
    }

    /// <summary>Every value keyed by column name — the parent side of a later FK lookup.</summary>
    internal IReadOnlyDictionary<string, object> Values { get; }

    /// <summary>The INSERT statement (parameterised, never interpolated, 00 §5.7 S1).</summary>
    internal string InsertSql { get; }

    /// <summary>The bound parameters of <see cref="InsertSql"/>.</summary>
    internal (string Name, object Value)[] Parameters { get; }

    /// <summary>Builds the statement from the generated values, in the table's column order so a failure reads deterministically.</summary>
    internal static GeneratedRow Build(TenantSchema schema, string table, IReadOnlyDictionary<string, object> values)
    {
        var columns = schema.ColumnsOf(table).Where(column => values.ContainsKey(column.Name)).ToArray();
        var parameters = new (string Name, object Value)[columns.Length];
        var placeholders = new string[columns.Length];

        for (var index = 0; index < columns.Length; index++)
        {
            var name = "p" + index.ToString(CultureInfo.InvariantCulture);
            parameters[index] = (name, values[columns[index].Name]);
            placeholders[index] = "@" + name + Cast(columns[index]);
        }

        var insert =
            $"insert into {table} ({string.Join(", ", columns.Select(column => column.Name))}) "
            + $"values ({string.Join(", ", placeholders)})";

        return new GeneratedRow(values, insert, parameters);
    }

    /// <summary>The store-type cast a parameter needs when Npgsql would infer a different type than the column's.</summary>
    private static string Cast(ScannedColumn column) => column.StoreType switch
    {
        "jsonb" => "::jsonb",
        "text[]" => "::text[]",
        _ => string.Empty,
    };
}

/// <summary>
/// 02.9.1-T2.1: the generic valid-row generator of the composite-FK suite. It has
/// no per-table knowledge — the whole point of the auto-discovery is that adding a
/// table must not mean editing the suite — and it derives everything from the
/// catalogue (00 §5.4, §7):
/// <list type="bullet">
/// <item><description>
/// <b>Foreign keys</b>: every column of every foreign key is filled from a parent
/// row created recursively for the same org, so an FK can never be the reason the
/// insert fails. A key that lands on the tenant root (its principal carries no
/// <c>org_id</c> and the dependent column is <c>org_id</c> itself) is generated with
/// the parent's <c>id</c> set to the org — the root row <em>is</em> the org (TL T4).
/// </description></item>
/// <item><description>
/// <b>NOT NULL without a default</b>: the column is filled by store type —
/// <c>uuid</c> from <c>Guid.CreateVersion7()</c> (00 §7, application-generated
/// UUIDv7), text from a lowercase-alphanumeric candidate, timestamps in the near
/// future, and the common scalar types from a fixed value. Columns with a default
/// or identity clause are omitted so the database fills them, which keeps the
/// generated row inside the 00 §7 conventions (<c>created_at</c>, <c>version</c>).
/// </description></item>
/// <item><description>
/// <b>Check constraints</b>: resolved by generating values in the shapes this
/// schema declares — text of 13 lowercase alphanumeric characters satisfies the
/// <c>char_length … between 1 and 128</c> and <c>^[a-z0-9][a-z0-9-]{1,62}$</c>
/// checks of 0004/0005, and a NOT NULL timestamp is generated in the near future,
/// which satisfies <c>idempotency_record</c>'s <c>expires_at &gt;= created_at + 24
/// hours</c> check. A constraint the candidates do not satisfy is not guessed at:
/// the insert is wrapped and re-thrown naming the table, its generated columns and
/// the violated constraint.
/// </description></item>
/// <item><description>
/// <b>Types it does not know</b> fail loudly
/// (<see cref="InvalidOperationException"/>) rather than skipping the table.
/// </description></item>
/// </list>
/// <para>
/// The insert runs through the caller's executor — always the <c>evidgate_app</c>
/// connection inside an <c>ITenantScope</c> transaction (00 §5.5 C3), never the
/// owner: with FORCE row level security the owner policy is permissive, so a
/// generated row that is only "valid" for the owner would prove nothing about the
/// path the API uses. A generated row is committed only by the caller's
/// <c>CommitAsync</c>; the suite never commits.
/// </para>
/// </summary>
internal sealed class TenantRowFactory
{
    // A fresh number per generated revision: corrupting one key reuses the row's other parents, so a constant would collide.
    private static int _revisionNo;

    private readonly TenantSchema _schema;
    private readonly TenantInsertExecutor _insert;
    private readonly Dictionary<(string Table, Guid Org), GeneratedRow> _created = [];

    internal TenantRowFactory(TenantSchema schema, TenantInsertExecutor insert)
    {
        _schema = schema;
        _insert = insert;
    }

    /// <summary>
    /// Generates a valid row for <paramref name="table"/> under
    /// <paramref name="org"/> and inserts it, creating any missing parent rows for
    /// the same org first. Returns the row so its values can seed a later corrupt
    /// attempt.
    /// </summary>
    internal Task<GeneratedRow> CreateAsync(string table, Guid org, CancellationToken cancellationToken) =>
        CreateAsync(table, org, forcedId: null, cancellationToken);

    /// <summary>
    /// Generates the same shape as <see cref="CreateAsync"/> for
    /// <paramref name="org"/> — so the row satisfies its own tenant and every other
    /// constraint — except that every foreign key to a table inside the tenant set
    /// is filled from the parents created for <paramref name="parentOrg"/>. The
    /// result is the corrupt row T2.1 asserts on: filed under one org while pointing
    /// at another org's parent. It is not inserted; the caller runs it and asserts
    /// the composite FK rejects it. With <paramref name="corruptedForeignKey"/> set, only that
    /// foreign key is moved to the other org (a table with two tenant parents is checked once per key,
    /// so the rejection can be attributed); null moves every tenant key.
    /// </summary>
    /// <remarks>
    /// Parents are read from the rows already created (never created here): the
    /// caller creates the row for <paramref name="parentOrg"/> first, and a missing
    /// parent is a generator bug, not something to paper over with an insert the
    /// transaction's tenant setting would reject.
    /// </remarks>
    internal async Task<GeneratedRow> PrepareForeignOrgRowAsync(
        string table,
        Guid org,
        Guid parentOrg,
        string? corruptedForeignKey,
        CancellationToken cancellationToken) =>
        GeneratedRow.Build(
            _schema,
            table,
            await ResolveAsync(table, org, parentOrg, forcedId: null, createMissingParents: false, cancellationToken, corruptedForeignKey));

    private async Task<GeneratedRow> CreateAsync(
        string table,
        Guid org,
        Guid? forcedId,
        CancellationToken cancellationToken)
    {
        var values = await ResolveAsync(table, org, org, forcedId, createMissingParents: true, cancellationToken);
        var row = GeneratedRow.Build(_schema, table, values);

        int affected;
        try
        {
            affected = await _insert(row.InsertSql, row.Parameters);
        }
        catch (PostgresException error)
        {
            throw new InvalidOperationException(
                $"the generated row for {table} (org {org}) was rejected by the database: {error.SqlState} "
                + $"{error.ConstraintName} {error.MessageText} — generated columns: "
                + string.Join(", ", row.Parameters.Select(parameter => parameter.Name)),
                error);
        }

        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"inserting the generated row into {table} (org {org}) affected {affected} rows, expected 1");
        }

        _created[(table, org)] = row;
        return row;
    }

    /// <summary>
    /// Resolves every value of one row: foreign-key columns from
    /// <paramref name="tenantParentOrg"/>'s parent rows, the row's own
    /// <c>org_id</c> from <paramref name="org"/>, and the remaining NOT NULL,
    /// default-less columns from the store-type rules.
    /// </summary>
    private async Task<Dictionary<string, object>> ResolveAsync(
        string table,
        Guid org,
        Guid tenantParentOrg,
        Guid? forcedId,
        bool createMissingParents,
        CancellationToken cancellationToken,
        string? corruptedForeignKey = null)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);

        // The row is filed under its own org. A table whose org_id is part of its
        // root foreign key gets the same value from the parent below (the root row's
        // id IS the org, TL T4); a table with no foreign key at all (rls_probe) has
        // only this line to give it its tenant — which is also what makes the corrupt
        // row differ from a valid one in exactly one value.
        if (_schema.HasOrgId(table))
        {
            values[TenantFkScan.TenantColumn] = org;
        }

        foreach (var foreignKey in _schema.ForeignKeysOf(table))
        {
            // A nullable reference is left null unless it is the key under test: following it could loop forever, through
            // a self-reference (ingest_token.rotated_to_id) or a cycle of tables (project.default_environment_id ->
            // environment -> project).
            var corrupted = corruptedForeignKey is null || string.Equals(corruptedForeignKey, foreignKey.Name, StringComparison.Ordinal);
            var ownColumns = foreignKey.Columns
                .Where(column => !string.Equals(column, TenantFkScan.TenantColumn, StringComparison.Ordinal))
                .ToArray();
            if (!(corruptedForeignKey is not null && corrupted)
                && ownColumns.Length > 0
                && ownColumns.All(column => !_schema.ColumnsOf(table).Single(c => string.Equals(c.Name, column, StringComparison.Ordinal)).NotNull))
            {
                continue;
            }

            // A foreign key to another tenant table is the one the corruption moves
            // to the other org; every other parent (the tenant root, a catalogue
            // row) belongs to the row's own org.
            var principalIsTenantTable = _schema.HasOrgId(foreignKey.PrincipalTable);
            var parentOrg = principalIsTenantTable && corrupted ? tenantParentOrg : org;
            var rootReference = !principalIsTenantTable
                && Array.IndexOf(foreignKey.Columns, TenantFkScan.TenantColumn) >= 0;

            var parent = await ResolveParentAsync(
                foreignKey.PrincipalTable,
                parentOrg,
                rootReference ? parentOrg : null,
                createMissingParents,
                cancellationToken);

            for (var index = 0; index < foreignKey.Columns.Length; index++)
            {
                var column = foreignKey.Columns[index];

                if (string.Equals(column, TenantFkScan.TenantColumn, StringComparison.Ordinal))
                {
                    // Already the row's own org (set above); the corruption is that the
                    // rest of the composite key still points at the other org's parent.
                    continue;
                }

                if (!parent.Values.TryGetValue(foreignKey.PrincipalColumns[index], out var parentValue))
                {
                    throw new InvalidOperationException(
                        $"the generated {foreignKey.PrincipalTable} row for org {parentOrg} has no value for "
                        + $"'{foreignKey.PrincipalColumns[index]}', which {table}.{column} ({foreignKey.Name}) references");
                }

                values[column] = parentValue;
            }
        }

        foreach (var column in _schema.ColumnsOf(table))
        {
            if (values.ContainsKey(column.Name) || !column.NotNull || column.HasDefault)
            {
                continue;
            }

            var isOwnId = string.Equals(column.Name, TenantFkScan.TenantKeyColumn, StringComparison.Ordinal);
            values[column.Name] = Value(table, column, isOwnId ? forcedId : null);
        }

        return values;
    }

    /// <summary>The parent row for <paramref name="org"/>: the generated one, or (when allowed) a freshly created and inserted one.</summary>
    private async Task<GeneratedRow> ResolveParentAsync(
        string table,
        Guid org,
        Guid? forcedId,
        bool createMissingParents,
        CancellationToken cancellationToken)
    {
        if (_created.TryGetValue((table, org), out var existing))
        {
            return existing;
        }

        if (!createMissingParents)
        {
            throw new InvalidOperationException(
                $"no {table} row was generated for org {org}: the corrupt row's parents must already exist "
                + "(create the valid row for both orgs before asking for the corruption)");
        }

        return await CreateAsync(table, org, forcedId, cancellationToken);
    }

    /// <summary>One value for a NOT NULL column the database cannot fill; the store type decides.</summary>
    private static object Value(string table, ScannedColumn column, Guid? forcedId)
    {
        if (forcedId is { } id && string.Equals(column.Name, TenantFkScan.TenantKeyColumn, StringComparison.Ordinal))
        {
            return id;
        }

        // audit_log.actor_type is constrained to a closed list (migration 0015).
        if (string.Equals(table, "public.audit_log", StringComparison.Ordinal)
            && string.Equals(column.Name, "actor_type", StringComparison.Ordinal))
        {
            return "system";
        }

        // Columns whose CHECK constraints name a closed vocabulary or a shape (migrations 0016-0024).
        var special = (table, column.Name) switch
        {
            ("public.ci_binding", "provider") or ("public.ci_token_issue", "provider") => "github_actions",
            ("public.ingest_token", "prefix") => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12],
            ("public.ingest_token", "scopes") => new[] { "run:create" },
            ("public.test_case", "first_seen_commit") => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12],
            ("public.case_identity_source", "source") => "annotation",
            ("public.test_revision", "commit_sha") => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12],
            ("public.test_revision", "revision_hash" or "content_sha256" or "assertion_hash") => "sha256:" + new string('a', 64),
            ("public.suite_revision", "revision_hash") or ("public.variant", "variant_hash") => "sha256:" + new string('a', 64),
            ("public.suite", "slug") => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12],
            ("public.environment_revision", "revision_no") => Interlocked.Increment(ref _revisionNo),
            ("public.manifest", "manifest_hash") or ("public.environment_revision", "revision_hash") =>
                "sha256:" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ("public.policy_revision", "hash") =>
                "sha256:" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            _ => (object?)null,
        };

        if (special is not null)
        {
            return special;
        }

        return column.StoreType switch
        {
            "bytea" => new byte[32],
            "uuid" => Guid.CreateVersion7(),
            "text" or "character varying" => "probe" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8],
            "jsonb" => "{}",
            "timestamp with time zone" => DateTimeOffset.UtcNow.AddDays(400),
            "date" => DateOnly.FromDateTime(DateTime.UtcNow),
            "integer" => 1,
            "smallint" => (short)1,
            "bigint" => 1L,
            "numeric" => 1m,
            "boolean" => false,
            "text[]" => new[] { "probe" },
            _ => throw new InvalidOperationException(
                $"TenantRowFactory has no value rule for {table}.{column.Name} of type '{column.StoreType}', which is NOT NULL "
                + "without a default: add its store type to the generator instead of skipping the table"),
        };
    }
}
