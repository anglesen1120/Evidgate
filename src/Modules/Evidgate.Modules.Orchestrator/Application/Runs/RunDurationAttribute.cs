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

    /// <inheritdoc />
    public override bool IsValid(object? value)
    {
        // A missing value is fine: the property keeps its valid default, and the binder only overwrites it.
        if (value is null)
        {
            return true;
        }

        return value is TimeSpan duration && duration > TimeSpan.Zero && duration <= Max;
    }

    /// <inheritdoc />
    public override string FormatErrorMessage(string name) =>
        $"{name} must be greater than 00:00:00 and at most 366 days.";
}
