using Npgsql;

namespace Isolation.Tests;

/// <summary>One table the scan can see, and whether it carries the tenant column.</summary>
internal sealed record ScannedTable(string Name, bool HasOrgId);

/// <summary>
/// One column of a scanned table, with exactly what the row generator needs:
/// the store type, whether it is NOT NULL, and whether the database can fill it
/// (a default or an identity clause) — a NOT NULL column without a default is the
/// one the generator must produce a value for.
/// </summary>
internal sealed record ScannedColumn(string Table, string Name, string StoreType, bool NotNull, bool HasDefault);

/// <summary>
/// One foreign key of the live catalogue: the dependent table and columns and the
/// principal table and columns, positionally paired (<c>Columns[i]</c> references
/// <c>PrincipalColumns[i]</c>).
/// </summary>
internal sealed record ScannedForeignKey(string Table, string Name, string PrincipalTable, string[] Columns, string[] PrincipalColumns);

/// <summary>One valid, immediate, non-partial unique key, as its column list sorted ordinal (so comparisons are order-insensitive).</summary>
internal sealed record ScannedUniqueKey(string Table, string[] Columns);

/// <summary>
/// The relation catalogue of one database as the composite-FK suite reads it
/// (02.9.1-T2.1). One query per relation kind, indexed once by table name so the
/// scan and the row generator never re-read the catalogue per table — the whole
/// schema is small, but the accessors are called per column and per key.
/// </summary>
internal sealed class TenantSchema
{
    private readonly Dictionary<string, List<ScannedColumn>> _columns;
    private readonly Dictionary<string, List<ScannedForeignKey>> _foreignKeys;
    private readonly Dictionary<string, List<ScannedUniqueKey>> _uniqueKeys;
    private readonly HashSet<string> _tablesWithOrgId;

    private TenantSchema(
        IReadOnlyList<ScannedTable> tables,
        IReadOnlyList<ScannedColumn> columns,
        IReadOnlyList<ScannedForeignKey> foreignKeys,
        IReadOnlyList<ScannedUniqueKey> uniqueKeys)
    {
        Tables = tables;
        _columns = Group(columns, column => column.Table);
        _foreignKeys = Group(foreignKeys, key => key.Table);
        _uniqueKeys = Group(uniqueKeys, key => key.Table);
        _tablesWithOrgId = tables.Where(table => table.HasOrgId).Select(table => table.Name).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every base table in a non-system schema, in name order.</summary>
    internal IReadOnlyList<ScannedTable> Tables { get; }

    internal IReadOnlyList<ScannedColumn> ColumnsOf(string table) => Find(_columns, table);

    internal IReadOnlyList<ScannedForeignKey> ForeignKeysOf(string table) => Find(_foreignKeys, table);

    internal IReadOnlyList<ScannedUniqueKey> UniqueKeysOf(string table) => Find(_uniqueKeys, table);

    /// <summary>True when <paramref name="table"/> carries an <c>org_id</c> column (00 §5.4.1).</summary>
    internal bool HasOrgId(string table) => _tablesWithOrgId.Contains(table);

    /// <summary>
    /// Reads the catalogue. <paramref name="transaction"/> is the transaction the
    /// fixture tables live in, when the scan runs inside one: PostgreSQL keeps
    /// uncommitted DDL private to its own session, so the reader must be on the same
    /// connection and transaction the scratch tables were created in.
    /// </summary>
    internal static async Task<TenantSchema> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The relation shapes mirror tests/Persistence.IntegrationTests/SchemaConventionScan.cs
        // (01.4.2-T2.2) — the same catalogue facts, read the same way, so the two
        // scans cannot disagree about what the database contains. Business tables
        // live in `public` (00 §5.4.1, and the exception list's `public.<table>`
        // token), but the scan reads every non-system schema: a table carrying
        // `org_id` elsewhere is still reported rather than silently skipped.
        const string tablesSql = """
            select n.nspname || '.' || c.relname,
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

        const string columnsSql = """
            select n.nspname || '.' || c.relname,
                   a.attname,
                   pg_catalog.format_type(a.atttypid, a.atttypmod),
                   a.attnotnull,
                   a.atthasdef
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

        // conkey/confkey are positional: generate_series with the ordinality keeps
        // each side's declared order, so Columns[i] pairs with PrincipalColumns[i].
        const string foreignKeysSql = """
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

        // Only a valid, immediate, non-partial unique key is a legal composite-FK
        // target (00 §5.4.6); the column list is sorted, so the check is
        // order-insensitive the way PostgreSQL matches a unique index.
        const string uniqueKeysSql = """
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

        var tables = new List<ScannedTable>();
        foreach (var row in await QueryAsync(connection, tablesSql, transaction, cancellationToken))
        {
            tables.Add(new ScannedTable((string)row[0]!, (bool)row[1]!));
        }

        var columns = new List<ScannedColumn>();
        foreach (var row in await QueryAsync(connection, columnsSql, transaction, cancellationToken))
        {
            columns.Add(new ScannedColumn((string)row[0]!, (string)row[1]!, (string)row[2]!, (bool)row[3]!, (bool)row[4]!));
        }

        var foreignKeys = new List<ScannedForeignKey>();
        foreach (var row in await QueryAsync(connection, foreignKeysSql, transaction, cancellationToken))
        {
            foreignKeys.Add(new ScannedForeignKey(
                (string)row[0]!,
                (string)row[1]!,
                (string)row[2]!,
                (string[])row[3]!,
                (string[])row[4]!));
        }

        var uniqueKeys = new List<ScannedUniqueKey>();
        foreach (var row in await QueryAsync(connection, uniqueKeysSql, transaction, cancellationToken))
        {
            var keyColumns = (string[])row[1]!;
            Array.Sort(keyColumns, StringComparer.Ordinal);
            uniqueKeys.Add(new ScannedUniqueKey((string)row[0]!, keyColumns));
        }

        return new TenantSchema(tables, columns, foreignKeys, uniqueKeys);
    }

    private static Dictionary<string, List<T>> Group<T>(IReadOnlyList<T> items, Func<T, string> key)
    {
        var groups = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!groups.TryGetValue(key(item), out var group))
            {
                group = [];
                groups[key(item)] = group;
            }

            group.Add(item);
        }

        return groups;
    }

    private static List<T> Find<T>(Dictionary<string, List<T>> groups, string table) =>
        groups.TryGetValue(table, out var found) ? found : [];

    private static async Task<IReadOnlyList<object?[]>> QueryAsync(
        NpgsqlConnection connection,
        string sql,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<object?[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[index] = await reader.IsDBNullAsync(index, cancellationToken)
                    ? null
                    : await reader.GetFieldValueAsync<object>(index, cancellationToken);
            }

            rows.Add(row);
        }

        return rows;
    }
}
