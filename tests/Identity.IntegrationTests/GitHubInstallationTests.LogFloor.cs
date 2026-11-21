using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.Modules.Identity.Infrastructure.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// No Logging configuration can lower the code-level floor of the request-start log category, which keeps the
/// callback's code and state out of that line: each case configures a way to lower it and asserts the floor held while
/// the override still applied elsewhere. Failure messages carry counts only: a captured request line holds the callback query.
/// </summary>
public sealed partial class GitHubInstallationTests
{
    private const string FloorProbeCategory = "Evidgate.LogFloorProbe";
    private const string OauthCodeNeedle = "code=probe-code";

    private static readonly string CaptureProviderName = typeof(CapturingLoggerProvider).FullName!;
    private static readonly string CaptureProviderSection = "Logging:" + CaptureProviderName;

    [Fact]
    public async Task LogFloor_ProviderlessSameCategoryKeyAtTrace_DoesNotLowerTheFloor()
    {
        var run = await RunCallbackWithConfigAsync(new Dictionary<string, string?>
        {
            [$"Logging:LogLevel:{HostingCategory}"] = "Trace",
            [$"Logging:LogLevel:{FloorProbeCategory}"] = "Trace",
        });

        run.ProbeCaptured.ShouldBeTrue("the Logging section reached the sink's rules: a Trace line of the probe category is captured");
        run.HasRule(null, HostingCategory, LogLevel.Trace).ShouldBeTrue("the override is among the filter rules the host built");
        run.HostingBelowWarningCount.ShouldBe(0, "the code rule of the same category added after the configuration wins the tie");
        run.CarriesSecret.ShouldBeFalse("no captured line carries the callback code or state");
    }

    [Fact]
    public async Task LogFloor_ProviderlessWildcardBelowTheHostingCategoryAtTrace_FindsNoLongerCategory()
    {
        var run = await RunCallbackWithConfigAsync(new Dictionary<string, string?>
        {
            [$"Logging:LogLevel:{HostingCategory}.*"] = "Trace",
            [$"Logging:LogLevel:{FloorProbeCategory}"] = "Trace",
        });

        run.ProbeCaptured.ShouldBeTrue("the configuration was applied");
        run.HasRule(null, HostingCategory + ".*", LogLevel.Trace).ShouldBeTrue("the override is among the filter rules the host built");
        run.Entries.Any(entry => entry.Category.StartsWith(HostingCategory + ".", StringComparison.Ordinal))
            .ShouldBeFalse("the request flow logs under no category longer than the floor's, so a longer provider-less rule has nothing to match");
        run.HostingBelowWarningCount.ShouldBe(0, "the floor still holds");
        run.CarriesSecret.ShouldBeFalse("no captured line carries the callback code or state");
    }

    [Fact]
    public async Task LogFloor_ProviderlessParentCategoryAtTrace_DoesNotLowerTheFloor()
    {
        var run = await RunCallbackWithConfigAsync(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
            [$"Logging:LogLevel:{FloorProbeCategory}"] = "Trace",
        });

