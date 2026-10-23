namespace Evidgate.Migrator;

/// <summary>One row of <c>public.schema_migration</c> (01.4.1 §4).</summary>
/// <param name="Name">Migration file name including <c>.sql</c>; the primary key.</param>
/// <param name="Checksum">Lowercase hex SHA-256 recorded when the file was applied.</param>
/// <param name="AppliedAt">When the script committed.</param>
/// <param name="DurationMs">How long the script took inside its transaction; null when unknown.</param>
public sealed record JournalEntry(string Name, string Checksum, DateTimeOffset AppliedAt, int? DurationMs);

/// <summary>Why an already-applied migration counts as drifted (01 §5).</summary>
public enum MigrationDriftKind
{
    /// <summary>The file is still there but its bytes changed after it was journaled.</summary>
    Modified,

    /// <summary>The journal has a row whose file no longer exists in the migrations directory.</summary>
    MissingOnDisk,
}

/// <summary>An applied migration that no longer matches the repository.</summary>
/// <param name="Name">File name from the journal.</param>
/// <param name="Kind">Modified or missing.</param>
/// <param name="FileChecksum">Current on-disk checksum; null when the file is gone.</param>
/// <param name="AppliedChecksum">Checksum recorded when the file was applied.</param>
public sealed record MigrationDrift(string Name, MigrationDriftKind Kind, string? FileChecksum, string AppliedChecksum);

/// <summary>The migration set versus the journal, computed before anything is applied.</summary>
/// <param name="Scripts">Every valid script in the directory, ordered by number.</param>
/// <param name="Applied">Journal rows, ordered by name.</param>
/// <param name="Pending">Scripts with no journal row, in apply order.</param>
/// <param name="Drifted">Applied migrations that no longer match the directory.</param>
public sealed record MigrationPlan(
    IReadOnlyList<MigrationScript> Scripts,
    IReadOnlyList<JournalEntry> Applied,
    IReadOnlyList<MigrationScript> Pending,
    IReadOnlyList<MigrationDrift> Drifted);

/// <summary>Outcome of one Migrator run.</summary>
/// <param name="ExitCode">One of <see cref="MigratorExitCodes"/>.</param>
/// <param name="Command">What was asked for.</param>
/// <param name="Messages">The transcript written to the output, one entry per line.</param>
/// <param name="Plan">The computed plan; null when the run failed before the migration set was compared to the journal.</param>
/// <param name="AppliedNow">Scripts applied by this run, in order.</param>
public sealed record MigrationRunResult(
    int ExitCode,
    MigrationCommand Command,
    IReadOnlyList<string> Messages,
    MigrationPlan? Plan,
    IReadOnlyList<string> AppliedNow);
