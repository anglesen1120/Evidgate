using Evidgate.Api;
using Evidgate.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// The request-start log category stays at Warning or above for every provider, whatever shape of Logging key tries
/// to lower it. Each case drives the real <see cref="LoggerFactory"/> with the host's post-configuration and counts
/// what reaches a provider; messages carry counts only.
/// </summary>
public sealed class RequestLogFloorTests
{
    private const string Category = RequestLogFloor.Category;
    private const string OtherCategory = "Evidgate.Other";
    private static readonly string FullNameSection = "Logging:" + typeof(FullNameProvider).FullName;

    public static TheoryData<string, string> LoweringKeys() => new()
    {
        { "Logging:LogLevel:" + Category, "exact, provider-less" },
        { "Logging:LogLevel:Microsoft.AspNetCore.Hosting.*", "prefix wildcard, provider-less" },
        { "Logging:LogLevel:Microsoft.*Diagnostics", "prefix and suffix, provider-less" },
        { "Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostic*s", "overlapping suffix, provider-less" },
        { "Logging:LogLevel:" + Category + "*", "trailing wildcard, provider-less" },
        { "Logging:LogLevel:" + Category.ToUpperInvariant(), "upper case, provider-less" },
        { "Logging:LogLevel:" + Category.ToLowerInvariant(), "lower case, provider-less" },
        { "Logging:Alias:LogLevel:Default", "alias Default" },
        { FullNameSection + ":LogLevel:Default", "full name Default" },
        { "Logging:Alias:LogLevel:Microsoft.AspNetCore", "alias parent category" },
        { FullNameSection + ":LogLevel:Microsoft", "full name parent category" },
        { "Logging:Alias:LogLevel:" + Category, "alias exact" },
        { "Logging:Alias:LogLevel:" + Category + "*", "alias trailing wildcard" },
        { "Logging:Alias:LogLevel:Microsoft.*Diagnostics", "alias prefix and suffix" },
        { "Logging:Alias:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostic*s", "alias overlapping suffix" },
        { "Logging:Alias:LogLevel:" + Category.ToUpperInvariant(), "alias upper case" },
        { FullNameSection + ":LogLevel:" + Category + "*", "full name trailing wildcard" },
        { "Logging:LogLevel:" + Category + "*" + Category, "longest legal pattern (whole category as prefix and as suffix), provider-less" },
        { "Logging:Alias:LogLevel:" + Category + "*" + Category, "longest legal pattern, alias" },
        { FullNameSection + ":LogLevel:" + Category.ToUpperInvariant() + "*" + Category.ToLowerInvariant(), "longest legal pattern, full name, mixed case" },
    };

    [Theory]
    [MemberData(nameof(LoweringKeys))]
    public void NoKeyShapeLowersTheFloor_ForAnyProvider(string key, string shape)
    {
        using var host = Build(new() { [key] = "Trace" }, out var aliased, out var fullName, out var ruleless);

        Send(host, Category);

        foreach (var sink in new[] { aliased, fullName, ruleless })
        {
            sink.Count(Category, below: true).ShouldBe(0, $"{shape}: lines below Warning reaching {sink.GetType().Name}");
            sink.Count(Category, below: false).ShouldBe(1, $"{shape}: the Warning line reaching {sink.GetType().Name}");
        }
    }

    [Fact]
    public void AliasAndFullNameInTheSameConfiguration_BothStayUnderTheFloor()
    {
        using var host = Build(
            new()
            {
                ["Logging:Alias:LogLevel:Default"] = "Trace",
                [FullNameSection + ":LogLevel:" + Category] = "Trace",
                ["Logging:LogLevel:" + Category + "*"] = "Trace",
            },
            out var aliased,
            out var fullName,
            out _);

        Send(host, Category);

        aliased.Count(Category, below: true).ShouldBe(0);
        fullName.Count(Category, below: true).ShouldBe(0);
    }

    [Fact]
    public void ProviderDefaultAndParentRules_KeepTheirLevelForOtherCategories()
    {
        using var host = Build(
            new()
            {
                ["Logging:Alias:LogLevel:Default"] = "Trace",
                [FullNameSection + ":LogLevel:Microsoft"] = "Trace",
            },
            out var aliased,
            out var fullName,
            out _);

        Send(host, Category);
        Send(host, OtherCategory);
        Send(host, "Microsoft.AspNetCore.Routing");

        aliased.Count(OtherCategory, below: true).ShouldBe(3, "the provider Default still applies to another category");
        fullName.Count("Microsoft.AspNetCore.Routing", below: true).ShouldBe(3, "the provider parent rule still applies to a sibling category");
        aliased.Count(Category, below: true).ShouldBe(0);
        fullName.Count(Category, below: true).ShouldBe(0);
    }

