using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Google;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Google;

/// <summary>
/// 02.3.1-T1.2 (AC2, google half) / 00 §9.1: the ID-token claim matrix. Every failure the story names
/// has one fact — bad signature, wrong issuer, wrong audience, expired, <c>nbf</c> at +59 s and +61 s,
/// nonce mismatch, every shape of a false <c>email_verified</c>, <c>alg</c> none, HS256 algorithm
/// confusion, unknown kid and a JWKS outage — plus the happy path that yields the identity.
/// </summary>
/// <remarks>
/// Pure: no database, no network, no host. Keys and tokens are built at runtime
/// (<see cref="System.Security.Cryptography.RSA"/>, <see cref="JsonWebTokenHandler"/>) and the clock is a
/// <see cref="MutableTimeProvider"/> pinned to a fixed instant, so the skew boundaries are exact and the
/// suite is deterministic. The shared test helpers of the whole Google login suite live at the bottom of
/// this file: it owns the key and token factories the other files use.
/// </remarks>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class TokenValidationTests
{
    [Fact]
    public async Task GoogleValidToken_YieldsTheVerifiedIdentity()
    {
        var harness = new GoogleValidatorHarness();

        var result = await harness.Validator.ValidateAsync(harness.Tokens.Mint(), GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Failure.ShouldBeNull();
        var identity = result.Identity.ShouldNotBeNull();
        identity.Provider.ShouldBe("google");
        identity.Subject.ShouldBe(GoogleTestHarness.Subject);
        identity.Email.ShouldBe(GoogleTestHarness.Email);
        identity.EmailVerified.ShouldBeTrue();
        identity.DisplayName.ShouldBe(GoogleTestHarness.DisplayName);
    }

    [Fact]
    public async Task GoogleStringEmailVerified_YieldsTheIdentity()
    {
        // Google has sent both JSON shapes; the string form must not be read as unverified.
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint((_, claims) => claims["email_verified"] = "true");

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Identity.ShouldNotBeNull().EmailVerified.ShouldBeTrue();
    }

    [Fact]
    public async Task GoogleBareIssuerAlias_IsAccepted()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint(signing => signing.Issuer = GoogleEndpoints.IssuerAlias);

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Identity.ShouldNotBeNull().Provider.ShouldBe("google");
    }

    [Fact]
    public async Task GoogleBadSignature_IsTokenInvalid()
    {
        // Same kid, different key: only the signature tells them apart, and it must be checked.
        var harness = new GoogleValidatorHarness();
        var impostor = new GoogleTestKeys(harness.Keys.KeyId);
        var token = harness.Tokens.Mint(signing =>
            signing.SigningCredentials = new SigningCredentials(new RsaSecurityKey(impostor.Key) { KeyId = impostor.KeyId }, SecurityAlgorithms.RsaSha256));

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleWrongIssuer_IsTokenInvalid()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint(signing => signing.Issuer = "https://issuer.evidgate.test");

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleWrongAudience_IsTokenInvalid()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint(signing => signing.Audience = "another-client.apps.googleusercontent.com");

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleAudienceArrayWithMatchingAzp_YieldsTheIdentity()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint((signing, claims) =>
        {
            signing.Audience = null;
            claims["aud"] = new[] { GoogleTestHarness.ClientId, "another-client.apps.googleusercontent.com" };
            claims["azp"] = GoogleTestHarness.ClientId;
        });

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Identity.ShouldNotBeNull();
    }

    [Fact]
    public async Task GoogleAudienceArrayWithoutAzp_IsTokenInvalid()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint((signing, claims) =>
        {
            signing.Audience = null;
            claims["aud"] = new[] { GoogleTestHarness.ClientId, "another-client.apps.googleusercontent.com" };
        });

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleExpiredToken_IsTokenInvalid()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint(signing => signing.ExpiresOffset = TimeSpan.FromSeconds(-61));

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleExpiredWithinSkew_IsAccepted()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint(signing => signing.ExpiresOffset = TimeSpan.FromSeconds(-59));

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Identity.ShouldNotBeNull();
    }

    [Fact]
    public async Task GoogleNotBeforeWithinSkew_IsAccepted()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint(signing => signing.NotBeforeOffset = TimeSpan.FromSeconds(59));

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Identity.ShouldNotBeNull();
    }

    [Fact]
    public async Task GoogleNotBeforeBeyondSkew_IsRefused()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint(signing => signing.NotBeforeOffset = TimeSpan.FromSeconds(61));

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleNonceMismatch_IsTokenInvalid()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint((_, claims) => claims["nonce"] = "someone-elses-nonce");

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleNonceMissing_IsTokenInvalid()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint((_, claims) => claims.Remove("nonce"));

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleEmailNotVerifiedBoolean_IsTokenInvalid()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint((_, claims) => claims["email_verified"] = false);

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleEmailNotVerifiedString_IsTokenInvalid()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint((_, claims) => claims["email_verified"] = "false");

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleEmailVerifiedMissing_IsTokenInvalid()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint((_, claims) => claims.Remove("email_verified"));

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleEmailMissing_IsTokenInvalid()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint((_, claims) => claims.Remove("email"));

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleSubjectMissing_IsTokenInvalid()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint((_, claims) => claims.Remove("sub"));

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleAlgorithmNone_IsTokenInvalid()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint(signing => signing.Unsigned = true);

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleAlgorithmHs256WithThePublicKey_IsTokenInvalid()
    {
        // Algorithm confusion: the attacker signs with HMAC keyed by the published RSA public key and
        // keeps the kid. Only a pinned RS256 (checked before the key is used) refuses this.
        var harness = new GoogleValidatorHarness();
        var publicKeyBytes = harness.Keys.Key.ExportRSAPublicKey();
        var token = harness.Tokens.Mint(signing => signing.SigningCredentials =
            new SigningCredentials(new SymmetricSecurityKey(publicKeyBytes), SecurityAlgorithms.HmacSha256));

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleKidMissing_IsTokenInvalid()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint(signing => signing.OmitKeyId = true);

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
        harness.Jwks.Lookups.ShouldBe(0, "a token without a kid never reaches the key set");
    }

    [Fact]
    public async Task GoogleUnknownKid_IsTokenInvalid()
    {
        var harness = new GoogleValidatorHarness();
        var token = harness.Tokens.Mint(signing => signing.KeyId = "unpublished-key");

        var result = await harness.Validator.ValidateAsync(token, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GoogleJwksUnavailable_IsProviderUnavailable()
    {
        var harness = new GoogleValidatorHarness();
        harness.Jwks.Unavailable = true;

        var result = await harness.Validator.ValidateAsync(harness.Tokens.Mint(), GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GoogleMalformedToken_IsTokenInvalid()
    {
        var harness = new GoogleValidatorHarness();

        var result = await harness.Validator.ValidateAsync("not-a-token", GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }
}

// ---------------------------------------------------------------------------------------------------
// Test helpers of the Google login suite (02.3.1-T1.2). They live in this file because the token and
// key factories belong to the claim matrix; the other Google test files use them read-only.
// ---------------------------------------------------------------------------------------------------

/// <summary>A clock the test advances; the whole suite runs against it instead of wall time.</summary>
internal sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    internal DateTimeOffset UtcNow => _now;

    public override DateTimeOffset GetUtcNow() => _now;

    internal void Advance(TimeSpan by) => _now += by;
}

/// <summary>The fixed values the Google login suite configures and asserts against.</summary>
internal static class GoogleTestHarness
{
    internal const string ClientId = "evidgate-test-client.apps.googleusercontent.com";
    internal const string ClientSecret = "test-client-secret-value";
    internal const string Nonce = "test-nonce-1";
    internal const string Subject = "109876543210987654321";
    internal const string Email = "user@evidgate.test";
    internal const string DisplayName = "Test User";

    /// <summary>The instant the suite's clock reports: tokens are minted relative to it, so the skew boundaries are exact.</summary>
    internal static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    internal static readonly Uri RedirectUri = new("https://app.evidgate.test/v1/auth/oidc/google/callback", UriKind.Absolute);

    internal static GoogleLoginOptions Options(
        string? clientId = ClientId,
        string? clientSecret = ClientSecret,
        int clockSkewSeconds = 60,
        int maxResponseBytes = 65536) => new()
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            ClockSkewSeconds = clockSkewSeconds,
            MaxResponseBytes = maxResponseBytes,
        };
}

/// <summary>A runtime RSA keypair with the <c>kid</c> the JWKS publishes it under.</summary>
internal sealed class GoogleTestKeys(string keyId = "test-key-1")
{
    private readonly RSA _rsa = RSA.Create(2048);

    internal string KeyId { get; } = keyId;

    internal RSA Key => _rsa;

    internal RsaSecurityKey PublicKey() =>
        new(_rsa.ExportParameters(includePrivateParameters: false)) { KeyId = KeyId };

    internal string JwksJson() => JwksJson(this);

    /// <summary>A JWKS document publishing <paramref name="keys"/> as RS256 verification keys.</summary>
    internal static string JwksJson(params GoogleTestKeys[] keys)
    {
        var entries = keys.Select(key =>
        {
            var parameters = key.Key.ExportParameters(includePrivateParameters: false);

            return $$"""{"kty":"RSA","kid":"{{key.KeyId}}","use":"sig","alg":"RS256","n":"{{Base64UrlEncoder.Encode(parameters.Modulus!)}}","e":"{{Base64UrlEncoder.Encode(parameters.Exponent!)}}"}""";
        });

        return $$"""{"keys":[{{string.Join(',', entries)}}]}""";
    }

    /// <summary>A JWKS document whose only key is symmetric, i.e. never a candidate for an RS256 token.</summary>
    internal static string SymmetricOnlyJwksJson(string keyId = "symmetric-key") =>
        $$"""{"keys":[{"kty":"oct","kid":"{{keyId}}","k":"{{Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32))}}"}]}""";

    /// <summary>A JWKS document with <paramref name="count"/> symmetric keys, to prove the key-count bound.</summary>
    internal static string OversizedJwksJson(int count)
    {
        var entries = Enumerable.Range(0, count).Select(index =>
            $$"""{"kty":"oct","kid":"key-{{index}}","k":"{{Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32))}}"}""");

        return $$"""{"keys":[{{string.Join(',', entries)}}]}""";
    }
}

/// <summary>
/// Mints ID tokens for <see cref="GoogleTestKeys"/> at the suite's fixed instant. Every claim and every
/// signing aspect a failing token needs is a knob, so no test has to hand-write a JWT.
/// </summary>
internal sealed class GoogleTokenFactory(MutableTimeProvider clock, GoogleTestKeys keys)
{
    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    /// <summary>
    /// A token carrying the claims of a complete Google login; <paramref name="tweak"/> breaks exactly
    /// what a test needs (a claim, the issuer, the audience, the lifetime, the signing).
    /// </summary>
    internal string Mint(Action<GoogleTokenSigning, Dictionary<string, object>>? tweak = null)
    {
        var signing = new GoogleTokenSigning();
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["nonce"] = GoogleTestHarness.Nonce,
            ["sub"] = GoogleTestHarness.Subject,
            ["email"] = GoogleTestHarness.Email,
            ["email_verified"] = true,
            ["name"] = GoogleTestHarness.DisplayName,
        };

        tweak?.Invoke(signing, claims);

        if (signing.Unsigned)
        {
            // Hand-built on purpose: an unsigned token has no signature to create, and the pair must stay
            // "alg none" whatever the handler would emit. Both parts are encoded at runtime, never literals.
            var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
            var payload = Base64UrlEncoder.Encode(JsonSerializer.Serialize(claims));

            return $"{header}.{payload}.";
        }

        var now = clock.UtcNow.UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = signing.Issuer,
            Audience = signing.Audience,
            NotBefore = now + signing.NotBeforeOffset,
            Expires = now + signing.ExpiresOffset,
            IssuedAt = now,
            Claims = claims,
            SigningCredentials = signing.SigningCredentials ?? Credentials(signing),
        };

        return Handler.CreateToken(descriptor);
    }

    /// <summary>A token whose signing aspect only (issuer, audience, lifetime, credentials) is tweaked.</summary>
    internal string Mint(Action<GoogleTokenSigning>? tweak) => Mint((signing, _) =>
    {
        tweak?.Invoke(signing);
    });

    /// <summary>The RS256 credentials the suite uses: the harness key under its <c>kid</c>, or without one.</summary>
    private SigningCredentials? Credentials(GoogleTokenSigning signing)
    {
        if (signing.Unsigned)
        {
            // No SigningCredentials is an unsigned token: the header says alg none.
            return null;
        }

        var key = new RsaSecurityKey(keys.Key);
        if (!signing.OmitKeyId)
        {
            key.KeyId = signing.KeyId;
        }

        return new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
    }
}

