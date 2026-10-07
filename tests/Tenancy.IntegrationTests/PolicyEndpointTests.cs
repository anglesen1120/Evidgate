using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.1.1 AC1/AC3/AC5 through the real host and a real PostgreSQL: the append-only policy-revision write path
/// (content-addressed dedupe, Corvus schema rejection before any row), the read path with its project-scope rule,
/// the cursor-paginated revision list with <c>If-Match</c> on the cached draft, and the P4 index plan.
/// </summary>
/// <remarks>
/// The routes, status codes, wire names, error codes and headers are the 07.1.1-T3 contract (s9-plan); the tests
/// drive the HTTP surface only, so they do not depend on the module's internal type names.
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class PolicyEndpointTests
{
    /// <summary>A body the <c>policy-revision.v1</c> schema accepts; two keys so a reorder is observable.</summary>
    private const string BodyA = """{"flaky_budget":3,"required_suites":["smoke","regression"]}""";

    /// <summary>The same body as <see cref="BodyA"/> with reordered keys and added whitespace.</summary>
    private const string BodyAReordered = """
        {
          "required_suites" : [ "smoke" , "regression" ],
          "flaky_budget" : 3
        }
        """;

    /// <summary>A different body (one value changed) — a new revision, never a conflict.</summary>
    private const string BodyB = """{"flaky_budget":4,"required_suites":["smoke","regression"]}""";

    private const string BodyC = """{"flaky_budget":5,"required_suites":["smoke","regression"]}""";

    private const string BodyD = """{"flaky_budget":6,"required_suites":["smoke"]}""";

    private readonly PostgresTenancyFixture _fixture;

    public PolicyEndpointTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    private static Task<ApiAnswer> CreateAsync(
        World world,
        string body,
        Guid? project = null,
        Action<HttpRequestHeaders>? caller = null,
        string? ifMatch = null) =>
        SendRawAsync(world.Client, HttpMethod.Post, $"/v1/projects/{project ?? world.Project}/policies", caller ?? world.Admin, body, ifMatch);

    /// <summary>Sends a raw body verbatim (the JSON-body helper of the registry suite cannot express whitespace).</summary>
    private static async Task<ApiAnswer> SendRawAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        Action<HttpRequestHeaders> caller,
        string raw,
        string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, url);
        caller(request.Headers);

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        request.Content = new StringContent(raw, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    private static IReadOnlyList<JsonElement> Items(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("items").EnumerateArray()];

    private static JsonElement Detail(ApiAnswer answer, int index = 0) =>
        answer.Body.GetProperty("error").GetProperty("details")[index];

    private async Task<long> RevisionCountAsync(World world) =>
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.policy_revision");

    /// <summary>Adds an active member of <paramref name="roleCode"/> to the org and returns her user id.</summary>
    private async Task<Guid> AddMemberAsync(World world, string roleCode)
    {
        var user = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values (gen_random_uuid(), '{world.Org}', '{user}', '{roleCode}@example.test', 'active');
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), '{world.Org}', m.id, r.id
              from public.membership m, public.role r
             where m.org_id = '{world.Org}' and m.user_id = '{user}'
               and r.code = '{roleCode}' and r.org_id is null;
            """);

        return user;
    }

    /// <summary>Compares two JSON values ignoring object key order (the <c>jsonb</c> column reorders keys).</summary>
    private static bool JsonSemanticallyEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                var leftProperties = left.EnumerateObject().ToList();
                var rightProperties = right.EnumerateObject().ToList();
                return leftProperties.Count == rightProperties.Count
                    && leftProperties.All(property =>
                        right.TryGetProperty(property.Name, out var other)
                        && JsonSemanticallyEqual(property.Value, other));
            case JsonValueKind.Array:
                var leftItems = left.EnumerateArray().ToList();
                var rightItems = right.EnumerateArray().ToList();
                return leftItems.Count == rightItems.Count
                    && leftItems.Zip(rightItems).All(pair => JsonSemanticallyEqual(pair.First, pair.Second));
            case JsonValueKind.Number:
                return left.GetDouble() == right.GetDouble();
            case JsonValueKind.String:
                return left.GetString() == right.GetString();
            default:
                return true;
        }
    }

    [Fact]
    public async Task CreatePolicyRevision_IdenticalBodyReturnsExisting_NewBodyAppends()
    {
        await using var world = await StartAsync();

        var first = await CreateAsync(world, BodyA);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        first.Body.GetProperty("revision_no").GetInt32().ShouldBe(1);
        first.Text("hash")!.ShouldMatch("^sha256:[0-9a-f]{64}$");
        first.Text("state").ShouldBe("draft");
        first.ETag.ShouldBe($"\"{first.Id()}\"");

        // The same body, keys reordered and whitespace added: the canonical hash is the idempotency key, so the
        // stored revision comes back with 200 and no second row.
        var replay = await CreateAsync(world, BodyAReordered);
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Id().ShouldBe(first.Id());
        replay.Text("hash").ShouldBe(first.Text("hash"));

        var list = await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{first.Id()}/revisions", world.Admin);
        Items(list).Count.ShouldBe(1);
        (await RevisionCountAsync(world)).ShouldBe(1);

        var second = await CreateAsync(world, BodyB);
        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);
        second.Body.GetProperty("revision_no").GetInt32().ShouldBe(2);
        second.Text("hash").ShouldNotBe(first.Text("hash"));
        (await RevisionCountAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task CreatePolicyRevision_UnknownFields_Are422_WithPointer_AndWriteNothing()
    {
        await using var world = await StartAsync();

        var topLevel = await CreateAsync(world, """{"flaky_budget":3,"bogus":true}""");
        topLevel.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        topLevel.ErrorCode.ShouldBe("policy_unknown_field");
        Detail(topLevel).GetProperty("path").GetString().ShouldBe("/bogus");

        var nested = await CreateAsync(world, """{"gating_refs":{"default_branch":true,"bogus":1}}""");
        nested.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        nested.ErrorCode.ShouldBe("policy_unknown_field");
        Detail(nested).GetProperty("path").GetString().ShouldBe("/gating_refs/bogus");

        (await RevisionCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task CreatePolicyRevision_WrongTypeAndInexactNumber_Are422_No500()
    {
        await using var world = await StartAsync();

        var wrongType = await CreateAsync(world, """{"flaky_budget":"many"}""");
        wrongType.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        wrongType.ErrorCode.ShouldBe("policy_invalid");

        // 2^53 + 1 cannot be represented exactly as an IEEE-754 double; the schema's integer fields carry a range,
        // so the rejection is either the range/type rule or the exactness rule — never a 500.
        var inexact = await CreateAsync(world, """{"flaky_budget":9007199254740993}""");
        inexact.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        inexact.ErrorCode.ShouldBeOneOf("policy_invalid", "policy_unknown_field");

        (await RevisionCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task CreatePolicyRevision_NonJsonAndOversize_Are422And413_NoRow()
    {
        await using var world = await StartAsync();

        var notJson = await CreateAsync(world, "this is not json");
        notJson.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        notJson.ErrorCode.ShouldBe("policy_invalid");

        var oversize = await CreateAsync(world, "{\"required_suites\":[\"" + new string('a', 70_000) + "\"]}");
        oversize.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        oversize.ErrorCode.ShouldBe("payload_too_large");

        (await RevisionCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Permissions_WriteNeedsPolicyWrite_ReadNeedsPolicyRead()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, BodyA);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var qaEngineer = TenancyApiFactory.Resolved(user: await AddMemberAsync(world, "qa_engineer"), orgHeader: world.Org);
        var viewer = TenancyApiFactory.Resolved(user: await AddMemberAsync(world, "viewer"), orgHeader: world.Org);

        // policy:write is release_approver-only (00 §2), so a qa_engineer member is refused the write.
        (await CreateAsync(world, BodyB, caller: qaEngineer)).Status.ShouldBe(HttpStatusCode.Forbidden);

        // policy:read is open to every member role, viewer included.
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}", viewer))
            .Status.ShouldBe(HttpStatusCode.OK);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions", viewer))
            .Status.ShouldBe(HttpStatusCode.OK);

        (await RevisionCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task ProjectScopedCredential_SeesNothingOfAnotherProject_AndCannotPost()
    {
        await using var world = await StartAsync();
        var projectB = await CreateProjectAsync(world.Client, world.User, world.Org, "other");
        var created = await CreateAsync(world, BodyA);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var scopedToB = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: projectB, servicePermissions: ["policy:read"]);
        var scopedToBWrite = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: projectB, servicePermissions: ["policy:write"]);

        // A credential scoped to project B learns nothing about project A's revision: the same 404 as an unknown id.
        var hidden = await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}", scopedToB);
        var unknown = await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{Guid.CreateVersion7()}", scopedToB);
        hidden.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        hidden.ErrorCode.ShouldBe(unknown.ErrorCode);
        hidden.ErrorMessage.ShouldBe(unknown.ErrorMessage);

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions", scopedToB))
            .Status.ShouldBe(HttpStatusCode.NotFound);

        // Writing into project A with a credential scoped to B is the 00 §9.1 project-scope mismatch.
        var mismatch = await CreateAsync(world, BodyB, caller: scopedToBWrite);
        mismatch.Status.ShouldBe(HttpStatusCode.Forbidden);
        mismatch.ErrorCode.ShouldBe("project_scope_mismatch");

        (await RevisionCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task CrossTenant_Get_Is404()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, BodyA);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");

        (await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/policies/{created.Id()}", TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg)))
            .Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPolicyRevision_ReturnsPostedBody_DraftState_AndEtag()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, BodyA);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var read = await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}", world.Admin);

        read.Status.ShouldBe(HttpStatusCode.OK);
        read.ETag.ShouldBe($"\"{created.Id()}\"");
        read.Text("state").ShouldBe("draft");
        read.Id("project_id").ShouldBe(world.Project);
        using var posted = JsonDocument.Parse(BodyA);
        JsonSemanticallyEqual(read.Body.GetProperty("body"), posted.RootElement).ShouldBeTrue();
    }

    [Fact]
    public async Task ListPolicyRevisions_Ascending_Paginates_AndEtagIsHead()
    {
        await using var world = await StartAsync();
        var first = await CreateAsync(world, BodyA);
        var second = await CreateAsync(world, BodyB);
        var third = await CreateAsync(world, BodyC);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);
        third.Status.ShouldBe(HttpStatusCode.Created, third.ErrorMessage);

        var all = await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{first.Id()}/revisions", world.Admin);
        all.Status.ShouldBe(HttpStatusCode.OK);
        all.ETag.ShouldBe($"\"{third.Id()}\"");
        Items(all).Select(item => item.GetProperty("revision_no").GetInt32()).ShouldBe([1, 2, 3]);
        all.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);

        var pageOne = await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{first.Id()}/revisions?limit=2", world.Admin);
        Items(pageOne).Select(item => item.GetProperty("revision_no").GetInt32()).ShouldBe([1, 2]);
        var cursor = pageOne.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrEmpty();

        var pageTwo = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/policies/{first.Id()}/revisions?limit=2&cursor={Uri.EscapeDataString(cursor!)}", world.Admin);
        Items(pageTwo).Select(item => item.GetProperty("revision_no").GetInt32()).ShouldBe([3]);
        pageTwo.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task StaleIfMatch_OnList_Is412_WithHeadId_AndOnPost_WritesNothing()
    {
        await using var world = await StartAsync();
        var first = await CreateAsync(world, BodyA);
        var second = await CreateAsync(world, BodyB);
        var third = await CreateAsync(world, BodyC);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);
        third.Status.ShouldBe(HttpStatusCode.Created, third.ErrorMessage);

        // The caller cached revision 2 as its head; the project's head is revision 3.
        var stale = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/policies/{first.Id()}/revisions", world.Admin, ifMatch: $"\"{second.Id()}\"");
        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        stale.ErrorCode.ShouldBe("precondition_failed");
        stale.Body.GetRawText().ShouldContain(third.Id().ToString());

        var fresh = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/policies/{first.Id()}/revisions", world.Admin, ifMatch: $"\"{third.Id()}\"");
        fresh.Status.ShouldBe(HttpStatusCode.OK);

        var before = await RevisionCountAsync(world);
        var stalePost = await CreateAsync(world, BodyD, ifMatch: $"\"{second.Id()}\"");
        stalePost.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        stalePost.ErrorCode.ShouldBe("precondition_failed");
        (await RevisionCountAsync(world)).ShouldBe(before);
    }

    [Fact]
    public async Task ListPolicyRevisions_InvalidLimit_AndGarbageCursor_Are422()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, BodyA);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions?limit=0", world.Admin))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions?limit=201", world.Admin))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions?cursor=not-a-cursor", world.Admin))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    /// <summary>
    /// 00 §5.7 P4: the revision list walks the unique <c>(org_id, project_id, revision_no)</c> btree. The target
    /// project is seeded sparsely and the rest of the table densely, then <c>ANALYZE</c>d, so the planner prefers
    /// the index without <c>enable_seqscan = off</c> (which the 07.1.1-T3 contract forbids).
    /// </summary>
    [Fact]
    public async Task ListPolicyRevisions_UsesTheRevisionNoIndex()
    {
        await using var world = await StartAsync();
        var otherProject = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.project (id, org_id, name, slug)
            values ('{otherProject}', '{world.Org}', 'other', 'other');
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            select gen_random_uuid(), '{world.Org}', '{world.Project}', g,
                   'sha256:' || lpad(to_hex(g), 64, '0'), jsonb_build_object(), gen_random_uuid()
              from generate_series(1, 100) g;
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            select gen_random_uuid(), '{world.Org}', '{otherProject}', g,
                   'sha256:' || lpad(to_hex(g), 64, '0'), jsonb_build_object(), gen_random_uuid()
              from generate_series(1, 50000) g;
            analyze public.policy_revision;
            """);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        // The list SQL shape of ListPolicyRevisionsQuery: filter (org_id, project_id), order (revision_no, id).
        await using var command = new NpgsqlCommand(
            """
            explain (format json)
            select id, revision_no, hash, state, created_by, created_at, version
              from public.policy_revision
             where org_id = @org_id and project_id = @project_id
             order by revision_no, id
             limit 51
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("org_id", world.Org);
        command.Parameters.AddWithValue("project_id", world.Project);

        var plan = (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        plan.ShouldContain("policy_revision_revision_no_key");
        plan.ShouldNotContain("Seq Scan");
    }
}
