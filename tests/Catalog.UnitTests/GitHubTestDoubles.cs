using System.Net;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Catalog.Infrastructure.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Catalog.UnitTests;

/// <summary>One request the stub handler saw, snapshotted because the client disposes the message.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Uri">Absolute request URI.</param>
/// <param name="Bearer">The bearer value, or null when the request carried none.</param>
/// <param name="ApiVersion">The <c>X-GitHub-Api-Version</c> header, or null.</param>
/// <param name="Accept">The <c>Accept</c> header GitHub was asked for.</param>
internal sealed record RequestSnapshot(HttpMethod Method, Uri Uri, string? Bearer, string? ApiVersion, string? Accept)
{
    /// <inheritdoc />
    public override string ToString() => $"{Method} {Uri} api={ApiVersion} accept={Accept} bearer={Bearer is null}";
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> over a delegate: it records what was sent and answers with a
/// prepared response, so no test opens a socket.
/// </summary>
/// <summary>Returns one prepared client for every name, as the host's factory would.</summary>
internal sealed class StubClientFactory(HttpClient client) : IHttpClientFactory
{
    /// <inheritdoc />
    public HttpClient CreateClient(string name) => client;
}

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly List<RequestSnapshot> _requests = [];

    /// <summary>Creates a handler that answers synchronously.</summary>
    /// <param name="respond">Builds the response for one request.</param>
    internal StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    /// <summary>Creates a handler that answers asynchronously, for concurrency tests.</summary>
    /// <param name="respond">Builds the response for one request.</param>
    internal StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        _respond = respond;

    /// <summary>Every request seen, in order.</summary>
    internal IReadOnlyList<RequestSnapshot> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var snapshot = new RequestSnapshot(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.Parameter,
            request.Headers.TryGetValues("X-GitHub-Api-Version", out var version) ? version.FirstOrDefault() : null,
            request.Headers.Accept.ToString());

        lock (_requests)
        {
            _requests.Add(snapshot);
        }

        return await _respond(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>An <see cref="ISecretProvider"/> over one private-key value; every other name is unset.</summary>
internal sealed class MapSecretProvider : ISecretProvider
{
    private readonly string? _privateKey;

    internal MapSecretProvider(string? privateKey) => _privateKey = privateKey;

    public ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(name == SecretNames.GitHubAppPrivateKey ? _privateKey : null);
}

/// <summary>Captures formatted log lines so a test can assert what did and did not reach a log.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<string> _lines = [];

    /// <summary>Every formatted line and exception text written so far, in order.</summary>
    internal IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_lines)
        {
            _lines.Add($"{logLevel}:{eventId.Id}:{formatter(state, exception)}:{exception}");
        }
    }
}

/// <summary>The composed App client a test drives: one stub handler behind both clients and a fake clock.</summary>
internal sealed class GitHubTestBench
{
    /// <summary>A private key that exists only in this process.</summary>
    internal static readonly string TestPem = CreatePem();

    private const string AppId = "12345";

    internal GitHubTestBench(StubHandler handler, GitHubOptions? options = null, string? pem = null)
    {
        Handler = handler;
        Options = options ?? DefaultOptions(pem: pem ?? TestPem);
        Options.PrivateKey ??= pem ?? TestPem;
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        ClientLogs = new ListLogger<GitHubClient>();
        TokenLogs = new ListLogger<GitHubInstallationTokenSource>();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") };
        var clients = new StubClientFactory(http);
        AppTokens = new GitHubAppTokenSource(Microsoft.Extensions.Options.Options.Create(Options), Time);
        Tokens = new GitHubInstallationTokenSource(clients, AppTokens, Microsoft.Extensions.Options.Options.Create(Options), Time, TokenLogs);
        Client = new GitHubClient(http, Tokens, AppTokens, Microsoft.Extensions.Options.Options.Create(Options), Time, ClientLogs);
    }

    internal StubHandler Handler { get; }

    internal GitHubOptions Options { get; }

    internal FakeTimeProvider Time { get; }

    internal GitHubAppTokenSource AppTokens { get; }

    internal GitHubInstallationTokenSource Tokens { get; }

    internal GitHubClient Client { get; }

    internal ListLogger<GitHubClient> ClientLogs { get; }

    internal ListLogger<GitHubInstallationTokenSource> TokenLogs { get; }

    /// <summary>The default options: configured App, short tree cap so a capped tree is cheap to build.</summary>
    internal static GitHubOptions DefaultOptions(string? appId = AppId, string? pem = null) => new()
    {
        AppId = appId,
        PrivateKey = pem,
        MaxTreeEntries = 4,
        MaxBlobBytes = 64,
    };

    private static string CreatePem()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }
}

/// <summary>A stalled body: every read waits until the token it is given fires.</summary>
internal sealed class StalledStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new InvalidOperationException("The reader must use the asynchronous path.");

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A body whose first read is cut short, as the pipeline deadline does mid-stream.</summary>
internal sealed class AbortingStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new OperationCanceledException();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        throw new OperationCanceledException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A non-seekable body of <paramref name="totalBytes"/> that counts what the reader consumed.</summary>
internal sealed class GeneratedStream(int totalBytes) : Stream
{
    /// <summary>Bytes handed to the caller so far.</summary>
    internal int BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = Math.Min(buffer.Length, totalBytes - BytesRead);
        if (read <= 0)
        {
            return 0;
        }

        buffer[..read].Fill((byte)'{');
        BytesRead += read;
        return read;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Builders for the answers GitHub would send.</summary>
internal static class GitHubResponses
{
    internal const string InstallationJson =
        """{"id":7,"account":{"login":"acme"},"repository_selection":"selected"}""";

    internal const string RepositoryJson =
        """{"id":5,"full_name":"acme/catalog","default_branch":"main"}""";

    /// <summary>A response with a JSON body and the given status.</summary>
    internal static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>An empty response with the given status and optional <c>Retry-After</c>.</summary>
    internal static HttpResponseMessage Status(HttpStatusCode status, int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    /// <summary>An installation-token exchange answer.</summary>
    internal static HttpResponseMessage AccessToken(string token, DateTimeOffset expiresAt) =>
        Json($$"""{"token":"{{token}}","expires_at":"{{expiresAt:O}}"}""");
}
