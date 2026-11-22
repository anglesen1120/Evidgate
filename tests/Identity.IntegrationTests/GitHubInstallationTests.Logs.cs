using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.Modules.Identity.Infrastructure.GitHub;
using Evidgate.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The log half of "no installation token and no private key in any log line": one real host whose sink keeps
/// every call with the deployed framework log filters and Evidgate's own categories at Trace, driven through start, callback, a token mint (miss, hit, failure) and disconnect.
/// </summary>
public sealed partial class GitHubInstallationTests
{
    private const int CatalogRepository = 42;
    private const int ChecksRepository = 43;
    private const string HostingCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    private static readonly Regex TokenShape = new("gh[a-z]_[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex JwtShape = new(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    [Fact]
    public async Task LogScan_EvidgateCategories_NeverCarryATokenAKeyOrAFlowSecret()
    {
        var run = await RunLoggedLifecycleAsync();

        AssertNoSecret(run, entry => !IsFramework(entry));
    }

    [Fact]
    public async Task LogScan_FrameworkCategories_NeverCarryATokenAKeyOrAFlowSecret()
    {
        var run = await RunLoggedLifecycleAsync();

        AssertNoSecret(run, IsFramework);
    }

    [Fact]
    public async Task LogScan_EachFlowIsCaptured_AtTrace_SoAnAbsenceIsNotAnEmptySink()
    {
        var run = await RunLoggedLifecycleAsync();

        run.ProbeLevels.ShouldBe([LogLevel.Trace, LogLevel.Debug], "a Debug or Trace line of Evidgate code reaches the sink");
        run.Entries.Any(entry => entry.Category == HostingCategory && entry.Level < LogLevel.Warning)
            .ShouldBeFalse("the host keeps the request-start line, which carries the callback query, below the sink");
        run.Missed.Any(entry => entry.EventId.Id == 4402).ShouldBeTrue("a mint miss logs the minted event");
        run.Hit.Any(entry => entry.EventId.Id == 4402).ShouldBeFalse("a cache hit mints nothing and logs no mint");
        run.Failed.Any(entry => entry.EventId.Id == 4401).ShouldBeTrue("a failed exchange logs the failure event");
        run.Endpoint.Calls.ShouldBe(2, "one exchange for the miss, none for the hit, one for the failure");
        run.Jwts.ShouldNotBeEmpty("the App JWT the exchange presented is a needle");
        run.Endpoint.MintedServed.ShouldBe(1, "the minted token was served once, so its needle names a value that existed");
        run.Endpoint.RejectedServed.ShouldBe(1, "the token-shaped value in the failure body was served once");
    }

    private static bool IsFramework(CapturedLog entry) =>
        entry.Category.StartsWith("Microsoft.", StringComparison.Ordinal)
        || entry.Category.StartsWith("System.", StringComparison.Ordinal);

    private async Task<LoggedRun> RunLoggedLifecycleAsync()
    {
        var sink = new CapturingLoggerProvider();
        var pem = RSA.Create(2048).ExportPkcs8PrivateKeyPem();
        var minted = "ghs_" + Guid.NewGuid().ToString("N");
        var rejected = "ghs_" + Guid.NewGuid().ToString("N");
        var endpoint = new GitHubTokenEndpointFake(minted, rejected);

        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = true };
        oauth.Installations.Add(new GitHubUserInstallation(InstallationId, "acme", ProbeUserId, "User"));

        await using var host = Host(org, admin, oauth, UsableApp(), configure: services => ConfigureLogScan(services, sink, endpoint, pem));
        using var client = host.CreateClient();

        using var start = await SendAsync(client, HttpMethod.Post, "/v1/github/installations/start", org);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
        var state = StateOf((await ReadJsonAsync(start)).RootElement.GetProperty("url").GetString()!);

        using var callback = await CallbackAsync(client, state);
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);

        var tokens = host.Services.GetRequiredService<IGitHubInstallationTokens>();
        var before = sink.Entries.Count;
        var miss = await tokens.GetAsync(org, GitHubFeature.Catalog, [CatalogRepository], Token);
        miss.Value.Token.ShouldBe(minted);
        var afterMiss = sink.Entries.Count;
        var hit = await tokens.GetAsync(org, GitHubFeature.Catalog, [CatalogRepository], Token);
        hit.Value.Token.ShouldBe(minted);
        var afterHit = sink.Entries.Count;
        endpoint.Fail = true;
        var failure = await tokens.GetAsync(org, GitHubFeature.Checks, [ChecksRepository], Token);
        failure.IsSuccess.ShouldBeFalse();
        var afterFailure = sink.Entries.Count;

