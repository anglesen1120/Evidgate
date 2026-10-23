using System.Security.Cryptography;
using Evidgate.Modules.Identity.Ci;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Tenancy.IntegrationTests;

/// <summary>The CI key material the test host is configured with (02.4.1/02.4.2), fixed per run.</summary>
internal sealed class CiTestKeys
{
    public CiTestKeys()
    {
        SigningKey = RandomNumberGenerator.GetBytes(32);
        Pepper = RandomNumberGenerator.GetBytes(32);
        PreviousPepper = RandomNumberGenerator.GetBytes(32);
    }

    public byte[] SigningKey { get; }

    public byte[] Pepper { get; }

    public byte[] PreviousPepper { get; }

    public const string Issuer = "evidgate-test";

    public const string Audience = "evidgate-test-aud";

    public const string GithubAudience = "evidgate-github";

    public IReadOnlyDictionary<string, string?> Configuration => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["EVIDGATE:Ci:SigningKey"] = Convert.ToBase64String(SigningKey),
        ["EVIDGATE:Ci:Issuer"] = Issuer,
        ["EVIDGATE:Ci:Audience"] = Audience,
        ["EVIDGATE:Ci:Pepper"] = Convert.ToBase64String(Pepper),
        ["EVIDGATE:Ci:PepperVersion"] = "2",
        ["EVIDGATE:Ci:GithubOidcAudience"] = GithubAudience,
        ["EVIDGATE:Ci:ExchangeBurstLimit"] = "1000",
    };
}

/// <summary>
/// A stand-in for GitHub's OIDC issuer: a local RSA keypair published as the JWKS, and a helper that
/// signs workflow tokens with any claims a test wants (02.4.1-T1.4).
/// </summary>
internal sealed class TestJwks : IGitHubJwksProvider
{
    public const string KeyId = "test-key-1";

    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    private readonly RSA _rsa = RSA.Create(2048);

    public bool KeysUnavailable { get; set; }

    public int Lookups { get; private set; }

    public ValueTask<SecurityKey?> GetKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        Lookups++;

        if (KeysUnavailable)
        {
            throw new HttpRequestException("the JWKS endpoint is down");
        }

        return ValueTask.FromResult<SecurityKey?>(
            keyId == KeyId ? new RsaSecurityKey(_rsa.ExportParameters(includePrivateParameters: false)) { KeyId = KeyId } : null);
    }

    /// <summary>A valid workflow token for <paramref name="repositoryId"/>; <paramref name="tweak"/> breaks whatever a test needs.</summary>
    public string Mint(long repositoryId, Action<Dictionary<string, object>, TokenOptions>? tweak = null)
    {
        var options = new TokenOptions();
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = $"repo:acme/web:ref:refs/heads/main",
            ["jti"] = Guid.NewGuid().ToString("D"),
            ["repository_id"] = repositoryId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["repository"] = "acme/web",
            ["ref"] = "refs/heads/main",
            ["sha"] = new string('a', 40),
            ["run_id"] = "9001",
            ["run_attempt"] = "1",
            ["job_workflow_ref"] = "acme/web/.github/workflows/ci.yml@refs/heads/main",
        };
        tweak?.Invoke(claims, options);

        var now = DateTime.UtcNow;
        var key = new RsaSecurityKey(_rsa) { KeyId = options.KeyId };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            NotBefore = now + options.NotBeforeOffset,
            Expires = now + options.ExpiresOffset,
            IssuedAt = now,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, options.Algorithm),
        };

        return Handler.CreateToken(descriptor);
    }

    internal sealed class TokenOptions
    {
        public string Issuer { get; set; } = "https://token.actions.githubusercontent.com";

        public string Audience { get; set; } = CiTestKeys.GithubAudience;

        public string KeyId { get; set; } = KeyId_;

        public string Algorithm { get; set; } = SecurityAlgorithms.RsaSha256;

        public TimeSpan NotBeforeOffset { get; set; } = TimeSpan.FromSeconds(-5);

        public TimeSpan ExpiresOffset { get; set; } = TimeSpan.FromMinutes(5);

        private const string KeyId_ = TestJwks.KeyId;
    }
}
