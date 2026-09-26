using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC7 (D6): an oversized request body must be rejected with the 00 §9.2
/// envelope and the framework's 413, not flattened to a 400 or reported as an
/// internal error. Kestrel enforces the limit, so this runs a real host process
/// and speaks raw HTTP: a client that announces a body over the limit is answered
/// (and its connection closed) before it finishes writing, which an
/// <see cref="HttpClient"/> reports as a broken pipe instead of the response.
/// </summary>
public sealed class EnvelopeTransportTests
{
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2";

    /// <summary>Kestrel's default request-body limit is 30 MB; stay above it.</summary>
    private const long OversizeLength = 31_000_000;

    [Fact]
    public async Task Oversize_Body_Returns_413_Envelope()
    {
        var port = HostLocator.FreePort();
        using var process = ProcessRunner.Start(
            HostLocator.AppHost("Evidgate.Api"),
            new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = DeadConnectionString,
            });

        try
        {
            await WaitForLiveAsync(port);

            using var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
            await using var stream = socket.GetStream();

            var head = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"POST /v1/slice/run HTTP/1.1\r\nHost: probe\r\nContent-Type: application/json\r\nContent-Length: {OversizeLength}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), TestContext.Current.CancellationToken);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));

            var response = await ReadResponseAsync(stream, deadline.Token);

            Assert.StartsWith("HTTP/1.1 413", response, StringComparison.Ordinal);
            Assert.Contains("\"code\":\"payload_too_large\"", response, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    private static async Task<string> ReadResponseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var received = new StringBuilder();

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return received.ToString();
            }

            received.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
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