/// <summary>The knobs a test may turn on a minted token.</summary>
internal sealed class GoogleTokenSigning
{
    internal string Issuer { get; set; } = GoogleEndpoints.Issuer;

    internal string? Audience { get; set; } = GoogleTestHarness.ClientId;

    internal string KeyId { get; set; } = "test-key-1";

    internal TimeSpan NotBeforeOffset { get; set; } = TimeSpan.FromSeconds(-5);

    internal TimeSpan ExpiresOffset { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Overrides how the token is signed (a foreign key, an HMAC key, another algorithm).</summary>
    internal SigningCredentials? SigningCredentials { get; set; }

    /// <summary>Mints an unsigned (<c>alg</c> none) token.</summary>
    internal bool Unsigned { get; set; }

    /// <summary>Leaves the <c>kid</c> out of the header.</summary>
    internal bool OmitKeyId { get; set; }
}

/// <summary>A key set a test controls: keys by <c>kid</c>, a lookup counter and an outage switch.</summary>
internal sealed class GoogleTestJwksProvider : IGoogleJwksProvider
{
    internal Dictionary<string, SecurityKey> Keys { get; } = new(StringComparer.Ordinal);

    internal bool Unavailable { get; set; }

    internal int Lookups { get; private set; }

    public ValueTask<SecurityKey?> GetKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        Lookups++;

