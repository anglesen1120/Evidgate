using System.Net;
using System.Net.Http.Json;
using Identity.IntegrationTests.Forwarding;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.DevLogin;

/// <summary>
/// What a sign-in writes behind a proxy: <c>session.ip</c> is the forwarded client's address while a listed
/// sender vouches for it, and the sender's own address when it is not listed.
/// </summary>
public sealed partial class DevSessionTests
{
    private const string Proxy = "203.0.113.10";

    private const string Client = "198.51.100.7";

    private const string UnlistedSender = "192.0.2.50";

    [Fact]
    public async Task A_Listed_Senders_Forwarded_Client_Is_The_Session_Address()
    {
        var orgId = await SeedDevAccountAsync(withMembership: true);

        using var plain = DevSessionTestHost.Start(Fixture, DevSessionTestHost.Development);
        using var host = plain.WithWebHostBuilder(builder =>
            ForwardedHeadersTestHost.Configure(builder, proxies: Proxy));
        using var client = ForwardedHeadersTestHost.CreateClient(host);

        using var response = await SignInAsync(client, plain.Secret, Proxy);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SessionAddressAsync()).ShouldBe(
            Client,
            "session.ip holds the client the listed proxy vouched for, never the proxy's own address");

        // The sign-in is still audited once for the org: the audit row carries the actor, the action and the
        // provider metadata, and audit_log has no client-address column for a forwarded address to change.
        (await DevSessionTestHost.LoginAuditRowsAsync(Fixture, orgId)).ShouldBe(1);
    }

    [Fact]
    public async Task An_Unlisted_Senders_Forwarded_Address_Is_Not_Believed()
    {
        await SeedDevAccountAsync(withMembership: true);

        using var plain = DevSessionTestHost.Start(Fixture, DevSessionTestHost.Development);
        using var host = plain.WithWebHostBuilder(builder =>
            ForwardedHeadersTestHost.Configure(builder, proxies: Proxy));
        using var client = ForwardedHeadersTestHost.CreateClient(host);

        using var response = await SignInAsync(client, plain.Secret, UnlistedSender);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SessionAddressAsync()).ShouldBe(
            UnlistedSender,
            "a sender that is not listed cannot choose the address the session records");
    }

    /// <summary>The <c>host(ip)</c> of the one session row: the literal the column holds.</summary>
    private Task<string> SessionAddressAsync() =>
        DevSessionTestHost.OwnerScalarAsync<string>(Fixture, "select host(ip) from public.session", []);

    /// <summary>One development sign-in arriving from <paramref name="sender"/> with a forwarded client.</summary>
    /// <param name="client">The host's client.</param>
    /// <param name="secret">The deployment's dev-login secret.</param>
    /// <param name="sender">The socket address the host's filter places on the connection.</param>
    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string secret, string sender)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DevSessionTestHost.Route)
        {
            Content = JsonContent.Create(new { secret }),
        };
        request.Headers.Add(ForwardedHeadersTestHost.SocketAddressHeader, sender);
        request.Headers.TryAddWithoutValidation(ForwardedHeadersTestHost.ForwardedForHeader, Client);

        return await client.SendAsync(request, DevSessionTestHost.Token);
    }
}
