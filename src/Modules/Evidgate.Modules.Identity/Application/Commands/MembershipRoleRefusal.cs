using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// The database's own refusal of a <c>membership_role</c> row (missing, non-member or foreign-org role),
/// mapped to the 409 the commands already answer when they cannot see the role. It only fires when a role
/// changes between the read and the write, or a write goes around the commands.
/// </summary>
internal static class MembershipRoleRefusal
{
    private const string ForeignKey = "membership_role_role_fk";
    private const string Guard = "membership_role_role_guard";

    /// <summary>The same 409 as an invisible or non-member role.</summary>
    internal static Error UnknownRole => Error.Conflict(
        ApiErrorCode.UnknownRole,
        "the role is neither a member role of the catalogue nor a role of this organization");

    /// <summary>True when the failure is the role key or the role guard trigger of <c>membership_role</c>.</summary>
    internal static bool Matches(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.CheckViolation,
        } postgres
        && postgres.ConstraintName is ForeignKey or Guard;

    /// <summary>The retryable 503 of the rank guard: a role guard lock wait that deadlocked or timed out.</summary>
    internal static Error Contention => MemberRankGuard.Contention;

    /// <summary>True when the grant waited on the role guard lock and lost (<c>40P01</c> or <c>55P03</c>).</summary>
    internal static bool IsContention(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.LockNotAvailable,
        };
}
