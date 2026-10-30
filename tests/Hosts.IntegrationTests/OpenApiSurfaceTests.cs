using System.Text.Json;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC7 (T1.3): `/internal/*` operational endpoints exist but are absent from the
/// served OpenAPI document; `/metrics` likewise lives outside `/v1`.
/// </summary>
public sealed class OpenApiSurfaceTests
{
    [Fact]
    public async Task Internal_Routes_Absent_From_Served_Document()
    {
        await using var factory = new EvidgateApiFactory(
            "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2");
        using var client = factory.CreateClient();

        // The operational surface itself exists and answers.
        using var live = await client.GetAsync("/internal/health/live", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)live.StatusCode);

        using var metrics = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)metrics.StatusCode);

        // The served OpenAPI document has the documented /v1 routes but no /internal/* path.
        using var openApi = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)openApi.StatusCode);
        var body = await openApi.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("/internal/", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/metrics", body, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(body);
        var paths = document.RootElement.GetProperty("paths").EnumerateObject().ToArray();
        Assert.Contains(paths, path => path.Name == "/v1/permissions");
        Assert.DoesNotContain(paths, path => path.Name.StartsWith("/internal/", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.Name.StartsWith("/v1/slice", StringComparison.Ordinal));
    }
}
