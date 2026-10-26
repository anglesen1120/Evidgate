using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Evidgate.BuildingBlocks.Observability.Redaction;

/// <summary>
/// The export-side half of the S3 redaction list (10.3.1-T1.4, AC2): it runs on every
/// <see cref="LogRecord"/> just before the export processor, and it is the guarantee that
/// no exported log attribute carries a secret — whatever wrote the record.
/// </summary>
/// <remarks>
/// <para>
/// The primary layer is <see cref="RedactingLoggerFactory"/>, which redacts the state before any
/// provider formats it. This processor exists for the paths that layer cannot see: a processor
/// (§10.3.1-T1.2's correlation enricher) that adds an attribute of its own, and the exception
/// object, which a provider renders itself rather than through the state.
/// </para>
/// <para>
/// <see cref="LogRecord.Attributes"/> is a settable property over a read-only list, so the pairs are
/// copied with each value run through <see cref="SecretRedactor.RedactValue"/> and the list is
/// assigned back; <see cref="LogRecord.FormattedMessage"/> and <see cref="LogRecord.Body"/> are
/// re-run through the text rules, because the formatted message embeds the argument values.
/// </para>
/// <para>
/// An exception whose text needed masking is replaced by the semantic attributes the OTLP exporter
/// would have written from it — <c>exception.type</c> (the original type, which a wrapper object
/// cannot report on its own), <c>exception.message</c> and <c>exception.stacktrace</c> — and
/// <see cref="LogRecord.Exception"/> is cleared, so the exporter cannot write the raw object too. A
/// clean exception keeps the exporter's own path untouched.
/// </para>
/// </remarks>
internal sealed class LogRedactionProcessor : BaseProcessor<LogRecord>
{
    private const string ExceptionTypeAttribute = "exception.type";
    private const string ExceptionMessageAttribute = "exception.message";
    private const string ExceptionStackTraceAttribute = "exception.stacktrace";

    private readonly SecretRedactor _redactor;

    internal LogRedactionProcessor(SecretRedactor? redactor = null) =>
        // SecretRedactor is stateless (compiled patterns only), so the processor's instance
        // and the one decorating the logger factory are interchangeable.
        _redactor = redactor ?? new SecretRedactor();

    /// <inheritdoc />
    public override void OnEnd(LogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        record.Attributes = RedactedAttributes(record);
        RedactText(record);
        RedactException(record);
    }

    private IReadOnlyList<KeyValuePair<string, object?>>? RedactedAttributes(LogRecord record)
    {
        if (record.Attributes is not { Count: > 0 } attributes)
        {
            return record.Attributes;
        }

        var pairs = new List<KeyValuePair<string, object?>>(attributes.Count);
        foreach (var pair in attributes)
        {
            var redacted = _redactor.RedactValue(pair.Key, pair.Value);

            // A value the list does not touch keeps its own type (a boolean stays a boolean tag).
            pairs.Add(string.Equals(redacted, SecretRedactor.TextOf(pair.Value), StringComparison.Ordinal)
                ? pair
                : new KeyValuePair<string, object?>(pair.Key, redacted));
        }

        return pairs;
    }

    private void RedactText(LogRecord record)
    {
        if (record.FormattedMessage is { } formatted)
        {
            record.FormattedMessage = _redactor.RedactText(formatted);
        }

        if (record.Body is string body)
        {
            record.Body = _redactor.RedactText(body);
        }
    }

    private void RedactException(LogRecord record)
    {
        if (_redactor.RedactException(record.Exception) is not RedactedException redacted)
        {
            // Null (nothing logged) or the same instance (nothing to mask): the exporter's
            // own exception path is already correct, so it stays untouched.
            return;
        }

        var pairs = new List<KeyValuePair<string, object?>>(record.Attributes ?? []);
        pairs.Add(new KeyValuePair<string, object?>(ExceptionTypeAttribute, redacted.OriginalTypeName));
        pairs.Add(new KeyValuePair<string, object?>(ExceptionMessageAttribute, redacted.Message));
        pairs.Add(new KeyValuePair<string, object?>(ExceptionStackTraceAttribute, redacted.StackTrace ?? string.Empty));

        record.Attributes = pairs;
        record.Exception = null;
    }
}
