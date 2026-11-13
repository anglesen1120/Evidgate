using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Catalog.Infrastructure.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

/// <summary>
/// The module's one composition entry: binding the App options and resolving the client through
/// <c>IHttpClientFactory</c> must work from a plain service collection, and the options must carry
/// what the host bound.
/// </summary>
public sealed class GitHubCompositionTests
{
    [Fact]
    public void AddCatalogGitHub_ResolvesTheClientAndBindsTheOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{GitHubOptions.SectionName}:AppId"] = "12345",
                [$"{GitHubOptions.SectionName}:MaxTreeEntries"] = "10",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProvider>(new MapSecretProvider(GitHubTestBench.TestPem));
        services.AddCatalogGitHub(configuration);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<GitHubClient>().ShouldNotBeNull();
        var options = provider.GetRequiredService<IOptions<GitHubOptions>>().Value;
        options.AppId.ShouldBe("12345");
        options.PrivateKey.ShouldBe(GitHubTestBench.TestPem);
        options.MaxTreeEntries.ShouldBe(10);
        new GitHubOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void AddCatalogGitHub_PinsNoRedirectsOnBothClients()
    {
        using var provider = BuildProvider();
        var handlers = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        foreach (var clientName in new[] { GitHubHttpClients.Api, GitHubHttpClients.Token, GitHubHttpClients.OAuth })
        {
            var primary = PrimaryHandler(handlers.CreateHandler(clientName));
            primary.AllowAutoRedirect.ShouldBeFalse(clientName);
            primary.PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(5));
        }
    }

    [Fact]
    public async Task ApiClient_OnRateLimit_ReturnsTheDelayWithoutRetrying()
    {
        var stub = new StubHandler(request => request.Method == HttpMethod.Post
            ? GitHubResponses.AccessToken("ghs_test", TokenExpiry)
            : GitHubResponses.Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 200_000));
        using var provider = BuildProvider(stub);
        var client = BuildClient(provider, stub);

        var result = await client.GetRepositoryAsync(GitHubTestBench.OrgId, 5, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.RateLimited);
        result.Error.Details!.Single().Actual.ShouldBe(200_000L);
        stub.Requests.Count(request => request.Method == HttpMethod.Get).ShouldBe(1);
    }

    [Fact]
    public async Task ApiClient_OnTransientStatus_StillRetriesInsideThePipeline()
    {
        var gets = 0;
        var stub = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return GitHubResponses.AccessToken("ghs_test", TokenExpiry);
            }

            gets++;
            return gets < 3
                ? GitHubResponses.Status(HttpStatusCode.ServiceUnavailable)
                : GitHubResponses.Json(GitHubResponses.RepositoryJson);
        });
        using var provider = BuildProvider(stub);
        var client = BuildClient(provider, stub);

        var result = await client.GetRepositoryAsync(GitHubTestBench.OrgId, 5, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        gets.ShouldBe(3);
    }

    private static readonly DateTimeOffset TokenExpiry = new(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);

    private static ServiceProvider BuildProvider(HttpMessageHandler? primary = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{GitHubOptions.SectionName}:AppId"] = "12345",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProvider>(new MapSecretProvider(GitHubTestBench.TestPem));
        services.AddCatalogGitHub(configuration);
        if (primary is not null)
        {
            services.AddSingleton<IHttpMessageHandlerBuilderFilter>(
                new StubPrimaryHandlerFilter(primary, GitHubHttpClients.Api));
        }

        return services.BuildServiceProvider();
    }

    private static GitHubClient BuildClient(IServiceProvider provider, HttpMessageHandler stub)
    {
        var options = provider.GetRequiredService<IOptions<GitHubOptions>>();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var appTokens = new GitHubAppTokenSource(options, time, new ListLogger<GitHubAppTokenSource>());
        var plain = new HttpClient(stub) { BaseAddress = new Uri("https://api.github.com") };
        var tokens = new GitHubInstallationTokenSource(
            GitHubTestBench.Meters,
            new StubClientFactory(plain),
            appTokens,
            options,
            time,
            new ListLogger<GitHubInstallationTokenSource>(),
            new StubInstallationResolver());

        return new GitHubClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubHttpClients.Api),
            tokens,
            appTokens,
            options,
            time,
            new ListLogger<GitHubClient>());
    }

    private static SocketsHttpHandler PrimaryHandler(HttpMessageHandler handler)
    {
        var current = handler;
        while (current is DelegatingHandler delegating && delegating.InnerHandler is { } inner)
        {
            current = inner;
        }

        return current.ShouldBeOfType<SocketsHttpHandler>();
    }
}
