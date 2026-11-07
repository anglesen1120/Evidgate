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

    /// <summary>
    /// The grammar itself: the <c>evg_</c> scheme, a twelve-character alphanumeric prefix, one separator and a
    /// forty-three-character base64url secret. Each refusal below breaks exactly one of those terms at a fixed
    /// position, so a green run means the parser reads that shape rather than refusing whatever it is given;
    /// the two literals above the loop are the positive control.
    /// </summary>
    [Fact]
    public void TryParse_AcceptsOnlyTheEvgShape()
    {
        var generated = ApiTokenFormat.Generate();

        ApiTokenFormat.TryParse(generated.Full, out var prefix, out var secret).ShouldBeTrue();
        prefix.ShouldBe(generated.Prefix);
        secret.ShouldBe(generated.Secret);

        ApiTokenFormat.TryParse(Valid, out var fixedPrefix, out var fixedSecret).ShouldBeTrue(
            "the control must be accepted, or the refusals below prove nothing about the grammar");
        fixedPrefix.ShouldBe(ValidPrefix);
        fixedSecret.ShouldBe(ValidSecret);

        foreach (var rejected in new[]
                 {
                     null,
                     string.Empty,
                     " " + Valid,
                     Valid + " ",
                     "hunter2",
                     "egt_" + ValidPrefix + "_" + ValidSecret,
                     "EVG_" + ValidPrefix + "_" + ValidSecret,
                     "evg_" + ValidPrefix[..11] + "_" + ValidSecret,
                     "evg_" + ValidPrefix + "a_" + ValidSecret,
                     "evg_" + ValidPrefix[..3] + "!" + ValidPrefix[4..] + "_" + ValidSecret,
                     "evg_" + ValidPrefix + "_" + ValidSecret[..20] + "!" + ValidSecret[21..],
                     "evg_" + ValidPrefix + "_" + ValidSecret[..20] + "+" + ValidSecret[21..],
                     "evg_" + ValidPrefix + "_" + ValidSecret[..42] + "=",
                     "evg_" + ValidPrefix + ValidSecret,
                     "evg" + ValidPrefix + "_" + ValidSecret,
                     "evg_" + ValidPrefix + "_" + ValidSecret[..42],
                     "evg_" + ValidPrefix + "_" + ValidSecret + "A",
                     "evg_" + ValidPrefix + "_" + ValidSecret + new string('A', 30),
                 })
        {
            var shown = rejected ?? "(null)";
            ApiTokenFormat.TryParse(rejected, out var refusedPrefix, out var refusedSecret).ShouldBeFalse(
                $"the parser accepted a value outside the evg grammar: {shown}");
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

    /// <summary>A prefix of exactly twelve alphanumeric characters, the shape the parser accepts.</summary>
    private const string ValidPrefix = "Ab3dEf6hIj9L";

    /// <summary>A secret of exactly forty-three base64url characters, carrying both <c>-</c> and <c>_</c>.</summary>
    private static readonly string ValidSecret = string.Concat(Enumerable.Repeat("Az09-_", 7)) + "A";

    /// <summary>The well-formed literal every refusal above is derived from by one fixed-position change.</summary>
    private static readonly string Valid = $"evg_{ValidPrefix}_{ValidSecret}";

    private static string Key(byte seed) => Convert.ToBase64String(Enumerable.Repeat(seed, 32).ToArray());
}
