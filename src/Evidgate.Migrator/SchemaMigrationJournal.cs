using System.Data;
using DbUp.Engine;
using Npgsql;

namespace Evidgate.Migrator;

/// <summary>
/// DbUp 7 journal on <c>public.schema_migration</c> (01.4.1-T1.2, ADR-0003).
/// DbUp's own journal stores names only ⚠️, so this one records the SHA-256 of
/// the file bytes and the script's own duration; that is what makes editing an
/// applied migration detectable (AC3) and what <c>--status</c> reads.
/// </summary>
/// <remarks>
/// Called by DbUp from inside the per-script transaction
/// (<c>WithTransactionPerScript</c>), so a journal row and the schema change it
/// describes commit together — a failed script leaves neither (AC2).
/// </remarks>
internal sealed class SchemaMigrationJournal : IJournal
{
    private readonly MigratorOptions _options;
    private readonly IReadOnlyDictionary<string, string> _checksums;
    private readonly ScriptTimingPreprocessor _timing;
    private readonly Action<string> _log;

    internal SchemaMigrationJournal(
        MigratorOptions options,
        IReadOnlyDictionary<string, string> checksums,
        ScriptTimingPreprocessor timing,
        Action<string> log)
    {
        _options = options;
        _checksums = checksums;
        _timing = timing;
        _log = log;
    }

    /// <inheritdoc />
    public void EnsureTableExistsAndIsLatestVersion(Func<IDbCommand> dbCommandFactory)
    {
        ArgumentNullException.ThrowIfNull(dbCommandFactory);
        using var command = dbCommandFactory();
        command.CommandType = CommandType.Text;
        command.CommandText = SchemaMigrationStore.CreateTableSql();
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void StoreExecutedScript(SqlScript script, Func<IDbCommand> dbCommandFactory)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(dbCommandFactory);

        if (!_checksums.TryGetValue(script.Name, out var checksum))
        {
            throw new InvalidOperationException(
                $"no loaded checksum for script '{script.Name}'; the journal refuses to record an unverified migration");
        }

        using var command = dbCommandFactory();
        command.CommandType = CommandType.Text;
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        command.CommandText =
            $"insert into \"{MigratorOptions.Schema}\".\"{MigratorOptions.JournalTableName}\" " +
            "(name, checksum, applied_at, duration_ms) values (@name, @checksum, now(), @duration_ms)";

        AddParameter(command, "name", script.Name);
        AddParameter(command, "checksum", checksum);
        AddParameter(command, "duration_ms", (object?)_timing.ElapsedMilliseconds ?? DBNull.Value);
        command.ExecuteNonQuery();

        _log($"journalled {script.Name}");
    }

    /// <summary>
    /// Applied script names. DbUp calls this outside the script transaction, so
    /// it opens its own short-lived connection; on a pristine database the table
    /// may not exist yet and an empty journal is the correct answer.
    /// </summary>
    public string[] GetExecutedScripts()
    {
        try
        {
            using var connection = new NpgsqlConnection(_options.SessionConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandTimeout = _options.CommandTimeoutSeconds;
            command.CommandText =
                $"select name from \"{MigratorOptions.Schema}\".\"{MigratorOptions.JournalTableName}\" order by name";
            using var reader = command.ExecuteReader();

            var names = new List<string>();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }

            return [.. names];
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return [];
        }
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
