using Microsoft.Extensions.Logging;

namespace Evidgate.BuildingBlocks.Observability.Redaction;

/// <summary>
/// Logger decorator of 01.1.2-T1.2: it rewrites the state through
/// <see cref="SecretRedactor"/> before any provider formats or exports it
/// (00 §5.7 S3). Exceptions pass through unchanged — 00 §5.5 C8 says exception
/// text is a bug signal, not a place to put secrets.
/// </summary>
internal sealed class RedactingLogger(ILogger inner, SecretRedactor redactor) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => inner.BeginScope(state);

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

        inner.Log(logLevel, eventId, redacted, exception, static (logged, _) => logged.ToString());
    }
}
