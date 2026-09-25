using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// The Worker's own readiness surface (01.1.2-T1.3, AC3) and its hardening (D2):
/// it answers 200/503 with the same shape and the same <c>ReadinessProbe</c> as
/// the Api, a stalled client cannot block another probe, and a stalled client is
/// dropped at the exchange deadline. The host is built through the real
/// <see cref="WorkerHostFactory"/> (same composition as the entry point).
/// </summary>
[Collection(PostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class WorkerReadinessTests
{
    private readonly PostgresFixture _postgres;

    public WorkerReadinessTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Ready_200_At_Head()
    {
        var connectionString = await _postgres.CreateDatabaseAtHeadAsync(PostgresFixture.ExpectedHead);
        using var worker = await StartWorkerAsync(connectionString, out var port);

        using var response = await GetAsync(port, "/internal/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("ready", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(PostgresFixture.ExpectedHead, document.RootElement.GetProperty("expected_head").GetString());
    }

    [Fact]
    public async Task Ready_503_When_Journal_Behind()
    {
        var connectionString = await _postgres.CreateDatabaseAtHeadAsync("0000_behind.sql");
        using var worker = await StartWorkerAsync(connectionString, out var port);

        using var response = await GetAsync(port, "/internal/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", response.Headers.RetryAfter?.ToString());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("unready", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("journal_behind", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Trickling_Client_Does_Not_Block_Concurrent_Probe()
    {
        var connectionString = await _postgres.CreateDatabaseAtHeadAsync(PostgresFixture.ExpectedHead);
        using var worker = await StartWorkerAsync(connectionString, out var port);

        // A client that opens the connection and never finishes the request head
        // (1 byte every 500 ms) must not hold the listener (D2).
        using var trickle = new TcpClient();
        await trickle.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        var trickleStream = trickle.GetStream();
        await trickleStream.WriteAsync(
            "GET /internal/health/live HTTP/1.1\r\nHost: probe\r\n"u8.ToArray(),
            TestContext.Current.CancellationToken);

        using var trickleStop = new CancellationTokenSource();
        var trickling = Task.Run(
            async () =>
            {
                try
                {
                    while (!trickleStop.Token.IsCancellationRequested)
                    {
                        await trickleStream.WriteAsync(new byte[] { (byte)'X' }, trickleStop.Token);
                        await Task.Delay(500, trickleStop.Token);
                    }
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException)
                {
                    // The server closing the stalled connection is the expected end.
                }
            },
            TestContext.Current.CancellationToken);

        try
        {
            // A concurrent probe still answers promptly while the other stalls.
            var stopwatch = Stopwatch.StartNew();
            using var response = await GetAsync(port, "/internal/health/live");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"a concurrent probe took {stopwatch.Elapsed.TotalSeconds:0.##}s while one client was stalled");

            // …and the stalled connection is dropped at the exchange deadline.
            Assert.True(
                await WaitForServerCloseAsync(trickleStream, TimeSpan.FromSeconds(6)),
                "the stalled connection must be closed at the exchange deadline");
        }
        finally
        {
            await trickleStop.CancelAsync();
            await trickling;
        }
    }

    private static Task<IHost> StartWorkerAsync(string connectionString, out int port)
    {
        port = HostLocator.FreePort();
        return WorkerHostFactory.StartAsync(
            connectionString,
            port,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> GetAsync(int port, string path)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        return await client.GetAsync($"http://127.0.0.1:{port}{path}", TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Reads the stalled socket until the server closes it (0 bytes) or resets it.
    /// Both count as "dropped"; only the deadline timing out is a failure.
    /// </summary>
    private static async Task<bool> WaitForServerCloseAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[64];
        try
        {
            return await stream.ReadAsync(buffer, cts.Token) == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
