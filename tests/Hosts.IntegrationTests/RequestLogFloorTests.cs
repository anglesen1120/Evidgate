using System.Collections.Concurrent;
using Evidgate.Api;
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
            sink.Count(Category, below: true).ShouldBe(0, $"{shape}: lines below Warning reaching {sink.Name}");
            sink.Count(Category, below: false).ShouldBe(1, $"{shape}: the Warning line reaching {sink.Name}");
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

        aliased.Count(OtherCategory, below: true).ShouldBe(1, "the provider Default still applies to another category");
        fullName.Count("Microsoft.AspNetCore.Routing", below: true).ShouldBe(1, "the provider parent rule still applies to a sibling category");
        aliased.Count(Category, below: true).ShouldBe(0);
        fullName.Count(Category, below: true).ShouldBe(0);
    }

    [Fact]
    public void ProviderWithoutRulesAndProviderAddedLater_FallUnderTheProviderlessFloor()
    {
        using var host = Build(new() { ["Logging:LogLevel:Default"] = "Trace" }, out _, out _, out var ruleless);
        var late = new Sink("late");
        host.Services.GetRequiredService<ILoggerFactory>().AddProvider(new LateProvider(late));

        Send(host, Category);
        Send(host, OtherCategory);

        ruleless.Count(Category, below: true).ShouldBe(0);
        late.Count(Category, below: true).ShouldBe(0);
        late.Count(OtherCategory, below: true).ShouldBe(1, "the floor touches the one category only");
    }

    [Fact]
    public void ReloadAfterStart_CannotLowerTheFloor()
    {
        var source = new ReloadableSource();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(config => config.Add(source))
            .ConfigureLogging((context, logging) =>
            {
                logging.ClearProviders();
                logging.AddConfiguration(context.Configuration.GetSection("Logging"));
            })
            .ConfigureServices(services => services.PostConfigure<LoggerFilterOptions>(RequestLogFloor.Apply))
            .Build();
        var sink = new Sink("reload");
        host.Services.GetRequiredService<ILoggerFactory>().AddProvider(new AliasedProvider(sink));

        source.Set("Logging:Alias:LogLevel:Default", "Trace");
        source.Set("Logging:LogLevel:" + Category + "*", "Trace");
        source.Reload();
        Send(host, Category);
        Send(host, OtherCategory);

        sink.Count(Category, below: true).ShouldBe(0, "after the reload the floor holds");
        sink.Count(OtherCategory, below: true).ShouldBe(1, "the reloaded key did apply to another category");
    }

    [Fact]
    public async Task OverridingKeys_AreReportedAtStartByShape_WithoutTheirValue()
    {
        var log = new Sink("notice");
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging((_, logging) =>
            {
                logging.ClearProviders();
                logging.AddProvider(new AliasedProvider(log));
            })
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:Alias:LogLevel:Default"] = "Trace-secret-value",
                ["Logging:LogLevel:" + Category + "*"] = "Debug",
                ["Logging:LogLevel:Default"] = "Information",
            }))
            .ConfigureServices(services => services
                .PostConfigure<LoggerFilterOptions>(RequestLogFloor.Apply)
                .AddHostedService<RequestLogFloorNotice>())
            .Build();

        await host.StartAsync(TestContext.Current.CancellationToken);

        var notice = log.Texts("Evidgate.Api.RequestLogFloorNotice").ShouldHaveSingleItem();
        notice.ShouldContain($"Logging:LogLevel:{Category}*");
        notice.ShouldContain("Logging:Alias:LogLevel:Default");
        notice.ShouldNotContain("Logging:LogLevel:Default,", Case.Sensitive, "the generic provider-less Default is not an override");
        notice.ShouldNotContain("Trace-secret-value");
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

    private static IHost Build(Dictionary<string, string?> settings, out Sink aliased, out Sink fullName, out Sink ruleless)
    {
        aliased = new Sink("aliased");
        fullName = new Sink("fullName");
        ruleless = new Sink("ruleless");
        var (a, f, r) = (aliased, fullName, ruleless);
        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings))
            .ConfigureLogging((context, logging) =>
            {
                logging.ClearProviders();
                logging.AddConfiguration(context.Configuration.GetSection("Logging"));
                logging.AddProvider(new AliasedProvider(a));
                logging.AddProvider(new FullNameProvider(f));
                logging.AddProvider(new RulelessProvider(r));
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

    private sealed class Sink(string name)
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Text)> _lines = new();

        public string Name { get; } = name;

        public void Add(string category, LogLevel level, string text) => _lines.Enqueue((category, level, text));

        public int Count(string category, bool below) =>
            _lines.Count(line => line.Category == category && (line.Level < LogLevel.Warning) == below);

        public List<string> Texts(string category) => _lines.Where(line => line.Category == category).Select(line => line.Text).ToList();
    }

    private abstract class SinkProvider(Sink sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new SinkLogger(categoryName, sink);

        public void Dispose()
        {
            // The sink is the only state.
        }
    }

    [ProviderAlias("Alias")]
    private sealed class AliasedProvider(Sink sink) : SinkProvider(sink);

    private sealed class FullNameProvider(Sink sink) : SinkProvider(sink);

    private sealed class RulelessProvider(Sink sink) : SinkProvider(sink);

    private sealed class LateProvider(Sink sink) : SinkProvider(sink);

    private sealed class SinkLogger(string category, Sink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Add(category, logLevel, formatter(state, exception));
    }

    private sealed class ReloadableSource : ConfigurationProvider, IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

        public new void Set(string key, string? value) => Data[key] = value;

        public void Reload() => OnReload();
    }
}