    [Fact]
    public void ProviderWithoutRulesAndProviderAddedLater_FallUnderTheProviderlessFloor()
    {
        using var host = Build(new() { ["Logging:LogLevel:Default"] = "Trace" }, out _, out _, out var ruleless);
        var late = new LateProvider();
        host.Services.GetRequiredService<ILoggerFactory>().AddProvider(late);

        Send(host, Category);
        Send(host, OtherCategory);

        ruleless.Count(Category, below: true).ShouldBe(0);
        late.Count(Category, below: true).ShouldBe(0);
        late.Count(OtherCategory, below: true).ShouldBe(3, "the floor touches the one category only");
    }

    [Fact]
    public void ReloadAfterStart_CannotLowerTheFloor()
    {
        var source = new ReloadableSource();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(config => config.Add(source))
            .ConfigureLogging((_, logging) =>
            {
                logging.ClearProviders();
            })
            .ConfigureServices(services => services.PostConfigure<LoggerFilterOptions>(RequestLogFloor.Apply))
            .Build();
        var sink = new AliasedProvider();
        host.Services.GetRequiredService<ILoggerFactory>().AddProvider(sink);

        source.Set("Logging:Alias:LogLevel:Default", "Trace");
        source.Set("Logging:LogLevel:" + Category + "*", "Trace");
        source.Reload();
        Send(host, Category);
        Send(host, OtherCategory);

        sink.Count(Category, below: true).ShouldBe(0, "after the reload the floor holds");
        sink.Count(OtherCategory, below: true).ShouldBe(3, "the reloaded key did apply to another category");
    }

