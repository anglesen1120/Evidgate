using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;
using World = Tenancy.IntegrationTests.RunCreationTests.World;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The six raw-body Catalog write routes: an anonymous caller is refused 401 before a byte of the body is buffered,
/// walked or parsed, so the answer never discloses whether the body was malformed, oversized or valid.
/// </summary>
/// <remarks>
/// The cap case sends an undeclared-length (chunked) body: a declared <c>Content-Length</c> above the route's or the
/// host's limit is answered 413 by <c>RequestBodyLimitMiddleware</c> before the endpoint runs, so it could not show
/// the route's own ordering. Cap and payload answers for authorized callers are unchanged.
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class CatalogAnonymousBodyTests(
    PostgresTenancyFixture fixture,
    TenancyHostPool hosts) : IClassFixture<TenancyHostPool>
{
    private const string UnstorableName = """{"\ud800":1}""";

    [Theory]
    [InlineData("suite-revision")]
    [InlineData("requirement-update")]
    [InlineData("case-update")]
    [InlineData("fixture-create")]
    [InlineData("fixture-revision")]
    [InlineData("git-binding")]
    public async Task CatalogWrite_AnonymousBodies_ReturnIdenticalUnauthenticatedEnvelope(string route)
    {
        await using var world = await StartAsync();
        var target = await TargetAsync(world, route);
        var valid = await SendBodyAsync(world, target, target.ValidBody, Anonymous);
        AssertUnauthenticated(valid.Answer);

        foreach (var (body, _, undeclared) in BadBodies(target))
        {
            var answer = await SendBodyAsync(world, target, body, Anonymous, undeclared);

            AssertUnauthenticated(answer.Answer);
            answer.Text.ShouldBe(valid.Text, "the refusal must not disclose which body was sent");
        }

        var control = await SendBodyAsync(world, target, target.ValidBody, world.Admin);
        control.Answer.Status.ShouldNotBe(HttpStatusCode.Unauthorized,
            "the same request from an authenticated caller must not be refused as anonymous");
    }

    [Theory]
    [InlineData("suite-revision")]
    [InlineData("requirement-update")]
    [InlineData("case-update")]
    [InlineData("fixture-create")]
    [InlineData("fixture-revision")]
    [InlineData("git-binding")]
    public async Task CatalogWrite_SessionWithoutPermission_PreservesBodyValidationBeforeForbidden(string route)
    {
        await using var world = await StartAsync();
        var target = await TargetAsync(world, route);
        var user = await AddMemberAsync(fixture, world.Database, world.Org, roleCode: null);
        var caller = TenancyApiFactory.Resolved(user: user, orgHeader: world.Org);
        (await fixture.ScalarAsync<long>(world.Database, $"""
            select count(*) from public.membership_role mr
              join public.membership m on m.id = mr.membership_id
             where m.org_id = '{world.Org}' and m.user_id = '{user}'
            """)).ShouldBe(0, $"the session holds no role granting {target.Permission}");

        foreach (var (body, status, undeclared) in BadBodies(target))
        {
            var answer = await SendBodyAsync(world, target, body, caller, undeclared);

            answer.Answer.Status.ShouldBe(status, answer.Text);
            answer.Answer.ErrorCode.ShouldBe(status == HttpStatusCode.RequestEntityTooLarge
                ? "payload_too_large"
                : "request_invalid");
        }

        var denied = await SendBodyAsync(world, target, target.ValidBody, caller);
        denied.Answer.Status.ShouldBe(HttpStatusCode.Forbidden, denied.Text);
        denied.Answer.ErrorCode.ShouldBe("forbidden");

        var control = await SendBodyAsync(world, target, target.ValidBody, world.Admin);
        if (route == "git-binding")
        {
            control.Answer.ErrorCode.ShouldBe("installation_not_owned", control.Text);
        }
        else
        {
            control.Answer.Status.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        }
    }

    [Theory]
    [InlineData("suite-revision")]
    [InlineData("requirement-update")]
    [InlineData("case-update")]
    [InlineData("fixture-create")]
    [InlineData("fixture-revision")]
    [InlineData("git-binding")]
    public async Task CatalogWrite_AuthorizedInvalidBody_PreservesValidationAnswer(string route)
    {
        await using var world = await StartAsync();
        var target = await TargetAsync(world, route);
        var valuePath = route == "requirement-update";
        var body = valuePath ? """{"requirement_ref":"\ud800"}""" : UnstorableName;

        var answer = await SendBodyAsync(world, target, body, world.Admin);

        answer.Answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.Text);
        answer.Answer.ErrorCode.ShouldBe("request_invalid");
        var detail = answer.Answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().Single();
        detail.GetProperty("path").GetString().ShouldBe(valuePath ? "requirement_ref" : string.Empty);
        detail.GetProperty("reason").GetString().ShouldBe("invalid_value");

        var malformed = await SendBodyAsync(world, target, "{", world.Admin);
        malformed.Answer.Status.ShouldBe(HttpStatusCode.BadRequest, malformed.Text);
        malformed.Answer.ErrorCode.ShouldBe("request_invalid");
    }

    [Theory]
    [InlineData("suite-revision")]
    [InlineData("requirement-update")]
    [InlineData("case-update")]
    [InlineData("fixture-create")]
    [InlineData("fixture-revision")]
    [InlineData("git-binding")]
    public async Task CatalogWrite_AnotherTenantsIdWithValidBody_PreservesNotFound(string route)
    {
        await using var world = await StartAsync();
        var target = await TargetAsync(world, route);
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("catalog-foreign"));
        var caller = TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg);

        var answer = await SendBodyAsync(world, target, target.ValidBody, caller);

        answer.Answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.Text);
        answer.Answer.ErrorCode.ShouldBe("not_found");
    }

    private Task<World> StartAsync() => RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);

    private static readonly Action<System.Net.Http.Headers.HttpRequestHeaders> Anonymous = _ => { };

    private static (string Body, HttpStatusCode Status, bool UndeclaredLength)[] BadBodies(Target target) =>
    [
        ("{", HttpStatusCode.BadRequest, false),
        (new string(' ', target.Cap + 1), HttpStatusCode.RequestEntityTooLarge, true),
        (UnstorableName, HttpStatusCode.UnprocessableEntity, false),
    ];

    private static void AssertUnauthenticated(ApiAnswer answer)
    {
        answer.Status.ShouldBe(HttpStatusCode.Unauthorized, answer.Body.GetRawText());
        answer.ErrorCode.ShouldBe("unauthenticated");
        answer.ErrorMessage.ShouldBe("the request is not authenticated");
    }
}
