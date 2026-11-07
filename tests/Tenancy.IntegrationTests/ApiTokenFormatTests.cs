using System.Security.Cryptography;
using System.Text;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Domain;
using Evidgate.Modules.Identity.Options;
using Evidgate.Modules.Identity.Tokens;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The token format and the comparison the verifier relies on, without a host: the shape the resolver's regex and the
/// public prefix agree on, the HMAC the create command stores, and the constant-time equality a wrong secret must fail.
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
public sealed class ApiTokenFormatTests
{
    [Fact]
    public void Generate_ProducesTheEvgShapeWithADistinctPrefixAndSecretEveryTime()
    {
        var first = ApiTokenFormat.Generate();
        var second = ApiTokenFormat.Generate();

        first.Full.ShouldBe($"evg_{first.Prefix}_{first.Secret}");
        first.Prefix.Length.ShouldBe(12);
        first.Prefix.ShouldAllBe(character => char.IsAsciiLetterOrDigit(character));
        first.Secret.Length.ShouldBe(43);
        first.Secret.ShouldNotContain('=');
        first.Secret.ShouldNotContain('+');
        first.Secret.ShouldNotContain('/');
        first.Prefix.ShouldNotBe(second.Prefix);
        first.Secret.ShouldNotBe(second.Secret);
    }

    [Fact]
    public void TryParse_AcceptsOnlyTheEvgShape()
    {
        var generated = ApiTokenFormat.Generate();

        ApiTokenFormat.TryParse(generated.Full, out var prefix, out var secret).ShouldBeTrue();
        prefix.ShouldBe(generated.Prefix);
        secret.ShouldBe(generated.Secret);

        foreach (var rejected in new[]
                 {
                     null,
                     string.Empty,
                     "hunter2",
                     "egt_" + generated.Prefix + "_" + generated.Secret,
                     "evg_" + generated.Prefix.ToLowerInvariant() + "_" + generated.Secret,
                     "evg_" + generated.Prefix + "_" + generated.Secret[..42],
                     "evg_" + generated.Prefix + "_" + generated.Secret + "A",
                     "evg_" + generated.Prefix + "_" + generated.Secret.Replace('-', '!'),
                     " " + generated.Full,
                 })
        {
            ApiTokenFormat.TryParse(rejected, out var refusedPrefix, out var refusedSecret).ShouldBeFalse();
            refusedPrefix.ShouldBeEmpty();
            refusedSecret.ShouldBeEmpty();
        }
    }

    [Fact]
    public void Verify_UsesConstantTimeCompare()
    {
        var secrets = new CiSecrets(Options.Create(new CiAuthOptions { Pepper = Key(1), PepperVersion = 2 }));
        var generated = ApiTokenFormat.Generate();
        var (version, hash) = secrets.HashCurrent(CiSecrets.Bytes(generated.Secret));
        var token = new ApiToken { Id = Guid.CreateVersion7(), Prefix = generated.Prefix, TokenHmac = hash, PepperVersion = version };

        // The stored HMAC is the credential: the same secret verifies, another one does not.
        token.MatchesHash(secrets.HashWithVersion(version, CiSecrets.Bytes(generated.Secret))!).ShouldBeTrue();
        token.MatchesHash(secrets.HashWithVersion(version, CiSecrets.Bytes(ApiTokenFormat.Generate().Secret))!).ShouldBeFalse();

        // A pepper version this deployment no longer holds verifies nothing at all.
        secrets.HashWithVersion(1, CiSecrets.Bytes(generated.Secret)).ShouldBeNull();

        // One flipped bit, an empty hash and a truncated hash all fail — the comparison is over the full digest.
        var flipped = hash.ToArray();
        flipped[0] ^= 0x01;
        token.MatchesHash(flipped).ShouldBeFalse();
        token.MatchesHash([]).ShouldBeFalse();
        token.MatchesHash(hash.AsSpan(0, 31)).ShouldBeFalse();

        // The property is the one FixedTimeEquals provides, and it is what the entity calls.
        token.MatchesHash(hash).ShouldBeTrue();
        CryptographicOperations.FixedTimeEquals(token.TokenHmac, hash).ShouldBeTrue();
    }

    [Fact]
    public void Revoke_IsFinal()
    {
        var now = DateTimeOffset.UtcNow;
        var token = new ApiToken { Id = Guid.CreateVersion7(), ExpiresAt = now.AddDays(30) };
        token.StatusAt(now).ShouldBe(ApiTokenStatus.Active);

        token.Revoke(now);
        token.StatusAt(now).ShouldBe(ApiTokenStatus.Revoked);
        token.StatusAt(now.AddDays(31)).ShouldBe(ApiTokenStatus.Revoked);
        Should.Throw<InvalidOperationException>(() => token.Revoke(now.AddMinutes(1)));

        var expired = new ApiToken { Id = Guid.CreateVersion7(), ExpiresAt = now.AddDays(-1) };
        expired.StatusAt(now).ShouldBe(ApiTokenStatus.Expired);
    }

    private static string Key(byte seed) => Convert.ToBase64String(Enumerable.Repeat(seed, 32).ToArray());
}
