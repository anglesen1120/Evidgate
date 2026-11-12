using System.Net;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// N9: <c>/metrics</c> and the readiness detail are served on the management listener only. The in-memory host
/// reports the listener through <see cref="EvidgateApiFactory.ListenerHeader"/>; the real socket split is proven
/// by <see cref="ManagementListenerProcessTests"/>.
/// </summary>
public sealed class ManagementSurfaceTests
{
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2";

    [Fact]
    public async Task Metrics_Is_Not_Served_On_The_Public_Port()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString, managementPort: EvidgateApiFactory.TestManagementPort);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"code\":\"not_found\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metrics_Is_Served_On_The_Management_Port()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString, managementPort: EvidgateApiFactory.TestManagementPort);
        using var client = factory.CreateManagementClient();

        using var response = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Metrics_Is_Not_Served_Anywhere_When_No_Management_Port_Is_Configured()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var publicClient = factory.CreateClient();
        using var claimsManagement = factory.CreateManagementClient();

        using var publicResponse = await publicClient.GetAsync("/metrics", TestContext.Current.CancellationToken);
        using var claimedResponse = await claimsManagement.GetAsync("/metrics", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, publicResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, claimedResponse.StatusCode);
    }

    [Fact]
    public async Task Readiness_Detail_Is_Served_On_The_Management_Port_Only()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString, managementPort: EvidgateApiFactory.TestManagementPort);
        using var publicClient = factory.CreateClient();
        using var management = factory.CreateManagementClient();

        var publicBody = await (await publicClient.GetAsync("/internal/health/ready", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var managementBody = await (await management.GetAsync("/internal/health/ready", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("database_unreachable", publicBody, StringComparison.Ordinal);
        Assert.Contains("database_unreachable", managementBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Management_Port_Answers_Nothing_But_The_Management_Paths()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString, managementPort: EvidgateApiFactory.TestManagementPort);
        using var management = factory.CreateManagementClient();

        using var v1 = await management.GetAsync("/v1/permissions", TestContext.Current.CancellationToken);
        using var openApi = await management.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, v1.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, openApi.StatusCode);
    }
}
