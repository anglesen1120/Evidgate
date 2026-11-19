using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.Modules.Catalog.Application.Queries;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <c>GET /v1/suite-revisions/{r}/execution-plan</c> through the real host and a real PostgreSQL: the CI credential the
/// manifest builder holds walks the plan in stable <c>(ordinal, id)</c> pages of a draft and a published revision, and
/// the route is a project-scoped read that answers the same 404 for an unknown, cross-project or cross-org revision.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class SuiteExecutionPlanTests
{
    private readonly PostgresTenancyFixture _fixture;

    public SuiteExecutionPlanTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Three pages of two members each cover a six-member revision exactly once, in the stored order, for a draft and a
    /// published revision. The minted <c>ci_producer</c> token carries the new permission with the old four and is still
    /// refused by the revision read that needs <c>suite:read</c>; a member credential reads the same plan.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_DraftOrPublished_CiTokenWalksThreeStablePages(bool published)
    {
        await using var world = await StartAsync();
        if (published)
        {
            await PublishAsync(world);
        }

        var minted = await MintAsync(world, world.Project);
        minted.Body.GetProperty("permission_set").EnumerateArray().Select(code => code.GetString()).Order(StringComparer.Ordinal)
            .ShouldBe(["artifact:write", "decision:read", "event:write", "run:create", "suite:plan_read"]);
        var caller = Bearer(minted.Text("token")!);
        var expected = await ExpectedAsync(world, world.Revision);
        expected.Count.ShouldBe(6);

        var actual = new List<string>();
        string? cursor = null;
        for (var pageNo = 0; pageNo < 3; pageNo++)
        {
            var query = "?limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var answer = await GetPlanAsync(world, world.Revision, caller, query);
            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
            answer.Id("suite_revision_id").ShouldBe(world.Revision);
            answer.Body.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal)
                .ShouldBe(["members", "next_cursor", "suite_revision_id"]);

            var members = answer.Body.GetProperty("members");
            members.GetArrayLength().ShouldBe(2);
            foreach (var member in members.EnumerateArray())
            {
                member.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal)
                    .ShouldBe(["case_id", "case_revision_id", "variant_hash"]);
                actual.Add(MemberKey(member));
            }

            cursor = answer.Body.GetProperty("next_cursor").GetString();
            if (pageNo < 2)
            {
                cursor.ShouldNotBeNullOrWhiteSpace("a page with a following page carries its cursor");
            }
        }

        cursor.ShouldBeNull("the last page terminates the walk");
        actual.ShouldBe(expected);
        actual.Distinct(StringComparer.Ordinal).Count().ShouldBe(6);

        var again = await GetPlanAsync(world, world.Revision, caller, "?limit=2");
        again.Body.GetProperty("members").EnumerateArray().Select(MemberKey).ShouldBe(expected.Take(2));

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{world.Revision}", caller))
            .Status.ShouldBe(HttpStatusCode.Forbidden, "the plan read must not hand out suite:read");
        (await GetPlanAsync(world, world.Revision, world.Admin)).Status.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The permission, not the revision: a token without <c>suite:plan_read</c> is refused <c>403 forbidden</c> before the
    /// revision is read, so an unknown id and an existing one answer the same body. The four scopes a producer token
    /// held before this permission existed are not enough either, and neither is one scope.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_TokenWithoutThePermission_IsForbiddenBeforeTheRevisionIsRead(bool legacyScopes)
    {
        await using var world = await StartAsync();
        (await GetPlanAsync(world, world.Revision, Bearer((await MintAsync(world, world.Project)).Text("token")!)))
            .Status.ShouldBe(HttpStatusCode.OK);

        string[] scopes = legacyScopes ? ["event:write", "artifact:write", "run:create", "decision:read"] : ["event:write"];
        var minted = await MintAsync(world, world.Project, scopes);
        minted.Body.GetProperty("permission_set").EnumerateArray().Select(code => code.GetString()).Order(StringComparer.Ordinal)
            .ShouldBe(scopes.Order(StringComparer.Ordinal));
        var narrow = Bearer(minted.Text("token")!);
        var existing = await GetPlanAsync(world, world.Revision, narrow);
        var unknown = await GetPlanAsync(world, Guid.CreateVersion7(), narrow);

        existing.Status.ShouldBe(HttpStatusCode.Forbidden);
        unknown.Status.ShouldBe(HttpStatusCode.Forbidden);
        existing.ErrorCode.ShouldBe("forbidden");
        ErrorShape(existing).ShouldBe(ErrorShape(unknown));
    }

    /// <summary>
    /// A revision of another project of the same org and a revision of a foreign org answer the unknown-id body exactly:
    /// the project gate is the revision lookup, never a per-member filter or an existence oracle.
    /// </summary>
    [Fact]
    public async Task Plan_UnknownCrossProjectAndCrossOrg_HaveTheSameNotFoundBody()
    {
        await using var world = await StartAsync();
        var caller = Bearer((await MintAsync(world, world.Project)).Text("token")!);
        (await GetPlanAsync(world, world.Revision, caller)).Status.ShouldBe(HttpStatusCode.OK);

        var unknown = await GetPlanAsync(world, Guid.CreateVersion7(), caller);
        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.ErrorCode.ShouldBe("not_found");

        var siblingProject = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("sibling"));
        var siblingSuite = await CreateSuiteRevisionAsync(world.Api, world.Client, world.User, world.Org, siblingProject, "sibling", 2);
        var siblingCaller = Bearer((await MintAsync(world, siblingProject)).Text("token")!);
        var crossProject = await GetPlanAsync(world, world.Revision, siblingCaller);
        crossProject.Status.ShouldBe(HttpStatusCode.NotFound);
        ErrorShape(crossProject).ShouldBe(ErrorShape(unknown));

        await using var foreign = await StartAsync(projectSlug: Unique.Slug("rival"), members: 2);
        var crossOrg = await GetPlanAsync(world, foreign.Revision, caller);
        crossOrg.Status.ShouldBe(HttpStatusCode.NotFound);
        ErrorShape(crossOrg).ShouldBe(ErrorShape(unknown));
        (await GetPlanAsync(world, siblingSuite.Revision, siblingCaller)).Status.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The route's own contract for its query: a canonical integer outside 1–500 is the fixable 422, a value that is not
    /// the canonical spelling (or an empty one) is the 400, and both limits the contract admits are served.
    /// </summary>
    [Theory]
    [InlineData("?limit=1", 200, null)]
    [InlineData("?limit=500", 200, null)]
    [InlineData("?limit=0", 422, "limit")]
    [InlineData("?limit=501", 422, "limit")]
    [InlineData("?limit=-1", 422, "limit")]
    [InlineData("?limit=2147483648", 422, "limit")]
    [InlineData("?limit=01", 400, "limit")]
    [InlineData("?limit=%2B2", 400, "limit")]
    [InlineData("?limit=1.5", 400, "limit")]
    [InlineData("?limit=", 400, "limit")]
    [InlineData("?limit=%202", 400, "limit")]
    [InlineData("?limit=abc", 400, "limit")]
    [InlineData("?limit=2&limit=2", 400, "limit")]
    [InlineData("?cursor=%25", 400, "cursor")]
    [InlineData("?cursor=", 400, "cursor")]
    public async Task Plan_InvalidQuery_IsTheContractsStatus(string query, int status, string? detailPath)
    {
        await using var world = await StartAsync();
        var caller = Bearer((await MintAsync(world, world.Project)).Text("token")!);
        var answer = await GetPlanAsync(world, world.Revision, caller, query);

        ((int)answer.Status).ShouldBe(status, answer.ErrorMessage);
        if (detailPath is not null)
        {
            answer.ErrorCode.ShouldBe("request_invalid");
            answer.Body.GetProperty("error").GetProperty("details")[0].GetProperty("path").GetString()
                .ShouldBe(detailPath);
        }
    }

    /// <summary>
    /// A cursor is bound to the revision and project it was minted for: the same value on another revision of the suite
    /// and on a revision of a sibling project is <c>422 request_invalid</c>, never a page position there.
    /// </summary>
    [Fact]
    public async Task Plan_CursorOfAnotherRevisionOrProject_Is422()
    {
        await using var world = await StartAsync();
        var caller = Bearer((await MintAsync(world, world.Project)).Text("token")!);
        var first = await GetPlanAsync(world, world.Revision, caller, "?limit=2");
        first.Status.ShouldBe(HttpStatusCode.OK);
        var cursor = first.Text("next_cursor")!;
        (await GetPlanAsync(world, world.Revision, caller, "?cursor=" + Uri.EscapeDataString(cursor)))
            .Status.ShouldBe(HttpStatusCode.OK);

        var shortRevision = await CreateRevisionAsync(world, world.Suite, [world.CaseRevisions[0]]);
        var otherRevision = await GetPlanAsync(world, shortRevision, caller, "?cursor=" + Uri.EscapeDataString(cursor));
        otherRevision.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        otherRevision.ErrorCode.ShouldBe("request_invalid");

        var siblingProject = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("sibling"));
        var sibling = await CreateSuiteRevisionAsync(world.Api, world.Client, world.User, world.Org, siblingProject, "sibling", 2);
        var siblingCaller = Bearer((await MintAsync(world, siblingProject)).Text("token")!);
        var otherProject = await GetPlanAsync(world, sibling.Revision, siblingCaller, "?cursor=" + Uri.EscapeDataString(cursor));
        otherProject.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        otherProject.ErrorCode.ShouldBe("request_invalid");
    }

    /// <summary>
    /// A continuation cursor is refused <c>422 request_invalid</c> when the anchor it names is not a member of the
    /// revision: the codec can spell a position in this revision and project that no row ever held, so the page
    /// statement reports the anchor's absence and the handler refuses instead of clamping to the nearest row. An
    /// unknown revision still answers <c>404</c> before the anchor verdict is judged.
    /// </summary>
    [Fact]
    public async Task Plan_CursorWithNonexistentAnchor_Is422()
    {
        await using var world = await StartAsync();
        var caller = Bearer((await MintAsync(world, world.Project)).Text("token")!);
        var first = await GetPlanAsync(world, world.Revision, caller, "?limit=2");
        first.Status.ShouldBe(HttpStatusCode.OK);
        var cursor = first.Text("next_cursor")!;
        (await GetPlanAsync(world, world.Revision, caller, "?cursor=" + Uri.EscapeDataString(cursor)))
            .Status.ShouldBe(HttpStatusCode.OK);

        var fabricated = SuitePlanCursor.Encode(world.Revision, world.Project, 1, Guid.CreateVersion7());
        var query = "?cursor=" + Uri.EscapeDataString(fabricated);
        var answer = await GetPlanAsync(world, world.Revision, caller, query);
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Body.GetProperty("error").GetProperty("details")[0].GetProperty("path").GetString().ShouldBe("cursor");
        (await GetPlanAsync(world, Guid.CreateVersion7(), caller, query)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Members whose ordinals repeat still page exactly once each: the keyset's tie-breaker is the member id, so the
    /// cursor of a page that ends inside a run of equal ordinals resumes at the next id and never repeats or skips one.
    /// </summary>
    [Fact]
    public async Task Plan_EqualOrdinalsAtPageBoundary_EachMemberAppearsOnce()
    {
        await using var world = await StartAsync();
        var revision = await SeedTiedOrdinalRevisionAsync(world);
        var expected = await ExpectedAsync(world, revision);
        expected.Count.ShouldBe(6);
        var caller = Bearer((await MintAsync(world, world.Project)).Text("token")!);
        var actual = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 2; page++)
        {
            var query = "?limit=3" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var answer = await GetPlanAsync(world, revision, caller, query);
            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
            answer.Body.GetProperty("members").GetArrayLength().ShouldBe(3);
            actual.AddRange(answer.Body.GetProperty("members").EnumerateArray().Select(MemberKey));
            cursor = answer.Text("next_cursor");
            if (page < 1)
            {
                cursor.ShouldNotBeNullOrWhiteSpace();
            }
        }

        cursor.ShouldBeNull();
        actual.ShouldBe(expected);
        actual.Distinct(StringComparer.Ordinal).Count().ShouldBe(6);
    }

    /// <summary>A revision with no members is an empty page with no cursor, not a 404.</summary>
    [Fact]
    public async Task Plan_RevisionWithoutMembers_IsAnEmptyPage()
    {
        await using var world = await StartAsync();
        var empty = await CreateRevisionAsync(world, world.Suite, []);
        var answer = await GetPlanAsync(world, empty, Bearer((await MintAsync(world, world.Project)).Text("token")!));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("members").GetArrayLength().ShouldBe(0);
        answer.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
