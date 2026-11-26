namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>
/// The result of one extraction: the text, or exactly one <see cref="IntakeFailureCode"/>. The only ways to build
/// one are <see cref="Ok"/> and <see cref="Refused"/>; <c>default</c> is neither a success nor a refusal.
/// </summary>
/// <param name="Text">The NFC-normalised text on success, otherwise null.</param>
/// <param name="FailureCode">The refusal reason on failure, otherwise null.</param>
internal readonly record struct ExtractionOutcome(string? Text, string? FailureCode)
{
    /// <summary>True only for an outcome built by <see cref="Ok"/>.</summary>
    public bool Succeeded => Text is not null && FailureCode is null;

    /// <summary>A successful extraction.</summary>
    /// <param name="text">The extracted, non-empty text.</param>
    /// <returns>The outcome.</returns>
    public static ExtractionOutcome Ok(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);

        return new ExtractionOutcome(text, null);
    }

    /// <summary>A refusal; the code must be one of <see cref="IntakeFailureCode.All"/>.</summary>
    /// <param name="failureCode">The machine reason.</param>
    /// <returns>The outcome.</returns>
    public static ExtractionOutcome Refused(string failureCode)
    {
        if (!IntakeFailureCode.All.Contains(failureCode))
        {
            throw new ArgumentException("The failure code is not in the closed set.", nameof(failureCode));
        }

        return new ExtractionOutcome(null, failureCode);
    }
}
