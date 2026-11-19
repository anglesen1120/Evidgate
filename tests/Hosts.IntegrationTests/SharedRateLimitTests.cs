using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class SharedRateLimitTests(PostgresFixture postgres)
{
    private const int Limit = 3;
    private const string Route = "/v1/auth/oidc/not-a-provider/start";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoginStart_RequestsAcrossReplicas_ShareOneAdmissionBudget(bool splitAcrossReplicas)
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        await using var first = new EvidgateApiFactory(database, extraConfiguration: Settings());
        await using var second = new EvidgateApiFactory(database, extraConfiguration: Settings());
        using var a = first.CreateClient();
        using var b = second.CreateClient();
        var admitted = 0;
        var refused = 0;

        for (var attempt = 0; attempt < Limit + 3; attempt++)
        {
            var client = splitAcrossReplicas && attempt % 2 != 0 ? b : a;
            using var response = await client.GetAsync(Route, TestContext.Current.CancellationToken);
            if (attempt < Limit)
            {
                // An unknown provider proves admission without external calls or login cookie writes.
                response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
                admitted++;
            }
            else
            {
                response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
                var header = response.Headers.GetValues("Retry-After").ShouldHaveSingleItem();
                int.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                    .ShouldBeTrue("Retry-After is an integer count of seconds");
                seconds.ShouldBeInRange(1, 3600);
                refused++;
            }
        }

        admitted.ShouldBe(Limit);
        refused.ShouldBe(3);
    }

    [Fact]
    public async Task LoginStart_UnreachableRateStore_Returns503InsteadOfAdmissionOr429()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        await using var healthy = new EvidgateApiFactory(database, extraConfiguration: Settings());
        using var healthyClient = healthy.CreateClient();
        using var control = await healthyClient.GetAsync(Route, TestContext.Current.CancellationToken);
        control.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var closed = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Username=evidgate_app;Timeout=1;Command Timeout=1;Pooling=false");
        await using var unavailable = new EvidgateApiFactory(database, configureServices: services =>
        {
            services.RemoveAll<NpgsqlDataSource>();
            services.AddSingleton(closed);
        }, extraConfiguration: Settings());
        using var unavailableClient = unavailable.CreateClient();

        using var response = await unavailableClient.GetAsync(Route, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.GetValues("Retry-After").ShouldHaveSingleItem().ShouldBe("1", "an outage answers with a short retry hint");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain(
                "the service cannot answer right now; retry shortly",
                Case.Sensitive,
                "the outage is the gate's neutral unavailable error, not a window refusal or the provider's");
    }

    private static Dictionary<string, string?> Settings() => new(StringComparer.Ordinal)
    {
        ["EVIDGATE:Identity:RateLimits:LoginLimit"] = "3",
        ["EVIDGATE:Identity:RateLimits:LoginWindowSeconds"] = "3600",
        ["EVIDGATE:Identity:RateLimits:LoginMaxKeys"] = "50000",
    };
}
