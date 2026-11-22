using System.Net;
using System.Text.Json;
using Evidgate.Modules.Catalog.Contracts;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class CatalogIdentityTests
{
    // ------------------------------------------------------------------ AC7 API

    private async Task<(Guid First, Guid Second, Guid Third)> SeedThreeAsync(World world)
    {
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        await Task.Delay(5, TestContext.Current.CancellationToken);
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB));
        await Task.Delay(5, TestContext.Current.CancellationToken);
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB), Annotated("c.spec.ts", IdC));
        var ids = await IdsAsync(world);

        return (ids[0], ids[1], ids[2]);
    }

    private async Task<List<Guid>> IdsAsync(World world)
    {
        var ids = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            ids.Add(await _fixture.ScalarAsync<Guid>(
                world.Database, $"select id from public.test_case where org_id = '{world.Org}' order by created_at, id offset {index} limit 1"));
        }

        return ids;
    }

    /// <summary>Runs one statement as the app role inside a tenant transaction, so RLS shows the rows and the guards fire.</summary>
    private async Task AsAppAsync(World world, string sql)
    {
        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task List_PagesInCreationOrder_WithACursor_AndFilters()
    {
        await using var world = await StartAsync();
        var (first, second, third) = await SeedThreeAsync(world);

        var page1 = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?limit=2", world.Admin);
        page1.Status.ShouldBe(HttpStatusCode.OK);
        page1.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldBe([first, second]);
        var cursor = page1.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNull();

        var page2 = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?limit=2&cursor={Uri.EscapeDataString(cursor)}", world.Admin);
        page2.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldBe([third]);
        page2.Body.GetProperty("next_cursor").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        await _fixture.ExecuteAsync(world.Database, $"update public.test_case set status = 'archived' where org_id = '{world.Org}' and id = '{second}'");
        var live = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases", world.Admin);
        live.Body.GetProperty("items").GetArrayLength().ShouldBe(2);
        var archived = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?status=archived", world.Admin);
        archived.Body.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(second);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?framework=jest", world.Admin))
            .Body.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task List_RefusesBadInput_AndHidesOtherTenantsProjects()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?limit=0", world.Admin)).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?limit=201", world.Admin)).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?status=bogus", world.Admin)).ErrorCode.ShouldBe("request_invalid");
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?cursor=garbage", world.Admin)).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Another org's project is the same 404 as a missing one.
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{otherProject}/cases", world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{Guid.CreateVersion7()}/cases", world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_NeedsCaseRead_AndServiceIdentitiesDoNotHaveIt()
    {
        await using var world = await StartAsync();

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases", TenancyApiFactory.Resolved(user: Guid.CreateVersion7(), orgHeader: world.Org)))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases", _ => { })).Status.ShouldBe(HttpStatusCode.Unauthorized);

        var service = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["case:read"]);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases", service)).Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task List_IsOneStatementPerPage_PlusTheProjectCheck()
    {
        await using var world = await StartAsync();
        await SeedThreeAsync(world);

        world.Api.Statements.Clear();
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?limit=2", world.Admin)).Status.ShouldBe(HttpStatusCode.OK);

        var work = world.Api.Statements.Snapshot()
            .Where(statement => statement.Contains("public.", StringComparison.Ordinal) && statement.Contains("test_case", StringComparison.Ordinal))
            .ToList();
        work.Count.ShouldBe(1, string.Join(Environment.NewLine, world.Api.Statements.Snapshot()));
    }

    [Fact]
    public async Task Get_ReturnsTheCase_WithItsIdentityTrail_AndAnETag()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("old.spec.ts", IdA));
        await RegisterAsync(world, Commit2, Annotated("new.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));

        world.Api.Statements.Clear();
        var answer = await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}", world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        var found = answer.Body.GetProperty("case");
        (found.GetProperty("annotation_id").GetGuid(), found.GetProperty("file_path").GetString(), found.GetProperty("status").GetString())
            .ShouldBe((Guid.Parse(IdA), "new.spec.ts", "active"));
        answer.Body.GetProperty("identity_sources").EnumerateArray().Single().GetProperty("source").GetString().ShouldBe("annotation");
        var rename = answer.Body.GetProperty("renames").EnumerateArray().Single();
        (rename.GetProperty("from_path").GetString(), rename.GetProperty("to_path").GetString()).ShouldBe(("old.spec.ts", "new.spec.ts"));
        answer.ETag.ShouldBe($"\"{found.GetProperty("version").GetInt32()}\"");

        world.Api.Statements.Snapshot().Count(statement => statement.Contains("from public.test_case", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Get_AnotherOrgsCase_IsNotFound_LikeAMissingOne()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        await RegisterAsync(world.Api, otherOrg, otherProject, Commit1, Annotated("a.spec.ts", IdA));
        var theirs = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(otherOrg, "select id from public.test_case"));

        var cross = await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{theirs}", world.Admin);
        var missing = await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{Guid.CreateVersion7()}", world.Admin);

        cross.Status.ShouldBe(HttpStatusCode.NotFound);
        missing.Status.ShouldBe(HttpStatusCode.NotFound);
        cross.ErrorMessage.ShouldBe(missing.ErrorMessage);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{theirs}/revisions", world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Revisions_HoldTheOneVersionTheFirstSyncAppended()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));

        var answer = await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}/revisions", world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("items").GetArrayLength().ShouldBe(1);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}/revisions?limit=0", world.Admin)).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Patch_ChangesMetadata_UnderIfMatch_AndAudits()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));
        var membership = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.membership", $"user_id = '{world.User}'"));
        var etag = (await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}", world.Admin)).ETag!;

        var patched = await world.Client.SendAsync(
            HttpMethod.Patch,
            $"/v1/cases/{caseId}",
            world.Admin,
            new { name = "  Login works  ", owner_membership_id = membership, labels = DuplicateLabels },
            NewKey(),
            etag);

        patched.Status.ShouldBe(HttpStatusCode.OK, patched.ErrorMessage);
        patched.Text("name").ShouldBe("Login works");
        patched.Id("owner_membership_id").ShouldBe(membership);
        patched.Body.GetProperty("labels").EnumerateArray().Select(label => label.GetString()).ShouldBe(["smoke", "p1"]);
        patched.ETag.ShouldBe("\"2\"");
        patched.Text("annotation_id").ShouldBe(IdA);

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.audit_log where org_id = '{world.Org}' and action = 'case.update' and object_id = '{caseId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Patch_StaleIfMatch_Is412_WithTheCurrentVersion_AndMissingIfMatchIs422()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));
        await _fixture.ExecuteAsync(world.Database, $"update public.test_case set name = 'changed elsewhere' where org_id = '{world.Org}' and id = '{caseId}'");

        var stale = await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, new { name = "mine" }, NewKey(), "\"1\"");
        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        stale.ErrorCode.ShouldBe("precondition_failed");
        stale.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Single(detail => detail.GetProperty("path").GetString() == "version").GetProperty("reason").GetString().ShouldBe("2");

        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, new { name = "mine" }, NewKey())).Status
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, new { name = "mine" }, NewKey(), "abc")).Status
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, new { name = "mine" }, null, "\"2\"")).Status
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("annotation_id")]
    [InlineData("file_path")]
    [InlineData("first_seen_commit")]
    [InlineData("project_id")]
    [InlineData("id")]
    public async Task Patch_IdentityFields_Are422_CaseFieldImmutable_AndNothingChanges(string field)
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));

        var body = new Dictionary<string, object?> { ["name"] = "sneaky", [field] = IdB };
        var answer = await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, body, NewKey(), "\"1\"");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("case_field_immutable");
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select name || version::text from public.test_case"))).ShouldBe("a.spec1");
    }

    [Fact]
    public async Task Patch_RefusesUnknownMembers_BadShapes_AndBadValues()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));

        async Task<ApiAnswer> Patch(object body) =>
            await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, body, NewKey(), "\"1\"");

        (await Patch(new { colour = "red" })).ErrorCode.ShouldBe("request_invalid");
        (await Patch(NotAnObject)).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Patch(new { name = "   " })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Patch(new { name = new string('x', 513) })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Patch(new { status = "missing" })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Patch(new { labels = Enumerable.Range(0, 33).Select(i => "l" + i).ToArray() })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Patch(new { labels = new[] { new string('x', 65) } })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Patch(new { owner_membership_id = Guid.Empty })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Patch(new { name = 5 })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var stranger = await Patch(new { owner_membership_id = Guid.CreateVersion7() });
        stranger.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        stranger.Body.GetProperty("error").GetProperty("details").EnumerateArray().Single().GetProperty("path").GetString().ShouldBe("owner_membership_id");
        (await _fixture.ScalarAsync<int>(world.Database, Scoped(world, "select version from public.test_case"))).ShouldBe(1);
    }

    [Fact]
    public async Task Patch_StatusMoves_FollowTheStateMachine()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));

        async Task<ApiAnswer> Move(string status, int version) =>
            await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, new { status }, NewKey(), $"\"{version}\"");

        (await Move("active", 1)).Status.ShouldBe(HttpStatusCode.OK);
        (await Move("archived", 1)).Text("status").ShouldBe("archived");
        var again = await Move("archived", 2);
        again.Status.ShouldBe(HttpStatusCode.OK);
        (await Move("active", 2)).Text("status").ShouldBe("active");

        // active -> missing is the system's move; a missing case can be archived but not "reactivated" by hand.
        await _fixture.ExecuteAsync(world.Database, $"update public.test_case set status = 'missing' where org_id = '{world.Org}'");
        var version = await _fixture.ScalarAsync<int>(world.Database, Scoped(world, "select version from public.test_case"));
        var blocked = await Move("active", version);
        blocked.Status.ShouldBe(HttpStatusCode.Conflict);
        blocked.ErrorCode.ShouldBe("invalid_transition");
        (await Move("archived", version)).Text("status").ShouldBe("archived");
    }

    [Fact]
    public async Task Patch_NeedsCaseWrite_AndAnotherOrgsCaseIs404()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        await RegisterAsync(world.Api, otherOrg, otherProject, Commit1, Annotated("a.spec.ts", IdA));
        var mine = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));
        var theirs = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(otherOrg, "select id from public.test_case"));

        var stranger = TenancyApiFactory.Resolved(user: Guid.CreateVersion7(), orgHeader: world.Org);
        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{mine}", stranger, new { name = "x" }, NewKey(), "\"1\"")).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{theirs}", world.Admin, new { name = "x" }, NewKey(), "\"1\"")).Status.ShouldBe(HttpStatusCode.NotFound);
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(otherOrg, "select name from public.test_case", $"id = '{theirs}'"))).ShouldBe("a.spec");
    }

    [Fact]
    public async Task Patch_IsIdempotent_ByKey()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));
        var key = NewKey();

        var first = await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, new { name = "Once" }, key, "\"1\"");
        var replay = await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, new { name = "Once" }, key, "\"1\"");

        first.Status.ShouldBe(HttpStatusCode.OK);
        replay.Status.ShouldBe(HttpStatusCode.OK);
        (await _fixture.ScalarAsync<int>(world.Database, Scoped(world, "select version from public.test_case"))).ShouldBe(2);
    }

    [Fact]
    public async Task Patch_BodyThatIsNotJson_Is400_AndOneOverSixteenKiB_Is413()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));

        async Task<HttpStatusCode> Send(string text)
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"/v1/cases/{caseId}") { Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json") };
            world.Admin(request.Headers);
            request.Headers.Add("Idempotency-Key", NewKey());
            request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
            using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);

            return response.StatusCode;
        }

        (await Send("{not json")).ShouldBe(HttpStatusCode.BadRequest);
        (await Send("{\"name\":\"" + new string('x', 16 * 1024) + "\"}")).ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }
}
