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
internal sealed partial class TenantRowFactory
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

    // A fresh extraction slot number per generated slot row (migration 0133): the slot is unique per org, so a
    // constant would collide with the valid row the corrupt one is generated next to before the FK is reached.
    private static int _intakeSlot;

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
    /// <remarks>
    /// <c>support_grant</c> (0115) is the second entry for a different reason: its <c>operator_user_id</c> is
    /// NOT NULL and references <c>public."user"</c>, which the app role deliberately holds no INSERT on
    /// (02.9.1 §8 T1), so the generated row needs a platform identity parent only the owner can write.
    /// <c>tracker_oauth_state</c> (0139) is the third, for the same reason: its <c>user_id</c> references the same table.
    /// </remarks>
    internal static readonly IReadOnlySet<string> OwnerProvisionedTables =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "public.healing_llm_budget",
            "public.support_grant",
            "public.tracker_oauth_state",
        };

    /// <summary>
    /// The tenant tables a generated row cannot be written for at all: the parent table's trigger writes
    /// them, and they are append-only afterwards. <c>run_state_event</c> is the first — its rows come from
    /// the trigger on <c>run</c>, the app role holds SELECT only, and the table refuses UPDATE and DELETE for
    /// every role, so a generated insert would fail and a cleanup delete would have to disarm the guard.
    /// Suites that seed one row per tenant table skip these and assert them by name instead; a generated row
    /// for one of them is a bug in the suite, so <see cref="CreateAsync(string, Guid, CancellationToken)"/>
    /// refuses it rather than producing a misleading failure.
    /// </summary>
    internal static readonly IReadOnlySet<string> TriggerWrittenTables =
        new HashSet<string>(StringComparer.Ordinal) { "public.run_state_event" };

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
        if (TriggerWrittenTables.Contains(table))
        {
            throw new InvalidOperationException(
                $"{table} is written by its parent's trigger and is append-only: no generated row exists for it, "
                + "so a suite must skip it (TriggerWrittenTables) instead of inserting one");
        }

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
    /// One more row of <paramref name="table"/> for <paramref name="org"/> beside the cached one, for a suite that
    /// needs a sibling the valid row does not name (another project, case or planned execution).
    /// </summary>
    internal Task<GeneratedRow> CreateBesideAsync(string table, Guid org, CancellationToken cancellationToken) =>
        CreateBesideCacheAsync(table, org, cancellationToken);
}