        if (Unavailable)
        {
            throw new HttpRequestException("the JWKS endpoint is down");
        }

        return ValueTask.FromResult(Keys.TryGetValue(keyId, out var key) ? key : null);
    }
}

/// <summary>A validator over the suite's clock and options, wired to an in-memory key set and a token factory.</summary>
internal sealed class GoogleValidatorHarness
{
    internal GoogleValidatorHarness(GoogleLoginOptions? options = null)
    {
        Settings = options ?? GoogleTestHarness.Options();
        Clock = new MutableTimeProvider(GoogleTestHarness.Now);
        Keys = new GoogleTestKeys();
        Tokens = new GoogleTokenFactory(Clock, Keys);
        Jwks = new GoogleTestJwksProvider();
        Jwks.Keys[Keys.KeyId] = Keys.PublicKey();
        Validator = new GoogleIdTokenValidator(Jwks, Microsoft.Extensions.Options.Options.Create(Settings), Clock);
    }

    internal GoogleLoginOptions Settings { get; }

    internal MutableTimeProvider Clock { get; }

    internal GoogleTestKeys Keys { get; }

    internal GoogleTokenFactory Tokens { get; }

    internal GoogleTestJwksProvider Jwks { get; }

    internal GoogleIdTokenValidator Validator { get; }
}

/// <summary>
/// A body whose length the sender cannot compute, so a Content-Length cap cannot fire: the cap under test
/// has to be enforced while the body is read, which is exactly what a chunked provider response does.
/// </summary>
internal sealed class LengthlessContent(string body) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask();

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(body), cancellationToken).AsTask();

    // False is the point: HttpContentHeaders.ContentLength stays null, like a chunked response.
    protected override bool TryComputeLength(out long length)
    {
        length = 0;

        return false;
    }

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(body)));

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        CreateContentReadStreamAsync();
}

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a delegate and records what it saw.</summary>
internal sealed class GoogleTestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int _calls;

    internal int Calls => Volatile.Read(ref _calls);

    internal ConcurrentQueue<string> Bodies { get; } = new();

    internal ConcurrentQueue<string> Uris { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        Uris.Enqueue(request.RequestUri?.AbsoluteUri ?? string.Empty);

        if (request.Content is { } content)
        {
            Bodies.Enqueue(await content.ReadAsStringAsync(cancellationToken));
        }

        return await respond(request, cancellationToken);
    }

    /// <summary>A JSON answer with the given status.</summary>
    internal static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
