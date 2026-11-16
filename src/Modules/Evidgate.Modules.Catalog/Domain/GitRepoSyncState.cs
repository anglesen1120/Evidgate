using System.Globalization;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>
/// Whether installation events let a repository binding synchronize. Only GitHub installation events move it, never an
/// operator: a disabled binding keeps its rows and its catalog while new commits stop being enqueued.
/// </summary>
internal enum GitRepoSyncState
{
    /// <summary>Commits may be synchronized.</summary>
    Active,

    /// <summary>The installation was suspended or removed; nothing new is synchronized.</summary>
    Disabled,
}

/// <summary>The wire and column spelling of <see cref="GitRepoSyncState"/>, so no caller repeats the literals.</summary>
internal static class GitRepoSyncStates
{
    internal const string Active = "active";

    internal const string Disabled = "disabled";

    /// <summary>The column value of <paramref name="state"/>.</summary>
    /// <param name="state">The state to spell.</param>
    internal static string ToWire(GitRepoSyncState state) => state switch
    {
        GitRepoSyncState.Active => Active,
        GitRepoSyncState.Disabled => Disabled,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "unknown sync state"),
    };

    /// <summary>Parses a stored column value; anything but the two known values is refused.</summary>
    /// <param name="value">The stored value.</param>
    internal static bool TryParse(string? value, out GitRepoSyncState state)
    {
        switch (value)
        {
            case Active:
                state = GitRepoSyncState.Active;
                return true;
            case Disabled:
                state = GitRepoSyncState.Disabled;
                return true;
            default:
                state = GitRepoSyncState.Active;
                return false;
        }
    }

    /// <summary>
    /// The secret-store reference of one binding's webhook secret. The name is derived from the binding id, never from
    /// request input, and carries no secret material; the value itself stays in the secret store.
    /// </summary>
    /// <param name="bindingId">The binding the secret belongs to.</param>
    internal static string WebhookSecretReference(Guid bindingId) =>
        string.Create(CultureInfo.InvariantCulture, $"secret:gh_wh_{bindingId:N}");
}
