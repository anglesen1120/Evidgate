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

    /// <summary>
    /// A foreign-key violation, optionally of a named constraint: one insert can violate several foreign keys, and a
    /// route answers each differently (an unknown project is 404, a vanished actor row is not).
    /// </summary>
    /// <param name="exception">The failed save.</param>
    /// <param name="constraint">The constraint the failure must be about, or null for any of them.</param>
    public static bool IsForeignKey(DbUpdateException exception, string? constraint = null) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } pg
        && (constraint is null || string.Equals(pg.ConstraintName, constraint, StringComparison.Ordinal));
}
