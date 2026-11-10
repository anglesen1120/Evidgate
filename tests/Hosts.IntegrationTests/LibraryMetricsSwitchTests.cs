using System.Diagnostics.Metrics;
using Evidgate.BuildingBlocks.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// The library-meter subscriptions are the only part of the metrics pipeline that fans every database
/// command out to every live provider, so a host can turn them off; the switch must keep the platform's
/// own meter and drop the libraries'.
/// </summary>
public sealed class LibraryMetricsSwitchTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task LibraryMeters_AreSubscribed_OnlyWhenTheSwitchIsOn(string? value, bool expected)
    {
        const string ProbeName = "probe.library.commands";
        using var npgsql = new Meter(TelemetryMeters.Npgsql);
        var commands = npgsql.CreateCounter<long>(ProbeName);

        var builder = WebApplication.CreateBuilder();
        if (value is not null)
        {
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["EVIDGATE:Telemetry:LibraryMetrics"] = value });
        }

        var collected = new List<Metric>();
        builder.AddEvidgateTelemetry("evidgate-switch-test")
            .WithMetrics(metrics => metrics.AddInMemoryExporter(collected));

        await using var app = builder.Build();
        await app.StartAsync(TestContext.Current.CancellationToken);
        commands.Add(1);
        app.Services.GetRequiredService<MeterProvider>().ForceFlush();

        Assert.Equal(expected, collected.Exists(metric => metric.Name == ProbeName));
        await app.StopAsync(TestContext.Current.CancellationToken);
    }
}
