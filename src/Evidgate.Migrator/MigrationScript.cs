namespace Evidgate.Migrator;

/// <summary>
/// One <c>migrations/NNNN_&lt;module&gt;_&lt;change&gt;.sql</c> file, loaded and
/// validated. <see cref="Checksum"/> is the lowercase hex SHA-256 of the file
/// bytes as stored (a UTF-8 BOM is part of the checksum but is stripped from
/// <see cref="Contents"/> before the SQL runs).
/// </summary>
/// <param name="Number">The 4-digit sequence number, a contiguous run from 1.</param>
/// <param name="Name">The file name, including <c>.sql</c> — also the journal key.</param>
/// <param name="Path">The full path the file was read from.</param>
/// <param name="Checksum">Lowercase hex SHA-256 of the file bytes.</param>
/// <param name="Contents">The script text executed verbatim.</param>
public sealed record MigrationScript(
    int Number,
    string Name,
    string Path,
    string Checksum,
    string Contents);
