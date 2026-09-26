using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Evidgate.BuildingBlocks.Observability.Redaction;

/// <summary>
/// Takes over <see cref="ILoggerFactory"/> so every registered provider (console,
/// OpenTelemetry, tests) logs through <see cref="RedactingLogger"/>. The inner
/// factory is the framework's <see cref="LoggerFactory"/> over the same providers
/// and filter options, so provider behaviour, filters and scopes are unchanged.
/// </summary>
internal sealed class RedactingLoggerFactory : ILoggerFactory
{
    private readonly LoggerFactory _inner;

    internal RedactingLoggerFactory(
        IEnumerable<ILoggerProvider> providers,
        IOptionsMonitor<LoggerFilterOptions> filterOptions,
        SecretRedactor redactor)
    {
        Redactor = redactor;
        _inner = new LoggerFactory(providers, filterOptions);
    }

    private SecretRedactor Redactor { get; }

    public ILogger CreateLogger(string categoryName) =>
        new RedactingLogger(_inner.CreateLogger(categoryName), Redactor);

    public void AddProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _inner.AddProvider(provider);
    }

    public void Dispose() => _inner.Dispose();
}
