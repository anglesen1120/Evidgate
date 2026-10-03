using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>Reads PostgreSQL error details off an EF exception, so handlers map constraints to API codes by name.</summary>
internal static class PgErrors
{
    /// <summary>A unique violation, optionally of a named constraint or index.</summary>
    public static bool IsUnique(DbUpdateException exception, string? constraint = null) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && (constraint is null || string.Equals(pg.ConstraintName, constraint, StringComparison.Ordinal));

    /// <summary>A foreign-key violation (e.g. the project does not exist in this tenant).</summary>
    public static bool IsForeignKey(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation };
}
