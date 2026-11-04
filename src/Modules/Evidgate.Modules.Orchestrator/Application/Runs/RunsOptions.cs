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
    /// ≈ p95 × 1.5). Bounded by <see cref="RunDurationAttribute"/>: a non-positive value would put
    /// <c>collection_deadline_at</c> at or before <c>created_at</c> on an immutable column, and an extreme value would
    /// overflow the <c>interval</c> arithmetic that computes it.
    /// </summary>
    [RunDuration]
    public TimeSpan DefaultCollectionTimeout { get; init; } = TimeSpan.FromHours(2);

    /// <summary>
    /// The late-window snapshot (T93, T62) written when no policy revision is pinned, or when the pinned revision's
    /// body does not set <c>late_window_minutes</c>: default 1440 minutes (24 h). Immutable after create — no command
    /// updates it.
    /// </summary>
    [Range(1, 525_600)]
    public int DefaultLateWindowMinutes { get; init; } = 1440;
}
