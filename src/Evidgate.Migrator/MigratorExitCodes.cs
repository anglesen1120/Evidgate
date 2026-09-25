namespace Evidgate.Migrator;

/// <summary>
/// Process exit codes of <see cref="MigratorRunner"/> (01.4.1 §5). They are a
/// contract: the deploy gate (10.2.2-T1) decides on them, so a new outcome needs
/// a new documented code rather than a reinterpretation of an existing one.
/// </summary>
public static class MigratorExitCodes
{
    /// <summary>The requested command completed; for a default run, every pending migration is applied.</summary>
    public const int Success = 0;

    /// <summary>Invalid invocation, missing/unreadable configuration, or an invalid migration set (bad name, duplicate number, gap).</summary>
    public const int InvalidInvocation = 1;

    /// <summary>An already-applied file was edited or removed — checksum drift. Nothing is applied.</summary>
    public const int ChecksumMismatch = 2;

    /// <summary>A migration script failed; its transaction was rolled back and it was not journaled.</summary>
    public const int MigrationFailed = 3;

    /// <summary>
    /// The database could not be reached, or the migration journal could not be
    /// prepared or read (permission denied, missing schema, timeout).
    /// </summary>
    public const int DatabaseUnavailable = 4;
}
