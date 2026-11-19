using System.Net;
using System.Text;
using Evidgate.Modules.Identity.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// A store outage on the link, invite-staging and CI-exchange routes answers the gate's neutral 503 with
/// <c>Retry-After: 1</c> before the route does any of its own work. The same request against a reachable store is
/// not refused by the gate, so the 503 can only come from the gate: a route that skipped it would answer with its own
/// status instead.
/// </summary>
[Trait("Category", "Docker")]
public sealed class SharedRateLimitOutageTests(PostgresFixture postgres)
{
    private const string Unavailable = "the service cannot answer right now; retry shortly";

    [Theory]
    [InlineData("link", "/v1/auth/link/not-a-provider")]
    [InlineData("invite", "/v1/auth/invite-token")]
    [InlineData("ci", "/v1/ci/token")]
    public async Task Post_UnreachableRateStore_Returns503WithRetryAfterAndNoSideEffect(string surface, string route)
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        await using var healthy = new EvidgateApiFactory(database, configureServices: ConfigureCi);
        using var healthyClient = healthy.CreateClient();
        using var control = await healthyClient.SendAsync(Request(route), TestContext.Current.CancellationToken);
        control.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests, surface);
        (await control.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain(Unavailable, Case.Sensitive, "a reachable store is never reported as an outage");

        await using var closed = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Username=evidgate_app;Timeout=1;Command Timeout=1;Pooling=false");
        await using var unavailable = new EvidgateApiFactory(database, configureServices: services =>
        {
            ConfigureCi(services);
            services.RemoveAll<NpgsqlDataSource>();
            services.AddSingleton(closed);
        });
        using var unavailableClient = unavailable.CreateClient();

        using var response = await unavailableClient.SendAsync(Request(route), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, surface);
        response.Headers.GetValues("Retry-After").ShouldHaveSingleItem().ShouldBe("1");
        response.Headers.Contains("Set-Cookie").ShouldBeFalse("an unadmitted request stages no cookie");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain(Unavailable, Case.Sensitive);
    }

    // The CI exchange answers 503 not_configured before it reaches the gate unless both secrets exist, so the
    // control must have them for the outage answer to be the gate's.
    private static void ConfigureCi(IServiceCollection services) =>
        services.PostConfigure<CiAuthOptions>(options =>
        {
            var key = Convert.ToBase64String(new byte[32].Select(_ => (byte)7).ToArray());
            options.SigningKey = key;
            options.Pepper = key;
        });

    private static HttpRequestMessage Request(string route) => new(HttpMethod.Post, route)
    {
        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
    };
}
