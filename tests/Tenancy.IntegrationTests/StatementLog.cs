using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Npgsql's <c>Executing command: {CommandText}</c> log line, captured once per command
/// before it is sent; transaction control is dropped so the log is the work itself.
/// </summary>
internal sealed partial class StatementLog : ILoggerFactory, ILogger
{
    private const string ExecutingCommandPrefix = "Executing command: ";

    private readonly ConcurrentQueue<string> _statements = new();

    /// <summary>Everything captured since the last <see cref="Clear"/>.</summary>
    public IReadOnlyList<string> Snapshot() => [.. _statements];

    /// <summary>Forgets every captured statement.</summary>
    public void Clear() => _statements.Clear();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => this;

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
        // One capture serves every category.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release.
    }

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

        var message = formatter(state, exception);
        if (message is null || !message.StartsWith(ExecutingCommandPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var text = Whitespace().Replace(message[ExecutingCommandPrefix.Length..], " ").Trim();
        if (!TransactionControl().IsMatch(text))
        {
            _statements.Enqueue(text);
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("^(BEGIN|COMMIT|ROLLBACK|START TRANSACTION|SET TRANSACTION|SAVEPOINT|RELEASE)", RegexOptions.IgnoreCase)]
    private static partial Regex TransactionControl();
}
