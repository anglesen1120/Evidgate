using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// Worker start-failure contract (D5): a process that cannot bring up its
/// readiness surface must exit non-zero rather than report success. Real process
/// so the host's own exit code is observed.
/// </summary>
public sealed class WorkerStartupTests
{
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2";

    [Fact]
    public async Task HealthPort_In_Use_Exits_NonZero()
    {
        // Hold the port the Worker will be told to bind. Bind the wildcard address
        // the Worker itself uses: a loopback-only bind would coexist with the
        // Worker's wildcard bind because .NET sets SO_REUSEADDR.
        var occupied = new TcpListener(IPAddress.Any, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;

        try
        {
            using var process = ProcessRunner.Start(
                HostLocator.AppHost("Evidgate.Worker"),
                new Dictionary<string, string>
                {
                    ["DOTNET_ENVIRONMENT"] = "Production",
                    ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = DeadConnectionString,
                    ["EVIDGATE__WORKER__HEALTHPORT"] = port.ToString(CultureInfo.InvariantCulture),
                    ["EVIDGATE__WORKER__READINESSPOLLMILLISECONDS"] = "100",
                });

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            ProcessRunner.Result result;
            try
            {
                result = await ProcessRunner.WaitForExitAsync(process, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                throw new TimeoutException("the Worker did not exit after failing to bind its readiness port");
            }

            Assert.NotEqual(0, result.ExitCode);

            var output = result.Stdout + result.Stderr;
            Assert.Contains("cannot bind port", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(port.ToString(CultureInfo.InvariantCulture), output, StringComparison.Ordinal);
        }
        finally
        {
            occupied.Stop();
        }
    }
}
