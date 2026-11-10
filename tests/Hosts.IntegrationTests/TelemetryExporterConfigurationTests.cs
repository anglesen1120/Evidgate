using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Evidgate.BuildingBlocks.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// The OTLP exporter exists only when <c>EVIDGATE__TELEMETRY__OTLPENDPOINT</c> is set, and then it
/// exports to that exact endpoint for traces, metrics and logs. Each case is proven twice: on the
/// exporter options the SDK creates for the host (an exporter cannot exist without one) and on the
/// wire, through a loopback listener that counts the connections the flushed providers open.
/// </summary>
/// <remarks>
/// The options probe registers an all-names <see cref="OtlpExporterOptions"/> setup. The unnamed
/// <c>AddOtlpExporter</c> overloads create their options through
/// <c>IOptionsFactory&lt;OtlpExporterOptions&gt;</c> and apply the inline callback to that same
/// instance, so the object this test captures is the object the exporter ends up with — including
/// its endpoint. That is what proves a keyless host creates no exporter at all: a hypothetical
/// registration that fell back to the default <c>:4317</c> would show up here, while a listener on a
/// random port could never see it. The listener probe then proves the configured endpoint is honoured
/// and that every signal leaves the process: each exporter owns its own <see cref="HttpClient"/>, so
/// three flushed providers with data open three connections, and one missing exporter is one short.
/// </remarks>
[Collection(ProcessGlobalTelemetrySuite.Name)]
public sealed partial class TelemetryExporterConfigurationTests
{
    /// <summary>One exporter per signal: traces, metrics and logs.</summary>
    private const int SignalExporters = 3;

    /// <summary>
    /// How long the exporters get to open their connections after the flushes. Kept below the
    /// exporter's 10 s request timeout, so a hung export cannot retry inside the window and turn a
    /// missing exporter into a third connection.
    /// </summary>
    private static readonly TimeSpan ConnectionDeadline = TimeSpan.FromSeconds(5);

    /// <summary>Long enough for a buffered signal to be sent, short enough not to wait out a call to a silent collector.</summary>
    private const int FlushTimeoutMilliseconds = 1_000;

    [Fact]
    public async Task An_Unset_Endpoint_Creates_No_Exporter_And_Opens_No_Connection()
    {
        await using var collector = ConnectionProbe.Start();
        var created = new ConcurrentQueue<OtlpExporterOptions>();
        var host = await StartHostAsync(otlpEndpoint: null, created);

        try
        {
            EmitAllSignals(host);
            ForceFlushAllSignals(host);

            // Building the three providers is what creates an exporter, so an exporter registered
            // without the key would be here whatever endpoint it defaulted to.
            Assert.Empty(created);

            Assert.False(
                await collector.WaitForConnectionsAsync(1, TimeSpan.FromSeconds(2)),
                $"a signal tried to export although no endpoint is configured: {collector.Count} connection(s) to {collector.Endpoint}");
        }
        finally
        {
            await StopAsync(host);
        }
    }

    [Fact]
    public async Task A_Set_Endpoint_Configures_Three_Exporters_And_Exports_To_It()
    {
        await using var collector = ConnectionProbe.Start();
        var created = new ConcurrentQueue<OtlpExporterOptions>();
        var host = await StartHostAsync(collector.Endpoint, created);

        try
        {
            EmitAllSignals(host);
            ForceFlushAllSignals(host);

            var exporters = created.ToArray();
            Assert.True(
                exporters.Length >= SignalExporters,
                $"expected an OTLP exporter for traces, metrics and logs, saw {exporters.Length}");
            Assert.All(
                exporters,
                exporter => Assert.Equal(collector.Endpoint.AbsoluteUri, exporter.Endpoint.AbsoluteUri));

            Assert.True(
                await collector.WaitForConnectionsAsync(SignalExporters, ConnectionDeadline),
                $"expected {SignalExporters} export connections (traces, metrics, logs) to {collector.Endpoint}, saw {collector.Count}");
        }
        finally
        {
            await StopAsync(host);
        }
    }

    private static async Task<IHost> StartHostAsync(
        Uri? otlpEndpoint,
        ConcurrentQueue<OtlpExporterOptions> created)
    {
        // The host also reads the process environment, so the key is pinned here: an exported
        // EVIDGATE__TELEMETRY__OTLPENDPOINT must not decide what this suite proves.
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:Telemetry:OtlpEndpoint"] = otlpEndpoint?.ToString(),
        };

        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = "Staging" });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddEvidgateTelemetry("evidgate-test");

        // All-names setup, so it applies to every instance the SDK asks for, named or not.
        builder.Services.ConfigureAll<OtlpExporterOptions>(created.Enqueue);

        var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        return host;
    }

    private static async Task StopAsync(IHost host)
    {
        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();
    }

    /// <summary>The source-generated log call of this suite (CA1848: no format strings at the call site).</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "otlp exporter probe")]
    private static partial void ProbeRecord(ILogger logger);

    /// <summary>One measurement per signal, so each provider has something to export.</summary>
    private static void EmitAllSignals(IHost host)
    {
        using (var activity = EvidgateTelemetry.StartModuleActivity("ingestion", "ExporterProbe"))
        {
            Assert.NotNull(activity);
        }

        EvidgateTelemetry.RecordSessionRevoked(1);

        var logger = host.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Hosts.IntegrationTests.ExporterProbe");
        ProbeRecord(logger);
    }

    /// <summary>
    /// Resolves the three providers, which is what builds the exporters, and sends what they
    /// buffered. The timeout is short on purpose: the probe accepts the connection and never answers,
    /// so the flush cannot complete — the connection is what is asserted.
    /// </summary>
    private static void ForceFlushAllSignals(IHost host)
    {
        _ = host.Services.GetRequiredService<TracerProvider>().ForceFlush(FlushTimeoutMilliseconds);
        _ = host.Services.GetRequiredService<MeterProvider>().ForceFlush(FlushTimeoutMilliseconds);
        _ = host.Services.GetRequiredService<LoggerProvider>().ForceFlush(FlushTimeoutMilliseconds);
    }

    /// <summary>
    /// A loopback listener that accepts connections and holds each socket open. Nothing is read or
    /// answered: an exporter has to open the connection, not complete the call. A client that keeps
    /// its connection counts once, so the count is the number of export clients, not of retries.
    /// </summary>
    private sealed class ConnectionProbe : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _accepted = [];
        private readonly Task _acceptLoop;
        private int _count;

        private ConnectionProbe()
        {
            _listener.Start();
            Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            _acceptLoop = AcceptAsync();
        }

        public static ConnectionProbe Start() => new();

        /// <summary>The absolute URI the exporters are configured with.</summary>
        public Uri Endpoint { get; }

        public int Count => Volatile.Read(ref _count);

        public async Task<bool> WaitForConnectionsAsync(int expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Count < expected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            return Count >= expected;
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
#pragma warning disable VSTHRD003 // The accept loop is started by this probe's constructor.
            await _acceptLoop;
#pragma warning restore VSTHRD003

            foreach (var client in _accepted)
            {
                client.Dispose();
            }
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(CancellationToken.None);
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _accepted.Add(client);
                Interlocked.Increment(ref _count);
            }
        }
    }
}