        var rowId = await ScalarAsync<Guid>("select id from public.github_installation where org_id = @org", ("org", org));
        using var disconnect = await SendAsync(client, HttpMethod.Delete, $"/v1/github/installations/{rowId}", org, "log-scan-disconnect");
        disconnect.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var all = sink.Entries;
        var probe = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Evidgate.LogScanProbe");
        probe.Log(LogLevel.Trace, default, "probe-trace", null, static (state, _) => state);
        probe.Log(LogLevel.Debug, default, "probe-debug", null, static (state, _) => state);
        var probeLevels = sink.Entries.Where(entry => entry.Category == "Evidgate.LogScanProbe").Select(entry => entry.Level).ToList();

        return new LoggedRun(
            all,
            all.Skip(before).Take(afterMiss - before).ToList(),
            all.Skip(afterMiss).Take(afterHit - afterMiss).ToList(),
            all.Skip(afterHit).Take(afterFailure - afterHit).ToList(),
            endpoint,
            endpoint.Jwts,
            rowId,
            probeLevels,
            Needles(minted, rejected, pem, endpoint.Jwts, oauth, state));
    }

    private static void ConfigureLogScan(
        IServiceCollection services,
        CapturingLoggerProvider sink,
        GitHubTokenEndpointFake endpoint,
        string pem)
    {
        // Only Evidgate's own categories are raised to Trace. Every framework rule stays the deployed one, notably
        // the Hosting.Diagnostics floor of ApiHost, so the scan sees the lines production would write.
        services.AddLogging(logging =>
        {
            logging.AddProvider(sink);
            logging.AddFilter("Evidgate", LogLevel.Trace);
        });

        // The shared Host() options carry no key; the key is generated by this run and never leaves the process.
        services.AddSingleton(Options.Create(new GitHubOptions
        {
            AppId = "1",
            ClientId = "Iv1.probe",
            AppSlug = "evidgate-app",
            PrivateKey = pem,
        }));
        services.AddSingleton<IGitHubInstallationResolver>(new FixedInstallationResolver(1));
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new GitHubTokenHandlerFilter(endpoint));
    }

    private static List<(string Label, string Value, bool IgnoreWhitespace)> Needles(
        string minted,
        string rejected,
        string pem,
        IReadOnlyList<string> jwts,
        FakeGitHubOAuth oauth,
        string state)
    {
        var body = string.Concat(pem.Split('\n').Where(line => !line.StartsWith("-----", StringComparison.Ordinal))).Trim();
        var nonce = NonceOf(state);
        var needles = new List<(string, string, bool)>
        {
            ("minted token", minted, false),
            ("rejected-response token", rejected, false),
            ("user token", FakeGitHubOAuth.UserToken, false),
            ("oauth code", "probe-code", false),
            ("state nonce", nonce, false),
            ("state", state, false),
            ("pem", pem, false),
            ("pem body", body, true),
            ("pem header", "-----BEGIN", false),
        };
        needles.AddRange(pem.Split('\n').Where(line => line.Length >= 32).Select(line => ("pem line", line.Trim(), true)));
        needles.AddRange(jwts.Select(jwt => ("app jwt", jwt, false)));

        return needles
            .SelectMany(needle => EncodedForms(needle.Item2).Select(form => (needle.Item1, form, needle.Item3)))
            .ToList();
    }

    private static IEnumerable<string> EncodedForms(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var base64 = Convert.ToBase64String(bytes);

        return new[]
        {
            value,
            base64,
            base64.TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            Uri.EscapeDataString(value),
            Uri.EscapeDataString(base64),
        }.Distinct(StringComparer.Ordinal);
    }

    private static void AssertNoSecret(LoggedRun run, Func<CapturedLog, bool> scope)
    {
        var entries = run.Entries.Where(scope).ToList();
        entries.ShouldNotBeEmpty("the scan must have lines to scan");

        foreach (var entry in entries)
        {
            foreach (var text in entry.Texts)
            {
                var squashed = Regex.Replace(text, @"\s+", string.Empty, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
                foreach (var (label, value, ignoreWhitespace) in run.Needles)
                {
                    var haystack = ignoreWhitespace ? squashed : text;
                    haystack.Contains(value, StringComparison.Ordinal)
                        .ShouldBeFalse($"{label} reached a log: category {entry.Category}, event {entry.EventId.Id}, level {entry.Level}");
                }

                TokenShape.IsMatch(text).ShouldBeFalse($"a token-shaped value reached a log: category {entry.Category}, event {entry.EventId.Id}");
                JwtShape.IsMatch(text).ShouldBeFalse($"a JWT-shaped value reached a log: category {entry.Category}, event {entry.EventId.Id}");
            }
        }
    }

    private sealed record LoggedRun(
        IReadOnlyList<CapturedLog> Entries,
        IReadOnlyList<CapturedLog> Missed,
        IReadOnlyList<CapturedLog> Hit,
        IReadOnlyList<CapturedLog> Failed,
        GitHubTokenEndpointFake Endpoint,
        IReadOnlyList<string> Jwts,
        Guid RowId,
        IReadOnlyList<LogLevel> ProbeLevels,
        List<(string Label, string Value, bool IgnoreWhitespace)> Needles);
}
