namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// One refusal a rate card earned: a stable reason and the JSON path that caused it. The reason set is
/// closed and machine-readable; the path names the member, never its value, so a refusal can be logged or returned
/// without echoing what the caller sent.
/// </summary>
/// <param name="Path">The JSON pointer of the offending member, e.g. <c>/prices/2/unit_price</c>.</param>
/// <param name="Reason">One of <see cref="UsagePriceRefusalReasons"/>.</param>
internal sealed record UsagePriceViolation(string Path, string Reason);

/// <summary>
/// The reason names a refused card reports. Each one is stable because a caller (or a test) matches on it, and none
/// ever carries the refused value.
/// </summary>
internal static class UsagePriceRefusalReasons
{
    /// <summary>A required member is absent or null.</summary>
    internal const string Required = "required";

    /// <summary>A member is present but is not of the kind or shape the card allows.</summary>
    internal const string InvalidValue = "invalid_value";

    /// <summary>An object carries a member the card does not declare.</summary>
    internal const string UnknownField = "unknown_field";

    /// <summary>An object repeats a member name.</summary>
    internal const string DuplicateMember = "duplicate_member";

    /// <summary>The body is not a JSON document the card profile accepts.</summary>
    internal const string InvalidJson = "invalid_json";

    /// <summary>A metric is not one of the seven the ledger stores.</summary>
    internal const string UnknownMetric = "unknown_metric";

    /// <summary>An amount is negative, exponent-form, not a plain decimal token, or out of the storable bounds.</summary>
    internal const string InvalidAmount = "invalid_amount";

    /// <summary>Two ranges of one metric overlap.</summary>
    internal const string OverlappingRange = "overlapping_range";

    /// <summary>The card version already holds a row for that metric in that scope.</summary>
    internal const string DuplicateVersion = "duplicate_version";

    /// <summary>The card carries no price rows.</summary>
    internal const string Empty = "empty";

    /// <summary>
    /// An instant is not aligned to the microsecond, the finest precision the <c>timestamptz</c> column stores, so the
    /// stored boundary would be rounded away from the instant the card named.
    /// </summary>
    internal const string InvalidInstant = "invalid_instant";

    /// <summary>The card carries more rows than one load accepts.</summary>
    internal const string TooMany = "too_many";

    /// <summary>
    /// The card earned more refusals than one response carries; the walk stopped at the cap rather than returning
    /// every one of them.
    /// </summary>
    internal const string TooManyViolations = "too_many_violations";

    /// <summary>The body is larger than one card may be.</summary>
    internal const string TooLarge = "too_large";

    /// <summary>A text member is longer than its column allows.</summary>
    internal const string TooLong = "too_long";
}
