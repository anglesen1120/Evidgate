using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Evidgate.Modules.Policy.Webhooks.Application;
using Evidgate.Modules.Policy.Webhooks.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tenancy.IntegrationTests;

/// <summary>One request the receiver saw: the path, every header and the exact body bytes.</summary>
internal sealed record ReceivedWebhook(string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>
/// A real HTTPS server on loopback that plays the customer's endpoint: it records requests and, by path, answers 200,
/// 500, a redirect, a stall or an endless body. The certificate names two hosts so a redirect to the second one can be
/// followed by a client that (wrongly) follows redirects.
/// </summary>
internal sealed class WebhookTestReceiver : IAsyncDisposable
{
    public const string Host = "receiver.example.com";
    public const string OtherHost = "other.example.com";

    private readonly WebApplication _app;
    private long _bigBytes;
    private int _bigAborted;

    private WebhookTestReceiver(WebApplication app, int port, string thumbprint)
    {
        _app = app;
        Port = port;
        CertificateThumbprint = thumbprint;
    }

    public int Port { get; }

    public string CertificateThumbprint { get; }

    public ConcurrentQueue<ReceivedWebhook> Requests { get; } = new();

    /// <summary>Bytes of the endless body the server managed to write before the client went away.</summary>
    public long BigBytesWritten => Interlocked.Read(ref _bigBytes);

    /// <summary>True once the server saw the connection of the endless response close.</summary>
    public bool BigAborted => Volatile.Read(ref _bigAborted) == 1;

    public int HitsOn(string path) => Requests.Count(request => request.Path == path);

    public static async Task<WebhookTestReceiver> StartAsync()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={Host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Host);
        names.AddDnsName(OtherHost);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), password: null);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        var thumbprint = certificate.GetCertHashString();
        WebhookTestReceiver? receiver = null;
        app.Run(context => receiver!.HandleAsync(context));
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        receiver = new WebhookTestReceiver(app, new Uri(address).Port, thumbprint);
        return receiver;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        var path = context.Request.Path.Value ?? "/";
        Requests.Enqueue(new ReceivedWebhook(
            path,
            context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            body.ToArray()));

        switch (path)
        {
            case "/fail":
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync("upstream exploded", context.RequestAborted);
                break;
            case "/redirect":
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = $"https://{OtherHost}/landing";
                break;
            case "/slow":
                await StallAsync(context);
                break;
            case "/big":
                await StreamEndlesslyAsync(context);
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status200OK;
                break;
        }
    }

    private static async Task StallAsync(HttpContext context)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // The client gave up, which is what the test expects.
        }
    }

    private async Task StreamEndlesslyAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        var chunk = new byte[8192];
        Array.Fill(chunk, (byte)'x');
        try
        {
            for (var i = 0; i < 4096; i++)
            {
                await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                Interlocked.Add(ref _bigBytes, chunk.Length);
            }
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException)
        {
            Volatile.Write(ref _bigAborted, 1);
        }
    }
}

/// <summary>A resolver the test controls, so a name can be public at registration and private at send time.</summary>
internal sealed class FakeWebhookHostResolver : IWebhookHostResolver
{
    public static readonly IPAddress PublicAddress = IPAddress.Parse("8.8.8.8");
    public static readonly IPAddress PrivateAddress = IPAddress.Parse("10.0.0.5");

    private readonly ConcurrentDictionary<string, IPAddress[]> _answers = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string host, params IPAddress[] addresses) => _answers[host] = addresses;

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult(_answers.TryGetValue(host, out var found) ? found : []);
}

/// <summary>
/// Stands in for the pinned connector: it records which addresses the sender was told to dial, then reaches the loopback
/// receiver, because the validated "public" address of a test is not a real server.
/// </summary>
internal sealed class RoutingWebhookConnector(int receiverPort) : IWebhookConnector
{
    public ConcurrentQueue<IPAddress[]> Dialed { get; } = new();

    public async ValueTask<Stream> ConnectAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken cancellationToken)
    {
        Dialed.Enqueue([.. addresses]);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, receiverPort), cancellationToken);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new NetworkStream(socket, ownsSocket: true);
    }
}
