using System.Security.Cryptography;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Google;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Google;

/// <summary>
/// 02.3.1-T2 (AC5, Q3): the Google <c>hd</c> claim as <see cref="VerifiedIdentity.HostedDomain"/>. The claim
/// is read ONLY from a fully verified ID token — signature, issuer, audience, lifetime and nonce — and never
/// from userinfo or a query value; it is trimmed and ASCII lower-cased, absent or non-string stays
/// <c>null</c>, an over-long value is dropped instead of truncated, and a token this build does not accept
/// contributes nothing at all.
/// </summary>
/// <remarks>
/// Pure, like the rest of the claim matrix: runtime keys and tokens, a pinned clock. The key and token
/// factories live in <c>TokenValidationTests</c> and are used read-only here.
/// </remarks>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class GoogleHostedDomainTests
{
    [Fact]
    public async Task A_Verified_Token_Yields_Its_Hd_Claim_Lower_Cased_And_Trimmed()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint((_, claims) => claims["hd"] = "  Evidgate.COM  ");

        var result = await ValidateAsync(harness, token);

        result.Identity.ShouldNotBeNull();
        result.Identity!.HostedDomain.ShouldBe("evidgate.com");
        result.Identity.EmailVerified.ShouldBeTrue("the hosted domain rides on the same verified identity");
    }

    [Fact]
    public async Task A_Token_Without_Hd_Leaves_The_Hosted_Domain_Null()
    {
        var harness = new GoogleValidatorHarness();

        var result = await ValidateAsync(harness, harness.Tokens.Mint());

        result.Identity.ShouldNotBeNull();
        result.Identity!.HostedDomain.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_Empty_Hd_Leaves_The_Hosted_Domain_Null(string claim)
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint((_, claims) => claims["hd"] = claim);

        var result = await ValidateAsync(harness, token);

        result.Identity.ShouldNotBeNull();
        result.Identity!.HostedDomain.ShouldBeNull();
    }

    [Fact]
    public async Task A_Non_String_Hd_Is_Never_Coerced_Into_A_Domain()
    {
        var harness = new GoogleValidatorHarness();

        // A number and a boolean: both are what a hostile or broken issuer might send, and neither is a
        // domain. Google sends the claim as a JSON string or not at all.
        var numeric = harness.Tokens.Mint((_, claims) => claims["hd"] = 4242);
        var boolean = harness.Tokens.Mint((_, claims) => claims["hd"] = true);

        (await ValidateAsync(harness, numeric)).Identity!.HostedDomain.ShouldBeNull();
        (await ValidateAsync(harness, boolean)).Identity!.HostedDomain.ShouldBeNull();
    }

    [Fact]
    public async Task An_Hd_Of_The_Maximum_Dns_Length_Is_Kept_And_A_Longer_One_Is_Dropped()
    {
        var harness = new GoogleValidatorHarness();
        var longest = new string('a', GoogleIdTokenValidator.MaxHostedDomainLength);
        var tooLong = new string('a', GoogleIdTokenValidator.MaxHostedDomainLength + 1);

        var kept = await ValidateAsync(harness, harness.Tokens.Mint((_, claims) => claims["hd"] = longest));
        var dropped = await ValidateAsync(harness, harness.Tokens.Mint((_, claims) => claims["hd"] = tooLong));

        kept.Identity!.HostedDomain.ShouldBe(longest);
        dropped.Identity!.HostedDomain.ShouldBeNull("half a domain is a different domain, so it is dropped whole");
    }

    [Fact]
    public async Task An_Hd_Claim_In_A_Token_With_A_Foreign_Signature_Is_Never_Accepted()
    {
        var harness = new GoogleValidatorHarness();

        // The key id is the published one, so the signature check is the only thing that can refuse this
        // token: it must refuse the identity AND the hosted domain it carries.
        using var foreign = RSA.Create(2048);
        var token = harness.Tokens.Mint((signing, claims) =>
        {
            claims["hd"] = "evidgate.com";
            signing.SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(foreign) { KeyId = harness.Keys.KeyId },
                SecurityAlgorithms.RsaSha256);
        });

        var result = await ValidateAsync(harness, token);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task An_Hd_Claim_In_An_Expired_Token_Is_Never_Accepted()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint((signing, claims) =>
        {
            claims["hd"] = "evidgate.com";
            signing.NotBeforeOffset = TimeSpan.FromMinutes(-30);
            signing.ExpiresOffset = TimeSpan.FromMinutes(-20);
        });

        var result = await ValidateAsync(harness, token);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task An_Hd_Claim_With_A_Mismatched_Nonce_Is_Never_Accepted()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint((_, claims) =>
        {
            claims["hd"] = "evidgate.com";
            claims["nonce"] = "another-nonce";
        });

        var result = await ValidateAsync(harness, token);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    private static ValueTask<LoginProviderResult> ValidateAsync(GoogleValidatorHarness harness, string token) =>
        harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);
}
