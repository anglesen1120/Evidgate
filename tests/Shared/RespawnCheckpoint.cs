using Npgsql;

namespace Evidgate.Testing;

/// <summary>
/// Respawn over the whole application surface (01.6.1-T2.2, AC2): every table of
/// every non-system schema is emptied between two tests of the shared harness
/// database, and <c>public.schema_migration</c> is preserved so the readiness
/// state the Migrator wrote survives the reset.
/// <para>
/// The reset is a single <c>truncate table … cascade</c> (Respawn 7's PostgreSQL
/// adapter; observed on the pinned version). Two consequences worth knowing:
/// TRUNCATE is not subject to row level security at all, so no policy is needed to
/// make the reset effective; and it requires table <em>ownership</em> (or an
/// explicit TRUNCATE privilege), which is why the checkpoint connects as
/// <c>evidgate_owner</c> — the role that owns every object the migrations created.
/// <c>evidgate_app</c> is the wrong role for it on rights, not on policy: 0001
/// grants it DML, never TRUNCATE, and goes out of its way to revoke TRUNCATE on the
/// journal. The container superuser would work too and is deliberately not used:
/// the harness must not need an escape hatch a real deployment does not have, and
/// the owner is exactly the role a deployment runs the sweeps as (TL T80).
/// </para>
/// <para>
/// Migration-seeded reference data survives the reset (<see cref="SeedOnlyTables"/> are never truncated;
/// <see cref="SeedAndTenantTables"/> are truncated with their tenant rows and the built-in rows, those with a
/// NULL <c>org_id</c>, are put back in the same statement), so a reset database is the freshly migrated one minus
/// every tenant row and a test that names a built-in role or permission never meets an empty catalogue.
/// </para>
/// <para>
/// The table list is read once, when the harness starts, from the catalog of the
/// schemas passed in — so a table created later in the same run is not covered.
/// <c>Respawn_LeavesNoRowInAnyApplicationTable</c> is the outcome check for that
/// plan (see <c>tests/README.md</c>).
/// </para>
/// </summary>
public sealed class RespawnCheckpoint
{
    /// <summary>Tables a migration seeds and no tenant writes: left alone by the reset.</summary>
    public static IReadOnlyList<string> SeedOnlyTables { get; } = ["public.permission", "public.ai_settings_template"];

    /// <summary>
    /// Tables that hold migration-seeded built-in rows (NULL <c>org_id</c>) next to tenant rows. Parents first: the
    /// restore inserts in this order.
    /// </summary>
    public static IReadOnlyList<string> SeedAndTenantTables { get; } =
        ["public.role", "public.role_permission", "public.report_parser"];

    private readonly string _truncate;
    private readonly string _restore;
    private readonly string _connectionString;
    private readonly string[] _snapshots;

    private RespawnCheckpoint(string truncate, string restore, string[] snapshots, string connectionString)
    {
        _truncate = truncate;
        _restore = restore;
        _snapshots = snapshots;
        _connectionString = connectionString;
    }

    /// <summary>
    /// Reads the table list once (every non-system schema, journal excluded) and returns a checkpoint that only replays one
    /// <c>truncate table … cascade</c>. Called once per assembly, so the catalog round trips are not paid per test.
    /// </summary>
    /// <remarks>
    /// Respawn itself is no longer used: with a circular foreign-key pair (<c>project</c> ↔ <c>environment</c>, 05.1.2) it
    /// falls back to <c>alter table … disable trigger all</c>, which only a superuser may run. One TRUNCATE naming every
    /// table is cycle-proof, needs only table ownership, and is what Respawn's single-statement path already did.
    /// </remarks>
    public static async Task<RespawnCheckpoint> CreateAsync(
        string ownerConnectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerConnectionString);

        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var schemas = await SchemasAsync(connection, cancellationToken).ConfigureAwait(false);

        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                select quote_ident(schemaname) || '.' || quote_ident(tablename)
                  from pg_catalog.pg_tables
                 where schemaname = any(@schemas)
                   and not (schemaname = 'public' and tablename = 'schema_migration')
                   and not (schemaname || '.' || tablename = any(@seed_only))
                 order by schemaname, tablename
                """;
            command.Parameters.AddWithValue("schemas", schemas.ToArray());
            command.Parameters.AddWithValue("seed_only", SeedOnlyTables.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }
        }

        if (tables.Count == 0)
        {
            throw new InvalidOperationException("the harness database has no application table to reset");
        }

        var snapshots = await SnapshotBuiltInRowsAsync(connection, cancellationToken).ConfigureAwait(false);

        var restore = string.Concat(SeedAndTenantTables.Select((table, i) =>
            $"; insert into {table} select * from jsonb_populate_recordset(null::{table}, @s{i}::jsonb)"));

        return new RespawnCheckpoint(
            $"truncate table {string.Join(", ", tables)} cascade",
            restore,
            snapshots,
            ownerConnectionString);
    }

    /// <summary>
    /// Empties every tenant row, leaving the migration journal and the migration-seeded reference rows alone.
    /// One command, so the truncate and the restore of the built-in rows commit together.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = _truncate + _restore;
        for (var i = 0; i < _snapshots.Length; i++)
        {
            command.Parameters.AddWithValue($"s{i}", NpgsqlTypes.NpgsqlDbType.Text, _snapshots[i]);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The built-in rows (NULL <c>org_id</c>) of <see cref="SeedAndTenantTables"/> as one JSON array per table, read
    /// while the database is freshly migrated. An empty table fails the start: the migrations seed every one.
    /// </summary>
    private static async Task<string[]> SnapshotBuiltInRowsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var snapshots = new string[SeedAndTenantTables.Count];
        for (var i = 0; i < snapshots.Length; i++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"select coalesce(jsonb_agg(to_jsonb(t)), '[]'::jsonb)::text from {SeedAndTenantTables[i]} t where t.org_id is null";
            snapshots[i] = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

            if (snapshots[i] == "[]")
            {
                throw new InvalidOperationException(
                    $"the built-in rows of {SeedAndTenantTables[i]} are missing at harness start: the migrations seed them");
            }
        }

        return snapshots;
    }

    /// <summary>
    /// Every application schema of the database, discovered instead of hard-coded:
    /// a migration that adds a schema (module-owned schemas are allowed by 0001's
    /// note) is then covered by the reset without an edit here. Catalog schemas and
    /// session temp schemas are excluded.
    /// </summary>
    private static async Task<IReadOnlyList<string>> SchemasAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select nspname
              from pg_catalog.pg_namespace
             where nspname <> 'information_schema'
               and nspname not like 'pg\_%'
             order by nspname
            """;

        var schemas = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            schemas.Add(reader.GetString(0));
        }

        return schemas;
    }
}
