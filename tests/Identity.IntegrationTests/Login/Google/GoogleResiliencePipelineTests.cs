using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Google;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Google;

/// <summary>
/// 02.3.1-T1.2 / AC8 (CR1): a JWKS timeout through the REAL resilience pipeline — the total timeout wrapping
/// one retry wrapping the per-attempt timeout — is a provider outage, not an unhandled 500. Polly's timeout
/// surfaces as <c>TimeoutRejectedException</c>, which is neither a <see cref="TimeoutException"/> nor an
/// <see cref="HttpRequestException"/> nor an <see cref="OperationCanceledException"/>, so it has to be mapped
/// explicitly. The failed attempt also stamps the refresh window before it awaits, so the next login inside
/// that window starts no new fetch.
/// </summary>
/// <remarks>
/// The composition is the host's own (<see cref="GoogleLoginServiceCollectionExtensions.AddGoogleLogin"/>);
/// only the two primary handlers are replaced, so no socket is opened while the pipeline under test stays
/// exactly as the host builds it.
/// </remarks>
public sealed class GoogleResiliencePipelineTests
{
    /// <summary>A test that hangs fails here instead of blocking the suite until it is killed.</summary>
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task GoogleJwksPipelineTimeout_IsProviderUnavailable_AndBacksOffWithoutAnotherFetch()
    {
        var keys = new GoogleTestKeys();

        // Minted against the wall clock: the validator's clock is the host's TimeProvider.System.
        var idToken = new GoogleTokenFactory(new MutableTimeProvider(DateTimeOffset.UtcNow), keys).Mint();

        var tokenHandler = new GoogleTestHttpHandler((_, _) => Task.FromResult(
            GoogleTestHttpHandler.Json($$"""{"access_token":"ignored","id_token":"{{idToken}}"}""")));

        // The JWKS endpoint accepts the call and never answers: the per-attempt timeout fires, the single
        // retry fires, and every attempt is cancelled by the pipeline itself.
        var jwksHandler = new GoogleTestHttpHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var services = Composition(tokenHandler, jwksHandler);
        var login = services.GetRequiredService<ILoginProvider>();
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await login.CompleteAsync(Callback(), cancellationToken).WaitAsync(TestDeadline, cancellationToken);

        first.Identity.ShouldBeNull();
        first.Failure.ShouldBe(
            LoginFailure.ProviderUnavailable,
            "a pipeline timeout is the provider being unavailable (503 + Retry-After), never an exception that becomes a 500");

        var attempts = jwksHandler.Calls;
        attempts.ShouldBeGreaterThan(0, "the hanging JWKS endpoint was really called");

        var second = await login.CompleteAsync(Callback(), cancellationToken).WaitAsync(TestDeadline, cancellationToken);

        second.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
        jwksHandler.Calls.ShouldBe(
            attempts,
            "the attempt is stamped before the fetch is awaited, so a second login inside the minimum refresh window issues no new HTTP request");
    }

    /// <summary>The real composition with both primary handlers replaced by in-memory stand-ins.</summary>
    private static ServiceProvider Composition(HttpMessageHandler tokenHandler, HttpMessageHandler jwksHandler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{GoogleLoginOptions.SectionName}:ClientId"] = GoogleTestHarness.ClientId,
                // The smallest budget the options allow: one second per attempt, so the test stays quick
                // while the pipeline still really times out.
                [$"{GoogleLoginOptions.SectionName}:HttpTimeoutSeconds"] = "1",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProvider>(new GoogleTestSecretProvider(GoogleTestHarness.ClientSecret));
        services.AddGoogleLogin(configuration);

        services.AddHttpClient(GoogleLoginHttpClients.TokenClient).ConfigurePrimaryHttpMessageHandler(() => tokenHandler);
        services.AddHttpClient(GoogleLoginHttpClients.JwksClient).ConfigurePrimaryHttpMessageHandler(() => jwksHandler);

        return services.BuildServiceProvider();
    }

    private static LoginCallbackRequest Callback() => new(
        "test-authorization-code",
        "test-code-verifier-0123456789abcdef",
        GoogleTestHarness.Nonce,
        GoogleTestHarness.RedirectUri);
}
