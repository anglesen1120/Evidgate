using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Evidgate.BuildingBlocks.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// 10.3.1-T1.3: the resource attributes every signal carries, and the head-sampling policy —
/// a ratio that drops a healthy trace, keeps an error span of that same dropped trace (including
/// a child span, not only a root), keeps everything at ratio 1, and refuses a value outside
/// <c>[0, 1]</c> before the host starts.
/// </summary>
/// <remarks>
/// The assertions read what an exporter receives, not what the sampler was built with: the OTel
/// .NET export processors skip a span whose recorded flag is off, so "the ratio dropped it" and
/// "the error was kept" are proven on the exported surface, which is the only one an operator sees.
/// </remarks>
public sealed class TelemetryResourceAndSamplingTests
{
    /// <summary>The deployment this suite boots: a named environment proves the attribute is not a constant.</summary>
    private const string EnvironmentName = "Staging";

    /// <summary>Configured <c>service.version</c>, distinct from any assembly version.</summary>
    private const string ServiceVersion = "9.9.9-test+sha";

    [Fact]
    public void Resource_Builder_Carries_Service_Name_Environment_And_Version()
    {
        var resource = ObservabilityExtensions
            .CreateTelemetryResource("evidgate-api", EnvironmentName, ServiceVersion)
            .Build();

        Assert.Equal("evidgate-api", AttributeOf(resource, "service.name"));
        Assert.Equal(EnvironmentName, AttributeOf(resource, "deployment.environment"));
        Assert.Equal(ServiceVersion, AttributeOf(resource, "service.version"));
    }

    [Fact]
    public async Task Traces_Metrics_And_Logs_Carry_The_Three_Resource_Attributes()
    {
        await using var host = await StartHostAsync(sampleRatio: "1");
        var traces = host.Services.GetRequiredService<TracerProvider>().GetResource();
        var metrics = host.Services.GetRequiredService<MeterProvider>().GetResource();
        var logs = host.Services.GetRequiredService<LoggerProvider>().GetResource();

        foreach (var resource in new[] { traces, metrics, logs })
        {
            Assert.Equal("evidgate-test", AttributeOf(resource, "service.name"));
            Assert.Equal(EnvironmentName, AttributeOf(resource, "deployment.environment"));
            Assert.Equal(ServiceVersion, AttributeOf(resource, "service.version"));
        }
    }

    [Fact]
    public async Task Ratio_One_Exports_A_Healthy_Root_And_An_Error_Root()
    {
        await using var host = await StartHostAsync(sampleRatio: "1");

        RecordRoot("OkOne");
        RecordRoot("ErrorOne", ActivityStatusCode.Error);

        await WaitForAsync(host, () => Host_Exported(host, "OkOne") && Host_Exported(host, "ErrorOne"));

        Assert.Contains(host.Exported, activity => activity.OperationName == "OkOne");
        Assert.Contains(host.Exported, activity => activity.OperationName == "ErrorOne");
    }

    [Fact]
    public async Task Ratio_Zero_Drops_A_Healthy_Root_And_Keeps_An_Error_Root()
    {
        await using var host = await StartHostAsync(sampleRatio: "0");

        RecordRoot("OkZero");
        RecordRoot("ErrorZero", ActivityStatusCode.Error);

        // The error span is the proof the capture works: once it lands, an absent OkZero is a
        // dropped span, not an exporter that never ran.
        await WaitForAsync(host, () => Host_Exported(host, "ErrorZero"));

        Assert.Contains(host.Exported, activity => activity.OperationName == "ErrorZero");
        Assert.DoesNotContain(host.Exported, activity => activity.OperationName == "OkZero");
    }

