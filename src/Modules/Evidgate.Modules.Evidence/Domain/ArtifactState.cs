namespace Evidgate.Modules.Evidence.Domain;

/// <summary>The closed list of <c>artifact.state</c> (00 §7, §8.4): <c>pending_upload</c> is the only initial state.</summary>
internal static class ArtifactState
{
    /// <summary>Minted, not yet uploaded; the only state 06.1.1 creates (00 §8.4).</summary>
    public const string PendingUpload = "pending_upload";

    /// <summary>The object is in storage, not yet verified.</summary>
    public const string Uploaded = "uploaded";

    /// <summary>Verified and servable.</summary>
    public const string Ready = "ready";

    /// <summary>Refused by a scan or a size/type check; terminal.</summary>
    public const string Rejected = "rejected";

    /// <summary>Marked for deletion by the retention job (06.4.x).</summary>
    public const string PendingDelete = "pending_delete";

    /// <summary>The object is gone; terminal.</summary>
    public const string Deleted = "deleted";

    /// <summary>The six states of the 00 §8.4 state machine.</summary>
    public static readonly IReadOnlyList<string> All =
        [PendingUpload, Uploaded, Ready, Rejected, PendingDelete, Deleted];

    /// <summary>True when <paramref name="value"/> is one of the six contract states.</summary>
    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// The state, or an <see cref="ArgumentOutOfRangeException"/> when it is outside the closed list (C6).
    /// Null is allowed: the column carries the <c>pending_upload</c> default and the database fills it (06.1.1 §6).
    /// </summary>
    public static string? EnsureIfSet(string? value) =>
        value is null || IsKnown(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "state is outside the 00 §8.4 closed list (06.1.1 §4)");

    /// <summary>
    /// The 00 §8.4 edges, the same list <c>artifact_guard_transition</c> (0095) enforces in SQL; <c>ready → rejected</c> is
    /// the parse verdict on stored bytes. <c>rejected</c> and <c>deleted</c> are terminal.
    /// </summary>
    private static readonly (string From, string To)[] Edges =
    [
        (PendingUpload, Uploaded),
        (PendingUpload, Ready),
        (PendingUpload, Rejected),
        (Uploaded, Ready),
        (Uploaded, Rejected),
        (Ready, Rejected),
        (Ready, PendingDelete),
        (PendingDelete, Deleted),
    ];

    /// <summary>True when <paramref name="from"/> → <paramref name="to"/> is an edge, or the same state (a no-op).</summary>
    /// <param name="from">The stored state; null is the column default, <c>pending_upload</c>.</param>
    /// <param name="to">The state to write.</param>
    public static bool IsAllowedMove(string? from, string to)
    {
        var source = from ?? PendingUpload;

        return string.Equals(source, to, StringComparison.Ordinal)
            || Array.Exists(Edges, edge => edge.From == source && edge.To == to);
    }

    /// <summary>The state, or an <see cref="ArgumentOutOfRangeException"/> when it is outside the closed list (C6).</summary>
    /// <param name="value">The state to check.</param>
    public static string Ensure(string? value) =>
        IsKnown(value)
            ? value!
            : throw new ArgumentOutOfRangeException(nameof(value), value, "state is outside the 00 §8.4 closed list (06.1.1 §4)");
}
