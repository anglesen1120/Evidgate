using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Identity.IntegrationTests.Login.Linking;

/// <summary>The log-capture half of <see cref="LinkTestHost"/>.</summary>
internal sealed partial class LinkTestHost
{
    /// <summary>
    /// An <see cref="ILoggerProvider"/> that keeps every entry the host's own filters let through, so a suite
    /// can assert on what the DEPLOYMENT would log rather than on what a hand-built logger would.
    /// </summary>
    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        /// <summary>Every entry logged so far, in order.</summary>
        internal IReadOnlyList<LogEntry> Entries => [.. _entries];

        /// <summary>Drops what was captured so far, so a case can assert on one request only.</summary>
        internal void Clear() => _entries.Clear();

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        /// <inheritdoc />
        public void Dispose()
        {
            // Nothing to release: the queue is managed memory owned by the test.
        }

        /// <summary>One log line: its category, level and formatted text.</summary>
        /// <param name="Category">The category name the logger was created under.</param>
        /// <param name="Level">The level the line was written at.</param>
        /// <param name="Message">The formatted message.</param>
        internal sealed record LogEntry(string Category, LogLevel Level, string Message);

        /// <summary>The logger a <see cref="CapturingLoggerProvider"/> hands out; every level is enabled.</summary>
        private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            /// <inheritdoc />
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <inheritdoc />
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception)));
            }
        }
    }
}
