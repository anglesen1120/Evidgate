using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.GitHub;
using Evidgate.Modules.Identity.Login.Google;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.GitHub;

/// <summary>
/// 02.3.1 §8 (S4): the outbound allowlist of GitHub login is a fixed code constant — <c>github.com</c> and
/// <c>api.github.com</c>, https only. A request whose URI leaves it is never sent, a 3xx is never followed
/// to another host, an options override that points anywhere else is refused at boot, and the composition
/// registers the provider so the <c>github</c> route value resolves instead of answering 404.
/// </summary>
/// <remarks>
/// Pure: the primary handler is a <see cref="GitHubTestHttpHandler"/>, so nothing here opens a socket.
/// </remarks>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class GitHubOutboundAllowlistTests
{
    private static readonly string[] AllowedOutboundUris =
    [
        "https://github.com/login/oauth/access_token",
        "https://api.github.com/user",
        "https://api.github.com/user/emails",
    ];

    [Fact]
    public async Task GitHubOutboundAllowlist_AllowsTheTokenAndApiHosts()
    {
        foreach (var uri in AllowedOutboundUris)
        {
            var (invoker, inner) = AllowlistHandler();

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, UriKind.Absolute));
            using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            inner.Calls.ShouldBe(1);
        }
    }

    [Fact]
    public async Task GitHubOutboundAllowlist_RefusesAnotherHost()
    {
        var (invoker, inner) = AllowlistHandler();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.evidgate-attacker.test/user", UriKind.Absolute));

        await Should.ThrowAsync<InvalidOperationException>(() => invoker.SendAsync(request, TestContext.Current.CancellationToken));
        inner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GitHubOutboundAllowlist_RefusesAPlainHttpUri()
    {
        var (invoker, inner) = AllowlistHandler();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://api.github.com/user", UriKind.Absolute));

        await Should.ThrowAsync<InvalidOperationException>(() => invoker.SendAsync(request, TestContext.Current.CancellationToken));
        inner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GitHubOutboundAllowlist_RefusesUserInfoInTheUri()
    {
        // Built with UriBuilder so no user:password pair ever appears as a literal in this file.
        var withUserInfo = new UriBuilder("https://api.github.com/user") { UserName = "u", Password = "p" }.Uri;
        withUserInfo.UserInfo.ShouldNotBeEmpty("the fixture must really carry userinfo");

        var (invoker, inner) = AllowlistHandler();

        using var request = new HttpRequestMessage(HttpMethod.Get, withUserInfo);

        await Should.ThrowAsync<InvalidOperationException>(() => invoker.SendAsync(request, TestContext.Current.CancellationToken));
        inner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GitHubOutboundAllowlist_RefusesANonDefaultPort()
    {
        var (invoker, inner) = AllowlistHandler();

        using var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder("https://api.github.com/user") { Port = 8443 }.Uri);

        await Should.ThrowAsync<InvalidOperationException>(() => invoker.SendAsync(request, TestContext.Current.CancellationToken));
        inner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GitHubOutboundAllowlist_DoesNotExposeTheUriInItsMessage()
    {
        var (invoker, _) = AllowlistHandler();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.evidgate-attacker.test/user", UriKind.Absolute));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => invoker.SendAsync(request, TestContext.Current.CancellationToken));
        exception.Message.ShouldNotContain("evidgate-attacker", Case.Insensitive);
    }

    [Fact]
    public void GitHubOutboundClients_DoNotFollowRedirects_AndKeepTheAllowlist()
    {
        using var provider = BuildProvider();
        var handlers = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        foreach (var client in new[] { GitHubLoginHttpClients.TokenClient, GitHubLoginHttpClients.ApiClient })
        {
            using var root = handlers.CreateHandler(client);
            var chain = Handlers(root).ToArray();

            chain.OfType<GitHubOutboundAllowlistHandler>().Count()
                .ShouldBe(1, $"{client} must refuse a request outside the allowlist");

            var primary = chain.OfType<SocketsHttpHandler>().ToArray();
            primary.Length.ShouldBe(1, $"{client} must own the primary handler");
            primary[0].AllowAutoRedirect.ShouldBeFalse($"{client} must never follow a redirect to another host (S4)");
        }
    }

    [Fact]
    public void GitHubLoginComposition_RegistersTheProviderSoTheGithubRouteResolves()
    {
        using var provider = BuildProvider();

        var providers = provider.GetServices<ILoginProvider>().ToArray();

        // AC of 02.3.1-T1.3: the `github` route value no longer answers 404 not_found.
        providers.ShouldContain(candidate => candidate.Name == "github");
        providers.ShouldContain(candidate => candidate.Name == "google", "the GitHub registration must not displace Google's");
    }

    [Fact]
    public void GitHubLoginOptionsValidator_AcceptsTheDefaults()
    {
        new GitHubLoginOptionsValidator().Validate(null, GitHubTestHarness.Options()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void GitHubLoginOptionsValidator_AcceptsAnUnconfiguredProvider()
    {
        var options = GitHubTestHarness.Options(clientId: null, clientSecret: null);

        new GitHubLoginOptionsValidator().Validate(null, options).Succeeded
            .ShouldBeTrue("absent credentials leave the provider unconfigured (503), they are not a misconfiguration");
    }

    [Fact]
    public void GitHubLoginOptionsValidator_RefusesATokenUrlOutsideTheAllowlist()
    {
        var options = GitHubTestHarness.Options(tokenUrl: "https://token.evidgate-attacker.test/access_token");

        var result = new GitHubLoginOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        var message = result.FailureMessage.ShouldNotBeNull();
        message.ShouldContain(nameof(GitHubLoginOptions.TokenUrl));
        message.ShouldNotContain("evidgate-attacker", Case.Insensitive);
    }

    [Fact]
    public void GitHubLoginOptionsValidator_RefusesAnApiBaseUrlOutsideTheAllowlist()
    {
        var options = GitHubTestHarness.Options(apiBaseUrl: "https://api.evidgate-attacker.test");

        new GitHubLoginOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void GitHubLoginOptionsValidator_RefusesAPlainHttpApiBaseUrl()
    {
        var options = GitHubTestHarness.Options(apiBaseUrl: "http://api.github.com");

        new GitHubLoginOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void GitHubLoginOptionsValidator_RefusesAnAuthorizeUrlOutsideGithub()
    {
        var options = GitHubTestHarness.Options(authorizeUrl: "https://github.evidgate-attacker.test/login/oauth/authorize");

        new GitHubLoginOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void GitHubLoginOptionsValidator_RefusesAClientSecretWithWhitespace_WithoutEchoingIt()
    {
        var options = GitHubTestHarness.Options(clientSecret: "two words");

        var result = new GitHubLoginOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotBeNull().ShouldNotContain("two words");
    }

    private static (HttpMessageInvoker Invoker, GitHubTestHttpHandler Inner) AllowlistHandler()
    {
        var inner = new GitHubTestHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var allowlist = new GitHubOutboundAllowlistHandler { InnerHandler = inner };

        return (new HttpMessageInvoker(allowlist), inner);
    }

    private static IEnumerable<HttpMessageHandler> Handlers(HttpMessageHandler root)
    {
        HttpMessageHandler? current = root;
        while (current is not null)
        {
            yield return current;
            current = current is DelegatingHandler delegating ? delegating.InnerHandler : null;
        }
    }

    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{GitHubLoginOptions.SectionName}:ClientId"] = GitHubTestHarness.ClientId,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProvider>(new GitHubTestSecretProvider(GitHubTestHarness.ClientSecret));
        services.AddGitHubLogin(configuration);
        services.AddGoogleLogin(configuration);

        return services.BuildServiceProvider();
    }
}