    [Fact]
    public async Task Ratio_Zero_Keeps_An_Error_Child_Of_A_Dropped_Parent()
    {
        await using var host = await StartHostAsync(sampleRatio: "0");

        using (EvidgateTelemetry.StartModuleActivity("ingestion", "DroppedParent"))
        {
            using var child = EvidgateTelemetry.StartModuleActivity("ingestion", "KeptErrorChild");
            Assert.NotNull(child);
            child.SetStatus(ActivityStatusCode.Error);
        }

        await WaitForAsync(host, () => Host_Exported(host, "KeptErrorChild"));

        Assert.Contains(host.Exported, activity => activity.OperationName == "KeptErrorChild");
        Assert.DoesNotContain(host.Exported, activity => activity.OperationName == "DroppedParent");
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    [InlineData(2d)]
    public void A_Ratio_Outside_The_Closed_Interval_Is_A_Validation_Error(double ratio)
    {
        var results = Validate(ratio);

        Assert.Contains(results, result =>
            result.MemberNames.Contains(nameof(TelemetryOptions.SampleRatio), StringComparer.Ordinal));
        Assert.Contains(results, result =>
            result.ErrorMessage?.Contains("EVIDGATE__TELEMETRY__SAMPLERATIO", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(0.0001)]
    [InlineData(0.5)]
    [InlineData(1d)]
    public void A_Ratio_Inside_The_Closed_Interval_Is_Valid(double ratio) =>
        Assert.Empty(Validate(ratio));

    [Fact]
    public void A_NaN_Ratio_Is_A_Validation_Error()
    {
        Assert.Contains(Validate(double.NaN), result =>
            result.MemberNames.Contains(nameof(TelemetryOptions.SampleRatio), StringComparer.Ordinal));
    }

    [Fact]
    public void An_Infinite_Ratio_Is_A_Validation_Error()
    {
        Assert.Contains(Validate(double.PositiveInfinity), result =>
            result.MemberNames.Contains(nameof(TelemetryOptions.SampleRatio), StringComparer.Ordinal));
    }

    [Fact]
    public void A_Blank_Service_Version_Is_A_Validation_Error()
    {
        var results = Validate(sampleRatio: 1d, serviceVersion: " ");

        Assert.Contains(results, result =>
            result.MemberNames.Contains(nameof(TelemetryOptions.ServiceVersion), StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_Unconfigured_Service_Version_Falls_Back_To_A_Non_Blank_Value()
    {
        await using var host = await StartHostAsync(sampleRatio: "1", serviceVersion: null);
        var version = AttributeOf(host.Services.GetRequiredService<TracerProvider>().GetResource(), "service.version");

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.NotEqual(ServiceVersion, version);
    }

    private static List<ValidationResult> Validate(double sampleRatio, string? serviceVersion = null)
    {
        var options = new TelemetryOptions { SampleRatio = sampleRatio, ServiceVersion = serviceVersion };

        return [.. options.Validate(new ValidationContext(options))];
    }

    private static bool Host_Exported(TelemetryHost host, string operation) =>
        host.Exported.Any(activity => activity.OperationName == operation);

    /// <summary>Creates one root span of the application source, optionally marked failed.</summary>
    private static void RecordRoot(string operation, ActivityStatusCode status = ActivityStatusCode.Unset)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity("ingestion", operation);
        Assert.NotNull(activity);

        if (status != ActivityStatusCode.Unset)
        {
            activity.SetStatus(status);
        }
    }

    private static async Task<TelemetryHost> StartHostAsync(
        string sampleRatio,
        string? serviceVersion = ServiceVersion)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:Telemetry:SampleRatio"] = sampleRatio,
        };

        if (serviceVersion is not null)
        {
            settings["EVIDGATE:Telemetry:ServiceVersion"] = serviceVersion;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = EnvironmentName });
        builder.Configuration.AddInMemoryCollection(settings);

        var exported = new ExportedActivities();
        builder.AddEvidgateTelemetry("evidgate-test")
            .WithTracing(tracing => tracing.AddInMemoryExporter(exported));

        var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        return new TelemetryHost(host, exported);
    }

    /// <summary>Batch export is asynchronous; poll until <paramref name="satisfied"/> holds.</summary>
    private static async Task WaitForAsync(TelemetryHost host, Func<bool> satisfied)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (satisfied())
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(satisfied(), $"no span was exported within 20 s (host {host.Services.GetHashCode()})");
    }

    private static string? AttributeOf(OpenTelemetry.Resources.Resource resource, string key) =>
        resource.Attributes
            .Where(attribute => attribute.Key == key)
            .Select(attribute => attribute.Value as string)
            .SingleOrDefault();

    /// <summary>
    /// The exporter's sink: the in-memory exporter wants an <see cref="ICollection{T}"/>, the batch
    /// thread writes to it, and the test reads it — so the storage is a queue.
    /// </summary>
    private sealed class ExportedActivities : ICollection<Activity>
    {
        private readonly ConcurrentQueue<Activity> _queue = new();

        public int Count => _queue.Count;

        public bool IsReadOnly => false;

        public void Add(Activity item) => _queue.Enqueue(item);

        public void Clear()
        {
            while (_queue.TryDequeue(out _))
            {
            }
        }

        public bool Contains(Activity item) => _queue.Contains(item);

        public void CopyTo(Activity[] array, int arrayIndex) =>
            _queue.ToArray().CopyTo(array, arrayIndex);

        public bool Remove(Activity item) => false;

        public IEnumerator<Activity> GetEnumerator() => _queue.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public Activity[] Snapshot() => _queue.ToArray();
    }

    /// <summary>The started host plus the spans its exporter received.</summary>
    private sealed class TelemetryHost(IHost host, ExportedActivities exported) : IAsyncDisposable
    {
        public IServiceProvider Services => host.Services;

        public IReadOnlyCollection<Activity> Exported => exported.Snapshot();

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
            host.Dispose();
        }
    }
}
