using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.Modules.Authoring.Trackers.Application;
using Evidgate.Modules.Authoring.Trackers.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed class TrackerProviderTests
{
    [Theory]
    [InlineData(TrackerProvider.JiraCloud, "read:jira-work offline_access", "auth.atlassian.com")]
    [InlineData(TrackerProvider.Linear, "read", "linear.app")]
    internal void Authorize_RequestsExactlyReadScopesAndFixedHost(TrackerProvider provider, string scope, string host)
    {
        using var client = new HttpClient(new StubHandler());
        var sut = Provider(client);
        var uri = new Uri(sut.Authorize(provider, "state", "verifier"));
        uri.Host.ShouldBe(host);
        var query = QueryHelpers.ParseQuery(uri.Query);
        query["scope"].ToString().ShouldBe(scope);
        query["response_type"].ToString().ShouldBe("code");
        if (provider == TrackerProvider.Linear)
        {
            query["code_challenge_method"].ToString().ShouldBe("S256");
            query["code_challenge"].ToString().ShouldBe(WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes("verifier"))));
        }
        else
        {
            query["audience"].ToString().ShouldBe("api.atlassian.com");
            query["prompt"].ToString().ShouldBe("consent");
            query.ContainsKey("code_challenge").ShouldBeFalse();
        }
        uri.AbsoluteUri.ShouldNotContain("client-secret");
    }

    [Fact]
    public async Task Linear_PkceVerifierMustMatchAuthorizationChallenge()
    {
        const string verifier = "pkce-verifier-0123456789-abcdefghijklmnopqrstuvwxyz";
        using var authorizeClient = new HttpClient(new StubHandler());
        var authorize = new Uri(Provider(authorizeClient).Authorize(TrackerProvider.Linear, "state", verifier));
        var challenge = QueryHelpers.ParseQuery(authorize.Query)["code_challenge"].ToString();
        challenge.ShouldNotBeNullOrEmpty();
        using var handler = new StubHandler(
            HttpStatusCode.OK, "{\"access_token\":\"token-canary\",\"scope\":\"read\"}", expectedChallenge: challenge);
        using var client = new HttpClient(handler);
        var sut = Provider(client);

        (await sut.ExchangeAsync(TrackerProvider.Linear, "code", "wrong-verifier", CancellationToken.None)).ShouldBeNull();
        (await sut.ExchangeAsync(TrackerProvider.Linear, "code", verifier, CancellationToken.None))
            .ShouldNotBeNull().AccessToken.ShouldBe("token-canary");
        handler.Calls.ShouldBe(2);
        handler.Uri.ShouldBe(new Uri("https://api.linear.app/oauth/token"));
    }

    [Theory]
    [InlineData(TrackerProvider.JiraCloud)]
    [InlineData(TrackerProvider.Linear)]
    internal async Task Exchange_Success_ReadsBoundedTokensAndExactScopes(TrackerProvider provider)
    {
        var scope = provider == TrackerProvider.Linear ? "read" : "read:jira-work offline_access";
        using var handler = new StubHandler(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            access_token = "token-canary",
            refresh_token = "refresh-canary",
            scope,
            token_type = "Bearer",
            expires_in = 3600,
        }));
        using var client = new HttpClient(handler);
        var token = await Provider(client).ExchangeAsync(provider, "code", "verifier", CancellationToken.None);
        token.ShouldNotBeNull();
        token.AccessToken.ShouldBe("token-canary");
        string.Join(' ', token.GrantedScopes).ShouldBe(scope);
        handler.Calls.ShouldBe(1);
        handler.Uri.ShouldBe(new Uri(provider == TrackerProvider.Linear
            ? "https://api.linear.app/oauth/token"
            : "https://auth.atlassian.com/oauth/token"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect, false)]
    [InlineData(HttpStatusCode.OK, true)]
    public async Task RegisteredClient_DisablesRedirectsAndCookies_AndRefusesRedirectStatus(HttpStatusCode status, bool succeeds)
    {
        using var handler = new StubHandler(status, "{\"access_token\":\"token-canary\",\"scope\":\"read\"}");
        var services = RegisteredServices();
        var inspectedTransport = false;
        services.Configure<HttpClientFactoryOptions>(nameof(ITrackerProviderClient), options =>
            options.HttpMessageHandlerBuilderActions.Add(builder =>
            {
                using var transport = builder.PrimaryHandler.ShouldBeOfType<SocketsHttpHandler>();
                transport.AllowAutoRedirect.ShouldBeFalse();
                transport.UseCookies.ShouldBeFalse();
                builder.AdditionalHandlers.ShouldContain(item => item is TrackerOutboundAllowlistHandler);
                inspectedTransport = true;
                builder.PrimaryHandler = handler;
            }));
        using var provider = services.BuildServiceProvider();
        var sut = provider.GetRequiredService<ITrackerProviderClient>();

        var result = await sut.ExchangeAsync(TrackerProvider.Linear, "code", "verifier", CancellationToken.None);

        inspectedTransport.ShouldBeTrue();
        (result is not null).ShouldBe(succeeds);
        handler.Calls.ShouldBe(1);
        handler.Uri.ShouldBe(new Uri("https://api.linear.app/oauth/token"));
    }

    [Fact]
    public async Task Exchange_StallingBody_DeadlineReturnsNullWithoutCancellingCaller()
    {
        using var stream = new StallingStream();
        using var handler = new StubHandler(responseStream: stream);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var caller = new CancellationTokenSource();
        var clock = new FakeTimeProvider();
        var exchange = Provider(client, clock).ExchangeAsync(TrackerProvider.Linear, "code", "verifier", caller.Token);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        exchange.IsCompleted.ShouldBeFalse();

        clock.Advance(TrackerProviderClient.Deadline + TimeSpan.FromSeconds(1));

        (await exchange.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBeNull();
        stream.ReadToken.IsCancellationRequested.ShouldBeTrue();
        caller.IsCancellationRequested.ShouldBeFalse();
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Exchange_StallingBody_CallerCancellationPropagates()
    {
        using var stream = new StallingStream();
        using var handler = new StubHandler(responseStream: stream);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var caller = new CancellationTokenSource();
        var exchange = Provider(client, new FakeTimeProvider()).ExchangeAsync(TrackerProvider.Linear, "code", "verifier", caller.Token);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => exchange.WaitAsync(TimeSpan.FromSeconds(5)));
        stream.ReadToken.IsCancellationRequested.ShouldBeTrue();
    }

    [Theory]
    [InlineData(TrackerProvider.JiraCloud, 'a', 4096)]
    [InlineData(TrackerProvider.Linear, 'a', 4096)]
    [InlineData(TrackerProvider.JiraCloud, 'é', 2048)]
    [InlineData(TrackerProvider.Linear, 'é', 2048)]
    internal async Task Exchange_TokensAtPlaintextByteCap_ProtectWithinCipherCap(TrackerProvider provider, char value, int count)
    {
        var plaintext = new string(value, count);
        Encoding.UTF8.GetByteCount(plaintext).ShouldBe(4096);
        using var handler = new StubHandler(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            access_token = plaintext,
            refresh_token = plaintext,
            scope = provider == TrackerProvider.Linear ? "read" : "read:jira-work offline_access",
        }));
        using var client = new HttpClient(handler);
        var token = (await Provider(client).ExchangeAsync(provider, "code", "verifier", CancellationToken.None)).ShouldNotBeNull();
        token.AccessToken.ShouldBe(plaintext);
        token.RefreshToken.ShouldBe(plaintext);
        var protector = new TrackerTokenProtector(new EphemeralDataProtectionProvider());

        foreach (var issued in new[] { token.AccessToken, token.RefreshToken! })
        {
            var cipher = protector.ProtectToken(provider, issued);
            cipher.Length.ShouldBeLessThanOrEqualTo(8192);
            protector.UnprotectToken(provider, cipher).ShouldBe(plaintext);
        }
    }

    [Theory]
    [InlineData(TrackerProvider.JiraCloud, false, 'a', 4096)]
    [InlineData(TrackerProvider.Linear, false, 'a', 4096)]
    [InlineData(TrackerProvider.JiraCloud, true, 'a', 4096)]
    [InlineData(TrackerProvider.Linear, true, 'a', 4096)]
    [InlineData(TrackerProvider.Linear, false, 'é', 2048)]
    [InlineData(TrackerProvider.Linear, true, 'é', 2048)]
    internal async Task Exchange_TokenOneByteOverCap_IsRefused(TrackerProvider provider, bool refresh, char value, int count)
    {
        var oversized = new string(value, count) + "a";
        Encoding.UTF8.GetByteCount(oversized).ShouldBe(4097);
        using var handler = new StubHandler(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            access_token = refresh ? "token-canary" : oversized,
            refresh_token = refresh ? oversized : "refresh-canary",
        }));
        using var client = new HttpClient(handler);

        (await Provider(client).ExchangeAsync(provider, "code", "verifier", CancellationToken.None)).ShouldBeNull();
        handler.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData("http://api.linear.app/oauth/token")]
    [InlineData("https://api.linear.app.evil.test/oauth/token")]
    [InlineData("https://user@api.linear.app/oauth/token")]
    [InlineData("https://127.0.0.1/oauth/token")]
    [InlineData("https://api.linear.app:444/oauth/token")]
    public async Task Outbound_NonAllowlistedHost_IsNeverSent(string destination)
    {
        using var handler = new StubHandler();
        using var guard = new TrackerOutboundAllowlistHandler { InnerHandler = handler };
        using var client = new HttpClient(guard);
        await Should.ThrowAsync<InvalidOperationException>(() => client.GetAsync(destination));
        handler.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("https://auth.atlassian.com/oauth/token")]
    [InlineData("https://api.atlassian.com/")]
    [InlineData("https://api.linear.app/oauth/token")]
    [InlineData("https://linear.app/oauth/authorize")]
    public async Task Outbound_AllowlistedHttpsHost_IsSent(string destination)
    {
        using var handler = new StubHandler();
        using var guard = new TrackerOutboundAllowlistHandler { InnerHandler = handler };
        using var client = new HttpClient(guard);

        using var response = await client.GetAsync(destination, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        handler.Uri.ShouldBe(new Uri(destination));
        handler.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(65536, true, false)]
    [InlineData(65537, false, false)]
    [InlineData(65536, true, true)]
    [InlineData(65537, false, true)]
    public async Task Exchange_ValidJsonAtResponseByteBoundary_IsBounded(int bytes, bool succeeds, bool streaming)
    {
        const string prefix = "{\"access_token\":\"token-canary\",\"scope\":\"read\",\"padding\":\"";
        const string suffix = "\"}";
        var body = prefix + new string('x', bytes - prefix.Length - suffix.Length) + suffix;
        Encoding.UTF8.GetByteCount(body).ShouldBe(bytes);
        using var document = JsonDocument.Parse(body);
        using var stream = new NonSeekableStream(Encoding.UTF8.GetBytes(body));
        using var handler = new StubHandler(HttpStatusCode.OK, body, responseStream: streaming ? stream : null);
        using var client = new HttpClient(handler);

        var result = await Provider(client).ExchangeAsync(TrackerProvider.Linear, "code", "verifier", CancellationToken.None);

        (result is not null).ShouldBe(succeeds);
        if (result is not null)
        {
            result.AccessToken.ShouldBe("token-canary");
        }
    }

    [Fact]
    public async Task Exchange_OversizedOrWriteScopeResponse_IsRefused()
    {
        foreach (var body in new[] { new string('x', 65537), "{\"access_token\":\"token-canary\",\"scope\":\"read write\"}" })
        {
            using var handler = new StubHandler(HttpStatusCode.OK, body);
            using var client = new HttpClient(handler);
            (await Provider(client).ExchangeAsync(TrackerProvider.Linear, "code", "verifier", CancellationToken.None)).ShouldBeNull();
        }
    }

    [Fact]
    public void Options_AbsentProviders_AreValidButUnconfigured()
    {
        var jira = new JiraCloudTrackerOptions();
        var linear = new LinearTrackerOptions();
        var validator = new TrackerOAuthOptionsValidator();
        validator.Validate(null, jira).Succeeded.ShouldBeTrue();
        validator.Validate(null, linear).Succeeded.ShouldBeTrue();
        using var client = new HttpClient(new StubHandler());
        var sut = new TrackerProviderClient(client, Options.Create(jira), Options.Create(linear), TimeProvider.System);
        sut.IsConfigured(TrackerProvider.Linear).ShouldBeFalse();
        sut.IsConfigured(TrackerProvider.JiraCloud).ShouldBeFalse();
    }

    [Fact]
    public void Options_RedirectUriOfAnotherPath_IsRefusedAtBootWithoutEchoingValues()
    {
        var result = new TrackerOAuthOptionsValidator().Validate(null, new LinearTrackerOptions
        {
            ClientId = "client",
            ClientSecret = "secret-canary",
            RedirectUri = "https://evidgate.test/v1/tracker-connections/jira_cloud/callback",
        });

        result.Failed.ShouldBeTrue();
        string.Join(' ', result.Failures!).ShouldNotContain("secret-canary");
        string.Join(' ', result.Failures!).ShouldContain("EVIDGATE:TRACKERS:LINEAR:RedirectUri");
    }

    private static TrackerProviderClient Provider(HttpClient client, TimeProvider? clock = null) => new(
        client,
        Options.Create(new JiraCloudTrackerOptions { ClientId = "client", ClientSecret = "client-secret", RedirectUri = "https://evidgate.test/v1/tracker-connections/jira_cloud/callback" }),
        Options.Create(new LinearTrackerOptions { ClientId = "client", ClientSecret = "client-secret", RedirectUri = "https://evidgate.test/v1/tracker-connections/linear/callback" }),
        clock ?? TimeProvider.System);

    private static ServiceCollection RegisteredServices()
    {
        var services = new ServiceCollection();
        services.AddTrackerConnections();
        services.AddSingleton(Options.Create(new JiraCloudTrackerOptions
        {
            ClientId = "client",
            ClientSecret = "client-secret",
            RedirectUri = "https://evidgate.test/v1/tracker-connections/jira_cloud/callback",
        }));
        services.AddSingleton(Options.Create(new LinearTrackerOptions
        {
            ClientId = "client",
            ClientSecret = "client-secret",
            RedirectUri = "https://evidgate.test/v1/tracker-connections/linear/callback",
        }));
        return services;
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }

    private sealed class StallingStream : MemoryStream
    {
        public override bool CanSeek => false;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReadToken { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadToken = cancellationToken;
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class StubHandler(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = "{}",
        string? expectedChallenge = null,
        Stream? responseStream = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Body { get; private set; } = "";
        public Uri? Uri { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (expectedChallenge is not null)
            {
                var verifier = QueryHelpers.ParseQuery(Body)["code_verifier"].ToString();
                var actualChallenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
                if (!string.Equals(actualChallenge, expectedChallenge, StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }
            }

            var response = new HttpResponseMessage(status)
            {
                Content = responseStream is null ? new StringContent(body) : new StreamContent(responseStream),
            };
            response.Headers.Location = new Uri("https://evil.test/collect");
            return response;
        }
    }
}
