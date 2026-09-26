using System.Text.RegularExpressions;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The single place an exception becomes <c>task.last_error</c> (0008) and a
/// <c>TaskDeadLetterAlert.LastError</c>: first line only, secret-shaped
/// assignments masked, bounded to the column's check length (00 §5.7 S3).
/// Persistence deliberately does not reference Observability, so the mask
/// mirrors <c>SecretRedactor.RedactText</c>'s pattern set here (the Migrator
/// carries the same trade-off in miniature) — if the pattern list grows,
/// extract a shared kernel rather than letting the copies drift.
/// </summary>
internal static class TaskErrorText
{
    /// <summary>Matches the <c>task_last_error_length_check</c> of 0008.</summary>
    internal const int MaxLength = 1000;

    private const string Mask = "***";

    private const string SecretKeys =
        "password|pwd|accountkey|sharedaccesskey|secret|token|apikey|api_key|key";

    private static readonly Regex QuotedSecretAssignment = new(
        $@"(?<key>{SecretKeys})\s*=\s*(?<quote>"")(?<value>(?:[^""]|"""")*)\k<quote>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SingleQuotedSecretAssignment = new(
        $@"(?<key>{SecretKeys})\s*=\s*(?<quote>')(?<value>(?:[^']|'')*)\k<quote>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SecretAssignment = new(
        $@"(?<key>{SecretKeys})\s*=\s*(?<value>[^;\r\n]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AuthorizationHeader = new(
        @"(?<key>authorization)\s*:\s*(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Only the first line of the message survives; the type name carries the category.</summary>
    internal static string Redact(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = exception.Message;
        var lineBreak = message.AsSpan().IndexOfAny('\r', '\n');
        var head = lineBreak < 0 ? message : message[..lineBreak];
        var text = head.Length == 0 ? exception.GetType().Name : $"{exception.GetType().Name}: {head}";

        // last_error is persisted and served on the operator list, so masking is
        // mechanism, not a convention on handler authors (security review wave 5).
        text = MaskSecrets(text);

        return text.Length <= MaxLength ? text : text[..MaxLength];
    }

    private static string MaskSecrets(string text)
    {
        var redacted = QuotedSecretAssignment.Replace(text, "${key}=" + Mask);
        redacted = SingleQuotedSecretAssignment.Replace(redacted, "${key}=" + Mask);
        redacted = SecretAssignment.Replace(redacted, "${key}=" + Mask);

        return AuthorizationHeader.Replace(redacted, "${key}: " + Mask);
    }
}
