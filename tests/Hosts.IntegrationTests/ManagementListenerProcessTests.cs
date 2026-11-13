using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// N9 on real sockets: the management listener is a second Kestrel endpoint added next to the public one, and a
/// declared body over the host default is refused with headers alone. A database-less Api is enough: readiness
/// is 503 and every assertion is about which listener answers what.
/// </summary>
public sealed class ManagementListenerProcessTests
{
    private const long DefaultLimit = 1024 * 1024;

    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Database=evidgate;Timeout=2";

    [Fact]
    public async Task Listeners_Split_The_Management_Surface_From_The_Public_One()
    {
        var publicPort = HostLocator.FreePort();
        var managementPort = HostLocator.FreePort();
        using var process = StartApi(publicPort, managementPort);

        try
        {
            await WaitForLiveAsync(publicPort);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var publicBase = $"http://127.0.0.1:{publicPort}";
            var managementBase = $"http://127.0.0.1:{managementPort}";

            using var publicMetrics = await client.GetAsync($"{publicBase}/metrics", TestContext.Current.CancellationToken);
            using var publicMetricsSlash = await client.GetAsync($"{publicBase}/metrics/", TestContext.Current.CancellationToken);
            using var managementMetrics = await client.GetAsync($"{managementBase}/metrics", TestContext.Current.CancellationToken);
            using var managementV1 = await client.GetAsync($"{managementBase}/v1/permissions", TestContext.Current.CancellationToken);
            using var publicReady = await client.GetAsync($"{publicBase}/internal/health/ready", TestContext.Current.CancellationToken);
            using var managementReady = await client.GetAsync($"{managementBase}/internal/health/ready", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, publicMetrics.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, publicMetricsSlash.StatusCode);
            Assert.Equal(HttpStatusCode.OK, managementMetrics.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, managementV1.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, publicReady.StatusCode);
            Assert.DoesNotContain(
                "database_unreachable",
                await publicReady.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                StringComparison.Ordinal);
            Assert.Contains(
                "database_unreachable",
                await managementReady.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                StringComparison.Ordinal);
        }
        finally
        {
            await ProcessRunner.StopAsync(process);
        }
    }

    [Fact]
    public async Task Declared_Body_Over_The_Host_Default_Is_Refused_Before_Any_Body_Byte_Is_Sent()
    {
        var publicPort = HostLocator.FreePort();
        using var process = StartApi(publicPort, managementPort: null);

        try
        {
            await WaitForLiveAsync(publicPort);

            // A route with no limit of its own, so the 413 can only come from the host default. Headers only: the
            // client never sends a body byte, so an answer proves the refusal did not wait for one.
            var oversize = await SendHeadersOnlyAsync(publicPort, DefaultLimit + 1);

            Assert.StartsWith("HTTP/1.1 413", oversize, StringComparison.Ordinal);
        }
        finally
        {
            await ProcessRunner.StopAsync(process);
        }
    }

    [Fact]
    public async Task Management_Listener_Is_Added_Next_To_HTTP_PORTS()
    {
        var publicPort = HostLocator.FreePort();
        var managementPort = HostLocator.FreePort();
        using var process = StartApi(publicPort, managementPort, httpPorts: true);

        try
        {
            await WaitForLiveAsync(publicPort);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            using var managementMetrics = await client.GetAsync(
                $"http://127.0.0.1:{managementPort}/metrics",
                TestContext.Current.CancellationToken);
            using var publicMetrics = await client.GetAsync(
                $"http://127.0.0.1:{publicPort}/metrics/",
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, managementMetrics.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, publicMetrics.StatusCode);
        }
        finally
        {
            await ProcessRunner.StopAsync(process);
        }
    }

    [Fact]
    public async Task Management_Port_Equal_To_A_Public_Port_Fails_Startup_Cleanly()
    {
        var port = HostLocator.FreePort();
        using var process = StartApi(port, port);

        var result = await ProcessRunner.WaitForExitAsync(process, TestContext.Current.CancellationToken);

        var output = result.Stdout + result.Stderr;
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Configuration invalid:", output, StringComparison.Ordinal);
        Assert.Contains("EVIDGATE__API__MANAGEMENTPORT", output, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", output, StringComparison.Ordinal);
    }

    private static async Task<string> SendHeadersOnlyAsync(int port, long contentLength)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        await using var stream = socket.GetStream();

        var head = string.Create(
            CultureInfo.InvariantCulture,
            $"POST /v1/projects/{Guid.NewGuid()}/suites HTTP/1.1\r\nHost: probe\r\nContent-Type: application/json\r\nContent-Length: {contentLength}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), TestContext.Current.CancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer, deadline.Token);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static System.Diagnostics.Process StartApi(int publicPort, int? managementPort, bool httpPorts = false)
    {
        var environment = new Dictionary<string, string>
        {
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = DeadConnectionString,
            ["EVIDGATE__RUNS__CANCEL__SOFTSTOPDELAY"] = "00:00:30",
            ["EVIDGATE__RUNS__CANCEL__KILLDELAY"] = "00:02:00",
            ["EVIDGATE__CI__SIGNINGKEY"] = Convert.ToBase64String(new byte[32]),
            ["EVIDGATE__CI__PEPPER"] = Convert.ToBase64String(new byte[32]),
            ["EVIDGATE__CI__PEPPERVERSION"] = "1",
            [IsolationSelfCheckEnvironment.Variable] = IsolationSelfCheckEnvironment.Disabled,
        };

        if (httpPorts)
        {
            environment["ASPNETCORE_HTTP_PORTS"] = publicPort.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{publicPort}";
        }

        if (managementPort is { } port)
        {
            environment["EVIDGATE__API__MANAGEMENTPORT"] = port.ToString(CultureInfo.InvariantCulture);
        }

        return ProcessRunner.Start(HostLocator.AppHost("Evidgate.Api"), environment);
    }

    private static async Task WaitForLiveAsync(int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(
                    $"http://127.0.0.1:{port}/internal/health/live",
                    TestContext.Current.CancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"the Api did not answer /internal/health/live on port {port} within 30 s");
    }
}
