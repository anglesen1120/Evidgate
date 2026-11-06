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
/// future, and the common scalar types from a fixed value. A type modifier is
/// dropped before the match, so <c>numeric(12,6)</c> is the <c>numeric</c> rule and
/// <c>character varying(64)</c> the <c>character varying</c> one. Columns with a default
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
/// The insert runs through the caller's executor — normally the <c>evidgate_app</c>
/// connection inside an <c>ITenantScope</c> transaction (00 §5.5 C3), not the
/// owner: with FORCE row level security the owner policy is permissive, so a
/// generated row that is only "valid" for the owner would prove nothing about the
/// path the API uses. The one exception is a table in
/// <see cref="OwnerProvisionedTables"/>: <c>0054</c> keeps both NOT NULL caps of
/// <c>healing_llm_budget</c> outside the app role's INSERT grant (its header's "the
/// cap writer is owed"), so the generated row can only be provisioned by the owner
/// path and a suite that needs it must pass the owner connection for that table —
/// its reads stay app-role. A generated row is committed only by the caller's
/// <c>CommitAsync</c>; the RLS suite is the one place that commits (an owner seed has
/// to be visible to the app-role read in another transaction) and it deletes the
/// seeded rows again before the case ends.
/// </para>
/// </summary>
internal sealed class TenantRowFactory
{
    // A fresh number per generated revision: corrupting one key reuses the row's other parents, so a constant would collide.
    private static int _revisionNo;

    // A fresh attempt number per generated attempt row: case_attempt's unique key is
    // (org_id, run_id, execution_key, attempt_no), and the corrupt row reuses the valid row's
    // run_id and execution_key, so a constant would collide on that key before the composite FK
    // under test is reached.
    private static int _attemptNo;

    // A fresh sibling position and a fresh case code per generated authoring row (migration 0039): the live
    // sibling index is unique over (org_id, project_id, parent_id, position) with NULL parents equal, and
    // manual_test_case's code is unique per project, so a constant would collide with the valid row the
    // corrupt one is generated next to.
    private static int _authoringPosition;

    private static int _authoringCode;

    // A fresh case-code prefix per generated project: 0039 makes `case_prefix` NOT NULL with the
    // grammar ^[A-Z][A-Z0-9]{1,7}$ and a per-org unique index, and each generated project row is a
    // different one, so a constant is either invalid or a duplicate ("P0001", "P0002", …).
    private static int _casePrefix;

    // A fresh sequence per generated event row: event's unique key is
    // (org_id, run_id, producer_id, sequence), and the corrupt row reuses the valid row's run_id and
    // producer_id (both the manifest-derived "shard-1"), so a constant would collide on that key before
    // the composite FK under test is reached.
    private static long _sequence;

    // A fresh evidence revision per generated evaluation row: its partial live key is (org_id, run_id,
    // evidence_revision), and the corrupt row reuses the valid row's run.
    private static int _evaluationRevision;

    private readonly TenantSchema _schema;
    private readonly TenantInsertExecutor _insert;
    private readonly Dictionary<(string Table, Guid Org), GeneratedRow> _created = [];

    /// <summary>
    /// The tenant tables whose generated row only the owner can provision, because a NOT NULL column
    /// the generator must fill is outside <c>evidgate_app</c>'s INSERT grant. <c>0054</c> is the first:
    /// it grants the app role INSERT on the five identity/period columns of
    /// <c>healing_llm_budget</c> and deliberately leaves both NOT NULL caps out (its header's "the cap
    /// writer is owed"), so its generated row is refused for the app role and exists only on the owner
    /// path. A suite that derives its tables from the catalogue passes the owner connection for these
    /// tables (an ordinal lookup on this set) and keeps its
    /// reads on <c>evidgate_app</c>; the moment the provisioning path lands with an app-callable seam,
    /// this entry is what has to be removed.
    /// </summary>
    internal static readonly IReadOnlySet<string> OwnerProvisionedTables =
        new HashSet<string>(StringComparer.Ordinal) { "public.healing_llm_budget" };

