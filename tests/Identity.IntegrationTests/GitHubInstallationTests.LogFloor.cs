using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.Modules.Identity.Infrastructure.GitHub;
using Evidgate.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// Which Logging configuration can lower the code-level floor of the request-start log category, which keeps the
/// callback's code and state out of that line. Each case states what was observed, so a known gap turns red the day
/// production closes it. Failure messages carry counts only: a captured request line holds the callback query.
/// </summary>
public sealed partial class GitHubInstallationTests
{
    private const string FloorProbeCategory = "Evidgate.LogFloorProbe";
    private const string OauthCodeNeedle = "code=probe-code";

    private static readonly string CaptureProviderSection = "Logging:" + typeof(CapturingLoggerProvider).FullName;

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
    public async Task LogFloor_ProviderSpecificDefaultAtTrace_ExposesCallbackQuery_KnownGap()
    {
        var run = await RunCallbackWithConfigAsync(new Dictionary<string, string?>
        {
            [$"{CaptureProviderSection}:LogLevel:Default"] = "Trace",
        });

        run.ProbeCaptured.ShouldBeTrue("the provider section bound to the capture provider by its full type name");
        run.HostingBelowWarningCount.ShouldBeGreaterThan(0, "a provider-specific rule beats the provider-less code rule of the floor");
        run.CarriesSecretIn(entry => entry.Category == HostingCategory)
            .ShouldBeTrue("known gap: the request-start line, with the callback query, reaches a provider whose own section lowers the level");
    }

    [Fact]
    public async Task LogFloor_ProviderSpecificHostingCategoryAtTrace_ExposesCallbackQuery_KnownGap()
    {
        var run = await RunCallbackWithConfigAsync(new Dictionary<string, string?>
        {
            [$"{CaptureProviderSection}:LogLevel:{HostingCategory}"] = "Trace",
            [$"{CaptureProviderSection}:LogLevel:{FloorProbeCategory}"] = "Trace",
        });

        run.ProbeCaptured.ShouldBeTrue("the provider section bound to the capture provider by its full type name");
        run.HostingBelowWarningCount.ShouldBeGreaterThan(0, "the provider-specific category rule beats the code rule");
        run.CarriesSecretIn(entry => entry.Category == HostingCategory)
            .ShouldBeTrue("known gap: the request-start line, with the callback query, reaches the provider");
    }

    [Fact]
    public async Task LogFloor_ProviderlessTrailingWildcardOnTheHostingCategoryAtTrace_ExposesCallbackQuery_KnownGap()
    {
        var run = await RunCallbackWithConfigAsync(new Dictionary<string, string?>
        {
            [$"Logging:LogLevel:{HostingCategory}*"] = "Trace",
            [$"Logging:LogLevel:{FloorProbeCategory}"] = "Trace",
        });

        run.ProbeCaptured.ShouldBeTrue("the configuration was applied");
        run.HasRule(null, HostingCategory + "*", LogLevel.Trace).ShouldBeTrue("the override is among the filter rules the host built");
        run.HostingBelowWarningCount.ShouldBeGreaterThan(0, "a wildcard pattern is longer than the exact category of the code rule, so it wins");
        run.CarriesSecretIn(entry => entry.Category == HostingCategory)
            .ShouldBeTrue("known gap: a provider-less key with a trailing wildcard lowers the floor for every provider");
    }

    private async Task<FloorRun> RunCallbackWithConfigAsync(Dictionary<string, string?> settings)
    {
        var sink = new CapturingLoggerProvider();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = true };
        oauth.Installations.Add(new GitHubUserInstallation(InstallationId, "acme", ProbeUserId, "User"));

        await using var baseHost = Host(org, admin, oauth, UsableApp(), configure: services => services.AddLogging(logging => logging.AddProvider(sink)));
        await using var host = baseHost.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings)));
        using var client = host.CreateClient();

        using var start = await SendAsync(client, HttpMethod.Post, "/v1/github/installations/start", org);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
        var state = StateOf((await ReadJsonAsync(start)).RootElement.GetProperty("url").GetString()!);

        using var callback = await CallbackAsync(client, state);
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);

        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(FloorProbeCategory)
            .Log(LogLevel.Trace, default, "floor-probe", null, static (message, _) => message);

        var rules = host.Services.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>().CurrentValue.Rules
            .Select(rule => (rule.ProviderName, rule.CategoryName, rule.LogLevel))
            .ToList();

        return new FloorRun(sink.Entries, state, rules);
    }

    private sealed record FloorRun(IReadOnlyList<CapturedLog> Entries, string State, List<(string? Provider, string? Category, LogLevel? Level)> Rules)
    {
        public bool ProbeCaptured => Entries.Any(entry => entry.Category == FloorProbeCategory && entry.Level == LogLevel.Trace);

        public int HostingBelowWarningCount =>
            Entries.Count(entry => entry.Category == HostingCategory && entry.Level < LogLevel.Warning);

        public bool HasRule(string? provider, string category, LogLevel level) =>
            Rules.Contains((provider, category, (LogLevel?)level));

        public bool CarriesSecret => CarriesSecretIn(_ => true);

        public bool CarriesSecretIn(Func<CapturedLog, bool> scope) =>
            Entries.Where(scope).Any(entry => entry.Texts.Any(text =>
                text.Contains(OauthCodeNeedle, StringComparison.Ordinal) || text.Contains(State, StringComparison.Ordinal)));
    }
}
