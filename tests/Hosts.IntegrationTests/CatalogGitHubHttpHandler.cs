using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Microsoft.Extensions.Http;

namespace Hosts.IntegrationTests;

internal sealed class CatalogGitHubHttpHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<(HttpMethod Method, string Path)> _requests = new();

    internal (HttpMethod Method, string Path)[] Requests => _requests.ToArray();

    internal string DefaultBranch { get; set; } = "main";

    internal bool Outage { get; set; }

    internal bool InstallationUnreachable { get; set; }

    /// <summary>A status the token exchange answers instead of a token, so a refused mint is provable.</summary>
    internal HttpStatusCode? MintStatus { get; set; }

    internal bool RepositoryUnreachable { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        _requests.Enqueue((request.Method, path));
        if (Outage)
        {
            throw new HttpRequestException("GitHub is unavailable");
        }

        if (request.Method == HttpMethod.Post && path.StartsWith("/app/installations/", StringComparison.Ordinal)
            && path.EndsWith("/access_tokens", StringComparison.Ordinal))
        {
            if (MintStatus is { } status)
            {
                return Task.FromResult(new HttpResponseMessage(status));
            }

            return Task.FromResult(InstallationUnreachable
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json(new { token = "host-test-installation-token", expires_at = DateTimeOffset.UtcNow.AddHours(1) }));
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/repositories/", StringComparison.Ordinal))
        {
            var id = long.Parse(path.AsSpan("/repositories/".Length), CultureInfo.InvariantCulture);
            return Task.FromResult(RepositoryUnreachable
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json(new { id, full_name = "acme/catalog", default_branch = DefaultBranch }));
        }

        throw new InvalidOperationException($"Unexpected GitHub request: {request.Method} {path}");
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
}

internal sealed class CatalogGitHubHandlerFilter(CatalogGitHubHttpHandler handler) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        if (builder.Name is GitHubHttpClients.Api or GitHubHttpClients.Token)
        {
            builder.PrimaryHandler = handler;
        }
    };
}
