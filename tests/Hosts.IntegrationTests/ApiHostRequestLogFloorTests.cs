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
        await using var app = BuildApi(sink, [], isolate: true);

        await StartNoticeAsync(app);

        sink.Entries.Where(entry => entry.Category == NoticeCategory).ShouldBeEmpty();
    }

    [Fact]
    public async Task RealApiHost_NoLoweringKey_IgnoresAnAmbientLoweringKey()
    {
        var sink = new CapturingLoggerProvider();
        await using var app = BuildApi(sink, [], isolate: true, ambient: new() { [LoweringKey] = "Trace", ["Logging:LogLevel:Microsoft.AspNetCore"] = "Debug" });

        await StartNoticeAsync(app);

        sink.Entries.Where(entry => entry.Category == NoticeCategory).ShouldBeEmpty();
    }

    [Fact]
    public async Task RealApiHost_NoticeSurvivesDefaultErrorAndANoneRuleOnEvidgate()
    {
        var sink = new CapturingLoggerProvider();
        var logging = new Dictionary<string, string?>
        {
            [LoweringKey] = "Trace",
            ["Logging:LogLevel:Default"] = "Error",
            ["Logging:LogLevel:Evidgate"] = "None",
            ["Logging:" + typeof(CapturingLoggerProvider).FullName + ":LogLevel:Default"] = "Error",
        };
        await using var app = BuildApi(sink, logging);

        await StartNoticeAsync(app);

        var notice = sink.Entries.Where(entry => entry.Category == NoticeCategory).ToList();
        notice.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public async Task RealApiHost_LaterRuleAfterTheFloor_RefusesToStart()
    {
        var sink = new CapturingLoggerProvider();
        await using var app = BuildApi(
            sink,
            [],
            services => services.PostConfigure<LoggerFilterOptions>(options =>
                options.Rules.Add(new LoggerFilterRule(null, Category + "*" + Category, LogLevel.Trace, null))));

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => StartNoticeAsync(app));

        failure.Message.ShouldContain("after the request-log floor");
        failure.Message.ShouldContain(Category);
    }

    [Fact]
    public async Task RealApiHost_FloorRulesAreTheLastRules_ForTheConfiguredProviderAndProviderLess()
    {
        var sink = new CapturingLoggerProvider();
        var providerKey = "Logging:" + typeof(CapturingLoggerProvider).FullName + ":LogLevel:Default";
        await using var app = BuildApi(sink, new() { [LoweringKey] = "Trace", [providerKey] = "Trace" });

        var rules = app.Services.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>().CurrentValue.Rules;

        var noticePattern = NoticeCategory + "*" + NoticeCategory;
        var floor = rules.Where(rule => rule.CategoryName == FloorPattern || rule.CategoryName == noticePattern).ToList();
        floor.ShouldNotBeEmpty("the floor is registered by the host builder");
        floor.ShouldAllBe(rule => rule.LogLevel == LogLevel.Warning);
        rules.Skip(rules.Count - floor.Count).ShouldBe(floor, "no rule follows the floor rules");
        foreach (var pattern in new[] { FloorPattern, noticePattern })
        {
            var providers = floor.Where(rule => rule.CategoryName == pattern).Select(rule => rule.ProviderName).ToList();
            providers.ShouldContain((string?)null);
            providers.ShouldContain(typeof(CapturingLoggerProvider).FullName);
        }
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

    private static WebApplication BuildApi(
        CapturingLoggerProvider sink,
        Dictionary<string, string?> logging,
        Action<IServiceCollection>? configure = null,
        bool isolate = false,
        Dictionary<string, string?>? ambient = null)
    {
        var builder = ApiHost.CreateBuilder([]);
        if (ambient is not null)
        {
            builder.Configuration.AddInMemoryCollection(ambient);
        }

        if (isolate)
        {
            // Whatever level keys the environment or appsettings carry are raised to Warning, so none can lower the category.
            var levelKeys = builder.Configuration.AsEnumerable().Where(pair => pair.Value is not null && pair.Key.StartsWith("Logging:", StringComparison.OrdinalIgnoreCase) && pair.Key.Contains(":LogLevel:", StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToList();
            builder.Configuration.AddInMemoryCollection(levelKeys.ToDictionary(key => key, _ => (string?)"Warning", StringComparer.OrdinalIgnoreCase));
        }

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:Database:ConnectionString"] = "Host=127.0.0.1;Port=1;Username=evidgate_app;Database=evidgate;Timeout=2",
            ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30",
            ["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00",
            ["EVIDGATE:Ci:Pepper"] = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()),
            [IsolationSelfCheckEnvironment.InMemoryKey] = IsolationSelfCheckEnvironment.Disabled,
        });
        builder.Configuration.AddInMemoryCollection(logging);
        builder.Logging.AddProvider(sink);
        configure?.Invoke(builder.Services);

        return builder.Build();
    }

    private static Task StartNoticeAsync(WebApplication app) =>
        app.Services.GetServices<IHostedService>().OfType<RequestLogFloorNotice>().ShouldHaveSingleItem()
            .StartAsync(CancellationToken.None);
}
