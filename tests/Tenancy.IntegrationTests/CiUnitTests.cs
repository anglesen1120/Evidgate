using System.Net;
using System.Text;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>Pure tests of the CI authentication building blocks (02.4.1, 02.4.2): no database, no host.</summary>
[Collection(ExclusiveTenancySuite.Name)]
public sealed class CiUnitTests
{
    // ------------------------------------------------------------ RefGlob

    [Theory]
    [InlineData("refs/heads/main", "refs/heads/main", true)]
    [InlineData("refs/heads/main", "refs/heads/main2", false)]
    [InlineData("refs/heads/main", "refs/heads/mai", false)]
    [InlineData("refs/tags/v*", "refs/tags/v1.2.3", true)]
    [InlineData("refs/tags/v*", "refs/tags/", false)]
    [InlineData("refs/tags/v*", "refs/tags/v", true)]
    [InlineData("refs/heads/*", "refs/heads/feature/x", true)]
    [InlineData("*", "anything", true)]
    [InlineData("a*b*c", "aXbYc", true)]
    [InlineData("a*b*c", "aXbY", false)]
    [InlineData("acme/web/.github/workflows/ci.yml@refs/heads/main", "acme/web/.github/workflows/ci.yml@refs/heads/main", true)]
    [InlineData("acme/web/.github/workflows/ci.yml@refs/heads/main", "evil/web/.github/workflows/ci.yml@refs/heads/main", false)]
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    public void RefGlob_IsAnchoredAndLiteralExceptForStar(string pattern, string value, bool expected) =>
        RefGlob.IsMatch(pattern, value).ShouldBe(expected);

    [Fact]
    public void RefGlob_HostilePatternsAreLinearTime()
    {
        var pattern = string.Concat(Enumerable.Repeat("a*", 2000)) + "b";
        var value = new string('a', 4000);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        RefGlob.IsMatch(pattern, value).ShouldBeFalse();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void RefGlob_IsAnyMatch_ChecksEveryEntry()
    {
        RefGlob.IsAnyMatch(["refs/heads/main", "refs/tags/v*"], "refs/tags/v9").ShouldBeTrue();
        RefGlob.IsAnyMatch(["refs/heads/main"], "refs/tags/v9").ShouldBeFalse();
        RefGlob.IsAnyMatch([], "x").ShouldBeFalse();
    }

    [Theory]
    [InlineData("refs/heads/main", true)]
    [InlineData("refs/tags/v*", true)]
    [InlineData("main", false)]
    [InlineData("refs/pull/1/merge", false)]
    [InlineData("refs/heads/has space", false)]
    public void RefPatternSyntax(string value, bool valid) => CiBindingShapes.IsRefPattern(value).ShouldBe(valid);

    [Theory]
    [InlineData("acme/web/.github/workflows/ci.yml@refs/heads/main", true)]
    [InlineData("acme/*/.github/workflows/*.yml@refs/heads/*", true)]
    [InlineData("ci.yml", false)]
    [InlineData("acme/web/workflows/ci.yml@refs/heads/main", false)]
    public void WorkflowPatternSyntax(string value, bool valid) => CiBindingShapes.IsWorkflowPattern(value).ShouldBe(valid);

    [Theory]
    [InlineData("acme/web", true)]
    [InlineData("acme", false)]
    [InlineData("a/b/c", false)]
    [InlineData("a b/c", false)]
    public void RepositoryNameSyntax(string value, bool valid) => CiBindingShapes.IsRepositoryFullName(value).ShouldBe(valid);

    // ------------------------------------------------------------ ingest token format

    [Fact]
    public void IngestToken_Generate_HasTheDocumentedShape_AndRoundTrips()
    {
        var generated = IngestTokenFormat.Generate();

        generated.Prefix.Length.ShouldBe(12);
        generated.Prefix.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c));
        generated.Secret.Length.ShouldBe(43);
        generated.Full.ShouldBe($"egt_{generated.Prefix}_{generated.Secret}");

