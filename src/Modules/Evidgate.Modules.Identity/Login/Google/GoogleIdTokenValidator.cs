using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.Modules.Identity.Ci;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Evidgate.Modules.Identity.Login.Google;

/// <summary>
/// Verifies a Google ID token (02.3.1-T1.2, AC2): RS256 only (an <c>alg</c> of <c>none</c> or HS256 is
/// never accepted, which closes algorithm confusion), the signature against the issuer's JWKS, the
/// configured issuer plus the other spelling of the same host (Google publishes both), the audience (with
/// <c>azp</c> when <c>aud</c> has several entries), the lifetime with the configured skew, the nonce in
/// constant time, and the subject, email and <c>email_verified</c> claims the identity needs. A false or
/// missing <c>email_verified</c> never yields an identity (D3/T6).
/// </summary>
/// <remarks>
/// No failure carries provider text: the result is a typed <see cref="LoginProviderResult"/> whose
/// failure the callback maps to 401 <c>token_invalid</c> or 503 <c>unavailable</c>. Nothing this class
/// observes (token, nonce, subject, email, key id) is logged.
/// </remarks>
internal sealed class GoogleIdTokenValidator(IGoogleJwksProvider keys, IOptions<GoogleLoginOptions> options, TimeProvider clock)
{
    /// <summary>Largest ID token accepted; anything longer is refused unread.</summary>
    internal const int MaxTokenLength = 8192;

    /// <summary>Largest nonce a request or a token may carry.</summary>
    internal const int MaxNonceLength = 512;

    /// <summary>Largest subject accepted (Google's <c>sub</c> is 21 digits today).</summary>
    internal const int MaxSubjectLength = 255;

    /// <summary>Largest email accepted (RFC 5321 path limit).</summary>
    internal const int MaxEmailLength = 320;

    /// <summary>Largest display name copied from the provider; a longer one is dropped, never truncated into a different name.</summary>
    internal const int MaxDisplayNameLength = 256;

    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    private readonly GoogleLoginOptions _options = options.Value;

    /// <summary>The accepted <c>iss</c> values, derived once from <see cref="GoogleLoginOptions.Issuer"/>.</summary>
    private readonly string[] _issuerSpellings = IssuerSpellings(options.Value.Issuer);

    /// <summary>Verifies <paramref name="idToken"/> against <paramref name="expectedNonce"/>.</summary>
    /// <param name="idToken">The compact ID token the token endpoint returned.</param>
    /// <param name="expectedNonce">The nonce the start route stored; the token must echo it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<LoginProviderResult> ValidateAsync(string idToken, string expectedNonce, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(idToken) || idToken.Length > MaxTokenLength)
        {
            return LoginProviderResult.Fail(LoginFailure.TokenInvalid);
        }

        JsonWebToken unverified;
        try
        {
            unverified = new JsonWebToken(idToken);
        }
        catch (ArgumentException)
        {
            return LoginProviderResult.Fail(LoginFailure.TokenInvalid);
        }

        // The header is untrusted: it only selects which published key to try. The algorithm is pinned below.
        if (string.IsNullOrEmpty(unverified.Kid) || unverified.Kid.Length > HttpGoogleJwksCache.MaxKeyIdLength)
        {
            return LoginProviderResult.Fail(LoginFailure.TokenInvalid);
        }

