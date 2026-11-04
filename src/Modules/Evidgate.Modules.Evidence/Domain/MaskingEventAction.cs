namespace Evidgate.Modules.Evidence.Domain;

/// <summary>
/// The closed list of <c>masking_event.action</c>: what the scan decided for a pattern class it matched. <c>flagged</c>
/// is alert-and-classify — the artifact stays evidence and is served as sensitive — and <c>rejected</c> is the
/// project's <c>reject_on_secret</c> moving the artifact out of evidence. The migration's CHECK carries the same two
/// words, so a value the database would refuse never reaches a statement.
/// </summary>
internal static class MaskingEventAction
{
    /// <summary>A match was classified and the artifact stays usable.</summary>
    public const string Flagged = "flagged";

    /// <summary>A match caused the artifact's rejection; the object is deleted by the retention job.</summary>
    public const string Rejected = "rejected";

    /// <summary>The two actions, in the order a scan reports them.</summary>
    public static readonly IReadOnlyList<string> All = [Flagged, Rejected];

    /// <summary>True when <paramref name="value"/> is one of the two actions.</summary>
    /// <param name="value">The candidate action.</param>
    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>The action, or an <see cref="ArgumentOutOfRangeException"/> when it is outside the closed list.</summary>
    /// <param name="value">The candidate action.</param>
    public static string Ensure(string value) =>
        IsKnown(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "action is outside the masking_event closed list");
}
