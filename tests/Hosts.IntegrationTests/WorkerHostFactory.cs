using System.Globalization;
using Evidgate.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hosts.IntegrationTests;

/// <summary>
/// Worker composition for the integration tests: the same
/// <see cref="WorkerHost.CreateBuilder"/> the entry point calls, plus test
/// overrides (connection string, health port, log capture) — same services,
/// same handlers, so AC5 proves sharing rather than a copy.
/// </summary>
internal static class WorkerHostFactory
{
    /// <summary>Creates a configured (not yet built) Worker host builder.</summary>
    public static HostApplicationBuilder Create(
        string connectionString,
        int healthPort,
        CaptureLoggerProvider? capture = null)
    {
        var builder = WorkerHost.CreateBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EVIDGATE:Database:ConnectionString"] = connectionString,
            ["EVIDGATE:Worker:HealthPort"] = healthPort.ToString(CultureInfo.InvariantCulture),
            ["EVIDGATE:Worker:ReadinessPollMilliseconds"] = "100",
        });

        if (capture is not null)
        {
            builder.Logging.AddProvider(capture);
        }

        return builder;
    }

    /// <summary>Creates and starts a Worker host; the caller owns disposal (StopAsync/DisposeAsync).</summary>
    public static async Task<IHost> StartAsync(
        string connectionString,
        int healthPort,
        CaptureLoggerProvider? capture = null,
        CancellationToken cancellationToken = default)
    {
        var host = Create(connectionString, healthPort, capture).Build();
        await host.StartAsync(cancellationToken);
        return host;
    }
}
