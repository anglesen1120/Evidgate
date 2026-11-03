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
            // This suite boots the Worker for the readiness and queue surfaces, on
            // purpose not always against a migrated database; the boot-time isolation
            // self-check (02.6.1-T1.4, AC5) is proven against real processes in
            // IsolationSelfCheckBootTests and is opted out of here explicitly.
            [IsolationSelfCheckEnvironment.InMemoryKey] = IsolationSelfCheckEnvironment.Disabled,
        });

        if (capture is not null)
        {
            builder.Logging.AddProvider(capture);
        }

        return builder;
    }

}

/// <summary>
/// A started Worker host that stops its hosted services when disposed, so a test
/// database is never dropped under a still-running poll loop; a host that fails
/// mid-start is stopped and disposed instead of leaked.
/// </summary>
internal sealed class RunningWorker : IAsyncDisposable
{
    private readonly IHost _host;

    private RunningWorker(IHost host) => _host = host;

    /// <summary>The worker's root service provider, for composition assertions.</summary>
    public IServiceProvider Services => _host.Services;

    public static async Task<RunningWorker> StartAsync(
        string connectionString,
        int healthPort,
        CaptureLoggerProvider? capture = null,
        CancellationToken cancellationToken = default)
    {
        var host = WorkerHostFactory.Create(connectionString, healthPort, capture).Build();
        try
        {
            await host.StartAsync(cancellationToken);
        }
        catch
        {
            await StopAsync(host);
            host.Dispose();
            throw;
        }

        return new RunningWorker(host);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(_host);
        _host.Dispose();
    }

    /// <summary>Hosted services stop within the host's own shutdown window; an overrun must not fail the test.</summary>
    private static async Task StopAsync(IHost host)
    {
        try
        {
            await host.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // ShutdownTimeout elapsed; DisposeAsync still cancels the hosted services.
        }
    }
}
