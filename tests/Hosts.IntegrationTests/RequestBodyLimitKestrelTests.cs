using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Evidgate.Api.Middleware;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// N9 on a real Kestrel socket: the middleware pushes the route's limit into the server, so a chunked body (which
/// has no declared length to refuse on) is stopped while it is read, and a body of exactly the limit is accepted.
/// The host is the production middleware over the production default limit with two probe routes that read the
/// whole body; the full Api cannot be used because no anonymous route reads a body under the default limit.
/// </summary>
public sealed class RequestBodyLimitKestrelTests
{
    private const long RouteLimit = 1000;

    [Fact]
    public async Task Chunked_Body_Over_The_Route_Limit_Is_Refused_413()
    {
        await using var host = await StartAsync();

        var response = await SendAsync(host.Port, "/limited", new byte[RouteLimit + 1], chunked: true);

        Assert.StartsWith("HTTP/1.1 413", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chunked_Body_Under_The_Route_Limit_Is_Accepted_In_Full()
    {
        await using var host = await StartAsync();

        var response = await SendAsync(host.Port, "/limited", new byte[RouteLimit / 2], chunked: true);

        Assert.StartsWith("HTTP/1.1 200", response, StringComparison.Ordinal);
        AssertBodyLength(response, RouteLimit / 2);
    }

    [Fact]
    public async Task Declared_Body_Of_Exactly_The_Route_Limit_Is_Accepted_And_One_More_Is_Refused()
    {
        await using var host = await StartAsync();

        var exact = await SendAsync(host.Port, "/limited", new byte[RouteLimit], chunked: false);
        var over = await SendHeadersOnlyAsync(host.Port, "/limited", RouteLimit + 1);

        Assert.StartsWith("HTTP/1.1 200", exact, StringComparison.Ordinal);
        AssertBodyLength(exact, RouteLimit);
        Assert.StartsWith("HTTP/1.1 413", over, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_Limit_Accepts_A_Full_Body_And_Refuses_One_Byte_More()
    {
        await using var host = await StartAsync();
        var limit = RequestBodyLimitMiddleware.DefaultBodyBytes;

        var exact = await SendAsync(host.Port, "/default", new byte[limit], chunked: false);
        var over = await SendHeadersOnlyAsync(host.Port, "/default", limit + 1);

        Assert.StartsWith("HTTP/1.1 200", exact, StringComparison.Ordinal);
        AssertBodyLength(exact, limit);
        Assert.StartsWith("HTTP/1.1 413", over, StringComparison.Ordinal);
    }

    // The probe answers chunked: the byte count is the only chunk, then the terminator.
    private static void AssertBodyLength(string response, long length) =>
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"\r\n{length}\r\n0\r\n"),
            response,
            StringComparison.Ordinal);

    private static async Task<ProbeHost> StartAsync()
    {
        var port = HostLocator.FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Limits.MaxRequestBodySize = RequestBodyLimitMiddleware.DefaultBodyBytes);
        builder.Services.TryAddEvidgateContractValidation();

        var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<RequestBodyLimitMiddleware>();
        app.MapPost("/limited", DrainAsync).WithBodyLimit(RouteLimit);
        app.MapPost("/default", DrainAsync);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new ProbeHost(app, port);
    }

    private static async Task DrainAsync(HttpContext context)
    {
        long total = 0;
        var buffer = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            total += read;
        }

        await context.Response.WriteAsync(total.ToString(CultureInfo.InvariantCulture), context.RequestAborted);
    }

    private static async Task<string> SendAsync(int port, string route, byte[] body, bool chunked)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        await using var stream = socket.GetStream();

        var framing = chunked ? "Transfer-Encoding: chunked" : $"Content-Length: {body.Length}";
        var head = $"POST {route} HTTP/1.1\r\nHost: probe\r\nContent-Type: application/octet-stream\r\n{framing}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), TestContext.Current.CancellationToken);
        if (chunked)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{body.Length:x}\r\n"), TestContext.Current.CancellationToken);
            await stream.WriteAsync(body, TestContext.Current.CancellationToken);
            await stream.WriteAsync("\r\n0\r\n\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
        }
        else
        {
            await stream.WriteAsync(body, TestContext.Current.CancellationToken);
        }

        return await ReadAllAsync(stream);
    }

    private static async Task<string> SendHeadersOnlyAsync(int port, string route, long contentLength)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        await using var stream = socket.GetStream();

        var head = string.Create(
            CultureInfo.InvariantCulture,
            $"POST {route} HTTP/1.1\r\nHost: probe\r\nContent-Type: application/octet-stream\r\nContent-Length: {contentLength}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), TestContext.Current.CancellationToken);
        return await ReadAllAsync(stream);
    }

    private static async Task<string> ReadAllAsync(NetworkStream stream)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var received = new StringBuilder();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
        {
            received.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return received.ToString();
    }

    private sealed class ProbeHost(WebApplication app, int port) : IAsyncDisposable
    {
        public int Port { get; } = port;

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}
