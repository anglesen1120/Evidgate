using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// 02.2.1-T2.1 (AC3, AC4, AC7): every refusal the C2 authorization stage can answer
/// reaches the client as the 00 §9.2 envelope with the RIGHT <c>error.code</c> — the
/// whole point of returning a <see cref="PermissionDecision"/> instead of a bool —
/// and as 403, never as 401 or 500. The route is the shipping <c>GET /v1/permissions</c>
/// (<c>role:read</c>) so the assertion is about the pipeline, not about a handler, and
/// the cases that need no membership rows run against the module's REAL evaluator.
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class DenyPathTests(DatabaseFixture fixture) : HarnessTest(fixture)
{
    /// <summary>The project the token of a service principal is scoped to.</summary>
    private static readonly Guid ScopedProject = Guid.Parse("44444444-4444-7444-8444-444444444444");

    /// <summary>
    /// A host that resolved a tenant but no caller (02.6.2-T1.1 publishes both or
    /// neither): the fail-closed answer, with the module's real evaluator registered.
    /// </summary>
    [Fact]
    public async Task No_Resolved_Caller_Is_403_Forbidden()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, new IdentityHostSetup(org));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/permissions", Token);

        await AssertDeniedAsync(response, ApiErrorCode.Forbidden);
    }

    /// <summary>
    /// A member of the org with no active membership row: the evaluator's
    /// <c>no_membership</c> verdict survives the pipeline (02.1.2 AC2, 02.3.1 AC3).
    /// The member road of the real evaluator is <c>EvaluatorTests</c>'; this case pins
    /// the mapping decision → envelope.
    /// </summary>
    [Fact]
    public async Task No_Membership_Verdict_Reaches_The_Envelope()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                Principal: RequestPrincipal.Member(Guid.CreateVersion7()),
                Evaluator: new FixedPermissionEvaluator(ApiErrorCode.NoMembership)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/permissions", Token);

        await AssertDeniedAsync(response, ApiErrorCode.NoMembership);
    }

    /// <summary>
    /// 00 §9.1 / 02.6.2 §5: a service token scoped to project A calling a request that
    /// runs as project B is <c>project_scope_mismatch</c>, not plain <c>forbidden</c> —
    /// the real evaluator decides it before it reads any row, so the code appears even
    /// though the module's custom-role flag is off (a handler would have answered
    /// <c>forbidden</c> there, which is exactly what this assertion excludes).
    /// </summary>
    [Fact]
    public async Task Service_Token_Outside_Its_Project_Scope_Is_403_ProjectScopeMismatch()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        ScopedProject.ShouldNotBe(project);

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                ProjectId: project,
                Principal: RequestPrincipal.Service(ScopedProject, [Permissions.Run.Create])));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/permissions", Token);

        await AssertDeniedAsync(response, ApiErrorCode.ProjectScopeMismatch);
    }

    /// <summary>
    /// AC4: a service identity is evaluated through the same behaviour as a member —
    /// the token may carry only catalogue codes, and only the ones it carries count.
    /// A claim the catalogue does not declare denies the whole request rather than
    /// granting the rest of the set.
    /// </summary>
    [Fact]
    public async Task Service_Token_Claim_Outside_The_Catalogue_Is_403_Forbidden()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                Principal: RequestPrincipal.Service(projectScope: null, [Permissions.Role.Read, "not_a:catalogue_code"])));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/permissions", Token);

        await AssertDeniedAsync(response, ApiErrorCode.Forbidden);
    }

    /// <summary>FR-02-7: a <c>ci_producer</c> token without <c>role:read</c> is refused — it cannot read the tenant's role model.</summary>
    [Fact]
    public async Task Service_Token_Without_The_Permission_Is_403_Forbidden()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                Principal: RequestPrincipal.Service(
                    projectScope: null,
                    [Permissions.Run.Create, Permissions.Event.Write, Permissions.Artifact.Write])));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/permissions", Token);

        await AssertDeniedAsync(response, ApiErrorCode.Forbidden);
    }

    /// <summary>
    /// The 02.8.1 deferral, end to end: an operator caller has no support grant yet,
    /// so the path denies — a host that ever resolves one must not serve it tenant
    /// data through a permissive default.
    /// </summary>
    [Fact]
    public async Task Operator_Without_A_Support_Grant_Is_403_Forbidden()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Operator()));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/permissions", Token);

        await AssertDeniedAsync(response, ApiErrorCode.Forbidden);
    }

    /// <summary>
    /// 00 §9.2 / C8: a denial is the envelope, with the request id echoed and
    /// <c>retryable</c> false — an authorization failure is never retried unchanged.
    /// </summary>
    private static async Task AssertDeniedAsync(HttpResponseMessage response, string expectedCode)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Headers.ShouldContain(header =>
            header.Key.Equals("X-Request-Id", StringComparison.OrdinalIgnoreCase));

        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var error = envelope.RootElement.GetProperty("error");

        error.GetProperty("code").GetString().ShouldBe(expectedCode);
        error.GetProperty("retryable").GetBoolean().ShouldBeFalse();
        error.GetProperty("details").ValueKind.ShouldBe(JsonValueKind.Array);
    }
}
