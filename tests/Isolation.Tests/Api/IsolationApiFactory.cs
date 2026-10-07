using System.Security.Cryptography;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
            services.AddSingleton<ICallerAuthenticator, MemberHeaderAuthenticator>());
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
