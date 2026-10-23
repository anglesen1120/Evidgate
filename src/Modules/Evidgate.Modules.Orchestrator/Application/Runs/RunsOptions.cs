using System.ComponentModel.DataAnnotations;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The run-create defaults of 05.2.1 (00 §5.5 C6, ruling T93), bound from <c>EVIDGATE:Runs</c>. They apply when the
/// project has no active policy revision, or when the pinned revision's body does not set the matching value; an active
/// revision's <c>collection_timeout_minutes</c>/<c>late_window_minutes</c> replace them (07.1.1-T4.2).
/// </summary>
internal sealed class RunsOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:Runs";

    /// <summary>
    /// The collection deadline offset applied when no policy revision is pinned, or when the pinned revision's body
    /// does not set <c>collection_timeout_minutes</c> (T93): <c>created_at</c> plus this value. Default 2 h (⚠️ tuned
    /// ≈ p95 × 1.5). Bounded by <see cref="CollectionTimeoutAttribute"/>: a non-positive value would put
    /// <c>collection_deadline_at</c> at or before <c>created_at</c> on an immutable column, and an extreme value would
    /// overflow the <c>interval</c> arithmetic that computes it.
    /// </summary>
    [CollectionTimeout]
    public TimeSpan DefaultCollectionTimeout { get; init; } = TimeSpan.FromHours(2);

    /// <summary>
    /// The late-window snapshot (T93, T62) written when no policy revision is pinned, or when the pinned revision's
    /// body does not set <c>late_window_minutes</c>: default 1440 minutes (24 h). Immutable after create — no command
    /// updates it.
    /// </summary>
    [Range(1, 525_600)]
    public int DefaultLateWindowMinutes { get; init; } = 1440;
}

/// <summary>
/// Boot validation of <see cref="RunsOptions.DefaultCollectionTimeout"/>: more than zero and at most 366 days
/// (the scale of the 525600-minute late-window cap). <see cref="RangeAttribute"/> cannot carry a
/// <see cref="TimeSpan"/>, so the bound is a dedicated attribute; it runs through the module's
/// <c>ValidateDataAnnotations</c> at start-up, exactly like <see cref="RunsOptions.DefaultLateWindowMinutes"/>'s
/// <see cref="RangeAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class CollectionTimeoutAttribute : ValidationAttribute
{
    /// <summary>The inclusive upper bound: 366 days.</summary>
    private static readonly TimeSpan Max = TimeSpan.FromDays(366);

    /// <inheritdoc />
    public override bool IsValid(object? value)
    {
        // A missing value is fine: the property keeps its valid default, and the binder only overwrites it.
        if (value is null)
        {
            return true;
        }

        return value is TimeSpan timeout && timeout > TimeSpan.Zero && timeout <= Max;
    }

    /// <inheritdoc />
    public override string FormatErrorMessage(string name) =>
        $"{name} must be greater than 00:00:00 and at most 366 days (EVIDGATE:Runs:DefaultCollectionTimeout).";
}
