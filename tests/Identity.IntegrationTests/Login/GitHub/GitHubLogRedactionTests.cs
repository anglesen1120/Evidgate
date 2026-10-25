using System.Net;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.GitHub;
using Identity.IntegrationTests.Login.Google;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.GitHub;

/// <summary>
/// 02.3.1 §8 (S3): GitHub login logs a closed failure category and nothing else. No authorization code,
/// PKCE verifier, nonce, client secret, access token, email, display name or subject reaches a log line or
/// an exception message, and provider text never does either.
/// </summary>
/// <remarks>
/// Pure: every provider answer is a <see cref="GitHubTestHttpHandler"/> stand-in and the logger is the
/// login suite's capturing <see cref="CapturingLoggerFactory"/>, so the assertion is on the exact strings
/// the shipping code would emit.
/// </remarks>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class GitHubLogRedactionTests
{
    private static readonly string[] ClosedFailureCategories =
    [
        "unconfigured",
        "token_invalid",
        "token_endpoint_rejected",
        "provider_unavailable",
    ];

    private const string Prefix = "GitHub login completion failed: ";

    [Fact]
    public async Task GitHubProvider_HappyPath_LogsNothing()
    {
        using var loggers = new CapturingLoggerFactory();
        var harness = new GitHubProviderHarness(loggers: loggers);

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldNotBeNull();
        loggers.Messages.ShouldBeEmpty("a successful login has nothing to say, least of all who logged in");
    }

    [Fact]
    public async Task GitHubProvider_Failures_LogOnlyClosedCategories()
    {
        using var loggers = new CapturingLoggerFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        var rejected = new GitHubProviderHarness(
            token: (_, _) => Task.FromResult(GitHubTestHttpHandler.Json(
                """{"error":"bad_verification_code","error_description":"provider-said-this"}""")),
            loggers: loggers);
        (await rejected.Provider.CompleteAsync(GitHubTestHarness.Callback(), cancellationToken)).Failure.ShouldBe(LoginFailure.TokenInvalid);

        var unavailable = new GitHubProviderHarness(
            token: (_, _) => Task.FromResult(GitHubTestHttpHandler.Json("""{"message":"provider-said-this"}""", HttpStatusCode.InternalServerError)),
            loggers: loggers);
        (await unavailable.Provider.CompleteAsync(GitHubTestHarness.Callback(), cancellationToken)).Failure.ShouldBe(LoginFailure.ProviderUnavailable);

        var unconfigured = new GitHubProviderHarness(
            settings: GitHubTestHarness.Options(clientSecret: null),
            loggers: loggers);
        (await unconfigured.Provider.CompleteAsync(GitHubTestHarness.Callback(), cancellationToken)).Failure.ShouldBe(LoginFailure.ProviderUnavailable);

        var noVerifiedEmail = new GitHubProviderHarness(
            api: (request, _) => Task.FromResult(
                request.RequestUri?.AbsolutePath.EndsWith("/user/emails", StringComparison.Ordinal) == true
                    ? GitHubTestHttpHandler.Json(GitHubTestHarness.EmailsJson(verified: false))
                    : GitHubTestHarness.DefaultApi(request)),
            loggers: loggers);
        (await noVerifiedEmail.Provider.CompleteAsync(GitHubTestHarness.Callback(), cancellationToken)).Failure.ShouldBe(LoginFailure.TokenInvalid);

        var messages = loggers.Messages;
        messages.ShouldNotBeEmpty("each failure is one line");

        foreach (var message in messages)
        {
            message.ShouldStartWith(Prefix);

            var category = message[Prefix.Length..];
            ClosedFailureCategories.ShouldContain(category, $"'{category}' must be one of the closed categories");
        }

        messages.ShouldContain($"{Prefix}token_endpoint_rejected");
        messages.ShouldContain($"{Prefix}provider_unavailable");
        messages.ShouldContain($"{Prefix}unconfigured");
        messages.ShouldContain($"{Prefix}token_invalid");
    }

    [Fact]
    public async Task GitHubProvider_NoSecretTokenOrIdentityValueEverReachesALogLine()
    {
        using var loggers = new CapturingLoggerFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        var failing = new GitHubProviderHarness(
            token: (_, _) => Task.FromResult(GitHubTestHttpHandler.Json(
                """{"error":"bad_verification_code","error_description":"provider-said-this"}""", HttpStatusCode.BadRequest)),
            loggers: loggers);

        await failing.Provider.CompleteAsync(GitHubTestHarness.Callback(), cancellationToken);

        var noVerifiedEmail = new GitHubProviderHarness(
            api: (request, _) => Task.FromResult(
                request.RequestUri?.AbsolutePath.EndsWith("/user/emails", StringComparison.Ordinal) == true
                    ? GitHubTestHttpHandler.Json(GitHubTestHarness.EmailsJson(verified: false))
                    : GitHubTestHarness.DefaultApi(request)),
            loggers: loggers);

        await noVerifiedEmail.Provider.CompleteAsync(GitHubTestHarness.Callback(), cancellationToken);

        var captured = loggers.Messages;
        captured.ShouldNotBeEmpty();

        foreach (var forbidden in new[]
                 {
                     "test-authorization-code",
                     "test-code-verifier-0123456789abcdef",
                     "test-nonce-1",
                     GitHubTestHarness.ClientSecret,
                     GitHubTestHarness.AccessToken,
                     GitHubTestHarness.Email,
                     GitHubTestHarness.Subject,
                     GitHubTestHarness.DisplayName,
                     GitHubTestHarness.Login,
                     "provider-said-this",
                 })
        {
            captured
                .Where(message => message.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                .ShouldBeEmpty($"'{forbidden}' must never be logged");
        }
    }

    [Fact]
    public async Task GitHubProvider_UnconfiguredProvider_LogsTheCategoryOnly()
    {
        using var loggers = new CapturingLoggerFactory();
        var harness = new GitHubProviderHarness(
            settings: GitHubTestHarness.Options(clientId: null, clientSecret: null),
            loggers: loggers);

        await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        loggers.Messages.ToArray().ShouldBe(new[] { $"{Prefix}unconfigured" });
    }

    [Fact]
    public async Task GitHubAllowlistRefusal_MessageCarriesNoUri()
    {
        var handler = new GitHubOutboundAllowlistHandler
        {
            InnerHandler = new GitHubTestHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))),
        };

        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.evidgate-attacker.test/user", UriKind.Absolute));

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => invoker.SendAsync(request, TestContext.Current.CancellationToken));

        exception.Message.ShouldNotContain("evidgate-attacker", Case.Insensitive);
        exception.Message.ShouldNotContain("Bearer", Case.Insensitive);
    }
}
