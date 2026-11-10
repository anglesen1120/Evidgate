using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
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
/// Each refusal asserts the org's row fingerprint is unchanged. A refusal that still wrote a
/// credential row would hand the fork job the very thing this test says it cannot get.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class ForkContextCredentialReachabilityTests(DatabaseFixture fixture) : CatalogueTestBase(fixture)
{
    /// <summary>The write route a <c>ci_producer</c> token's own set covers — the positive control.</summary>
    private static readonly ServedRoute ManifestUpload = new("POST", "/v1/projects/{p}/manifests", "run:create");

    /// <summary>Tenant-level credential creation: a project ingest token.</summary>
    private static readonly ServedRoute IngestTokenCreate = new("POST", "/v1/projects/{p}/ingest-tokens", "ingest_token:write");

    /// <summary>Tenant-level credential creation: a project API token.</summary>
    private static readonly ServedRoute ApiTokenCreate = new("POST", "/v1/projects/{p}/api-tokens", "api_token:write");

    /// <summary>Tenant-level membership creation, the org-scoped write a service token must not reach.</summary>
    private static readonly ServedRoute MembershipCreate = new("POST", "/v1/orgs/{o}/memberships", "membership:write");

    /// <summary>
    /// A real ingest token secret sent as <c>Authorization: Bearer</c> is not a credential: the
    /// request resolves no caller and answers 401 <c>unauthenticated</c>, writing nothing.
    /// </summary>
    /// <remarks>
    /// The secret is genuine — exchanging it at <c>POST /v1/ci/token</c> succeeds — so the refusal
    /// is the presentation, not a forged token. No authenticator reads an ingest token; only the
    /// minted JWT of the exchange authenticates a service caller.
    /// </remarks>
    [Fact]
    public async Task IngestToken_AsBearer_Is401Unauthenticated()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var adminUserId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);

        await using var factory = CiProducerJwtFactory.Start(
            Fixture,
            CiKeyMaterial.Create(),
            org,
            project,
            adminUserId,
            $"admin-{adminUserId:N}@example.test");
        using var client = factory.CreateClient();

        var ingestToken = await CiProducerScopeTests.CreateIngestTokenAsync(client, project);
        var context = RouteRequestContext.Fresh(org, project);

        var before = await OrgRowCounter.CountAsync(Fixture, org, Token);
        using var refused = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(ManifestUpload, context) with { Bearer = ingestToken },
            Token);
        var after = await OrgRowCounter.CountAsync(Fixture, org, Token);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RouteProbe.CodeAsync(refused, Token)).ShouldBe(ApiErrorCode.Unauthenticated);
        after.ShouldBe(before, "an ingest token presented as a bearer must not write anything");

        var minted = await CiProducerScopeTests.ExchangeAsync(client, ingestToken);
        minted.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// A real minted <c>ci_producer</c> JWT asking for a tenant-level credential is 403
    /// <c>forbidden</c> on every such route, and no row of the org changes.
    /// </summary>
    /// <remarks>
    /// The token authenticates on the route its set covers, so the denials are the permission
    /// evaluator's verdict and not a rejected credential. The refused routes are the ingest-token
    /// create, the API-token create and the membership invite — the three writes that hand out what
    /// a fork job could keep.
    /// </remarks>
    [Fact]
    public async Task MintedCiProducer_OnCredentialCreation_Is403Forbidden()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var adminUserId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);

        await using var factory = CiProducerJwtFactory.Start(
            Fixture,
            CiKeyMaterial.Create(),
            org,
            project,
            adminUserId,
            $"admin-{adminUserId:N}@example.test");
        using var client = factory.CreateClient();

        var jwt = await CiProducerScopeTests.MintAsync(client, project);
        var context = RouteRequestContext.Fresh(org, project);

        var before = await OrgRowCounter.CountAsync(Fixture, org, Token);
        await AssertForbiddenAsync(client, context, jwt, IngestTokenCreate);
        await AssertForbiddenAsync(client, context, jwt, ApiTokenCreate);
        await AssertForbiddenAsync(client, context, jwt, MembershipCreate);
        var after = await OrgRowCounter.CountAsync(Fixture, org, Token);

        after.ShouldBe(before, "a ci_producer token must not write a tenant credential row");

        // Positive control: the same token on the route its own set covers is neither 401 nor 403,
        // so the refusals above are authorization verdicts rather than an unusable credential.
        using var allowed = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(ManifestUpload, context) with { Bearer = jwt },
            Token);
        allowed.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized, "the minted token must authenticate");
        allowed.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden, "run:create is in the ci_producer set");
    }

    /// <summary>
    /// A fork job that presents no credential at all cannot upload a manifest: the route answers
    /// 401 <c>unauthenticated</c> before a byte of the body is read, and nothing is written.
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

        var before = await OrgRowCounter.CountAsync(Fixture, org, Token);
        using var refused = await RouteProbe.SendAsync(client, RouteRequests.Build(ManifestUpload, context), Token);
        var after = await OrgRowCounter.CountAsync(Fixture, org, Token);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RouteProbe.CodeAsync(refused, Token)).ShouldBe(ApiErrorCode.Unauthenticated);
        after.ShouldBe(before, "a fork job without a credential must not write anything");

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
