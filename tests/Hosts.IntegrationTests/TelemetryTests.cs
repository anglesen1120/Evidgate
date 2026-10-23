using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC6 (T3.2): a full slice call exports one trace whose activity tree carries
/// a span per module crossed, each tagged <c>evidgate.module</c>.
/// </summary>
public sealed class TelemetryTests
{
    [Fact]
    public async Task Slice_Trace_Has_Module_Spans()
    {
        var exported = new List<Activity>();

        await using var factory = new EvidgateApiFactory(
            "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2",
            configureServices: services =>
                services.AddOpenTelemetry()
                    .WithTracing(tracing => tracing.AddInMemoryExporter(exported)));

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/v1/slice/run",
            new { project_ref = "slice/test", event_count = 2 },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        // In-memory export is batch-based; poll until the slice trace lands.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        List<Activity> trace = [];
        while (DateTime.UtcNow < deadline)
        {
            var slice = exported
                .Where(a => a.Tags.Any(t => t.Key == "evidgate.module"))
                .ToList();
            if (slice.Count >= 4)
            {
                trace = slice;
                break;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        var modules = trace
            .SelectMany(a => a.Tags)
            .Where(t => t.Key == "evidgate.module")
            .Select(t => t.Value as string)
            .Where(v => v is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ingestion", modules);
        Assert.Contains("orchestrator", modules);
        Assert.Contains("catalog", modules);
        Assert.Contains("policy", modules);
    }
}
