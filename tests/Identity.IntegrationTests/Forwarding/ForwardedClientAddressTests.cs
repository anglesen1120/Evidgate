using System.Net;
using Evidgate.Api.Forwarding;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Identity.IntegrationTests.Login;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Forwarding;

/// <summary>
/// The client-address buckets of the limited login routes when a proxy sets <c>X-Forwarded-For</c>: a listed
/// proxy's clients keep their own windows, an unlisted sender's header is ignored, a multi-hop header resolves
/// to the first untrusted hop from the right, and a direct Api ignores the header altogether.
/// </summary>
/// <remarks>
/// Each case drives the real <c>start</c> route through the real composition, so the limiter key is the address
/// the forwarded-headers middleware left on the connection — no case reads the key directly.
/// </remarks>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class ForwardedClientAddressTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private const string Proxy = "203.0.113.10";

    private const string SecondProxy = "203.0.113.20";

    private const string Client = "198.51.100.7";

    private const string OtherClient = "198.51.100.8";

    private const string UnlistedSender = "192.0.2.50";

    private const string ListedSender = "192.0.2.60";

    [Fact]
    public async Task Two_Clients_Behind_A_Listed_Proxy_Keep_Separate_Budgets()
    {
        using var plain = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var host = plain.WithWebHostBuilder(builder =>
            ForwardedHeadersTestHost.Configure(builder, proxies: Proxy));
        using var client = ForwardedHeadersTestHost.CreateClient(host);
        var burst = ForwardedHeadersTestHost.Burst(host);

        for (var attempt = 1; attempt <= burst; attempt++)
        {
            using var allowed = await SendAsync(client, Proxy, Client);
            allowed.StatusCode.ShouldBe(
                HttpStatusCode.Found,
                $"start {attempt} of the {burst} the forwarded client's window allows");
        }

        using var refused = await SendAsync(client, Proxy, Client);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await LoginTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.RateLimited);

        // A second client reaches the same proxy and must still have its whole window: the bucket is the
        // forwarded address, never the proxy's.
        using var other = await SendAsync(client, Proxy, OtherClient);
        other.StatusCode.ShouldBe(HttpStatusCode.Found);
    }

    [Fact]
    public async Task An_Unlisted_Sender_Cannot_Rotate_X_Forwarded_For_To_Escape_Its_Budget()
    {
        using var plain = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var host = plain.WithWebHostBuilder(builder =>
            ForwardedHeadersTestHost.Configure(builder, proxies: Proxy + ", " + ListedSender));
        using var client = ForwardedHeadersTestHost.CreateClient(host);
        var burst = ForwardedHeadersTestHost.Burst(host);

        for (var attempt = 1; attempt <= burst; attempt++)
        {
            using var allowed = await SendAsync(client, UnlistedSender, "192.0.2." + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            allowed.StatusCode.ShouldBe(HttpStatusCode.Found, $"start {attempt} of the {burst} the sender's window allows");
        }

        // Every attempt named a different client; if any of them were believed, each would carry a fresh
        // window and this request would be served.
        using var refused = await SendAsync(client, UnlistedSender, "198.18.0.1");
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Positive control on the same host: a listed sender's client is believed and untouched by that flood.
        using var listed = await SendAsync(client, ListedSender, Client);
        listed.StatusCode.ShouldBe(HttpStatusCode.Found);
    }

    [Fact]
    public async Task A_Multi_Hop_Header_Resolves_To_The_Rightmost_Untrusted_Hop()
    {
        // Both proxies sit in one network, so the sender address and the header's last hop are both trusted;
        // the header is the deployed shape `spoof, client, proxy2`.
        using var plain = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var host = plain.WithWebHostBuilder(builder =>
            ForwardedHeadersTestHost.Configure(builder, proxies: string.Empty, networks: "203.0.113.0/24"));
        using var client = ForwardedHeadersTestHost.CreateClient(host);
        var burst = ForwardedHeadersTestHost.Burst(host);

        var chain = Client + ", " + SecondProxy;
        for (var attempt = 1; attempt <= burst; attempt++)
        {
            using var allowed = await SendAsync(client, SecondProxy, "203.0.113.99, " + chain);
            allowed.StatusCode.ShouldBe(HttpStatusCode.Found, $"start {attempt} of the {burst} the client's window allows");
        }

        // A value the client planted to the left of its own address changes nothing: the resolved client is
        // the same one, so the same window is spent.
        using var planted = await SendAsync(client, SecondProxy, "198.18.0.1, " + chain);
        planted.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // The untrusted hop of another chain is that client, and its window is its own.
        using var other = await SendAsync(client, SecondProxy, "203.0.113.99, " + OtherClient + ", " + SecondProxy);
        other.StatusCode.ShouldBe(HttpStatusCode.Found);
    }

    [Fact]
    public async Task Malformed_X_Forwarded_For_Values_Fall_Back_To_The_Senders_Key()
    {
        using var plain = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var host = plain.WithWebHostBuilder(builder =>
            ForwardedHeadersTestHost.Configure(builder, proxies: Proxy));
        using var client = ForwardedHeadersTestHost.CreateClient(host);
        var burst = ForwardedHeadersTestHost.Burst(host);

        string[] malformed = ["garbage", "1.2.3.4:abc", new string('9', 400), string.Empty];
        (burst - malformed.Length).ShouldBeGreaterThan(
            0,
            "the case needs room to spend the rest of the window without a header");

        // Served while the sender's window has room, and never an error the parser turned into a 500.
        foreach (var value in malformed)
        {
            using var allowed = await SendAsync(client, Proxy, value);
            allowed.StatusCode.ShouldBe(HttpStatusCode.Found, $"the header '{value}' must not fail the request");
        }

        // The rest of the window is spent with no header at all: the sender's own key.
        for (var attempt = malformed.Length + 1; attempt <= burst; attempt++)
        {
            using var allowed = await SendAsync(client, Proxy, forwardedFor: null);
            allowed.StatusCode.ShouldBe(HttpStatusCode.Found);
        }

        // Each malformed value now meets the spent window, which is the sender's: none of them opened a
        // bucket of its own, and none of them is a 500.
        foreach (var value in malformed.Append("1.2.3.4, , garbage"))
        {
            using var refused = await SendAsync(client, Proxy, value);
            refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, $"the header '{value}' must fall back to the sender");
        }
    }

    [Fact]
    public async Task A_Direct_Api_Ignores_X_Forwarded_For()
    {
        using var plain = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var host = plain.WithWebHostBuilder(builder =>
            ForwardedHeadersTestHost.Configure(builder, proxies: TrustedProxyOptions.None));

        Trusts(host).ShouldBeFalse("a declared direct Api adds no forwarded-headers middleware");

        using var client = ForwardedHeadersTestHost.CreateClient(host);
        var burst = ForwardedHeadersTestHost.Burst(host);

        for (var attempt = 1; attempt <= burst; attempt++)
        {
            using var allowed = await SendAsync(client, UnlistedSender, "198.18.0." + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            allowed.StatusCode.ShouldBe(HttpStatusCode.Found, $"start {attempt} of the {burst} the sender's window allows");
        }

        // The header is inert here, so all of those attempts were one bucket.
        using var refused = await SendAsync(client, UnlistedSender, "198.18.1.1");
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    /// <summary>True when the booted host trusts a proxy, i.e. adds the forwarded-headers middleware.</summary>
    /// <param name="host">The variant host.</param>
    private static bool Trusts(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<IOptions<TrustedProxyOptions>>().Value.HasTrust;

    /// <summary>One <c>start</c> request that arrives from <paramref name="sender"/> naming a forwarded client.</summary>
    /// <param name="sender">The socket address the host's filter places on the connection.</param>
    /// <param name="forwardedFor">The <c>X-Forwarded-For</c> value, or null for none.</param>
    private static HttpRequestMessage Start(string sender, string? forwardedFor)
    {
        var request = LoginTestHost.StartRequest("fake");
        request.Headers.Add(ForwardedHeadersTestHost.SocketAddressHeader, sender);

        if (forwardedFor is not null)
        {
            request.Headers.TryAddWithoutValidation(ForwardedHeadersTestHost.ForwardedForHeader, forwardedFor);
        }

        return request;
    }

    /// <summary>Sends one <c>start</c> request through <paramref name="client"/>.</summary>
    /// <param name="client">The host's client.</param>
    /// <param name="sender">The socket address of the request.</param>
    /// <param name="forwardedFor">The <c>X-Forwarded-For</c> value, or null for none.</param>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string sender, string? forwardedFor)
    {
        using var request = Start(sender, forwardedFor);

        return await LoginTestHost.SendAsync(client, request);
    }
}
