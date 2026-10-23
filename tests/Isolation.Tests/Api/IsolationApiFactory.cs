using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Isolation.Tests.Api;

/// <summary>
/// The real Api composition (<c>Program</c> → <c>ApiHost</c>) over one migrated database for
/// the 02.9.1-T1 API isolation suite: the production routes, the C2 pipeline, the real
/// <c>TenantResolutionFilter</c>/<c>TenantResolver</c>, the real permission evaluator (never a
/// grant-all stand-in) and the real <c>ci_producer</c> bearer authenticator.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <c>tests/Tenancy.IntegrationTests/TenancyApiFactory.cs</c> in its <c>Ci</c>
/// mode. The one stand-in is <see cref="MemberHeaderAuthenticator"/>: no session
/// authenticator exists until 02.3.2 ships (<c>ApiHost.cs</c> registers none), so a member is
/// identified from <see cref="UserHeader"/>/<see cref="EmailHeader"/>. Everything after
/// identification — the org a request names, membership, project scope, permissions — is
/// decided by production code against the database.
/// </para>
/// <para>
/// Authenticators compose: <c>TenantResolutionFilter</c> takes every registered
/// <see cref="ICallerAuthenticator"/> and uses the first that recognises the request. The
/// header stand-in returns null for a request without <see cref="UserHeader"/>, so a
/// <c>Authorization: Bearer &lt;ci_producer JWT&gt;</c> request reaches the module's own
/// <c>CiProducerAuthenticator</c> unchanged.
/// </para>
/// <para>
/// The boot isolation self-check (<c>EVIDGATE:Isolation:SelfCheck</c>) is left at its
/// production default (on), so the host refuses to start against a database whose tenant
/// tables lost their protection.
/// </para>
/// </remarks>
public sealed class IsolationApiFactory : WebApplicationFactory<Program>
{
    /// <summary>The stand-in member credential: the user's id.</summary>
    public const string UserHeader = "X-Test-User";

    /// <summary>The stand-in member credential: the user's address (optional).</summary>
    public const string EmailHeader = "X-Test-Email";

    /// <summary>The production org selector for member callers (00 §9.1).</summary>
    public const string OrgHeader = "X-Evidgate-Org";

    /// <summary>The issuer the host signs <c>ci_producer</c> tokens with.</summary>
    public const string CiIssuer = "evidgate-test";

    /// <summary>The audience of the host's <c>ci_producer</c> tokens.</summary>
    public const string CiAudience = "evidgate-test-aud";

    /// <summary>The GitHub OIDC audience a binding exchange expects.</summary>
    public const string GithubAudience = "evidgate-github";

    /// <summary>
    /// The typed client name the Identity module registers its JWKS provider under:
    /// <c>AddHttpClient&lt;TClient, TImplementation&gt;</c> names the client after <c>TClient</c>
    /// (<c>HttpClientBuilderExtensions</c>), which is the internal <c>IGitHubJwksProvider</c>.
    /// </summary>
    private const string JwksClientName = "IGitHubJwksProvider";

    /// <summary>The GitHub OIDC stand-in: a local RSA keypair published as the JWKS the host fetches (02.4.1-T1.4).</summary>
    public TestJwks Jwks { get; } = new();

    private readonly string _connectionString;

    // Synthetic, per-run key material — the same shape as CiTestKeys in
    // tests/Tenancy.IntegrationTests/CiTestSupport.cs (not linked: that file also holds TestJwks,
    // which implements the Identity module's internal IGitHubJwksProvider). Never a real secret.
    private readonly byte[] _signingKey = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _pepper = RandomNumberGenerator.GetBytes(32);

    /// <summary>Creates a host over <paramref name="appConnectionString"/> (the <c>evidgate_app</c> login).</summary>
    /// <param name="appConnectionString">The application-role connection string of a migrated database.</param>
    public IsolationApiFactory(string appConnectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(appConnectionString);
        _connectionString = appConnectionString;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Database:ConnectionString"] = _connectionString,

                // The production default; custom roles stay off (POST /v1/roles is refused).
                ["EVIDGATE:Identity:CustomRolesEnabled"] = "false",

                // CI token issue and ingest-token verification (02.4.1/02.4.2), values as in
                // CiTestKeys.Configuration (tests/Tenancy.IntegrationTests/CiTestSupport.cs:30-39).
                ["EVIDGATE:Ci:SigningKey"] = Convert.ToBase64String(_signingKey),
                ["EVIDGATE:Ci:Issuer"] = CiIssuer,
                ["EVIDGATE:Ci:Audience"] = CiAudience,
                ["EVIDGATE:Ci:Pepper"] = Convert.ToBase64String(_pepper),
                ["EVIDGATE:Ci:PepperVersion"] = "2",
                ["EVIDGATE:Ci:GithubOidcAudience"] = GithubAudience,
                ["EVIDGATE:Ci:ExchangeBurstLimit"] = "1000",
            }));

        // Added after the modules' registrations, so the production ci_producer authenticator
        // stays first in the enumeration and keeps owning every Bearer request.
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ICallerAuthenticator, MemberHeaderAuthenticator>();

            // The Identity module's JWKS client is a typed HttpClient named after its TClient
            // (IGitHubJwksProvider), so replacing that named client's primary handler makes the
            // real HttpGitHubJwksProvider verify the test-minted OIDC tokens against the local
            // keypair (02.4.1-T1.4). The module registers its own handler first, so this one wins.
            services.AddHttpClient(JwksClientName).ConfigurePrimaryHttpMessageHandler(() => Jwks.MessageHandler);

            // The world mints its OIDC tokens with the same keypair (IsolationWorld.SeedAsync).
            services.AddSingleton(Jwks);
        });
    }

    /// <summary>
    /// The member stand-in until 02.3.2's session authenticator: <see cref="UserHeader"/> becomes a
    /// member principal; any other request is left to the next authenticator.
    /// </summary>
    private sealed class MemberHeaderAuthenticator : ICallerAuthenticator
    {
        /// <inheritdoc />
        public ValueTask<AuthenticatedCaller?> AuthenticateAsync(ICallerRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!Guid.TryParse(request.Header(UserHeader), out var user))
            {
                return ValueTask.FromResult<AuthenticatedCaller?>(null);
            }

            var email = request.Header(EmailHeader);

            return ValueTask.FromResult<AuthenticatedCaller?>(new AuthenticatedCaller(
                RequestPrincipal.Member(user, string.IsNullOrEmpty(email) ? null : email)));
        }
    }
}

