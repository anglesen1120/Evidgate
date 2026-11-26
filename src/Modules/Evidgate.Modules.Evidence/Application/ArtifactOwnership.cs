namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// Who may finish a pending upload: only the ingest token that minted it. The completion endpoint and the writer
/// command apply this one rule, so a token of the same project cannot complete or reject another producer's artifact.
/// </summary>
internal static class ArtifactOwnership
{
    /// <summary>
    /// True when <paramref name="caller"/> may complete or reject an artifact owned by <paramref name="owner"/>.
    /// A null owner is a row minted before ownership was recorded: it keeps the earlier rule (any caller already
    /// authorized for the project), because nothing expires a pending upload that could be stranded otherwise.
    /// </summary>
    /// <param name="owner">The row's recorded owner, or null for such a row.</param>
    /// <param name="caller">The caller's stable ingest-token id; null for a principal without one.</param>
    public static bool Permits(string? owner, string? caller) =>
        owner is null || string.Equals(owner, caller, StringComparison.Ordinal);
}
