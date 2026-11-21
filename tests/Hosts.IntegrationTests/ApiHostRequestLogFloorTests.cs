using Evidgate.Api;
using Evidgate.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// The request-log floor and its start-up notice are wired by the real <see cref="ApiHost.CreateBuilder"/>: dropping
/// either registration there must turn a test red, which the hand-made hosts of <see cref="RequestLogFloorTests"/>
/// cannot show. The host is built, never started, so no database is needed; the notice is resolved from the
/// registered hosted services and started alone.
/// </summary>
public sealed class ApiHostRequestLogFloorTests
{
    private const string Category = RequestLogFloor.Category;
    private const string FloorPattern = Category + "*" + Category;
    private const string LoweringKey = "Logging:LogLevel:" + Category;
    private static readonly string NoticeCategory = typeof(RequestLogFloorNotice).FullName!;

    [Fact]
    public async Task RealApiHost_StartingTheNotice_WarnsWithTheLoweringKeyShapeOnly()
    {
        var sink = new CapturingLoggerProvider();
        await using var app = BuildApi(sink, new() { [LoweringKey] = "Trace" });

        await StartNoticeAsync(app);

        var notice = sink.Entries.Where(entry => entry.Category == NoticeCategory).ToList();
        notice.Count.ShouldBe(1, "one notice at start");
        notice[0].Level.ShouldBe(LogLevel.Warning);
        notice[0].EventId.Name.ShouldBe("RequestLogFloorHeld");
        notice[0].Message.ShouldContain(LoweringKey);
        notice[0].Texts.Any(text => text.Contains("Trace", StringComparison.Ordinal)).ShouldBeFalse("a configured value is never logged");
    }

    [Fact]
    public async Task RealApiHost_WithNoLoweringKey_StartsTheNoticeWithoutALine()
    {
        var sink = new CapturingLoggerProvider();
        await using var app = BuildApi(sink, []);

        await StartNoticeAsync(app);

        sink.Entries.Where(entry => entry.Category == NoticeCategory).ShouldBeEmpty();
    }

    [Fact]
    public async Task RealApiHost_FloorRulesAreTheLastRules_ForEveryProviderAndProviderLess()
    {
        var sink = new CapturingLoggerProvider();
        var providerKey = "Logging:" + typeof(CapturingLoggerProvider).FullName + ":LogLevel:Default";
        await using var app = BuildApi(sink, new() { [LoweringKey] = "Trace", [providerKey] = "Trace" });

        var rules = app.Services.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>().CurrentValue.Rules;

        var floor = rules.Where(rule => rule.CategoryName == FloorPattern).ToList();
        floor.ShouldNotBeEmpty("the floor is registered by the host builder");
        floor.ShouldAllBe(rule => rule.LogLevel == LogLevel.Warning);
        rules.Skip(rules.Count - floor.Count).ShouldBe(floor, "no rule follows the floor rules");
        floor.Select(rule => rule.ProviderName).ShouldContain((string?)null);
        floor.Select(rule => rule.ProviderName).ShouldContain(typeof(CapturingLoggerProvider).FullName);
    }

    [Fact]
    public async Task RealApiHost_RequestStartLog_ReachesAProviderOnlyAtWarningOrAbove()
    {
        var sink = new CapturingLoggerProvider();
        var providerKey = "Logging:" + typeof(CapturingLoggerProvider).FullName + ":LogLevel:Default";
        await using var app = BuildApi(sink, new() { [LoweringKey] = "Trace", [providerKey] = "Trace" });
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(Category);

        logger.Log(LogLevel.Trace, default, "below", null, static (message, _) => message);
        logger.Log(LogLevel.Information, default, "info", null, static (message, _) => message);
        logger.Log(LogLevel.Warning, default, "warn", null, static (message, _) => message);

        sink.Entries.Where(entry => entry.Category == Category).Select(entry => entry.Message).ShouldBe(["warn"]);
    }

    private static WebApplication BuildApi(CapturingLoggerProvider sink, Dictionary<string, string?> logging)
    {
        var builder = ApiHost.CreateBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:Database:ConnectionString"] = "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=x;Database=evidgate;Timeout=2",
            ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30",
            ["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00",
            ["EVIDGATE:Ci:Pepper"] = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()),
            [IsolationSelfCheckEnvironment.InMemoryKey] = IsolationSelfCheckEnvironment.Disabled,
        });
        builder.Configuration.AddInMemoryCollection(logging);
        builder.Logging.AddProvider(sink);

        return builder.Build();
    }

    private static Task StartNoticeAsync(WebApplication app) =>
        app.Services.GetServices<IHostedService>().OfType<RequestLogFloorNotice>().ShouldHaveSingleItem()
            .StartAsync(CancellationToken.None);
}
