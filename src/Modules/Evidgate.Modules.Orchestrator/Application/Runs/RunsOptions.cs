using System.ComponentModel.DataAnnotations;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The run-create defaults of 05.2.1 (00 §5.5 C6, ruling T93), bound from <c>EVIDGATE:Runs</c>. The policy-pinned
/// values are not available until 07.1.1-T4, so T1 always uses these defaults and writes
/// <c>run.policy_revision_id = null</c> (hand-over to 07.1.1-T4).
/// </summary>
internal sealed class RunsOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:Runs";

    /// <summary>
    /// The collection deadline offset applied when no policy revision is pinned (T93): <c>created_at</c> plus this
    /// value. Default 2 h (⚠️ tuned ≈ p95 × 1.5).
    /// </summary>
    public TimeSpan DefaultCollectionTimeout { get; init; } = TimeSpan.FromHours(2);

    /// <summary>
    /// The late-window snapshot (T93, T62) written when no policy revision is pinned: default 1440 minutes (24 h).
    /// Immutable after create — no command updates it.
    /// </summary>
    [Range(1, 525_600)]
    public int DefaultLateWindowMinutes { get; init; } = 1440;
}