        run.ProbeCaptured.ShouldBeTrue("the configuration was applied");
        run.HasRule(null, "Microsoft.AspNetCore", LogLevel.Trace).ShouldBeTrue("the override is among the filter rules the host built");
        run.HostingBelowWarningCount.ShouldBe(0, "the longer category of the code rule beats the shorter configured one");
        run.CarriesSecretIn(entry => entry.Category == HostingCategory).ShouldBeFalse("no Hosting.Diagnostics line carries the callback code or state");
    }

    [Fact]
    public async Task LogFloor_ProviderSpecificDefaultAtTrace_DoesNotLowerTheFloor()
    {
        var run = await RunCallbackWithConfigAsync(new Dictionary<string, string?>
        {
            [$"{CaptureProviderSection}:LogLevel:Default"] = "Trace",
        });

        run.ProbeCaptured.ShouldBeTrue("the provider Default applied to another category: its Trace line is captured");
        run.HasRule(CaptureProviderName, null, LogLevel.Trace).ShouldBeTrue("the override is among the filter rules the host built");
        run.HostingBelowWarningCount.ShouldBe(0, "the per-provider floor rule outranks the provider Default");
        run.CarriesSecret.ShouldBeFalse("no captured line carries the callback code or state");
    }

    [Fact]
    public async Task LogFloor_ProviderSpecificHostingCategoryAtTrace_DoesNotLowerTheFloor()
    {
        var run = await RunCallbackWithConfigAsync(new Dictionary<string, string?>
        {
            [$"{CaptureProviderSection}:LogLevel:{HostingCategory}"] = "Trace",
            [$"{CaptureProviderSection}:LogLevel:{FloorProbeCategory}"] = "Trace",
        });

        run.ProbeCaptured.ShouldBeTrue("the provider section applied to another category");
        run.HasRule(CaptureProviderName, HostingCategory, LogLevel.Trace).ShouldBeTrue("the override is among the filter rules the host built");
        run.HostingBelowWarningCount.ShouldBe(0, "the per-provider floor rule outranks the provider category rule");
        run.CarriesSecret.ShouldBeFalse("no captured line carries the callback code or state");
    }

    [Fact]
    public async Task LogFloor_ProviderlessTrailingWildcardOnTheHostingCategoryAtTrace_DoesNotLowerTheFloor()
    {
        var run = await RunCallbackWithConfigAsync(new Dictionary<string, string?>
        {
            [$"Logging:LogLevel:{HostingCategory}*"] = "Trace",
            [$"Logging:LogLevel:{FloorProbeCategory}"] = "Trace",
        });

        run.ProbeCaptured.ShouldBeTrue("the configuration was applied");
        run.HasRule(null, HostingCategory + "*", LogLevel.Trace).ShouldBeTrue("the override is among the filter rules the host built");
        run.HostingBelowWarningCount.ShouldBe(0, "the floor pattern is longer than any pattern that can match the category");
        run.CarriesSecret.ShouldBeFalse("no captured line carries the callback code or state");
    }

    [Fact]
    public async Task LogFloor_ProviderParentCategoryAtTrace_DoesNotLowerTheFloor()
    {
        var run = await RunCallbackWithConfigAsync(new Dictionary<string, string?>
        {
            [$"{CaptureProviderSection}:LogLevel:Microsoft.AspNetCore"] = "Trace",
            [$"{CaptureProviderSection}:LogLevel:{FloorProbeCategory}"] = "Trace",
        });

        run.ProbeCaptured.ShouldBeTrue("the provider section applied to another category");
        run.HasRule(CaptureProviderName, "Microsoft.AspNetCore", LogLevel.Trace).ShouldBeTrue("the override is among the filter rules the host built");
        run.HostingBelowWarningCount.ShouldBe(0, "a provider parent rule does not reach the floored category");
        run.CarriesSecret.ShouldBeFalse("no captured line carries the callback code or state");
    }

    [Fact]
    public async Task LogFloor_ReloadAfterStartCannotLowerTheFloor()
    {
        var run = await RunCallbackWithConfigAsync(
            new Dictionary<string, string?>(),
            reloaded: new Dictionary<string, string?>
            {
                [$"{CaptureProviderSection}:LogLevel:Default"] = "Trace",
                [$"Logging:LogLevel:{HostingCategory}*"] = "Trace",
            });

        run.ProbeCaptured.ShouldBeTrue("the reloaded provider Default applied to another category");
        run.HasRule(CaptureProviderName, null, LogLevel.Trace).ShouldBeTrue("the reload reached the filter rules");
        run.HostingBelowWarningCount.ShouldBe(0, "the floor is applied again after every reload");
        run.CarriesSecret.ShouldBeFalse("no captured line carries the callback code or state");
    }

    [Fact]
    public async Task LogFloor_ConsoleProviderByAliasAtTrace_DoesNotLowerTheFloor()
    {
        var console = new ConsoleCapture();
        var run = await RunCallbackWithConfigAsync(
            new Dictionary<string, string?>
            {
                ["Logging:Console:FormatterName"] = ConsoleCapture.FormatterName,
                ["Logging:Console:LogLevel:Default"] = "Trace",
            },
            console: console);

        run.ProbeCaptured.ShouldBeTrue("the Console section applied by its alias: its Trace probe line reached the console formatter");
        run.HasRule("Console", null, LogLevel.Trace).ShouldBeTrue("the override is among the filter rules the host built");
        run.HostingBelowWarningCount.ShouldBe(0, "the per-provider floor rule outranks the Console Default");
        run.CarriesSecret.ShouldBeFalse("no console line carries the callback code or state");
    }

    private async Task<FloorRun> RunCallbackWithConfigAsync(
        Dictionary<string, string?> settings,
        Dictionary<string, string?>? reloaded = null,
        ConsoleCapture? console = null)
    {
        var source = new ReloadableConfiguration(settings);
        var sink = new CapturingLoggerProvider();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = true };
        oauth.Installations.Add(new GitHubUserInstallation(InstallationId, "acme", ProbeUserId, "User"));

        await using var baseHost = Host(org, admin, oauth, UsableApp(), configure: services => services.AddLogging(logging =>
        {
            logging.AddProvider(sink);
            if (console is not null)
            {
                logging.Services.AddSingleton(console);
                logging.AddConsoleFormatter<ConsoleCapture.Formatter, ConsoleFormatterOptions>();
            }
        }));
        await using var host = baseHost.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.Add(source)));
        using var client = host.CreateClient();

        using var start = await SendAsync(client, HttpMethod.Post, "/v1/github/installations/start", org);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
        var state = StateOf((await ReadJsonAsync(start)).RootElement.GetProperty("url").GetString()!);

        if (reloaded is not null)
        {
            source.Replace(reloaded);
        }

        using var callback = await CallbackAsync(client, state);
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);

        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(FloorProbeCategory)
            .Log(LogLevel.Trace, default, "floor-probe", null, static (message, _) => message);

        if (console is not null)
        {
            await console.WaitForAsync(FloorProbeCategory, Token);
        }

        var rules = host.Services.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>().CurrentValue.Rules
            .Select(rule => (rule.ProviderName, rule.CategoryName, rule.LogLevel))
            .ToList();

        return new FloorRun(console?.Entries ?? sink.Entries, state, rules);
    }

    private sealed class ReloadableConfiguration(Dictionary<string, string?> initial) : ConfigurationProvider, IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            Data = new Dictionary<string, string?>(initial, StringComparer.OrdinalIgnoreCase);
            return this;
        }

        public void Replace(Dictionary<string, string?> values)
        {
            Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
            OnReload();
        }
    }

    private sealed class ConsoleCapture
    {
        public const string FormatterName = "floor-capture";

        private readonly System.Collections.Concurrent.ConcurrentQueue<CapturedLog> _entries = new();

        public IReadOnlyList<CapturedLog> Entries => _entries.ToArray();

        /// <summary>The console processor formats on its own thread; its queue is FIFO, so the probe line arriving means every earlier line did.</summary>
        public async Task WaitForAsync(string category, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!_entries.Any(entry => entry.Category == category) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25, cancellationToken);
            }
        }

        public sealed class Formatter(ConsoleCapture capture) : ConsoleFormatter(FormatterName)
        {
            public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter) =>
                capture._entries.Enqueue(new CapturedLog(
                    logEntry.Category,
                    logEntry.LogLevel,
                    logEntry.EventId,
                    logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception) ?? string.Empty,
                    [],
                    [],
                    logEntry.Exception?.ToString()));
        }
    }

    private sealed record FloorRun(IReadOnlyList<CapturedLog> Entries, string State, List<(string? Provider, string? Category, LogLevel? Level)> Rules)
    {
        public bool ProbeCaptured => Entries.Any(entry => entry.Category == FloorProbeCategory && entry.Level == LogLevel.Trace);

        public int HostingBelowWarningCount =>
            Entries.Count(entry => entry.Category == HostingCategory && entry.Level < LogLevel.Warning);

        public bool HasRule(string? provider, string? category, LogLevel level) =>
            Rules.Contains((provider, category, (LogLevel?)level));

        public bool CarriesSecret => CarriesSecretIn(_ => true);

        public bool CarriesSecretIn(Func<CapturedLog, bool> scope) =>
            Entries.Where(scope).Any(entry => entry.Texts.Any(text =>
                text.Contains(OauthCodeNeedle, StringComparison.Ordinal) || text.Contains(State, StringComparison.Ordinal)));
    }
}
