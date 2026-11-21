using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.Extensions.Http;

namespace Identity.IntegrationTests;

/// <summary>
/// The installation-token endpoint, faked: a mint answers with a token built by the test, and a failure answers 500
/// with a body that itself holds a token-shaped value, which the client must not echo into a log.
/// </summary>
internal sealed class GitHubTokenEndpointFake(string token, string rejectedBodyToken) : HttpMessageHandler
{
    private readonly ConcurrentQueue<string> _jwts = new();
    private int _calls;

    public bool Fail { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>The App JWTs the exchanges presented.</summary>
    public IReadOnlyList<string> Jwts => _jwts.ToArray();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (request.Headers.Authorization?.Parameter is { } jwt)
        {
            _jwts.Enqueue(jwt);
        }

        var response = Fail
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($$"""{"message":"upstream failure","token":"{{rejectedBodyToken}}"}""", Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    $$"""{"token":"{{token}}","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1):yyyy-MM-ddTHH:mm:ssZ}}"}""",
                    Encoding.UTF8,
                    "application/json"),
            };

        return Task.FromResult(response);
    }
}

internal sealed class GitHubTokenHandlerFilter(GitHubTokenEndpointFake handler) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        if (builder.Name == GitHubHttpClients.Token)
        {
            builder.PrimaryHandler = handler;
        }
    };
}

/// <summary>Answers one installation id for every org, so a token mint needs no request tenant.</summary>
internal sealed class FixedInstallationResolver(long installationId) : IGitHubInstallationResolver
{
    public Task<Result<long>> ResolveAsync(Guid orgId, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(installationId));
}