    // The rows currently being built, outermost first: a foreign-key cycle that reaches the same
    // (table, org) twice must fail one test instead of recursing until the host's stack dies.
    private readonly List<(string Table, Guid Org)> _creating = [];

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
        var key = (Table: table, Org: org);
        var position = _creating.IndexOf(key);
        if (position >= 0)
        {
            throw new InvalidOperationException(
                "the generated-row walk revisits "
                + string.Join(" -> ", _creating.Skip(position).Select(step => $"{step.Table} (org {step.Org})"))
                + $" -> {table} (org {org}): this foreign-key cycle has no valid row to generate");
        }

        _creating.Add(key);
        try
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

            _created[key] = row;
            return row;
        }
        finally
        {
            _creating.RemoveAt(_creating.Count - 1);
        }
    }

    /// <summary>
    /// Creates one more row of a parent table for <paramref name="org"/> without taking it from, or leaving it
    /// in, the factory's cache: a corrupt row needs a parent the valid row does not name, and the cache must
    /// keep meaning "the valid row".
    /// </summary>
    private async Task<GeneratedRow> CreateBesideCacheAsync(string table, Guid org, CancellationToken cancellationToken)
    {
        var key = (Table: table, Org: org);
        var hadCached = _created.TryGetValue(key, out var cached);
        _created.Remove(key);

        try
        {
            return await CreateAsync(table, org, forcedId: null, cancellationToken);
        }
        finally
        {
            if (hadCached)
            {
                _created[key] = cached!;
            }
            else
            {
                _created.Remove(key);
            }
        }
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

        // The columns of the foreign key the caller corrupts, so the collision guard below never moves
        // a value that is the corruption itself. The key itself is resolved after every other one (see
        // below), because a key that shares a column with another key of the row
        // (authoring_change_target_fk carries project_id) would otherwise have that column resolved a
        // second time from the row's own org and the corruption would be repaired before the insert,
        // making a row the suite expects rejected look valid.
        string[] corruptedColumns = [];
        (ScannedForeignKey Key, GeneratedRow Parent)? corruption = null;

        void Assign(ScannedForeignKey foreignKey, GeneratedRow parent, Guid parentOrg, bool onlyUnset, ref bool assigned)
        {
            for (var index = 0; index < foreignKey.Columns.Length; index++)
            {
                var column = foreignKey.Columns[index];

                if (string.Equals(column, TenantFkScan.TenantColumn, StringComparison.Ordinal))
                {
                    // Already the row's own org (set above); the corruption is that the
                    // rest of the composite key still points at the other org's parent.
                    continue;
                }

                if (onlyUnset && values.ContainsKey(column))
                {
                    continue;
                }

                if (!parent.Values.TryGetValue(foreignKey.PrincipalColumns[index], out var parentValue))
                {
                    throw new InvalidOperationException(
                        $"the generated {foreignKey.PrincipalTable} row for org {parentOrg} has no value for "
                        + $"'{foreignKey.PrincipalColumns[index]}', which {table}.{column} ({foreignKey.Name}) references");
                }

                values[column] = parentValue;
                assigned = true;
            }
        }

        foreach (var foreignKey in _schema.ForeignKeysOf(table))
        {
            // MATCH SIMPLE (the only match type in this schema): a composite key with a NULL member
            // is not enforced, so such a reference is left NULL unless it is the key under test.
            // Following it can also loop forever — a self-reference (feature_node.parent_id,
            // ingest_token.rotated_to_id) or a cycle of tables (project.default_environment_id ->
            // environment -> project). `feature_node_parent_fk` is exactly the trap: its other
            // member `project_id` is NOT NULL, so an `All` test here followed `parent_id` into a
            // second `feature_node`, whose own parent was followed again (CompositeFkTests.cs:141
            // uses the same `Any` rule).
            var corrupted = corruptedForeignKey is null || string.Equals(corruptedForeignKey, foreignKey.Name, StringComparison.Ordinal);
            if (corrupted && corruptedForeignKey is not null)
            {
                corruptedColumns = foreignKey.Columns;
            }
            var ownColumns = foreignKey.Columns
                .Where(column => !string.Equals(column, TenantFkScan.TenantColumn, StringComparison.Ordinal))
                .ToArray();
            if (!(corruptedForeignKey is not null && corrupted)
                && ownColumns.Length > 0
                && ownColumns.Any(column => !_schema.ColumnsOf(table).Single(c => string.Equals(c.Name, column, StringComparison.Ordinal)).NotNull))
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

            if (corrupted && corruptedForeignKey is not null)
            {
                // Its parent is kept and written after the loop; see the comment above.
                corruption = (foreignKey, parent);
                continue;
            }

            var assigned = false;
            Assign(foreignKey, parent, parentOrg, onlyUnset: false, ref assigned);
        }

        if (corruption is { } deferred)
        {
            // The corruption takes only the columns no other key filled, so the keys it shares a column
            // with keep their own org's parent and stay satisfied. When every one of its columns was
            // already filled the corruption would vanish and the row would be inserted, so it then takes
            // all of them and the foreign key is the rejection the caller asserts.
            var assigned = false;
            Assign(deferred.Key, deferred.Parent, tenantParentOrg, onlyUnset: true, ref assigned);
            if (!assigned)
            {
                Assign(deferred.Key, deferred.Parent, tenantParentOrg, onlyUnset: false, ref assigned);
            }
        }

        // policy_activation's unique key is (org_id, project_id): one activation per project. A row that shares a
        // project with another row of the same org trips that key (23505) before the composite foreign key the
        // suite checks, so each generated row gets a project of its own. When the project foreign key is the one
        // under test, the project id IS the corruption and must keep pointing at the other org's project.
        if (string.Equals(table, "public.policy_activation", StringComparison.Ordinal)
            && !string.Equals(corruptedForeignKey, "policy_activation_project_fk", StringComparison.Ordinal))
        {
            var ownProject = await CreateAsync("public.project", org, forcedId: null, cancellationToken);
            values["project_id"] = ownProject.Values[TenantFkScan.TenantKeyColumn];
        }

        // quarantine_entry's partial unique index (org_id, project_id, case_id) WHERE state = 'active'
        // is not a composite-FK target, so the schema scan above never sees it — but it would reject the
        // corrupt row with 23505 before the composite FK under test is reached: the corrupt row differs
        // from the valid row inserted just before it only in `owner`, and the app role cannot write
        // `state` to move it out of the index (its INSERT grant omits that column). The corrupt row
        // therefore takes a case of its own org, so the composite FK is the only rejection.
        if (corruptedForeignKey is not null
            && string.Equals(table, "public.quarantine_entry", StringComparison.Ordinal)
            && !string.Equals(corruptedForeignKey, "quarantine_entry_case_fk", StringComparison.Ordinal))
        {
            var ownCase = await CreateAsync("public.test_case", org, forcedId: null, cancellationToken);
            values["case_id"] = ownCase.Values[TenantFkScan.TenantKeyColumn];
        }

        // raw_report (0078) names a parser release in the append-only report_parser catalogue, which the app
        // role may read but never write (0075), so a generated pair would have to be a release the migration
        // seeded. `unsupported` is the outcome whose row needs no parser at all: the pair stays null and the
        // refusal carries the empty counters and discrepancy list the factory already produces.
        if (string.Equals(table, "public.raw_report", StringComparison.Ordinal))
        {
            values["outcome"] = "unsupported";
            values["reason"] = "no_parser";
            values["producer_kind"] = "pytest-junitxml";
        }

        // raw_report's (org_id, artifact_id, parser_id, parser_version) key is a partial unique index, so the
        // duplicate repair below (non-partial keys only) cannot see it: a corrupt row reusing the valid row's
        // artifact would be answered with 23505 before the composite key under test. A second artifact of the
        // row's own org keeps that key distinct; when the artifact foreign key IS the corruption, its parent is
        // the value the suite asserts on and stays.
        if (corruptedForeignKey is not null
            && string.Equals(table, "public.raw_report", StringComparison.Ordinal)
            && !string.Equals(corruptedForeignKey, "raw_report_artifact_fk", StringComparison.Ordinal))
        {
            var ownArtifact = await CreateBesideCacheAsync("public.artifact", org, cancellationToken);
            values["artifact_id"] = ownArtifact.Values[TenantFkScan.TenantKeyColumn];
        }

        // raw_report_reprocess (0081) pins the release it will parse with, so a generated row must name one the
        // migration seeded: the catalogue is owner-written, so the factory cannot mint a pair. Its natural key is a
        // plain unique (no partial index), so a corrupt row reusing the valid row's artifact needs a second artifact
        // of its own org for the same reason as raw_report's, and a source report of its own too.
        if (string.Equals(table, "public.raw_report_reprocess", StringComparison.Ordinal))
        {
            values["parser_id"] = "junit-pytest";
            values["parser_version"] = "1.0.0";
        }

        if (corruptedForeignKey is not null
            && string.Equals(table, "public.raw_report_reprocess", StringComparison.Ordinal)
            && !string.Equals(corruptedForeignKey, "raw_report_reprocess_artifact_fk", StringComparison.Ordinal))
        {
            var ownArtifact = await CreateBesideCacheAsync("public.artifact", org, cancellationToken);
            values["artifact_id"] = ownArtifact.Values[TenantFkScan.TenantKeyColumn];
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

        // artifact.object_key is a function of the row's own identity (0028's shape CHECK): the
        // server-chosen key embeds org, project, run, execution key, attempt and id, so the generic
        // per-column rule cannot produce it — derive it from the values just resolved.
        if (string.Equals(table, "public.artifact", StringComparison.Ordinal))
        {
            values["object_key"] = $"org/{values[TenantFkScan.TenantColumn]}/project/{values["project_id"]}"
                + $"/run/{values["run_id"]}/attempt/{values["execution_key"]}/{values["attempt_no"]}"
                + $"/{values[TenantFkScan.TenantKeyColumn]}";
        }

        // usage_ledger_entry's adjustment CHECK (0046) demands a reason exactly when adjustment_of is
        // set. The corrupt row of the composite-FK suite fills adjustment_of from the other org's row,
        // so the reason has to travel with it — otherwise the CHECK (23514) would answer before the
        // foreign key (23503) this suite asserts.
        if (string.Equals(table, "public.usage_ledger_entry", StringComparison.Ordinal)
            && values.ContainsKey("adjustment_of"))
        {
            values["reason"] = "generated adjustment";
        }

        // A corrupt row is the valid row with one parent moved to the other org. When that parent is not
        // part of a unique key of the row's table, the corrupt row duplicates the valid row under that
        // key, and PostgreSQL answers 23505 before the composite foreign key under test: manual_case_link
        // moves only linked_by_membership_id, so `manual_case_link_uq` still sees the valid row's project,
        // manual case and test case. Such a key gets one of the row's other tenant parents regenerated for
        // the row's own org, so the key differs from the valid row's while the row stays valid everywhere
        // else (a same-org parent is what the valid row names) and the corrupted key remains the only
        // rejection. Only a tenant parent can be regenerated here: a catalogue parent is not org-scoped and
        // a generated scalar already differs from the valid row's.
        if (corruptedForeignKey is not null && _created.TryGetValue((table, org), out var validRow))
        {
            foreach (var uniqueKey in _schema.UniqueKeysOf(table))
            {
                var duplicatesValidRow = uniqueKey.Columns.All(column =>
                    values.TryGetValue(column, out var value)
                    && validRow.Values.TryGetValue(column, out var validValue)
                    && Equals(value, validValue));
                if (!duplicatesValidRow)
                {
                    continue;
                }

                // One parent is enough to move the key, and the narrowest key is the least it can disturb.
                // A candidate must share a non-tenant column with the key: `org_id` is in every composite key
                // and in every tenant foreign key, so an overlap on it alone would leave the key exactly as
                // it was. A foreign key that shares a column with the corruption is never touched.
                var candidates = _schema.ForeignKeysOf(table)
                    .Where(foreignKey => foreignKey.Columns.All(values.ContainsKey))
                    .Where(foreignKey => foreignKey.Columns.Any(column =>
                        !string.Equals(column, TenantFkScan.TenantColumn, StringComparison.Ordinal)
                        && uniqueKey.Columns.Contains(column)))
                    .Where(foreignKey => _schema.HasOrgId(foreignKey.PrincipalTable))
                    .Where(foreignKey => !foreignKey.Columns
                        .Where(column => !string.Equals(column, TenantFkScan.TenantColumn, StringComparison.Ordinal))
                        .Intersect(corruptedColumns, StringComparer.Ordinal)
                        .Any())
                    .OrderBy(foreignKey => foreignKey.Columns.Length)
                    .ThenBy(foreignKey => foreignKey.Name, StringComparer.Ordinal)
                    .ToArray();

                var moved = false;
                foreach (var replacement in candidates)
                {
                    // The valid row's own parent cannot be reused: its key is exactly what duplicates, so a
                    // second parent of the same org is created beside the cached one.
                    var parent = await CreateBesideCacheAsync(replacement.PrincipalTable, org, cancellationToken);

                    for (var index = 0; index < replacement.Columns.Length; index++)
                    {
                        if (!string.Equals(replacement.Columns[index], TenantFkScan.TenantColumn, StringComparison.Ordinal))
                        {
                            values[replacement.Columns[index]] = parent.Values[replacement.PrincipalColumns[index]];
                        }
                    }

                    // The replacement must actually move the key: one that left every key column equal to
                    // the valid row's would still answer 23505, so the next candidate gets its turn.
                    moved = !uniqueKey.Columns.All(column =>
                        values.TryGetValue(column, out var value)
                        && validRow.Values.TryGetValue(column, out var validValue)
                        && Equals(value, validValue));
                    if (moved)
                    {
                        break;
                    }
                }

                if (moved)
                {
                    break;
                }
            }
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

        // Columns whose CHECK constraints name a closed vocabulary or a shape (migrations 0016-0031).
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
            ("public.fixture", "fixture_key") or ("public.fixture_revision", "fixture_key") =>
                "fixture" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12],
            ("public.fixture_revision", "revision_hash") => "sha256:" + new string('a', 64),
            ("public.fixture_revision", "seed_version") => "v1",
            ("public.fixture_revision", "cleanup_policy") => "drop",
            ("public.fixture_revision", "resource_prefix") => "fx_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..16],
            ("public.fixture_revision", "body_ref") => "seeds/" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8] + ".json",
            ("public.environment_revision", "revision_no") => Interlocked.Increment(ref _revisionNo),
            ("public.manifest", "manifest_hash") or ("public.environment_revision", "revision_hash") =>
                "sha256:" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ("public.policy_revision", "hash") =>
                "sha256:" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ("public.adapter_capability", "mode") => "full",
            ("public.waiver", "state") => "requested",
            ("public.waiver", "scope") => "case",
            ("public.quarantine_entry", "state") => "active",
            ("public.artifact", "kind") => "trace",
            ("public.artifact", "execution_key") => new string('a', 64),
            ("public.artifact", "checksum") => "sha256:" + new string('b', 64),
            ("public.artifact", "sensitivity") => "normal",
            ("public.artifact", "scan_state") => "pending",
            ("public.artifact", "state") => "pending_upload",
            ("public.artifact_kind_policy", "kind") => "trace",
            ("public.artifact_kind_policy", "default_sensitivity") => "normal",
            // redaction_rule (0071) closes `kind` to the eight contract kinds and `rule_type` to the four-type
            // allowlist, so the generic text candidate is rejected by both CHECKs; `name_pattern` stays generic on
            // purpose — 13 lowercase alphanumerics satisfy the 1..128 bounded-charset CHECK, and a value unique per
            // generated row keeps the (org_id, project_id, kind, rule_type, name_pattern) key from colliding with the
            // valid row the corrupt one is compared to.
            ("public.redaction_rule", "kind") => "trace",
            ("public.redaction_rule", "rule_type") => "header",
            ("public.producer_slot", "shard_index") => 1,
            ("public.feature_node", "kind") => "module",
            ("public.feature_node", "position") or ("public.checklist_item", "position") =>
                Interlocked.Increment(ref _authoringPosition),
            ("public.checklist_item" or "public.manual_test_case", "priority") => "high",
            ("public.checklist_item" or "public.manual_test_case", "state") => "draft",
            ("public.checklist_item" or "public.manual_test_case", "source") or ("public.authoring_version", "source") => "manual",
            ("public.manual_test_case", "steps") => "{}",
            ("public.manual_test_case", "code") =>
                "PROBE-" + Interlocked.Increment(ref _authoringCode).ToString("D3", CultureInfo.InvariantCulture),
            ("public.authoring_version", "entity_kind") => "feature_node",
            ("public.authoring_version", "change_kind") => "create",
            ("public.authoring_version", "content_hash") => "sha256:" + new string('c', 64),
            // authoring_change (0067) closes change_type to four tokens and wants a non-empty
            // affected_versions object, so the generic text and '{}' candidates are both rejected before
            // a composite FK under test is reached. A comment payload needs its body and no other member,
            // and a comment is the one type whose pending row never takes the structural-overlap lock.
            ("public.authoring_change", "change_type") => "comment",
            ("public.authoring_change", "payload") => "{\"body\":\"probe\"}",
            ("public.authoring_change", "affected_versions") =>
                "{\"" + Guid.CreateVersion7().ToString("D", CultureInfo.InvariantCulture) + "\":1}",
            ("public.project", "case_prefix") =>
                "P" + Interlocked.Increment(ref _casePrefix).ToString("D4", CultureInfo.InvariantCulture),
            ("public.producer_slot", "producer_id") => "shard-1",
            // The usage ledger, roll-up and quota counter (0046/0047/0077) close their vocabulary in
            // CHECKs the generic text rule cannot satisfy: `metric` is the plan §4 list and `period` is
            // its `YYYY-MM` shape. `billing_key` stays generic on purpose — the generic candidate is 13
            // characters and unique per generated row, which is what `unique (org_id, billing_key)` needs
            // so the corrupt row never trips the dedupe key before the composite FK under test.
            ("public.usage_ledger_entry" or "public.usage_rollup" or "public.quota_counter", "metric") => "results",
            ("public.usage_ledger_entry" or "public.usage_rollup" or "public.quota_counter", "period") => "2026-10",
            // usage_price (0073) closes `metric` to the same list and its `currency` to three uppercase
            // letters, which the generic text candidate (13 chars) fails; every other constrained member is
            // either nullable (`effective_to`, `included_quota`) or satisfied by the generic candidate
            // (`unit_price`'s 1, the identity `id`, and `effective_from`'s timestamp).
            ("public.usage_price", "metric") => "results",
            ("public.usage_price", "currency") => "USD",
            // evidence_revision (0049) closes `completeness` to the 00 §8.5 pair, so the generic text
            // candidate is rejected there; `manifest_hash` is generic on purpose (13 characters is
            // inside the 1-200 CHECK) and `missing` keeps its '{}' default, which is what the
            // "complete has no missing codes" CHECK requires. `revision_no` is pinned to 1 because the
            // table's BEFORE INSERT trigger refuses anything but the run's max + 1: every generated
            // revision row is the first revision of a run this factory created for the same org, so a
            // shared counter (environment_revision above) would hand a fresh run revision 2 and the
            // trigger would answer 23514 before the composite FK under test is reached.
            ("public.evidence_revision", "completeness") => "complete",
            ("public.evidence_revision", "revision_no") => 1,
            // evaluation (0059) closes completeness, outcome, decision and mode to the evaluator's wire
            // tokens, and its two content addresses to the 00 §7 sha256 shape: the generic text candidate
            // satisfies none of them. `missing`/`reasons` keep their '{}' default (a CHECK rejects the
            // generic {"probe"} element), and the live key is satisfiable because every generated row gets
            // a freshly created run and policy revision.
            ("public.evaluation", "completeness") => "complete",
            ("public.evaluation", "outcome") => "all_passed",
            ("public.evaluation", "decision") => "PASS",
            ("public.evaluation", "mode") => "live",
            // A fresh frozen revision per generated row: evaluation's live key is
            // (org_id, run_id, evidence_revision) where mode = 'live', and the corrupt row reuses the valid
            // row's run, so a constant would collide on that index (23505) before the composite FK under test
            // is reached.
            ("public.evaluation", "evidence_revision") => Interlocked.Increment(ref _evaluationRevision),
            ("public.evaluation", "policy_hash" or "exceptions_hash") =>
                "sha256:" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            // The LLM gateway tables (0054) close `purpose`, `status` and `period` in CHECKs the generic
            // text candidate cannot satisfy. `purpose` is deliberately the one value whose row needs NO
            // incident: `incident_id`/`candidate_id` are nullable and therefore left unset, so
            // healing_llm_call_incident_required_check holds (it demands an incident only for the other
            // purposes) together with the authoring CHECK that wants both attribution columns null.
            ("public.healing_llm_call", "purpose") => "authoring_generation",
            ("public.healing_llm_call", "status") => "ok",
            ("public.healing_llm_budget", "period") => "daily",
            // planned_execution's execution_key is the 00 §7 sha256 hex and its unique key is
            // (org_id, run_id, execution_key), so a constant would collide with the valid row the
            // corrupt attempt inserts just before it: a fresh 64-hex value per generated row.
            ("public.planned_execution", "execution_key") =>
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ("public.planned_execution", "variant_hash") => "sha256:" + new string('a', 64),
            ("public.planned_execution", "shard_id") => "shard-1",
            // case_attempt's execution_key is the 00 §7 sha256 hex and its unique key is
            // (org_id, run_id, execution_key, attempt_no), so a constant would collide with the valid
            // row the corrupt attempt inserts just before it: a fresh 64-hex value per generated row.
            // Its composite FK to planned_execution fills the column from the parent, so this rule is
            // only reached when the parent lookup did not set it.
            ("public.case_attempt", "execution_key") =>
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            // case_attempt's unique key is (org_id, run_id, execution_key, attempt_no) and the corrupt
            // row reuses the valid row's run_id and execution_key, so a constant attempt_no would
            // collide on that key (23505) before the composite FK under test is reached: a fresh
            // number per generated row, like planned_execution's execution_key above.
            ("public.case_attempt", "attempt_no") => Interlocked.Increment(ref _attemptNo),
            ("public.case_attempt", "raw_status") => "passed",
            ("public.case_attempt", "normalized_status") => "passed",
            // event's execution_key is the 00 §7 sha256 hex (0034 CHECK ^[0-9a-f]{64}$), its type is the
            // envelope's dotted lower_snake shape and its payload_hash is the 00 §7 RFC 8785 content
            // address (0034 CHECK ^sha256:[0-9a-f]{64}$): the generic text rule satisfies none of the three.
            ("public.event", "execution_key") => new string('a', 64),
            ("public.event", "type") => "test.step",
            ("public.event", "payload_hash") => "sha256:" + new string('a', 64),
            // event's unique key is (org_id, run_id, producer_id, sequence) and the corrupt row reuses the
            // valid row's run_id and producer_id, so a constant sequence would collide on that key (23505)
            // before the composite FK under test is reached: a fresh number per generated row, like
            // case_attempt's attempt_no above.
            ("public.event", "sequence") => Interlocked.Increment(ref _sequence),
            // project_ai_settings (0064) is the append-only guidance chain. Its `content_hash` is the 00 §7 content
            // address (CHECK ^sha256:[0-9a-f]{64}$) and `languages` is closed to the two G1 locales with a
            // cardinality 1..2 / `<@ {vi,en}` CHECK, so the generic text and text[] candidates are both rejected by
            // the table before a composite FK under test is reached. Every other constrained member has a default
            // (`description`, `writing_style`, … are `not null default ''`), and `template_code`/`template_version`
            // are nullable and written as a pair, so leaving them unset satisfies `project_ai_settings_template_check`.
            // `version_no` stays the generic 1: the unique key is (org_id, project_id, version_no) and the corrupt row
            // differs from the valid one in its org and parent, never only in the version.
            ("public.project_ai_settings", "content_hash") => "sha256:" + new string('d', 64),
            ("public.project_ai_settings", "languages") => new[] { "en" },
            // masking_event (0066) closes `pattern_class` to the scanner's three classes and `action` to the scan's
            // two decisions, so the generic text candidate satisfies neither CHECK; `hit_count`'s generic 1 already
            // satisfies `hit_count > 0`. The composite FK fills `artifact_id` from the parent's tenant.
            ("public.masking_event", "pattern_class") => "aws_access_key",
            ("public.masking_event", "action") => "flagged",
            // release_component (0072) closes `digest` to the content-address grammar (CHECK
            // ^sha256:[0-9a-f]{64}$), which the generic text candidate satisfies nowhere. `component_key`'s
            // candidate is 13 lowercase alphanumerics and fits the key grammar; the composite FKs fill
            // `release_id` and `environment_revision_id` from parents of the same org, and the trigger allows
            // the insert because every generated parent release is a draft.
            ("public.release_component", "digest") => "sha256:" + new string('e', 64),
            // billing_statement closes `kind` to the one statement kind, `period` to the `YYYY-MM` shape,
            // `currency` to three uppercase letters and `content_hash` to the lowercase sha256 hex, so the
            // generic text candidate satisfies none of them. The generic `{}` candidate is an object and
            // inside the document's size cap, and `total_amount`/`line_count` accept the generic numbers.
            ("public.billing_statement", "kind") => "invoice",
            ("public.billing_statement", "period") => "2026-10",
            ("public.billing_statement", "currency") => "USD",
            ("public.billing_statement", "content_hash") => new string('a', 64),
            _ => (object?)null,
        };

        if (special is not null)
        {
            return special;
        }

        return WithoutTypeModifier(column.StoreType) switch
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

    /// <summary>
    /// The store type without its modifier, so one rule covers every precision and length:
    /// <c>numeric(12,6)</c> (0054's caps and costs) and the parameterised <c>character varying(64)</c> or
    /// <c>timestamp(3) with time zone</c> forms match the <c>numeric</c>/<c>character varying</c>/
    /// <c>timestamp with time zone</c> arms instead of falling through to the "no value rule" error.
    /// </summary>
    private static string WithoutTypeModifier(string storeType)
    {
        var open = storeType.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return storeType;
        }

        var close = storeType.IndexOf(')', open);
        return close < 0
            ? storeType
            : (storeType[..open] + storeType[(close + 1)..]).Trim();
    }
}
