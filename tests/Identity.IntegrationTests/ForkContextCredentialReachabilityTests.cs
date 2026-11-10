using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// What a fork pull request's job can reach with the credentials that must never leave the
/// trusted side of the pipeline: an ingest token presented as an API credential, and a minted
/// <c>ci_producer</c> token attempting to mint tenant-level credentials.
/// </summary>
/// <remarks>
/// <para>
/// Every request goes through the production routes and the C2 pipeline, so the status and the
/// <c>error.code</c> are the ones a deployed host answers: the ingest-token credential is not a
/// bearer, and a <c>ci_producer</c> token holds only its own catalogue set.
/// </para>
/// <para>
/// Each refusal asserts that the CREDENTIAL rows of the affected orgs are unchanged — ingest and
/// API tokens, memberships (an invite is a pending membership) and the CI issue cache. Deliberately
/// not the whole-org fingerprint (<see cref="OrgRowCounter"/>): a refusal MAY legitimately record a
/// <c>security_event</c> row, so pinning its absence would fail the day that row is added (S356
/// review F6).
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class ForkContextCredentialReachabilityTests(DatabaseFixture fixture) : CatalogueTestBase(fixture)
{
    /// <summary>The write route a <c>ci_producer</c> token's own set covers — the positive control.</summary>
    private static readonly ServedRoute ManifestUpload = new("POST", "/v1/projects/{p}/manifests", "run:create");

    /// <summary>Tenant-level credential creation: a project ingest token.</summary>
    private static readonly ServedRoute IngestTokenCreate = new("POST", "/v1/projects/{p}/ingest-tokens", "ingest_token:write");

    /// <summary>Revocation of an ingest token: immediate and final (02.4.2 AC4).</summary>
    private static readonly ServedRoute IngestTokenRevoke = new("DELETE", "/v1/ingest-tokens/{t}", "ingest_token:write");

    /// <summary>Tenant-level credential creation: a project API token.</summary>
    private static readonly ServedRoute ApiTokenCreate = new("POST", "/v1/projects/{p}/api-tokens", "api_token:write");

    /// <summary>Tenant-level membership creation, the org-scoped write a service token must not reach.</summary>
    private static readonly ServedRoute MembershipCreate = new("POST", "/v1/orgs/{o}/memberships", "membership:write");

    /// <summary>Base64 of a fresh 32-byte invite pepper for this process (00 §5.7 S3: no literal key in a test).</summary>
    private static class InvitePepper
    {
        /// <summary>One value per process, above <c>InviteSecrets.MinKeyBytes</c> (32).</summary>
        internal static string Value { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>
    /// A real ingest token secret sent as <c>Authorization: Bearer</c> is not a credential: the
    /// request resolves no caller and answers 401 <c>unauthenticated</c>, writing no credential row —
    /// whether the route names the token's own project or another project of the same org.
    /// </summary>
    /// <remarks>
    /// The secret is genuine — exchanging it at <c>POST /v1/ci/token</c> succeeds — so the refusal
    /// is the presentation, not a forged token. No authenticator reads an ingest token; only the
    /// minted JWT of the exchange authenticates a service caller.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IngestToken_AsBearer_OnManifestUpload_Is401Unauthenticated(bool namesAnotherProject)
    {
        var started = await StartCiHostAsync();
        await using var host = started.Host;
        using var client = started.Client;

        var ingestToken = await CiProducerScopeTests.CreateIngestTokenAsync(client, started.Project);
        var context = RouteRequestContext.Fresh(started.Org, await AimedProjectAsync(started.Org, started.Project, namesAnotherProject));

        var before = await CredentialRowCountAsync(started.Org);
        using var refused = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(ManifestUpload, context) with { Bearer = ingestToken },
            Token);
        var after = await CredentialRowCountAsync(started.Org);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RouteProbe.CodeAsync(refused, Token)).ShouldBe(ApiErrorCode.Unauthenticated);
        after.ShouldBe(before, "an ingest token presented as a bearer must not write a credential row");

        var minted = await CiProducerScopeTests.ExchangeAsync(client, ingestToken);
        minted.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// A real minted <c>ci_producer</c> JWT asking for a project credential is 403 <c>forbidden</c> —
    /// on its own project and on another project of the same org — and no credential row appears.
    /// </summary>
    /// <remarks>
    /// The token authenticates on the route its set covers, so the denial is the permission
    /// evaluator's verdict and not a rejected credential. Two same-route controls show that: the
    /// manifest upload accepts the JWT, and the admin caller (the host's stand-in
    /// <c>tenant_admin</c>) still mints an API token. The membership half of the denial set moved to
    /// <see cref="MintedCiProducer_OnForeignOrgMembership_Is404NotFound"/>: the own-org membership
    /// request is already pinned by
    /// <see cref="CiProducerScopeTests.Real_Minted_CiProducer_Token_Is_403_On_Membership_Write"/>.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MintedCiProducer_OnTokenCreation_Is403Forbidden(bool namesAnotherProject)
    {
        var started = await StartCiHostAsync();
        await using var host = started.Host;
        using var client = started.Client;

        var jwt = await CiProducerScopeTests.MintAsync(client, started.Project);
        var context = RouteRequestContext.Fresh(started.Org, await AimedProjectAsync(started.Org, started.Project, namesAnotherProject));

        var before = await CredentialRowCountAsync(started.Org);
        await AssertForbiddenAsync(client, context, jwt, IngestTokenCreate);
        await AssertForbiddenAsync(client, context, jwt, ApiTokenCreate);
        var after = await CredentialRowCountAsync(started.Org);

        after.ShouldBe(before, "a ci_producer token must not write a credential row");

        // Control 1: the org's own tenant_admin mints an API token on the very route the JWT was
        // refused on, so the 403 is the token's permission set and not the route's.
        var ownContext = RouteRequestContext.Fresh(started.Org, started.Project);
        using var allowed = await RouteProbe.SendAsync(client, RouteRequests.Build(ApiTokenCreate, ownContext), Token);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, "api_token:write must still mint a project API token");

        // Control 2: the same JWT on the route its own set covers is neither 401 nor 403 and does
        // not fail the server.
        using var manifest = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(ManifestUpload, ownContext) with { Bearer = jwt },
            Token);
        manifest.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized, "the minted token must authenticate");
        manifest.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden, "run:create is in the ci_producer set");
        ((int)manifest.StatusCode).ShouldBeLessThan(500, "an authorized manifest upload must not fail the server");
    }

    /// <summary>
    /// A minted <c>ci_producer</c> token naming ANOTHER org on the membership route is answered 404
    /// <c>not_found</c>, and no credential row appears in either org.
    /// </summary>
    /// <remarks>
    /// <c>TenantResolver.ResolveService</c> refuses a requested org the claims do not fix before the
    /// pipeline reaches the permission gate, and it answers <c>not_found</c> rather than <c>forbidden</c>
    /// so a service token cannot probe which org ids are real. The org's own <c>tenant_admin</c>
    /// invites on the same route, so the answer is the credential's, not the route's — which is why the
    /// host carries an invite pepper (<c>InviteMember.cs:135-137</c> answers 503 without one).
    /// </remarks>
    [Fact]
    public async Task MintedCiProducer_OnForeignOrgMembership_Is404NotFound()
    {
        var started = await StartCiHostAsync();
        await using var host = started.Host;
        using var client = started.Client;

        var jwt = await CiProducerScopeTests.MintAsync(client, started.Project);
        var foreignOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var context = RouteRequestContext.Fresh(started.Org, started.Project);

        var before = await CredentialRowCountAsync(started.Org, foreignOrg);
        using var refused = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(MembershipCreate, context) with
            {
                Url = $"/v1/orgs/{foreignOrg}/memberships",
                Bearer = jwt,
            },
            Token);
        var after = await CredentialRowCountAsync(started.Org, foreignOrg);

        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a service token cannot name a foreign org");
        (await RouteProbe.CodeAsync(refused, Token)).ShouldBe(ApiErrorCode.NotFound);
        after.ShouldBe(before, "a refused membership invite must not write a membership row");

        // Control: the org's own tenant_admin invites on the same route with the same body.
        using var allowed = await RouteProbe.SendAsync(client, RouteRequests.Build(MembershipCreate, context), Token);
        allowed.StatusCode.ShouldBe(HttpStatusCode.Created, "membership:write must still invite on this route");
    }

    /// <summary>
    /// A REVOKED project ingest secret sent as <c>Authorization: Bearer</c> is still 401
    /// <c>unauthenticated</c> and writes no credential row.
    /// </summary>
    /// <remarks>
    /// Revocation is answered at the exchange (<c>token_revoked</c>), and no authenticator reads an
    /// ingest secret, so a revoked secret is refused exactly like a live one. The revoke route is the
    /// existing admin one, as <c>CiAuthTests.IngestToken_Revoke_IsImmediate</c> uses it.
    /// </remarks>
    [Fact]
    public async Task RevokedIngestToken_AsBearer_Is401Unauthenticated()
    {
        var started = await StartCiHostAsync();
        await using var host = started.Host;
        using var client = started.Client;

        var context = RouteRequestContext.Fresh(started.Org, started.Project);
        using var created = await RouteProbe.SendAsync(client, RouteRequests.Build(IngestTokenCreate, context), Token);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(Token);
        var tokenId = createdBody.GetProperty("token").GetProperty("id").GetGuid();
        var secret = createdBody.GetProperty("secret").GetString()!;
        secret.ShouldStartWith("egt_");

        var revokeContext = RouteRequestContext.Fresh(started.Org, started.Project) with { IngestTokenId = tokenId };
        using var revoked = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(IngestTokenRevoke, revokeContext) with { IdempotencyKey = $"revoke-{tokenId:N}" },
            Token);
        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent, "revocation is immediate (02.4.2 AC4)");

        var before = await CredentialRowCountAsync(started.Org);
        using var refused = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(ManifestUpload, context) with { Bearer = secret },
            Token);
        var after = await CredentialRowCountAsync(started.Org);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RouteProbe.CodeAsync(refused, Token)).ShouldBe(ApiErrorCode.Unauthenticated);
        after.ShouldBe(before, "a revoked secret presented as a bearer must not write a credential row");
    }

    /// <summary>
    /// The exchange mints a token that is still valid and lives at most the documented hour
    /// (<c>CiAuthOptions.TokenLifetimeMinutes</c> = 60): a token a fork job could keep past its job
    /// would outlive the pipeline this guarantee is about.
    /// </summary>
    /// <remarks>
    /// Read from the payload, unverified: the bearer request on the manifest route above is what
    /// proves the host validates it, and <see cref="ExpiredMintedCiProducer_Is401Unauthenticated"/>
    /// proves the hour is enforced (the module's replaceable <c>TimeProvider</c> is the seam it moves).
    /// </remarks>
    [Fact]
    public async Task MintedCiProducer_Lifetime_IsAtMostOneHour_AndNotExpired()
    {
        var started = await StartCiHostAsync();
        await using var host = started.Host;
        using var client = started.Client;

        var jwt = await CiProducerScopeTests.MintAsync(client, started.Project);
        var issued = JwtSeconds(jwt, "iat");
        var expires = JwtSeconds(jwt, "exp");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        expires.ShouldBeGreaterThan(now, "the minted token must not be expired when the job receives it");
        expires.ShouldBeGreaterThan(issued, "exp must follow iat");
        (expires - issued).ShouldBeLessThanOrEqualTo(3600, "the minted token lives at most one hour");
    }

    /// <summary>
    /// A minted <c>ci_producer</c> token past its <c>exp</c> is 401 <c>unauthenticated</c> and writes no
    /// credential row: the one hour is enforced by the host, not merely declared in the claim.
    /// </summary>
    /// <remarks>
    /// The clock is the module's own replaceable seam — <c>IdentityModule</c> registers
    /// <c>TryAddSingleton(TimeProvider.System)</c> and <c>CiProducerTokens</c> mints and validates through
    /// it (<c>CiProducerTokens.cs:76,139-160</c>) — so the production authenticator runs unchanged while the
    /// test moves time past <c>exp</c>: no re-signed token, no wait. The fresh token is accepted on the
    /// manifest route first, so the later refusal is the lifetime and not the credential.
    /// </remarks>
    [Fact]
    public async Task ExpiredMintedCiProducer_Is401Unauthenticated()
    {
        var clock = new ShiftedClock(TimeProvider.System);
        var started = await StartCiHostAsync(clock);
        await using var host = started.Host;
        using var client = started.Client;

        var jwt = await CiProducerScopeTests.MintAsync(client, started.Project);
        var context = RouteRequestContext.Fresh(started.Org, started.Project);

        using var fresh = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(ManifestUpload, context) with { Bearer = jwt },
            Token);
        fresh.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized, "the fresh token must authenticate");

        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));

        var before = await CredentialRowCountAsync(started.Org);
        using var refused = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(ManifestUpload, context) with
            {
                Bearer = jwt,
                IdempotencyKey = $"expired-{Guid.CreateVersion7():N}",
            },
            Token);
        var after = await CredentialRowCountAsync(started.Org);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "a token past exp must not authenticate");
        (await RouteProbe.CodeAsync(refused, Token)).ShouldBe(ApiErrorCode.Unauthenticated);
        after.ShouldBe(before, "an expired token must not write a credential row");
    }

    /// <summary>A fork job that presents no credential at all cannot upload a manifest: the route answers
    /// 401 <c>unauthenticated</c> before a byte of the body is read, and no credential row is written.
    /// </summary>
    /// <remarks>
    /// The host publishes the tenant but no caller, which is the state a request without any
    /// credential reaches. The positive control publishes a caller with the same composition and
    /// the same request, so only the credential differs.
    /// </remarks>
    [Fact]
    public async Task Manifest_WithoutACredential_Is401Unauthenticated()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var context = RouteRequestContext.Fresh(org, project);

        await using var anonymous = IdentityApiFactory.Start(Fixture, new IdentityHostSetup(org, ProjectId: project));
        using var client = anonymous.CreateClient();

        var before = await CredentialRowCountAsync(org);
        using var refused = await RouteProbe.SendAsync(client, RouteRequests.Build(ManifestUpload, context), Token);
        var after = await CredentialRowCountAsync(org);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RouteProbe.CodeAsync(refused, Token)).ShouldBe(ApiErrorCode.Unauthenticated);
        after.ShouldBe(before, "a fork job without a credential must not write a credential row");

        await using var authenticated = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                ProjectId: project,
                Principal: RequestPrincipal.Service(project, RoleMatrixFixture.Load().PermissionsOf("ci_producer"))));
        using var allowedClient = authenticated.CreateClient();
        using var allowed = await RouteProbe.SendAsync(
            allowedClient,
            RouteRequests.Build(ManifestUpload, context),
            Token);
        allowed.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized, "a resolved caller must not be refused as unauthenticated");
        allowed.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden, "run:create is in the ci_producer set");
        ((int)allowed.StatusCode).ShouldBeLessThan(500, "an authorized manifest upload must not fail the server");
    }

    /// <summary>A clock the test moves forward, over the host's own — the module's replaceable <c>TimeProvider</c>.</summary>
    private sealed class ShiftedClock(TimeProvider origin) : TimeProvider
    {
        private TimeSpan _offset;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => origin.GetUtcNow() + _offset;

        /// <summary>Moves the clock forward by <paramref name="by"/>.</summary>
        internal void Advance(TimeSpan by) => _offset += by;
    }

    /// <summary>
    /// The CI host over a fresh org, its project and an active <c>tenant_admin</c> member — the caller a
    /// request WITHOUT a bearer runs as. The host also carries a random invite pepper, without which the
    /// membership route answers 503 <c>unavailable</c> (<c>InviteMember.cs:135-137</c>), and, when
    /// <paramref name="clock"/> is given, that clock in place of the module's replaceable one. The caller
    /// owns the host's disposal.
    /// </summary>
    private async Task<(WebApplicationFactory<Program> Host, HttpClient Client, Guid Org, Guid Project)> StartCiHostAsync(
        TimeProvider? clock = null)
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var adminUserId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);

        var host = CiProducerJwtFactory
            .Start(Fixture, CiKeyMaterial.Create(), org, project, adminUserId, $"admin-{adminUserId:N}@example.test")
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["EVIDGATE:Invite:Pepper"] = InvitePepper.Value,
                    }));

                if (clock is { } shifted)
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<TimeProvider>();
                        services.AddSingleton<TimeProvider>(shifted);
                    });
                }
            });

        return (host, host.CreateClient(), org, project);
    }

    /// <summary>
    /// The project a refused route names: the host's own project, or a second project of the SAME
    /// org when the case is about the route id rather than the tenant.
    /// </summary>
    private async Task<Guid> AimedProjectAsync(Guid org, Guid project, bool namesAnotherProject) =>
        namesAnotherProject
            ? (await Fixture.SeedProjectAsync(orgId: org, cancellationToken: Token)).ProjectId
            : project;

    /// <summary>
    /// The credential rows a fork job must never cause, over <paramref name="orgIds"/>: ingest and
    /// API tokens, memberships and the CI issue cache. A refusal may write a <c>security_event</c> or
    /// an audit row, so those tables are deliberately NOT counted (S356 review F6).
    /// </summary>
    private async Task<long> CredentialRowCountAsync(params Guid[] orgIds)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select (select count(*) from public.ingest_token where org_id = any(@orgs::uuid[]))
                 + (select count(*) from public.api_token where org_id = any(@orgs::uuid[]))
                 + (select count(*) from public.membership where org_id = any(@orgs::uuid[]))
                 + (select count(*) from public.ci_token_issue where org_id = any(@orgs::uuid[]))
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("orgs", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = orgIds });

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A numeric claim (<c>iat</c>, <c>exp</c>) of a minted token, read without verifying the
    /// signature — the same unverified payload decode as <see cref="CiProducerJwtFactory.PermissionClaim"/>,
    /// which exposes only <c>perm</c>.
    /// </summary>
    private static long JwtSeconds(string jwt, string name)
    {
        var segments = jwt.Split('.');
        segments.Length.ShouldBe(3, "a JWT has three dot-separated segments");

        var payload = segments[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

        using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
        return document.RootElement.GetProperty(name).GetInt64();
    }

    /// <summary>Sends <paramref name="route"/> with <paramref name="jwt"/> and asserts the 403 <c>forbidden</c> envelope.</summary>
    private static async Task AssertForbiddenAsync(
        HttpClient client,
        RouteRequestContext context,
        string jwt,
        ServedRoute route)
    {
        using var response = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(route, context) with { Bearer = jwt },
            Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{route} must refuse a ci_producer token");
        (await RouteProbe.CodeAsync(response, Token)).ShouldBe(ApiErrorCode.Forbidden, $"{route} must answer forbidden");
    }
}