        if (!string.Equals(unverified.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
        {
            return LoginProviderResult.Fail(LoginFailure.TokenInvalid);
        }

        SecurityKey? key;
        try
        {
            key = await keys.GetKeyAsync(unverified.Kid, cancellationToken);
        }
        catch (Exception ex) when (GoogleProviderFailure.IsUnavailable(ex, cancellationToken))
        {
            return LoginProviderResult.Fail(LoginFailure.ProviderUnavailable);
        }

        if (key is null)
        {
            // Unknown kid, no refetch left inside the minimum refresh window: fail closed.
            return LoginProviderResult.Fail(LoginFailure.TokenInvalid);
        }

        TokenValidationResult result;
        try
        {
            result = await Handler.ValidateTokenAsync(idToken, ValidationParameters(key)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return LoginProviderResult.Fail(LoginFailure.TokenInvalid);
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            return LoginProviderResult.Fail(LoginFailure.TokenInvalid);
        }

        return Extract(jwt, expectedNonce) is { } identity
            ? LoginProviderResult.Ok(identity)
            : LoginProviderResult.Fail(LoginFailure.TokenInvalid);
    }

    private TokenValidationParameters ValidationParameters(SecurityKey key) => new()
    {
        // The configured issuer and the other spelling of the same host: the option decides which form is
        // canonical, and the boot validator keeps it inside GoogleEndpoints.IssuerAliases.
        ValidIssuers = _issuerSpellings,
        ValidAudience = _options.ClientId,
        IssuerSigningKey = key,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ClockSkew = TimeSpan.FromSeconds(_options.ClockSkewSeconds),
        LifetimeValidator = (notBefore, expires, _, parameters) =>
            LifetimeCheck.IsWithin(clock.GetUtcNow(), notBefore, expires, parameters.ClockSkew),
    };

    /// <summary>
    /// Every <c>iss</c> an ID token may carry: the configured issuer plus the other spelling of the same
    /// host, because Google publishes both the <c>https://</c> URL and the bare host form. A blank
    /// configuration accepts no issuer at all, which fails closed.
    /// </summary>
    /// <param name="configuredIssuer">The configured <see cref="GoogleLoginOptions.Issuer"/>.</param>
    private static string[] IssuerSpellings(string? configuredIssuer)
    {
        var configured = configuredIssuer?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            return [];
        }

        const string scheme = "https://";

        if (configured.StartsWith(scheme, StringComparison.Ordinal))
        {
            var bare = configured[scheme.Length..].TrimEnd('/');

            return bare.Length == 0 ? [configured] : [configured, bare];
        }

        // An absolute address of another scheme is not Google's at all: only the value itself.
        return configured.Contains("://", StringComparison.Ordinal) ? [configured] : [configured, scheme + configured.TrimEnd('/')];
    }

    private VerifiedIdentity? Extract(JsonWebToken jwt, string expectedNonce)
    {
        if (!AudienceIsOurs(jwt) || !NonceMatches(jwt, expectedNonce) || !EmailIsVerified(jwt))
        {
            return null;
        }

        if (!Text(jwt, "sub", MaxSubjectLength, out var subject)
            || !Text(jwt, "email", MaxEmailLength, out var email))
        {
            return null;
        }

        var displayName = Text(jwt, "name", MaxDisplayNameLength, out var name) ? name : null;

        return new VerifiedIdentity(GoogleLoginMetrics.Google, subject, email, EmailVerified: true, displayName);
    }

    /// <summary>
    /// A single-valued <c>aud</c> must equal the client id (the handler already enforced that). When
    /// Google lists several audiences, <c>azp</c> must name this client, so a token minted for another
    /// client that merely lists ours is refused.
    /// </summary>
    private bool AudienceIsOurs(JsonWebToken jwt)
    {
        if (jwt.Audiences.Count() <= 1)
        {
            return true;
        }

        return jwt.TryGetPayloadValue<string>("azp", out var authorizedParty)
               && string.Equals(authorizedParty, _options.ClientId, StringComparison.Ordinal);
    }

    private static bool NonceMatches(JsonWebToken jwt, string expectedNonce)
    {
        if (string.IsNullOrEmpty(expectedNonce) || expectedNonce.Length > MaxNonceLength)
        {
            return false;
        }

        if (!jwt.TryGetPayloadValue<string>("nonce", out var actual) || string.IsNullOrEmpty(actual) || actual.Length > MaxNonceLength)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expectedNonce));
    }

    /// <summary>
    /// Google sends <c>email_verified</c> as a JSON boolean today and has sent the string form before;
    /// only <c>true</c> or <c>"true"</c> counts. Anything else — <c>false</c>, <c>"false"</c>, a number,
    /// a missing claim — is unverified (D3/T6).
    /// </summary>
    private static bool EmailIsVerified(JsonWebToken jwt)
    {
        // Read untyped: the typed JsonElement read returned false for valid Google claims.
        if (!jwt.TryGetPayloadValue<object>("email_verified", out var value))
        {
            return false;
        }

        return value switch
        {
            bool verified => verified,
            string text => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase),
            JsonElement element => element.ValueKind == JsonValueKind.True
                || (element.ValueKind == JsonValueKind.String && string.Equals(element.GetString(), "true", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    private static bool Text(JsonWebToken jwt, string claim, int maxLength, out string value)
    {
        value = string.Empty;

        if (!jwt.TryGetPayloadValue<string>(claim, out var found) || string.IsNullOrWhiteSpace(found) || found.Length > maxLength)
        {
            return false;
        }

        value = found;

        return true;
    }
}
