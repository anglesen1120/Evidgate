using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Identity.Login.Google;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Google;

/// <summary>
/// 02.3.1 §8 (S4): the outbound allowlist of Google login is a fixed code constant. A request whose URI
/// leaves <c>oauth2.googleapis.com</c> / <c>www.googleapis.com</c> over https is never sent, a 3xx is never
/// followed to another host, and an options override that points anywhere else is refused at boot.
/// </summary>
/// <remarks>
/// Pure: the primary handler is a <see cref="GoogleTestHttpHandler"/>, so nothing here opens a socket.
/// </remarks>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class OutboundAllowlistTests
{
    private static readonly string[] AllowedOutboundUris =
    [
        "https://oauth2.googleapis.com/token",
        "https://www.googleapis.com/oauth2/v3/certs",
    ];

    [Fact]
    public async Task GoogleOutboundAllowlist_AllowsTheTokenAndJwksHosts()
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
    public async Task GoogleOutboundAllowlist_RefusesAnotherHost()
    {
        var (invoker, inner) = AllowlistHandler();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://token.evidgate-attacker.test/token", UriKind.Absolute));

        await Should.ThrowAsync<InvalidOperationException>(() => invoker.SendAsync(request, TestContext.Current.CancellationToken));
        inner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GoogleOutboundAllowlist_RefusesPlainHttp()
    {
        var (invoker, inner) = AllowlistHandler();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://www.googleapis.com/oauth2/v3/certs", UriKind.Absolute));

        await Should.ThrowAsync<InvalidOperationException>(() => invoker.SendAsync(request, TestContext.Current.CancellationToken));
        inner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GoogleOutboundAllowlist_RefusesUserInfoInTheUri()
    {
        // Built with UriBuilder so no user:password pair ever appears as a literal in this file.
        var withUserInfo = new UriBuilder("https://www.googleapis.com/token") { UserName = "u", Password = "p" }.Uri;
        withUserInfo.UserInfo.ShouldNotBeEmpty("the fixture must really carry userinfo");

        var (invoker, inner) = AllowlistHandler();

        using var request = new HttpRequestMessage(HttpMethod.Get, withUserInfo);

        await Should.ThrowAsync<InvalidOperationException>(() => invoker.SendAsync(request, TestContext.Current.CancellationToken));
        inner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GoogleOutboundAllowlist_RefusesANonDefaultPort()
    {
        var (invoker, inner) = AllowlistHandler();

        using var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder("https://oauth2.googleapis.com/token") { Port = 8443 }.Uri);

        await Should.ThrowAsync<InvalidOperationException>(() => invoker.SendAsync(request, TestContext.Current.CancellationToken));
        inner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GoogleOutboundAllowlist_DoesNotExposeTheUriInItsMessage()
    {
        var (invoker, _) = AllowlistHandler();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://token.evidgate-attacker.test/token", UriKind.Absolute));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => invoker.SendAsync(request, TestContext.Current.CancellationToken));
        exception.Message.ShouldNotContain("evidgate-attacker", Case.Insensitive);
    }

    [Fact]
    public void GoogleOutboundClients_DoNotFollowRedirects_AndKeepTheAllowlist()
    {
        using var provider = BuildProvider();
        var handlers = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        foreach (var client in new[] { GoogleLoginHttpClients.TokenClient, GoogleLoginHttpClients.JwksClient })
        {
            using var root = handlers.CreateHandler(client);
            var chain = Handlers(root).ToArray();

            chain.OfType<GoogleOutboundAllowlistHandler>().Count()
                .ShouldBe(1, $"{client} must refuse a request outside the allowlist");

            var primary = chain.OfType<SocketsHttpHandler>().ToArray();
            primary.Length.ShouldBe(1, $"{client} must own the primary handler");
            primary[0].AllowAutoRedirect.ShouldBeFalse($"{client} must never follow a redirect to another host (S4)");
        }
    }

    [Fact]
    public void GoogleLoginOptionsValidator_AcceptsTheDefaults()
    {
        new GoogleLoginOptionsValidator().Validate(null, GoogleTestHarness.Options()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void GoogleLoginOptionsValidator_AcceptsAnUnconfiguredProvider()
    {
        var options = GoogleTestHarness.Options(clientId: null, clientSecret: null);

        new GoogleLoginOptionsValidator().Validate(null, options).Succeeded
            .ShouldBeTrue("absent credentials leave the provider unconfigured (503), they are not a misconfiguration");
    }

    [Fact]
    public void GoogleLoginOptionsValidator_RefusesATokenEndpointOutsideTheAllowlist()
    {
        var options = new GoogleLoginOptions { ClientId = GoogleTestHarness.ClientId, TokenEndpoint = "https://token.evidgate-attacker.test/token" };

        var result = new GoogleLoginOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        var message = result.FailureMessage.ShouldNotBeNull();
        message.ShouldContain(nameof(GoogleLoginOptions.TokenEndpoint));
        message.ShouldNotContain("evidgate-attacker", Case.Insensitive);
    }

    [Fact]
    public void GoogleLoginOptionsValidator_RefusesAJwksUriOutsideTheAllowlist()
    {
        var options = new GoogleLoginOptions { ClientId = GoogleTestHarness.ClientId, JwksUri = "https://keys.evidgate-attacker.test/certs" };

        new GoogleLoginOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void GoogleLoginOptionsValidator_RefusesAPlainHttpJwksUri()
    {
        var options = new GoogleLoginOptions { ClientId = GoogleTestHarness.ClientId, JwksUri = "http://www.googleapis.com/oauth2/v3/certs" };

        new GoogleLoginOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void GoogleLoginOptionsValidator_RefusesAnAuthorizationEndpointOutsideGoogle()
    {
        var options = new GoogleLoginOptions
        {
            ClientId = GoogleTestHarness.ClientId,
            AuthorizationEndpoint = "https://accounts.evidgate-attacker.test/o/oauth2/v2/auth",
        };

        new GoogleLoginOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void GoogleLoginOptionsValidator_RefusesAnIssuerOutsideGoogle()
    {
        var options = new GoogleLoginOptions { ClientId = GoogleTestHarness.ClientId, Issuer = "https://issuer.evidgate-attacker.test" };

        new GoogleLoginOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void GoogleLoginOptionsValidator_RefusesAWhitespaceClientSecret_WithoutEchoingIt()
    {
        var options = GoogleTestHarness.Options(clientSecret: "two words");

        var result = new GoogleLoginOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotBeNull().ShouldNotContain("two words");
    }

    private static (HttpMessageInvoker Invoker, GoogleTestHttpHandler Inner) AllowlistHandler()
    {
        var inner = new GoogleTestHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var allowlist = new GoogleOutboundAllowlistHandler { InnerHandler = inner };

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
                [$"{GoogleLoginOptions.SectionName}:ClientId"] = GoogleTestHarness.ClientId,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProvider>(new GoogleTestSecretProvider(GoogleTestHarness.ClientSecret));
        services.AddGoogleLogin(configuration);

        return services.BuildServiceProvider();
    }
}

/// <summary>Resolves the one Google secret and nothing else, so a composition test needs no environment.</summary>
internal sealed class GoogleTestSecretProvider(string? clientSecret) : ISecretProvider
{
    public ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(string.Equals(name, SecretNames.OidcGoogleClientSecret, StringComparison.Ordinal) ? clientSecret : null);
}
