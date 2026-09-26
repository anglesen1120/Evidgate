using Npgsql;
using Respawn;
using Respawn.Graph;

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
/// The table list is read once, when the harness starts, from the catalog of the
/// schemas passed in — so a table created later in the same run is not covered.
/// <c>Respawn_LeavesNoRowInAnyApplicationTable</c> is the outcome check for that
/// plan (see <c>tests/README.md</c>).
/// </para>
/// </summary>
public sealed class RespawnCheckpoint
{
    /// <summary>The migration journal Respawn must never touch (01.1.2-T1.3).</summary>
    private static readonly Table[] Preserved = [new Table("public", "schema_migration")];

    private readonly Respawner _respawner;
    private readonly string _connectionString;

    private RespawnCheckpoint(Respawner respawner, string connectionString)
    {
        _respawner = respawner;
        _connectionString = connectionString;
    }

    /// <summary>
    /// Reads the reset plan once (every non-system schema, journal ignored) and
    /// returns a checkpoint that only replays it. Called once per assembly, so the
    /// catalog round trips are not paid per test.
    /// </summary>
    public static async Task<RespawnCheckpoint> CreateAsync(
        string ownerConnectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerConnectionString);

        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var schemas = await SchemasAsync(connection, cancellationToken).ConfigureAwait(false);

        var respawner = await Respawner.CreateAsync(
            connection,
            new RespawnerOptions
            {
                DbAdapter = DbAdapter.Postgres,
                SchemasToInclude = [.. schemas],
                TablesToIgnore = [.. Preserved],
            }).ConfigureAwait(false);

        return new RespawnCheckpoint(respawner, ownerConnectionString);
    }

    /// <summary>Empties every application table, leaving the migration journal alone.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await _respawner.ResetAsync(connection).ConfigureAwait(false);
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
