using System.ComponentModel.DataAnnotations;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// Boot validation of a run duration: more than zero and at most 366 days (the scale of the 525600-minute late-window
/// cap). A non-positive value would put a deadline at or before the timestamp it is derived from, and an extreme value
/// would overflow the <c>interval</c> arithmetic that computes it. <see cref="RangeAttribute"/> cannot carry a
/// <see cref="TimeSpan"/>, so the bound is a dedicated attribute; it runs through each options class's
/// <c>ValidateDataAnnotations</c> at start-up.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class RunDurationAttribute : ValidationAttribute
{
    /// <summary>The inclusive upper bound: 366 days.</summary>
    private static readonly TimeSpan Max = TimeSpan.FromDays(366);

    /// <summary>
    /// The default wording, set as the message rather than through an override of <see cref="FormatErrorMessage(string)"/>:
    /// a use site that names the configuration key in <see cref="ValidationAttribute.ErrorMessage"/> must replace this
    /// text, not be silently replaced by it.
    /// </summary>
    public RunDurationAttribute()
        : base("{0} must be greater than 00:00:00 and at most 366 days.")
    {
    }

    /// <inheritdoc />
    public override bool IsValid(object? value)
    {
        // A setting this attribute guards may have no default at all: the binder then leaves the property at
        // TimeSpan.Zero, which is the value a missing key binds to and which the bound below refuses like a
        // configured zero. A null (an unbound nullable member) is refused for the same reason.
        return value is TimeSpan duration && duration > TimeSpan.Zero && duration <= Max;
    }
}
