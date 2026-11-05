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
/// <para>
/// Neither window has a default. The kill deadline is the moment a request still stopping is recorded <c>killed</c>,
/// so the operator of a deployment states both windows instead of inheriting a number this code chose for them: a
/// missing key binds to zero and fails the boot validation below, which is the same refusal a zero or negative value
/// gets.
/// </para>
/// </summary>
internal sealed class RunCancelOptions : IValidatableObject
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:Runs:Cancel";

    /// <summary>
    /// The soft-stop window: <c>requested_at + SoftStopDelay</c> is stored as <c>soft_stop_deadline_at</c> — long
    /// enough for a producer's current event batch to commit, short enough that an operator sees the stop begin.
    /// </summary>
    /// <remarks>
    /// <see cref="RequiredAttribute"/> is the declaration the configuration lint reads; a <see cref="TimeSpan"/> can
    /// never be null, so the value a missing key binds — zero — is what <see cref="RunDurationAttribute"/> refuses.
    /// </remarks>
    [Required(ErrorMessage = "EVIDGATE__RUNS__CANCEL__SOFTSTOPDELAY is required")]
    [RunDuration(
        ErrorMessage = "EVIDGATE__RUNS__CANCEL__SOFTSTOPDELAY must be set and greater than 00:00:00 and at most 366 days.")]
    public TimeSpan SoftStopDelay { get; init; }

    /// <summary>
    /// The kill window: <c>requested_at + KillDelay</c> is stored as <c>kill_at</c>, the instant a request still
    /// stopping is recorded <c>killed</c>, and the moment the durable kill task becomes claimable.
    /// </summary>
    /// <remarks>
    /// Required for the same reason as <see cref="SoftStopDelay"/>: no default, and zero is refused.
    /// </remarks>
    [Required(ErrorMessage = "EVIDGATE__RUNS__CANCEL__KILLDELAY is required")]
    [RunDuration(
        ErrorMessage = "EVIDGATE__RUNS__CANCEL__KILLDELAY must be set and greater than 00:00:00 and at most 366 days.")]
    public TimeSpan KillDelay { get; init; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        ArgumentNullException.ThrowIfNull(validationContext);

        // A kill deadline before the soft-stop deadline would stop execution before the graceful window it is meant to
        // follow, which inverts the two stages of the cancel instead of merely shortening them.
        if (KillDelay < SoftStopDelay)
        {
            yield return new ValidationResult(
                "EVIDGATE__RUNS__CANCEL__KILLDELAY must be at least EVIDGATE__RUNS__CANCEL__SOFTSTOPDELAY "
                + "(EVIDGATE:Runs:Cancel).",
                [nameof(KillDelay)]);
        }
    }
}
