using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;
using World = Tenancy.IntegrationTests.RunCreationTests.World;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The six raw-body Catalog write routes refuse anonymous callers before buffering, walking or parsing their body,
/// except when the host's body-limit middleware answers a declared length 413 first.
/// </summary>
/// <remarks>
/// A declared <c>Content-Length</c> over the limit <c>RequestBodyLimitMiddleware</c> enforces — the host's 1 MiB
/// default, or the 4 MiB the suite-revision route declares — is answered 413 before authentication. The in-process
/// host declares every body's length, so only suite-revision's over-cap case sees that refusal.
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

        foreach (var (body, status) in BadBodies(target))
        {
            var answer = await SendBodyAsync(world, target, body, Anonymous);

            if (route == "suite-revision" && status == HttpStatusCode.RequestEntityTooLarge)
            {
                answer.Answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, answer.Text);
                answer.Answer.ErrorCode.ShouldBe("payload_too_large");
            }
            else
            {
                AssertUnauthenticated(answer.Answer);
                answer.Text.ShouldBe(valid.Text, "the refusal must not disclose which body was sent");
            }
        }

        var control = await SendBodyAsync(world, target, target.ValidBody, world.Admin);
        if (route == "git-binding")
        {
            // This domain refusal proves the authenticated request progressed past authentication.
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

        foreach (var (body, status) in BadBodies(target))
        {
            var answer = await SendBodyAsync(world, target, body, caller);

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

    private static (string Body, HttpStatusCode Status)[] BadBodies(Target target) =>
    [
        ("{", HttpStatusCode.BadRequest),
        (new string(' ', target.Cap + 1), HttpStatusCode.RequestEntityTooLarge),
        (UnstorableName, HttpStatusCode.UnprocessableEntity),
    ];

    private static void AssertUnauthenticated(ApiAnswer answer)
    {
        answer.Status.ShouldBe(HttpStatusCode.Unauthorized, answer.Body.GetRawText());
        answer.ErrorCode.ShouldBe("unauthenticated");
        answer.ErrorMessage.ShouldBe("the request is not authenticated");
    }
}
