using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// Recognises the refusal of the <c>artifact_guard_transition</c> trigger (migration 0095), so every writer of
/// <c>artifact.state</c> answers a lost race to an illegal move the same way: as a state conflict, never a 500.
/// </summary>
internal static class ArtifactTransitionFault
{
    private const string MessagePrefix = "artifact_illegal_transition";

    /// <summary>True when <paramref name="exception"/> is, or wraps, the trigger's check violation.</summary>
    /// <param name="exception">What a save raised.</param>
    public static bool IsIllegalTransition(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.CheckViolation,
            MessageText: { } text,
        }
        && text.StartsWith(MessagePrefix, StringComparison.Ordinal);
}
