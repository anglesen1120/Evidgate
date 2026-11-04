using System.ComponentModel.DataAnnotations;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The two cancel windows of a run, bound from <c>EVIDGATE:Runs:Cancel</c> and validated at boot like every other
/// <c>EVIDGATE__*</c> group. The request is accepted immediately; these windows act on <b>execution</b> only, never on
/// <c>run.state</c>, which is already terminal when they start.
/// <para>
/// <see cref="SoftStopDelay"/> is how long a producer or agent gets to stop on its own before the kill deadline, and
/// <see cref="KillDelay"/> is measured from the same accepted request. Both are relative to the request because a
/// deadline is stored on the row: an absolute timestamp that a restart can re-read, never an in-memory timer.
/// </para>
/// </summary>
internal sealed class RunCancelOptions : IValidatableObject
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:Runs:Cancel";

    /// <summary>
    /// The soft-stop window: <c>requested_at + SoftStopDelay</c> is stored as <c>soft_stop_deadline_at</c>. Default 30 s
    /// — long enough for a producer's current event batch to commit, short enough that an operator sees the stop begin.
    /// </summary>
    [RunDuration]
    public TimeSpan SoftStopDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The kill window: <c>requested_at + KillDelay</c> is stored as <c>kill_at</c>, the instant execution is stopped
    /// whatever it is doing. Default 120 s, i.e. 90 s after the soft-stop deadline of the default windows.
    /// </summary>
    [RunDuration]
    public TimeSpan KillDelay { get; init; } = TimeSpan.FromSeconds(120);

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        ArgumentNullException.ThrowIfNull(validationContext);

        // A kill deadline before the soft-stop deadline would stop execution before the graceful window it is meant to
        // follow, which inverts the two stages of the cancel instead of merely shortening them.
        if (KillDelay < SoftStopDelay)
        {
            yield return new ValidationResult(
                $"{nameof(KillDelay)} must be at least {nameof(SoftStopDelay)} (EVIDGATE:Runs:Cancel).",
                [nameof(KillDelay)]);
        }
    }
}