    [Fact]
    public async Task OverridingKeys_AreReportedAtStartByShape_WithoutTheirValue()
    {
        var log = new AliasedProvider();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging((_, logging) =>
            {
                logging.ClearProviders();
                logging.AddProvider(log);
            })
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:Alias:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:" + Category + "*"] = "Debug",
                ["Logging:LogLevel:Default"] = "Information",
                ["Logging:Console:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Logging:Alias:LogLevel:" + Category + "*" + Category] = "Debug",
            }))
            .ConfigureServices(services => services
                .PostConfigure<LoggerFilterOptions>(RequestLogFloor.Apply)
                .AddHostedService<RequestLogFloorNotice>())
            .Build();

        await host.StartAsync(TestContext.Current.CancellationToken);

        var notice = log.Texts("Evidgate.Api.RequestLogFloorNotice").ShouldHaveSingleItem();
        notice.ShouldContain($"Logging:LogLevel:{Category}*");
        notice.ShouldContain("Logging:Alias:LogLevel:Default");
        notice.ShouldContain("Logging:Alias:LogLevel:" + Category + "*" + Category, Case.Sensitive, "an operator rule of the floor's own pattern is kept and reported");
        notice.ShouldContain("Logging:Console:LogLevel:Microsoft.AspNetCore", Case.Sensitive, "a parent category is a prefix rule the selector applies to the category");
        notice.ShouldNotContain("Logging:LogLevel:Default,", Case.Sensitive, "the generic provider-less Default is not an override");
        notice.ShouldNotContain("Trace", Case.Sensitive, "a configured value is never logged");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReloadWithAnOverridingKey_ReportsItAgain()
    {
        var source = new ReloadableSource();
        var log = new AliasedProvider();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(config => config.Add(source))
            .ConfigureLogging((_, logging) =>
            {
                logging.ClearProviders();
                logging.AddProvider(log);
            })
            .ConfigureServices(services => services
                .PostConfigure<LoggerFilterOptions>(RequestLogFloor.Apply)
                .AddHostedService<RequestLogFloorNotice>())
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        log.Texts("Evidgate.Api.RequestLogFloorNotice").ShouldBeEmpty("nothing overrides the floor at start");

        source.Set("Logging:Alias:LogLevel:Default", "Trace");
        source.Reload();

        log.Texts("Evidgate.Api.RequestLogFloorNotice").ShouldHaveSingleItem().ShouldContain("Logging:Alias:LogLevel:Default");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void NoOverridingKey_NoNotice()
    {
        var options = new LoggerFilterOptions();
        options.Rules.Add(new LoggerFilterRule(null, null, LogLevel.Trace, null));
        options.Rules.Add(new LoggerFilterRule(null, "Microsoft.AspNetCore.Hosting.Diagnostics.Other", LogLevel.Trace, null));

        RequestLogFloor.OverriddenKeyShapes(options).ShouldBeEmpty();
    }

    [Fact]
    public void Apply_KeepsAnOperatorRuleOfTheFloorsOwnPattern()
    {
        var options = new LoggerFilterOptions();
        options.Rules.Add(new LoggerFilterRule("p", RequestLogFloor.Category + "*" + RequestLogFloor.Category, LogLevel.Trace, null));

        RequestLogFloor.Apply(options);

        options.Rules.Count(rule => rule.ProviderName == "p" && rule.LogLevel == LogLevel.Trace).ShouldBe(1);
        var floor = options.Rules.Single(rule => rule.ProviderName == "p" && rule.LogLevel == LogLevel.Warning && rule.CategoryName == (RequestLogFloor.Category + "*" + RequestLogFloor.Category));
        options.Rules.IndexOf(floor).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var options = new LoggerFilterOptions();
        options.Rules.Add(new LoggerFilterRule("p", null, LogLevel.Trace, null));

        RequestLogFloor.Apply(options);
        var once = options.Rules.Count;
        RequestLogFloor.Apply(options);

        options.Rules.Count.ShouldBe(once);
        options.Rules[^1].LogLevel.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public void Apply_AddsTheNoticeRuleForEveryProviderAndProviderLess_AtWarning()
    {
        var options = new LoggerFilterOptions();
        options.Rules.Add(new LoggerFilterRule("p", null, LogLevel.Error, null));

        RequestLogFloor.Apply(options);

        var notice = options.Rules.Where(rule => rule.CategoryName == (typeof(RequestLogFloorNotice).FullName + "*" + typeof(RequestLogFloorNotice).FullName)).ToList();
        notice.Select(rule => rule.ProviderName).ShouldBe(["p", null], ignoreOrder: true);
        notice.ShouldAllBe(rule => rule.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public void AssertFloorIsLast_AfterApply_Passes()
    {
        var options = new LoggerFilterOptions();
        options.Rules.Add(new LoggerFilterRule("p", null, LogLevel.Trace, null));
        RequestLogFloor.Apply(options);

        Should.NotThrow(() => RequestLogFloor.AssertFloorIsLast(options));
    }

    [Fact]
    public void AssertFloorIsLast_ARuleAfterTheFloor_Throws()
    {
        var options = new LoggerFilterOptions();
        RequestLogFloor.Apply(options);
        options.Rules.Add(new LoggerFilterRule(null, "Evidgate", LogLevel.None, null));

        Should.Throw<InvalidOperationException>(() => RequestLogFloor.AssertFloorIsLast(options)).Message.ShouldContain("after the request-log floor");
    }

    [Fact]
    public void AssertFloorIsLast_ProviderLessNoticeRuleMissing_Throws()
    {
        var options = new LoggerFilterOptions();
        RequestLogFloor.Apply(options);
        options.Rules.Remove(options.Rules.Single(rule => rule.ProviderName is null && rule.CategoryName!.StartsWith("Evidgate.", StringComparison.Ordinal)));

        Should.Throw<InvalidOperationException>(() => RequestLogFloor.AssertFloorIsLast(options)).Message.ShouldContain("not registered");
    }

    [Fact]
    public void AssertFloorIsLast_NoFloorRegistered_Throws()
    {
        Should.Throw<InvalidOperationException>(() => RequestLogFloor.AssertFloorIsLast(new LoggerFilterOptions())).Message.ShouldContain("not registered");
    }

    private static IHost Build(
        Dictionary<string, string?> settings,
        out CapturingLoggerProviderBase aliased,
        out CapturingLoggerProviderBase fullName,
        out CapturingLoggerProviderBase ruleless)
    {
        aliased = new AliasedProvider();
        fullName = new FullNameProvider();
        ruleless = new RulelessProvider();
        var (a, f, r) = (aliased, fullName, ruleless);
        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings))
            .ConfigureLogging((_, logging) =>
            {
                logging.ClearProviders();
                logging.AddProvider(a);
                logging.AddProvider(f);
                logging.AddProvider(r);
            })
            .ConfigureServices(services => services.PostConfigure<LoggerFilterOptions>(RequestLogFloor.Apply))
            .Build();
    }

    private static void Send(IHost host, string category)
    {
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);
        foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning })
        {
            logger.Log(level, default, "line", null, static (message, _) => message);
        }
    }

    [ProviderAlias("Alias")]
    private sealed class AliasedProvider : CapturingLoggerProviderBase;

    private sealed class FullNameProvider : CapturingLoggerProviderBase;

    private sealed class RulelessProvider : CapturingLoggerProviderBase;

    private sealed class LateProvider : CapturingLoggerProviderBase;

    private sealed class ReloadableSource : ConfigurationProvider, IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

        public new void Set(string key, string? value) => Data[key] = value;

        public void Reload() => OnReload();
    }
}

/// <summary>What the floor tests read back from a capturing provider.</summary>
internal static class CapturedLogReads
{
    public static int Count(this CapturingLoggerProviderBase provider, string category, bool below) =>
        provider.Entries.Count(entry => entry.Category == category && (entry.Level < LogLevel.Warning) == below);

    public static List<string> Texts(this CapturingLoggerProviderBase provider, string category) =>
        provider.Entries.Where(entry => entry.Category == category).Select(entry => entry.Message).ToList();
}
