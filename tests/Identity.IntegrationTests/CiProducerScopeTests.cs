using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// AC4 / 02.4.1 AC7 (the hand-over to 02.2.1-T3): a <c>ci_producer</c> service identity is
/// evaluated through the same C2 behaviour as a member — its token claims are bounded by
/// the catalogue, it may call only the routes its permission set covers, and a token
/// scoped to one project is refused on another project's request.
/// </summary>
/// <remarks>
/// <para>
/// The principal is <see cref="RequestPrincipal.Service"/> carrying exactly
/// <see cref="RoleMatrixFixture.PermissionsOf"/> of <c>ci_producer</c> (the 00 §2-derived
/// expectation, never the catalogue file), with the tenant published for the seeded org and
/// project. The route set is the served table, so a new endpoint is covered the moment it
/// ships.
/// </para>
/// <para>
/// <see cref="Real_Minted_CiProducer_Token_Is_403_On_Membership_Write"/> exercises the same
/// rule through a REAL minted token: an ingest token created on the admin route, exchanged
/// at <c>POST /v1/ci/token</c>, presented as <c>Authorization: Bearer</c>. The stand-in
/// member of <see cref="CiProducerJwtFactory"/> is published only for requests without a
/// bearer, so the real <c>CiProducerAuthenticator</c> owns the bearer request — the
/// <see cref="IdentityApiFactory"/> middleware always publishes a principal and would
/// therefore never authenticate one.
/// </para>
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class CiProducerScopeTests(DatabaseFixture fixture) : CatalogueTestBase(fixture)
{
    /// <summary>The explicit route of the hand-over: a <c>ci_producer</c> calling <c>membership:write</c>.</summary>
    private static readonly ServedRoute MembershipWrite = new("POST", "/v1/orgs/{o}/memberships", "membership:write");

    /// <summary>The one served route whose spec code is in the <c>ci_producer</c> set (<c>run:create</c>).</summary>
    private static readonly ServedRoute ManifestUpload = new("POST", "/v1/projects/{p}/manifests", "run:create");

    /// <summary>AC4: a valid <c>ci_producer</c> token calling <c>membership:write</c> is 403 <c>forbidden</c>.</summary>
    [Fact]
    public async Task Membership_Write_Is_403()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var matrix = RoleMatrixFixture.Load();

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                ProjectId: project,
                Principal: RequestPrincipal.Service(project, matrix.PermissionsOf("ci_producer"))));
        using var client = factory.CreateClient();

        var served = ServedRouteTable.From(factory.Services);
        var route = served.Single(candidate => candidate.Method == MembershipWrite.Method && candidate.Template == MembershipWrite.Template);

        using var response = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(route, RouteRequestContext.Fresh(org, project)),
            Token);

        var violation = await RouteProbe.ForbiddenViolationAsync(route, response, Token);
        violation.ShouldBeNull();
    }

    /// <summary>
    /// AC4: over every served route that declares a catalogue code, a <c>ci_producer</c>
    /// token is refused with 403 <c>forbidden</c> unless the code is in its set — and the
    /// one route it does cover is not refused. Both branches are non-empty.
    /// </summary>
    [Fact]
    public async Task Every_Served_Route_Respects_The_CiProducer_Set()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var matrix = RoleMatrixFixture.Load();
        var ciProducer = matrix.PermissionsOf("ci_producer").ToHashSet(StringComparer.Ordinal);

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                ProjectId: project,
                Principal: RequestPrincipal.Service(project, matrix.PermissionsOf("ci_producer"))));
        using var client = factory.CreateClient();

        var served = ServedRouteTable.From(factory.Services);
        var withCode = served
            .Where(route => route.Permission is not (ServedRouteTable.NoPermission or ServedRouteTable.Authenticated))
            .ToList();
        withCode.ShouldNotBeEmpty("the host serves routes that declare a catalogue permission");

        var context = RouteRequestContext.Fresh(org, project);
        var violations = new List<string>();
        var granted = 0;
        var denied = 0;

        foreach (var route in withCode)
        {
            using var response = await RouteProbe.SendAsync(client, RouteRequests.Build(route, context), Token);
            if (ciProducer.Contains(route.Permission))
            {
                granted++;
                var violation = await RouteProbe.NotForbiddenViolationAsync(route, response, Token);
                if (violation is not null)
                {
                    violations.Add(violation);
                }
            }
            else
            {
                denied++;
                var violation = await RouteProbe.ForbiddenViolationAsync(route, response, Token);
                if (violation is not null)
                {
                    violations.Add(violation);
                }
            }
        }

        granted.ShouldBeGreaterThan(0, "the ci_producer set must cover at least one served route");
        denied.ShouldBeGreaterThan(0, "the ci_producer set must not cover every served route");
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// FR-02-2 / AC3: a token scoped to project A calling a request that runs as project B
    /// (same org) is 403 <c>project_scope_mismatch</c> on every route its permission set
    /// covers, and no row of the org changes.
    /// </summary>
    [Fact]
    public async Task CrossProjectDenied()
    {
        var (org, projectB) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var projectA = Guid.CreateVersion7();
        var matrix = RoleMatrixFixture.Load();
        var ciProducer = matrix.PermissionsOf("ci_producer").ToHashSet(StringComparer.Ordinal);

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                ProjectId: projectB,
                Principal: RequestPrincipal.Service(projectA, matrix.PermissionsOf("ci_producer"))));
        using var client = factory.CreateClient();

        var served = ServedRouteTable.From(factory.Services);
        var scoped = served.Where(route => ciProducer.Contains(route.Permission)).ToList();
        scoped.ShouldNotBeEmpty("the ci_producer set must cover at least one served route");

        var context = RouteRequestContext.Fresh(org, projectB);
        var before = await OrgRowCounter.CountAsync(Fixture, org, Token);

        var violations = new List<string>();
        foreach (var route in scoped)
        {
            using var response = await RouteProbe.SendAsync(client, RouteRequests.Build(route, context), Token);
            var violation = await RouteProbe.DeniedViolationAsync(route, response, ApiErrorCode.ProjectScopeMismatch, Token);
            if (violation is not null)
            {
                violations.Add(violation);
            }
        }

        var after = await OrgRowCounter.CountAsync(Fixture, org, Token);
        after.ShouldBe(before, "a scope-mismatched write must not change the org's rows");
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// W7-SEC-06 / 02.6.2 AC6: a token claiming a code outside the service ceiling — a
    /// staff code, a member code or a string the catalogue does not declare — is malformed,
    /// so it holds nothing, not even the <c>run:create</c> it also claims.
    /// </summary>
    [Fact]
    public async Task OverReach_Claims_Are_403_Forbidden()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var matrix = RoleMatrixFixture.Load();
        var held = matrix.PermissionsOf("ci_producer");
        var context = RouteRequestContext.Fresh(org, project);

        foreach (var overreach in new[] { "billing:read", "support:grant", "membership:write", "not_a:catalogue_code" })
        {
            await using var factory = IdentityApiFactory.Start(
                Fixture,
                new IdentityHostSetup(
                    org,
                    ProjectId: project,
                    Principal: RequestPrincipal.Service(project, [.. held, overreach])));
            using var client = factory.CreateClient();

            var served = ServedRouteTable.From(factory.Services);
            var route = served.Single(candidate => candidate.Method == ManifestUpload.Method && candidate.Template == ManifestUpload.Template);

            using var response = await RouteProbe.SendAsync(client, RouteRequests.Build(route, context), Token);
            var violation = await RouteProbe.ForbiddenViolationAsync(route, response, Token);
            violation.ShouldBeNull(
                $"a token claiming '{overreach}' must be refused (403 forbidden) even though it also holds run:create");
        }
    }

    /// <summary>
    /// The same rule through a REAL minted token: an ingest token created on the admin
    /// route, exchanged at <c>POST /v1/ci/token</c>, presented as a bearer. The minted
    /// <c>ci_producer</c> calling <c>POST /v1/orgs/{o}/memberships</c> is 403
    /// <c>forbidden</c>.
    /// </summary>
    [Fact]
    public async Task Real_Minted_CiProducer_Token_Is_403_On_Membership_Write()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var adminUserId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var keys = CiKeyMaterial.Create();

        await using var factory = CiProducerJwtFactory.Start(
            Fixture,
            keys,
            org,
            project,
            adminUserId,
            $"admin-{adminUserId:N}@example.test");
        using var client = factory.CreateClient();

        // 1. Mint an ingest token through the real admin route (the stand-in member is a tenant_admin).
        using var created = await client.PostAsync(
            $"/v1/projects/{project}/ingest-tokens",
            new StringContent("""{"name":"probe"}""", Encoding.UTF8, "application/json"),
            Token);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var secret = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("secret").GetString();
        secret.ShouldNotBeNullOrWhiteSpace();

        // 2. Exchange it for a real ci_producer JWT.
        using var exchanged = await client.PostAsync(
            "/v1/ci/token",
            new StringContent($$"""{"ingest_token":"{{secret}}"}""", Encoding.UTF8, "application/json"),
            Token);
        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK);
        var jwt = (await exchanged.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("token").GetString();
        jwt.ShouldNotBeNullOrWhiteSpace();

        // 3. The minted token calling membership:write is 403 forbidden.
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/v1/orgs/{org}/memberships")
        {
            Content = new StringContent("""{"email":"probe@example.test","roles":[]}""", Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        message.Headers.TryAddWithoutValidation("Idempotency-Key", "probe-jwt-1");
        using var response = await client.SendAsync(message, Token);

        var violation = await RouteProbe.ForbiddenViolationAsync(MembershipWrite, response, Token);
        violation.ShouldBeNull();
    }
}

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

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Database:ConnectionString"] = _connectionString,
                ["EVIDGATE:Identity:CustomRolesEnabled"] = "false",
                ["EVIDGATE:Ci:SigningKey"] = _keys.SigningKey,
                ["EVIDGATE:Ci:Issuer"] = CiKeyMaterial.Issuer,
                ["EVIDGATE:Ci:Audience"] = CiKeyMaterial.Audience,
                ["EVIDGATE:Ci:Pepper"] = _keys.Pepper,
                ["EVIDGATE:Ci:PepperVersion"] = "2",
                ["EVIDGATE:Ci:GithubOidcAudience"] = CiKeyMaterial.GithubAudience,
                ["EVIDGATE:Ci:ExchangeBurstLimit"] = "1000",
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