        IngestTokenFormat.TryParse(generated.Full, out var prefix, out var secret).ShouldBeTrue();
        prefix.ShouldBe(generated.Prefix);
        secret.ShouldBe(generated.Secret);
    }

    [Fact]
    public void IngestToken_Generate_IsRandom()
    {
        var all = Enumerable.Range(0, 200).Select(_ => IngestTokenFormat.Generate()).ToList();

        all.Select(token => token.Secret).Distinct().Count().ShouldBe(200);
        all.Select(token => token.Prefix).Distinct().Count().ShouldBe(200);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("egt_short_x")]
    [InlineData("egt_abcdefghijkl_tooshort")]
    [InlineData("xyz_abcdefghijkl_0123456789012345678901234567890123456789012")]
    [InlineData("egt_abcdefghijk!_0123456789012345678901234567890123456789012")]
    [InlineData("egt_abcdefghijkl_01234567890123456789012345678901234567890123456789")]
    public void IngestToken_TryParse_RejectsMalformed(string? token) =>
        IngestTokenFormat.TryParse(token, out _, out _).ShouldBeFalse();

    [Fact]
    public void Challenge_IsLongAndUnique()
    {
        var a = IngestTokenFormat.NewChallenge();

        a.Length.ShouldBeGreaterThanOrEqualTo(43);
        a.ShouldNotBe(IngestTokenFormat.NewChallenge());
    }

    // ------------------------------------------------------------ secrets and options

    private static CiAuthOptions Options(string? signing = null, string? pepper = null, int version = 1, string? previous = null, int previousVersion = 2) =>
        new() { SigningKey = signing, Pepper = pepper, PepperVersion = version, PreviousPepper = previous, PreviousPepperVersion = previousVersion };

    private static string Key(byte seed) => Convert.ToBase64String(Enumerable.Repeat(seed, 32).ToArray());

    [Fact]
    public void Secrets_HashUnderThePepperOfTheStoredVersion()
    {
        var secrets = new CiSecrets(Microsoft.Extensions.Options.Options.Create(Options(pepper: Key(1), version: 3, previous: Key(2), previousVersion: 2)));
        var data = Encoding.UTF8.GetBytes("secret");

        var (version, hash) = secrets.HashCurrent(data);
        version.ShouldBe(3);
        secrets.HashWithVersion(3, data).ShouldBe(hash);
        secrets.HashWithVersion(2, data).ShouldNotBe(hash);
        secrets.HashWithVersion(1, data).ShouldBeNull();
        secrets.HashUnderEveryPepper(data).Count.ShouldBe(2);
        hash.Length.ShouldBe(32);
    }

    [Fact]
    public void Secrets_WithoutPepper_CannotHash()
    {
        var secrets = new CiSecrets(Microsoft.Extensions.Options.Options.Create(Options()));

        secrets.CanHash.ShouldBeFalse();
        secrets.SigningKey.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => secrets.HashCurrent(new byte[1]));
        secrets.HashUnderEveryPepper(new byte[1]).ShouldBeEmpty();
    }

    [Fact]
    public void OptionsValidator_RejectsShortKeysBadBase64SamePepperVersionsAndForeignJwksHosts()
    {
        var validator = new CiAuthOptionsValidator();

        validator.Validate(null, Options(signing: Key(1), pepper: Key(2))).Succeeded.ShouldBeTrue();
        validator.Validate(null, Options()).Succeeded.ShouldBeTrue();
        validator.Validate(null, Options(signing: Convert.ToBase64String(new byte[8]))).Failed.ShouldBeTrue();
        validator.Validate(null, Options(pepper: "%%%not-base64%%%")).Failed.ShouldBeTrue();
        validator.Validate(null, Options(pepper: Key(1), version: 2, previous: Key(2), previousVersion: 2)).Failed.ShouldBeTrue();

        var foreign = new CiAuthOptions { GithubJwksUri = "https://evil.example/.well-known/jwks" };
        validator.Validate(null, foreign).Failed.ShouldBeTrue();
        var plain = new CiAuthOptions { GithubJwksUri = "http://token.actions.githubusercontent.com/.well-known/jwks" };
        validator.Validate(null, plain).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("  ,, ", false)]
    [InlineData("push", true)]
    [InlineData("push, merge_group", true)]
    [InlineData("Push", false)]
    [InlineData("push;release", false)]
    [InlineData("1push", false)]
    [InlineData("workflow_run", false)]
    [InlineData("pull_request_target", false)]
    public void OptionsValidator_AllowedEventNames_RefusesEmptyInvalidAndForbiddenEntries(string value, bool valid)
    {
        new CiAuthOptionsValidator().Validate(null, new CiAuthOptions { AllowedEventNames = value }).Succeeded.ShouldBe(valid, value);
    }

    [Fact]
    public void OptionsValidator_LeftoverDeniedEventNames_FailsBootWithANamedMessage()
    {
        var result = new CiAuthOptionsValidator().Validate(null, new CiAuthOptions { DeniedEventNames = "schedule" });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("EVIDGATE__CI__DENIEDEVENTNAMES");
        new CiAuthOptionsValidator().Validate(null, new CiAuthOptions { DeniedEventNames = string.Empty }).Failed.ShouldBeTrue();
    }

    // ------------------------------------------------------------ lifetime

    [Theory]
    [InlineData(-5, 300, 60, true)]
    [InlineData(59, 300, 60, true)]
    [InlineData(61, 300, 60, false)]
    [InlineData(-300, -59, 60, true)]
    [InlineData(-300, -61, 60, false)]
    [InlineData(-5, 300, 0, true)]
    [InlineData(1, 300, 0, false)]
    public void Lifetime_HonoursTheSkewOnBothEdges(int notBeforeOffset, int expiresOffset, int skewSeconds, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

        LifetimeCheck.IsWithin(now, now.UtcDateTime.AddSeconds(notBeforeOffset), now.UtcDateTime.AddSeconds(expiresOffset), TimeSpan.FromSeconds(skewSeconds))
            .ShouldBe(expected);
    }

    [Fact]
    public void Lifetime_RequiresAnExpiry() =>
        LifetimeCheck.IsWithin(DateTimeOffset.UtcNow, DateTime.UtcNow, null, TimeSpan.FromSeconds(60)).ShouldBeFalse();

    // ------------------------------------------------------------ rate limiter

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void RateLimiter_AllowsTheBurst_ThenRefusesWithRetryAfter_ThenResets()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        var limiter = new ExchangeRateLimiter(
            Microsoft.Extensions.Options.Options.Create(new CiAuthOptions { ExchangeBurstLimit = 3, ExchangeWindowSeconds = 60 }), clock);

        for (var i = 0; i < 3; i++)
        {
            limiter.TryAcquire("k", out _).ShouldBeTrue();
        }

        limiter.TryAcquire("k", out var retryAfter).ShouldBeFalse();
        retryAfter.ShouldBeInRange(1, 60);
        limiter.TryAcquire("other", out _).ShouldBeTrue();

        clock.Advance(TimeSpan.FromSeconds(61));
        limiter.TryAcquire("k", out _).ShouldBeTrue();
    }

    [Fact]
    public void RateLimiter_DoesNotGrowWithoutBound()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var limiter = new ExchangeRateLimiter(
            Microsoft.Extensions.Options.Options.Create(new CiAuthOptions { ExchangeBurstLimit = 5, ExchangeWindowSeconds = 1 }), clock);

        for (var i = 0; i < 1000; i++)
        {
            limiter.TryAcquire("k" + i, out _).ShouldBeTrue();
        }

        clock.Advance(TimeSpan.FromSeconds(5));
        limiter.TryAcquire("fresh", out _).ShouldBeTrue();
    }

    [Fact]
    public void RateLimiter_ClientKey_GroupsAnIpv6SubscriberBySlash64_AndUnwrapsMappedIpv4()
    {
        ExchangeRateLimiter.ClientKey(System.Net.IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd"))
            .ShouldBe(ExchangeRateLimiter.ClientKey(System.Net.IPAddress.Parse("2001:db8:1:2::1")));
        ExchangeRateLimiter.ClientKey(System.Net.IPAddress.Parse("2001:db8:1:3::1"))
            .ShouldNotBe(ExchangeRateLimiter.ClientKey(System.Net.IPAddress.Parse("2001:db8:1:2::1")));
        ExchangeRateLimiter.ClientKey(System.Net.IPAddress.Parse("::ffff:203.0.113.9")).ShouldBe("203.0.113.9");
        ExchangeRateLimiter.ClientKey(null).ShouldBe("none");
    }

    [Fact]
    public void RateLimiter_WhenFullOfLiveWindows_RefusesNewKeys_ButKeepsServingTrackedOnes()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var limiter = new ExchangeRateLimiter(
            Microsoft.Extensions.Options.Options.Create(new CiAuthOptions { ExchangeBurstLimit = 5, ExchangeWindowSeconds = 60 }), clock);

        for (var i = 0; i < 50_000; i++)
        {
            limiter.TryAcquire("k" + i, out _).ShouldBeTrue();
        }

        limiter.TryAcquire("sprayed", out var retryAfter).ShouldBeFalse();
        retryAfter.ShouldBeGreaterThan(0);
        limiter.TryAcquire("k0", out _).ShouldBeTrue();

        clock.Advance(TimeSpan.FromSeconds(61));
        limiter.TryAcquire("sprayed", out _).ShouldBeTrue();
    }

    // ------------------------------------------------------------ JWKS provider

    private sealed class CountingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(respond());
        }
    }

    private static string Jwks(params string[] keyIds)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var parameters = rsa.ExportParameters(false);
        var entries = keyIds.Select(kid =>
            $"{{\"kty\":\"RSA\",\"kid\":\"{kid}\",\"use\":\"sig\",\"alg\":\"RS256\",\"n\":\"{Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(parameters.Modulus!)}\",\"e\":\"{Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(parameters.Exponent!)}\"}}");

        return "{\"keys\":[" + string.Join(',', entries) + "]}";
    }

    [Fact]
    public async Task JwksProvider_CachesTheDocument_AndRefetchesAnUnknownKidAtMostOncePerInterval()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        var handler = new CountingHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Jwks("k1", "k2")) });
        using var provider = new HttpGitHubJwksProvider(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(new CiAuthOptions()), clock);
        var ct = TestContext.Current.CancellationToken;

        (await provider.GetKeyAsync("k1", ct)).ShouldNotBeNull();
        (await provider.GetKeyAsync("k2", ct)).ShouldNotBeNull();
        handler.Calls.ShouldBe(1);

        // Random kids: the first forces no refetch inside the interval (the document is fresh), none ever amplifies.
        for (var i = 0; i < 20; i++)
        {
            (await provider.GetKeyAsync("rotated-" + i, ct)).ShouldBeNull();
        }

        handler.Calls.ShouldBe(1);

        clock.Advance(HttpGitHubJwksProvider.MinRefreshInterval + TimeSpan.FromSeconds(1));
        (await provider.GetKeyAsync("unknown", ct)).ShouldBeNull();
        handler.Calls.ShouldBe(2);
        (await provider.GetKeyAsync("unknown-again", ct)).ShouldBeNull();
        handler.Calls.ShouldBe(2);

        clock.Advance(HttpGitHubJwksProvider.CacheTtl + TimeSpan.FromSeconds(1));
        (await provider.GetKeyAsync("k1", ct)).ShouldNotBeNull();
        handler.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task JwksProvider_FailsClosed_WhenTheEndpointFails()
    {
        var handler = new CountingHandler(() => new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var provider = new HttpGitHubJwksProvider(
            new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(new CiAuthOptions()), TimeProvider.System);

        await Should.ThrowAsync<HttpRequestException>(async () => await provider.GetKeyAsync("k1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task JwksProvider_ServesStaleKeysDuringAnOutage_AndBacksOffInsteadOfQueueing()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        var healthy = true;
        var handler = new CountingHandler(() => healthy
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Jwks("k1")) }
            : new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var provider = new HttpGitHubJwksProvider(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(new CiAuthOptions()), clock);
        var ct = TestContext.Current.CancellationToken;

        (await provider.GetKeyAsync("k1", ct)).ShouldNotBeNull();
        healthy = false;
        clock.Advance(HttpGitHubJwksProvider.CacheTtl + TimeSpan.FromSeconds(1));

        // The TTL passed and GitHub is down: the old key still verifies, and a burst costs one failed fetch, not one each.
        for (var i = 0; i < 20; i++)
        {
            (await provider.GetKeyAsync("k1", ct)).ShouldNotBeNull();
        }

        handler.Calls.ShouldBe(2);

        clock.Advance(HttpGitHubJwksProvider.FailureBackoff + TimeSpan.FromSeconds(1));
        (await provider.GetKeyAsync("k1", ct)).ShouldNotBeNull();
        handler.Calls.ShouldBe(3);

        // Past the grace window nothing is trusted: the outage surfaces as keys-unavailable.
        clock.Advance(HttpGitHubJwksProvider.StaleGrace);
        await Should.ThrowAsync<HttpRequestException>(async () => await provider.GetKeyAsync("k1", ct));
        await Should.ThrowAsync<HttpRequestException>(async () => await provider.GetKeyAsync("k1", ct));
        handler.Calls.ShouldBe(4);
    }

    [Fact]
    public async Task JwksProvider_RefusesAnOversizedBody()
    {
        var handler = new CountingHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 300 * 1024)) });
        using var provider = new HttpGitHubJwksProvider(
            new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(new CiAuthOptions()), TimeProvider.System);

        await Should.ThrowAsync<HttpRequestException>(async () => await provider.GetKeyAsync("k1", TestContext.Current.CancellationToken));
    }
}
