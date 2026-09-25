using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Readiness;
using Evidgate.Worker.Options;
using Microsoft.Extensions.Options;

namespace Evidgate.Worker.Health;

/// <summary>
/// The Worker's readiness surface (01.1.2-T1.3, AC3): a minimal HTTP/1.1 listener
/// serving <c>/internal/health/live</c> and <c>/internal/health/ready</c> with the
/// same JSON shape and the same <see cref="ReadinessProbe"/> as the Api's
/// <c>/internal/health/*</c>. A raw TCP listener, not ASP.NET Core, because the
/// Worker is deliberately a Generic Host with hosted services only (00 §5.1) —
/// a second web stack is a dependency a health probe does not need.
/// </summary>
/// <remarks>
/// Hardened per review: the bind happens in <see cref="StartAsync"/> (so a taken
/// port is a start failure, D5); each accepted connection is served on its own
/// task with bounded concurrency — excess connections are dropped rather than
/// queued behind a slow client (D2); and every exchange has an absolute deadline
/// enforced through the <see cref="CancellationToken"/> handed to the socket reads,
/// because <c>ReceiveTimeout</c>/<c>SendTimeout</c> do not apply to async I/O.
/// </remarks>
internal sealed partial class HealthProbeListener : BackgroundService
{
    /// <summary>Connections served at once; further connections are dropped (D2).</summary>
    private const int MaxConcurrentConnections = 16;

    /// <summary>Absolute deadline for reading a request head and writing the response.</summary>
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Largest request head accepted before the exchange is answered and closed.</summary>
    private const int MaxHeadBytes = 16 * 1024;

    private readonly WorkerOptions _options;
    private readonly ReadinessProbe _probe;
    private readonly ILogger<HealthProbeListener> _logger;
    private readonly SemaphoreSlim _connections = new(MaxConcurrentConnections, MaxConcurrentConnections);

    private TcpListener? _listener;

    /// <summary>Creates the listener.</summary>
    public HealthProbeListener(
        IOptions<WorkerOptions> options,
        ReadinessProbe probe,
        ILogger<HealthProbeListener> logger)
    {
        _options = options.Value;
        _probe = probe;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The socket is bound here, not in <see cref="ExecuteAsync"/>, so an
    /// unbindable port fails host start (D5) instead of stopping a host that
    /// already reported success.
    /// </remarks>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, _options.HealthPort);
        try
        {
            listener.Start();
        }
        catch (SocketException exception)
        {
            LogListenerFailed(exception, _options.HealthPort);
            throw new HealthListenerBindException(_options.HealthPort, exception);
        }

        _listener = listener;
        LogListening(_options.HealthPort);

        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = _listener
            ?? throw new InvalidOperationException("the readiness listener must be bound in StartAsync");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);

                // Bounded concurrency: never make one client's exchange delay
                // another's. The connection is closed immediately when saturated.
                if (!await _connections.WaitAsync(0, stoppingToken))
                {
                    LogConnectionDropped();
                    client.Dispose();
                    continue;
                }

                _ = ServeClientAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown: fall through and stop accepting.
        }
        catch (SocketException) when (stoppingToken.IsCancellationRequested)
        {
            // AcceptTcpClientAsync can surface the listener stop this way too.
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        using (client)
        {
            try
            {
                using var exchange = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                exchange.CancelAfter(ExchangeTimeout);

                await using var stream = client.GetStream();

                var head = await ReadHeadAsync(stream, exchange.Token);
                if (head.Length == 0)
                {
                    await RespondAsync(stream, "400 Bad Request", "{\"status\":\"bad_request\"}", exchange.Token);
                    return;
                }

                var requestLine = head.Split('\r', 2)[0];
                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var method = parts.Length > 0 ? parts[0] : string.Empty;
                var path = parts.Length > 1 ? parts[1] : "/";

                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await RespondAsync(
                        stream, "405 Method Not Allowed", "{\"status\":\"method_not_allowed\"}", exchange.Token);
                    return;
                }

                switch (path)
                {
                    case "/internal/health/live":
                        await RespondAsync(stream, "200 OK", "{\"status\":\"live\"}", exchange.Token);
                        return;

                    case "/internal/health/ready":
                        var result = await _probe.CheckAsync(exchange.Token);
                        var body = string.Create(
                            CultureInfo.InvariantCulture,
                            $"{{\"status\":\"{(result.Ready ? "ready" : "unready")}\",\"reason\":\"{result.Reason}\",\"journal_head\":{(result.JournalHead is null ? "null" : $"\"{result.JournalHead}\"")},\"expected_head\":{(result.ExpectedHead is null ? "null" : $"\"{result.ExpectedHead}\"")}}}");

                        // 00 §9.2: 503 carries Retry-After.
                        var retryAfter = result.Ready ? null : "5";
                        await RespondAsync(
                            stream,
                            result.Ready ? "200 OK" : "503 Service Unavailable",
                            body,
                            exchange.Token,
                            retryAfter);

                        return;

                    default:
                        await RespondAsync(stream, "404 Not Found", "{\"status\":\"not_found\"}", exchange.Token);
                        return;
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or OperationCanceledException)
            {
                LogClientFailure(exception);
            }
            finally
            {
                _connections.Release();
            }
        }
    }

    /// <summary>
    /// Reads the request head, scanning the bytes already buffered for the blank
    /// line instead of re-materialising a string per read (D2).
    /// </summary>
    private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var readBuffer = new byte[1024];
        var head = new byte[MaxHeadBytes];
        var length = 0;

        while (length < MaxHeadBytes)
        {
            var read = await stream.ReadAsync(
                readBuffer.AsMemory(0, Math.Min(readBuffer.Length, MaxHeadBytes - length)),
                cancellationToken);

            if (read == 0)
            {
                break;
            }

            readBuffer.AsSpan(0, read).CopyTo(head.AsSpan(length));
            length += read;

            if (length >= 4
                && head[length - 4] == (byte)'\r'
                && head[length - 3] == (byte)'\n'
                && head[length - 2] == (byte)'\r'
                && head[length - 1] == (byte)'\n')
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(head, 0, length);
    }

    private static async Task RespondAsync(
        NetworkStream stream,
        string statusLine,
        string body,
        CancellationToken cancellationToken,
        string? retryAfter = null)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var retryAfterHeader = retryAfter is null ? string.Empty : $"Retry-After: {retryAfter}\r\n";

        var head = string.Create(
            CultureInfo.InvariantCulture,
            $"HTTP/1.1 {statusLine}\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\n{retryAfterHeader}Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "Readiness listener is up on port {Port}")]
    private partial void LogListening(int port);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Error, Message = "Readiness listener cannot bind port {Port}")]
    private partial void LogListenerFailed(Exception exception, int port);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Debug, Message = "Readiness request failed mid-exchange")]
    private partial void LogClientFailure(Exception exception);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Warning, Message = "Readiness request dropped: all connection slots are busy")]
    private partial void LogConnectionDropped();
}
