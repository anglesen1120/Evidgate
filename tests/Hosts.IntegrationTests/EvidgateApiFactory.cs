using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hosts.IntegrationTests;

/// <summary>
/// WebApplicationFactory for the Api: the same composition as production
/// (Program → ApiHost) plus a test connection string, optional extra services
/// (telemetry exporters) and an in-memory log capture behind the same redaction
/// layer. The environment is pinned to Production so the tests exercise the same
/// middleware/JSON surface a deployed host does (AC7), not Development conveniences.
/// </summary>
public sealed class EvidgateApiFactory : WebApplicationFactory<Program>
{
    private readonly string? _connectionString;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly int? _managementPort;

    /// <summary>Creates a factory; <paramref name="connectionString"/> feeds EVIDGATE__DATABASE__CONNECTIONSTRING.</summary>
    public EvidgateApiFactory(
        string? connectionString,
        CaptureLoggerProvider? capture = null,
        Action<IServiceCollection>? configureServices = null,
        int? managementPort = null)
    {
        _managementPort = managementPort;
        _connectionString = connectionString;
        _configureServices = configureServices;
        Capture = capture;
    }

    /// <summary>
    /// Header a test sets to <see cref="ManagementPortHeaderValue"/> to make the in-memory server report the
    /// management listener's local port, which a real connection takes from the socket.
    /// </summary>
    public const string ListenerHeader = "X-Test-Listener";

    /// <summary>The <see cref="ListenerHeader"/> value that selects the management listener.</summary>
    public const string ManagementPortHeaderValue = "management";

    /// <summary>The management port a factory built with one reports; the in-memory server has no real listener.</summary>
    public const int TestManagementPort = 18081;

    /// <summary>Client of this factory whose requests arrive on the management listener.</summary>
    public HttpClient CreateManagementClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ListenerHeader, ManagementPortHeaderValue);
        return client;
    }

    /// <summary>Provider the tests read; added inside the app so redaction proves itself.</summary>
    public CaptureLoggerProvider? Capture { get; }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                // This factory boots hosts that are not the production entry point:
                // several of them are pointed at a closed port on purpose, and the
                // boot-time isolation self-check (02.6.1-T1.4, AC5) would refuse them
                // because it cannot read the catalogue. Saying so explicitly is the
                // contract — the refusal and the passing boot are proven with real
                // processes in IsolationSelfCheckBootTests.
                [IsolationSelfCheckEnvironment.InMemoryKey] = IsolationSelfCheckEnvironment.Disabled,
            };

            if (_connectionString is not null)
            {
                settings["EVIDGATE:Database:ConnectionString"] = _connectionString;
            }

            // The run-cancel windows have no option default, so every booting host states them.
            settings["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30";
            settings["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00";

            if (_managementPort is { } managementPort)
            {
                settings["EVIDGATE:Api:ManagementPort"] = managementPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            configuration.AddInMemoryCollection(settings);
        });

        builder.ConfigureServices(services =>
        {
            // The transport suites POST to an anonymous JSON route; the host owns no such
            // route any more, so the probe joins the host pipeline (routing, envelope, filters).
            services.AddSingleton<IStartupFilter, Evidgate.Testing.TransportProbeStartupFilter>();
            services.AddSingleton<IStartupFilter, ListenerPortStartupFilter>();

            if (Capture is not null)
            {
                var capture = Capture;
                services.AddLogging(logging => logging.AddProvider(capture));
            }

            _configureServices?.Invoke(services);
        });
    }
}

/// <summary>Simple structured-line log capture for the redaction assertions.</summary>
public sealed class CaptureLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _logs = new();

    /// <summary>Captured lines, in order.</summary>
    public IReadOnlyCollection<CapturedLog> Logs => _logs.ToArray();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, _logs);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class CaptureLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly ConcurrentQueue<CapturedLog> _logs;

        public CaptureLogger(string categoryName, ConcurrentQueue<CapturedLog> logs)
        {
            _categoryName = categoryName;
            _logs = logs;
        }

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

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
            var pairs = state as IReadOnlyList<KeyValuePair<string, object?>>;
            var text = pairs is null
                ? formatter(state, exception)
                : $"{formatter(state, exception)} | {string.Join(" | ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))}";

            _logs.Enqueue(new CapturedLog(_categoryName, logLevel, eventId, text));
        }
    }
}

/// <summary>One captured log line.</summary>
public sealed record CapturedLog(string Category, LogLevel Level, EventId EventId, string Text);

/// <summary>Reports the management listener's local port for a request that says it arrived there.</summary>
internal sealed class ListenerPortStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers[EvidgateApiFactory.ListenerHeader] == EvidgateApiFactory.ManagementPortHeaderValue)
            {
                context.Connection.LocalPort = EvidgateApiFactory.TestManagementPort;
            }

            return nextMiddleware(context);
        });
        next(app);
    };
}