/// <summary>
/// A stand-in for GitHub's OIDC issuer (02.4.1-T1.4, copied from
/// <c>tests/Tenancy.IntegrationTests/CiTestSupport.cs</c>): a local RSA keypair published as the JWKS the host's
/// <c>HttpGitHubJwksProvider</c> fetches, and a helper that signs workflow tokens with any claims a test wants.
/// </summary>
/// <remarks>
/// The Identity module's <c>IGitHubJwksProvider</c> is internal, so this project cannot implement it; instead
/// <see cref="IsolationApiFactory"/> replaces the JWKS client's primary handler with <see cref="MessageHandler"/>,
/// and the real provider verifies the tokens <see cref="Mint"/> signs. Never a real secret.
/// </remarks>
public sealed class TestJwks
{
    /// <summary>The <c>kid</c> the JWKS publishes and the minted tokens carry.</summary>
    public const string KeyId = "test-key-1";

    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    private readonly RSA _rsa = RSA.Create(2048);

    /// <summary>Creates the keypair and its JWKS document.</summary>
    public TestJwks()
    {
        var parameters = _rsa.ExportParameters(includePrivateParameters: false);
        Document = "{\"keys\":[{\"kty\":\"RSA\",\"kid\":\"" + KeyId + "\",\"use\":\"sig\",\"alg\":\"RS256\",\"n\":\""
            + Base64UrlEncoder.Encode(parameters.Modulus!) + "\",\"e\":\"" + Base64UrlEncoder.Encode(parameters.Exponent!) + "\"}]}";
        MessageHandler = new JwksHandler(() => Document);
    }

    /// <summary>The JWKS document the host's JWKS client fetches.</summary>
    public string Document { get; }

    /// <summary>The primary handler serving <see cref="Document"/> for the JWKS client.</summary>
    public HttpMessageHandler MessageHandler { get; }

    /// <summary>A valid workflow token for <paramref name="repositoryId"/>; <paramref name="tweak"/> changes any claim.</summary>
    public string Mint(long repositoryId, Action<Dictionary<string, object>, TokenOptions>? tweak = null)
    {
        var options = new TokenOptions();
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = "repo:acme/web:ref:refs/heads/main",
            ["jti"] = Guid.NewGuid().ToString("D"),
            ["repository_id"] = repositoryId.ToString(CultureInfo.InvariantCulture),
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

    /// <summary>The knobs a test may turn on a minted token.</summary>
    public sealed class TokenOptions
    {
        /// <summary>The <c>iss</c> claim (the configured GitHub issuer by default).</summary>
        public string Issuer { get; set; } = "https://token.actions.githubusercontent.com";

        /// <summary>The <c>aud</c> claim (the configured GitHub audience by default).</summary>
        public string Audience { get; set; } = IsolationApiFactory.GithubAudience;

        /// <summary>The signing key's <c>kid</c>.</summary>
        public string KeyId { get; set; } = TestJwks.KeyId;

        /// <summary>The signing algorithm.</summary>
        public string Algorithm { get; set; } = SecurityAlgorithms.RsaSha256;

        /// <summary>The <c>nbf</c> offset from now.</summary>
        public TimeSpan NotBeforeOffset { get; set; } = TimeSpan.FromSeconds(-5);

        /// <summary>The <c>exp</c> offset from now.</summary>
        public TimeSpan ExpiresOffset { get; set; } = TimeSpan.FromMinutes(5);
    }

    /// <summary>Serves the JWKS document to the host's JWKS client.</summary>
    private sealed class JwksHandler(Func<string> document) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(document(), Encoding.UTF8, "application/json"),
            });
    }
}
