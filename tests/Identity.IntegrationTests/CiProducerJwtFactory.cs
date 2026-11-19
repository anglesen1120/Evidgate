using System.Security.Cryptography;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Identity.IntegrationTests;

/// <summary>The CI key material <see cref="CiProducerJwtFactory"/> configures, fixed per test run.</summary>
internal sealed class CiKeyMaterial
{
    /// <summary>The minted token's <c>iss</c>.</summary>
    internal const string Issuer = "evidgate-test";

    /// <summary>The minted token's <c>aud</c>.</summary>
    internal const string Audience = "evidgate-test-aud";

    /// <summary>The GitHub OIDC audience the host expects.</summary>
    internal const string GithubAudience = "evidgate-github";

    private CiKeyMaterial(string signingKey, string pepper)
    {
        SigningKey = signingKey;
        Pepper = pepper;
    }

    /// <summary>Base64 HS256 signing key (32 bytes).</summary>
    internal string SigningKey { get; }

    /// <summary>Base64 current pepper (32 bytes).</summary>
    internal string Pepper { get; }

    /// <summary>Fresh key material.</summary>
    internal static CiKeyMaterial Create() => new(
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
}

/// <summary>
/// A host that runs the real CI authentication path: the <c>EVIDGATE:Ci:*</c> key material
/// is configured (so the exchange mints and the authenticator validates), and a stand-in
/// member is published only for requests WITHOUT a bearer — so the real
/// <c>CiProducerAuthenticator</c> owns a bearer request, which the
/// <see cref="IdentityApiFactory"/> middleware (it always publishes a principal) would
/// otherwise shadow.
/// </summary>
internal sealed class CiProducerJwtFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly CiKeyMaterial _keys;
    private readonly Guid _orgId;
    private readonly Guid _projectId;
    private readonly Guid _userId;
    private readonly string _email;

    private CiProducerJwtFactory(
        string connectionString,
        CiKeyMaterial keys,
        Guid orgId,
        Guid projectId,
        Guid userId,
        string email)
    {
        _connectionString = connectionString;
        _keys = keys;
        _orgId = orgId;
        _projectId = projectId;
        _userId = userId;
        _email = email;
    }

    /// <summary>A host over <paramref name="fixture"/>'s database, configured for CI authentication.</summary>
    internal static CiProducerJwtFactory Start(
        DatabaseFixture fixture,
        CiKeyMaterial keys,
        Guid orgId,
        Guid projectId,
        Guid userId,
        string email)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(keys);

        return new CiProducerJwtFactory(fixture.AppConnectionString, keys, orgId, projectId, userId, email);
    }

    /// <summary>
    /// The <c>perm</c> claim of a minted JWT, read without verifying the signature: the
    /// test asserts what the exchange minted, not that the host can validate it — the
    /// bearer request itself proves that.
    /// </summary>
    internal static IReadOnlyList<string> PermissionClaim(string jwt)
    {
        jwt.ShouldNotBeNullOrWhiteSpace();

        var segments = jwt.Split('.');
        segments.Length.ShouldBe(3, "a JWT has three dot-separated segments");

        var payload = segments[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

        using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
        return [.. document.RootElement.GetProperty("perm").EnumerateArray().Select(claim => claim.GetString()!)];
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Database:ConnectionString"] = _connectionString,
                ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30",
                ["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00",
                ["EVIDGATE:Identity:CustomRolesEnabled"] = "false",
                ["EVIDGATE:Ci:SigningKey"] = _keys.SigningKey,
                ["EVIDGATE:Ci:Issuer"] = CiKeyMaterial.Issuer,
                ["EVIDGATE:Ci:Audience"] = CiKeyMaterial.Audience,
                ["EVIDGATE:Ci:Pepper"] = _keys.Pepper,
                ["EVIDGATE:Ci:PepperVersion"] = "2",
                ["EVIDGATE:Ci:GithubOidcAudience"] = CiKeyMaterial.GithubAudience,
                ["EVIDGATE:Identity:RateLimits:CiExchangeLimit"] = "1000",
            }));

        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new StandInMemberFilter(_orgId, _projectId, _userId, _email)));
    }

    /// <summary>Publishes a fixed tenant and member for requests without a bearer, then runs the app's pipeline.</summary>
    private sealed class StandInMemberFilter(Guid orgId, Guid projectId, Guid userId, string email) : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                // A bearer request is the real ci_producer path: leave the pipeline to authenticate it.
                if (context.Request.Headers.ContainsKey("Authorization"))
                {
                    await nextMiddleware().ConfigureAwait(false);
                    return;
                }

                var tenant = context.RequestServices.GetRequiredService<IRequestTenantContextSetter>();
                using var tenantScope = tenant.Publish(orgId, projectId);

                var principal = context.RequestServices.GetRequiredService<IRequestPrincipalSetter>();
                using var principalScope = principal.Publish(RequestPrincipal.Member(userId, email));

                await nextMiddleware().ConfigureAwait(false);
            });

            next(app);
        };
    }
}

/// <summary>
/// A host that publishes a fixed tenant and a SERVICE principal whose claim set is read
/// from the <see cref="ClaimsHeader"/> of each request, so a matrix of claim sets runs on
/// ONE host instead of one host per claim (02.2.1-T3, P0). The composition is otherwise the
/// production one: the same routes, the same C2 pipeline and the module's real evaluator,
/// exactly as <see cref="IdentityApiFactory"/> composes it — only the principal source
/// differs, because <see cref="IdentityHostSetup.Principal"/> is fixed per host.
/// </summary>
internal sealed class ServicePrincipalProbeFactory : WebApplicationFactory<Program>
{
    /// <summary>The request header carrying the comma-separated claim set the request runs as.</summary>
    internal const string ClaimsHeader = "X-Probe-Claims";

    private readonly string _connectionString;
    private readonly Guid _orgId;
    private readonly Guid _projectId;

    private ServicePrincipalProbeFactory(string connectionString, Guid orgId, Guid projectId)
    {
        _connectionString = connectionString;
        _orgId = orgId;
        _projectId = projectId;
    }

    /// <summary>A host over <paramref name="fixture"/>'s database that reads the claim set per request.</summary>
    internal static ServicePrincipalProbeFactory Start(DatabaseFixture fixture, Guid orgId, Guid projectId)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return new ServicePrincipalProbeFactory(fixture.AppConnectionString, orgId, projectId);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Database:ConnectionString"] = _connectionString,
                ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30",
                ["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00",
                ["EVIDGATE:Identity:CustomRolesEnabled"] = "false",
            }));

        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new HeaderPrincipalFilter(_orgId, _projectId)));
    }

    /// <summary>Publishes the fixed tenant and a service principal built from the claim header.</summary>
    private sealed class HeaderPrincipalFilter(Guid orgId, Guid projectId) : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var tenant = context.RequestServices.GetRequiredService<IRequestTenantContextSetter>();
                using var tenantScope = tenant.Publish(orgId, projectId);

                var claims = context.Request.Headers[ClaimsHeader].ToString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                var principal = context.RequestServices.GetRequiredService<IRequestPrincipalSetter>();
                using var principalScope = principal.Publish(RequestPrincipal.Service(projectId, claims));

                await nextMiddleware().ConfigureAwait(false);
            });

            next(app);
        };
    }
}
