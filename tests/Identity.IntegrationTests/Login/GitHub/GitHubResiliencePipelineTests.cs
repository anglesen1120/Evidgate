using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.GitHub;

/// <summary>
/// 02.3.1-T1.3 / AC8 through the REAL resilience pipeline of the API client (one bounded retry wrapped in a
/// total budget and a per-attempt timeout). The two profile GETs are idempotent, so a 5xx is retried
/// exactly once; a hanging endpoint ends on the pipeline's timeout, which is a provider outage and not an
/// unhandled 500. The token POST stays un-retried: its authorization code is single-use.
/// </summary>
/// <remarks>
/// The composition is the host's own (<see cref="GitHubLoginServiceCollectionExtensions.AddGitHubLogin"/>);
/// only the two primary handlers are replaced, so no socket is opened while the pipeline under test stays
/// exactly as the host builds it.
/// </remarks>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class GitHubResiliencePipelineTests
{
    /// <summary>A test that hangs fails here instead of blocking the suite until it is killed.</summary>
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task GitHubApiGets_RetryExactlyOnce_ThenAnswerUnavailable()
    {
        var apiHandler = new GitHubTestHttpHandler((_, _) => Task.FromResult(
            GitHubTestHttpHandler.Json("""{"message":"Server Error"}""", HttpStatusCode.InternalServerError)));

        using var services = Composition(TokenHandler(), apiHandler);
        var login = services.GetRequiredService<ILoginProvider>();
        var cancellationToken = TestContext.Current.CancellationToken;

        var result = await login.CompleteAsync(GitHubTestHarness.Callback(), cancellationToken).WaitAsync(TestDeadline, cancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
        apiHandler.Calls.ShouldBe(2, "one attempt plus exactly one retry: the GET is idempotent, so two is the budget");
    }

    [Fact]
    public async Task GitHubApiPipelineTimeout_IsProviderUnavailable_NotAnException()
    {
        // The endpoint accepts the call and never answers: the per-attempt timeout fires, the single retry
        // fires, and every attempt is cancelled by the pipeline itself (Polly's TimeoutRejectedException).
        var apiHandler = new GitHubTestHttpHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var services = Composition(TokenHandler(), apiHandler);
        var login = services.GetRequiredService<ILoginProvider>();
        var cancellationToken = TestContext.Current.CancellationToken;

        var result = await login.CompleteAsync(GitHubTestHarness.Callback(), cancellationToken).WaitAsync(TestDeadline, cancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(
            LoginFailure.ProviderUnavailable,
            "a pipeline timeout is the provider being unavailable (503 + Retry-After), never an exception that becomes a 500");
        apiHandler.Calls.ShouldBeGreaterThan(0, "the hanging endpoint was really called");
    }

    [Fact]
    public async Task GitHubTokenPost_RunsWithoutARetryPipeline()
    {
        // The token client has no resilience handler at all, so one 5xx is one call.
        var tokenHandler = new GitHubTestHttpHandler((_, _) => Task.FromResult(
            GitHubTestHttpHandler.Json("""{"message":"Server Error"}""", HttpStatusCode.InternalServerError)));

        using var services = Composition(tokenHandler, new GitHubTestHttpHandler((request, _) => Task.FromResult(GitHubTestHarness.DefaultApi(request))));
        var login = services.GetRequiredService<ILoginProvider>();
        var cancellationToken = TestContext.Current.CancellationToken;

        var result = await login.CompleteAsync(GitHubTestHarness.Callback(), cancellationToken).WaitAsync(TestDeadline, cancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
        tokenHandler.Calls.ShouldBe(1, "an authorization code is single-use: the exchange is never retried");
    }

    private static GitHubTestHttpHandler TokenHandler() =>
        new((_, _) => Task.FromResult(GitHubTestHttpHandler.Json(GitHubTestHarness.TokenJson())));

    /// <summary>The real composition with both primary handlers replaced by in-memory stand-ins.</summary>
    private static ServiceProvider Composition(HttpMessageHandler tokenHandler, HttpMessageHandler apiHandler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{GitHubLoginOptions.SectionName}:ClientId"] = GitHubTestHarness.ClientId,
                // The smallest budget the options allow: one second per attempt, so the test stays quick
                // while the pipeline still really times out.
                [$"{GitHubLoginOptions.SectionName}:HttpTimeoutSeconds"] = "1",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProvider>(new GitHubTestSecretProvider(GitHubTestHarness.ClientSecret));
        services.AddGitHubLogin(configuration);

        services.AddHttpClient(GitHubLoginHttpClients.TokenClient).ConfigurePrimaryHttpMessageHandler(() => tokenHandler);
        services.AddHttpClient(GitHubLoginHttpClients.ApiClient).ConfigurePrimaryHttpMessageHandler(() => apiHandler);

        return services.BuildServiceProvider();
    }
}
