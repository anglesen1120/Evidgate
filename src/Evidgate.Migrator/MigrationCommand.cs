namespace Evidgate.Migrator;

/// <summary>What the process was asked to do (01.4.1 §5).</summary>
public enum MigrationCommand
{
    /// <summary>Apply every pending migration, one transaction per file.</summary>
    Apply,

    /// <summary>Print applied, pending and drifted files; exit non-zero on drift. Applies nothing.</summary>
    Status,

    /// <summary>Print the pending files without touching the database. Applies nothing.</summary>
    DryRun,
}
