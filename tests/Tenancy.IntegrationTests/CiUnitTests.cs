using System.Text;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Options;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>Pure tests of the CI authentication building blocks (02.4.1, 02.4.2): no database, no host.</summary>
[Collection(ExclusiveTenancySuite.Name)]
public sealed class CiUnitTests
{
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
        hash.Length.ShouldBe(32);
    }

    [Fact]
    public void Secrets_WithoutPepper_CannotHash()
    {
        var secrets = new CiSecrets(Microsoft.Extensions.Options.Options.Create(Options()));

        secrets.CanHash.ShouldBeFalse();
        secrets.SigningKey.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => secrets.HashCurrent(new byte[1]));
    }

    [Fact]
    public void OptionsValidator_RejectsShortKeysBadBase64AndRepeatedPepperVersions()
    {
        var validator = new CiAuthOptionsValidator();

        validator.Validate(null, Options(signing: Key(1), pepper: Key(2))).Succeeded.ShouldBeTrue();
        validator.Validate(null, Options()).Succeeded.ShouldBeTrue();
        validator.Validate(null, Options(signing: Convert.ToBase64String(new byte[8]))).Failed.ShouldBeTrue();
        validator.Validate(null, Options(pepper: "%%%not-base64%%%")).Failed.ShouldBeTrue();
        validator.Validate(null, Options(pepper: Key(1), version: 2, previous: Key(2), previousVersion: 2)).Failed.ShouldBeTrue();
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
}
