using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Evidgate.Testing;

/// <summary>One log call as a sink would see it: the rendered line plus everything structured behind it.</summary>
/// <param name="Category">Logger category.</param>
/// <param name="Level">Level the call was made at.</param>
/// <param name="EventId">Event id of the call.</param>
/// <param name="Message">The formatted message.</param>
/// <param name="State">Every state pair, as <c>key=value</c>, including the original template.</param>
/// <param name="Scopes">Every active scope, flattened the same way.</param>
/// <param name="Exception">The full exception text (type, message, stack, inner exceptions), or null.</param>
internal sealed record CapturedLog(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyList<string> State,
    IReadOnlyList<string> Scopes,
    string? Exception)
{
    /// <summary>Every string a log sink could persist for this call.</summary>
    public IEnumerable<string> Texts
    {
        get
        {
            yield return Message;

            foreach (var pair in State)
            {
                yield return pair;
            }

            foreach (var scope in Scopes)
            {
                yield return scope;
            }

            if (Exception is not null)
            {
                yield return Exception;
            }
        }
    }
}

/// <summary>
/// Keeps every call of every logger, at every level, with its structured state, its scopes and the full exception
/// text, so a test can assert on what a sink would have stored and not only on the rendered message. A test that must tell
/// providers apart by type (a provider alias or full-name filter section) derives from this base; the rest use
/// <see cref="CapturingLoggerProvider"/>.
/// </summary>
internal abstract class CapturingLoggerProviderBase : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    /// <summary>Every call captured so far, in order.</summary>
    public IReadOnlyList<CapturedLog> Entries => _entries.ToArray();

    /// <summary>Drops what was captured so far, so a case can observe one request at a time.</summary>
    public void Clear() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
        // The queue is the only state.
    }

    private static void Flatten(object? value, List<string> into, int depth)
    {
        switch (value)
        {
            case null:
                return;
            case string text:
                into.Add(text);
                return;
            case IEnumerable<KeyValuePair<string, object?>> pairs when depth < 4:
                foreach (var pair in pairs)
                {
                    var nested = new List<string>();
                    Flatten(pair.Value, nested, depth + 1);
                    into.Add($"{pair.Key}=");
                    into.AddRange(nested.Select(item => $"{pair.Key}={item}"));
                }

                return;
            default:
                into.Add(value.ToString() ?? string.Empty);
                return;
        }
    }

    private sealed class Logger(string category, CapturingLoggerProviderBase owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var stateText = new List<string>();
            Flatten(state, stateText, 0);

            var scopeText = new List<string>();
            owner._scopes.ForEachScope((scope, list) => Flatten(scope, list, 0), scopeText);

            owner._entries.Enqueue(new CapturedLog(
                category,
                logLevel,
                eventId,
                formatter(state, exception),
                stateText,
                scopeText,
                exception?.ToString()));
        }
    }
}

/// <summary>The capturing provider for a test that needs one instance and no provider identity of its own.</summary>
internal sealed class CapturingLoggerProvider : CapturingLoggerProviderBase;
