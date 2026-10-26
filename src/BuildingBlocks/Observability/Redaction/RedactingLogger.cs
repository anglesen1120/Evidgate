using Microsoft.Extensions.Logging;

namespace Evidgate.BuildingBlocks.Observability.Redaction;

/// <summary>
/// Logger decorator of 01.1.2-T1.2: it rewrites the state through
/// <see cref="SecretRedactor"/> before any provider formats or exports it
/// (00 §5.7 S3).
/// </summary>
/// <remarks>
/// Scopes and exceptions are redacted here too (10.3.1-T1.4): a scope value is state the
/// providers may render, and an exception message is text a collector receives as an
/// attribute, so both are masked at the same seam as the state. An exception with nothing
/// to mask is passed through unchanged, identity and type included; a masked one is
/// replaced by <see cref="RedactedException"/>, which keeps the original type name.
/// </remarks>
internal sealed class RedactingLogger(ILogger inner, SecretRedactor redactor) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => inner.BeginScope(new RedactedLogState<TState>(
            state,
            redactor,
            static (scope, _) => scope?.ToString() ?? string.Empty,
            exception: null));

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!inner.IsEnabled(logLevel))
        {
            return;
        }

        var redacted = new RedactedLogState<TState>(state, redactor, formatter, exception);

        inner.Log(logLevel, eventId, redacted, redactor.RedactException(exception), static (logged, _) => logged.ToString());
    }
}
