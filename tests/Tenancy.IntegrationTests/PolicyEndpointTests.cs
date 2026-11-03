using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Evidgate.Modules.Policy.Application.Queries;
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

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        DedicatedDatabase Dedicated) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await Api.DisposeAsync();
            }
            finally
            {
                // The database is this world's to drop even when the host's own disposal throws.
                await Dedicated.DisposeAsync();
            }
        }
    }

    // Private database: this class asserts unfiltered policy_revision counts across the whole database.
    private Task<World> StartAsync() =>
        _fixture.StartDedicatedAsync(async dedicated =>
        {
            var database = dedicated.Name;
            var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
            var client = api.CreateClient();
            var user = Guid.CreateVersion7();
            var org = await CreateOrgAsync(client, user, "acme");
            var project = await CreateProjectAsync(client, user, org, "checkout");

            return new World(api, client, database, user, org, project, dedicated);
        });

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
        string? ifMatch = null,
        string contentType = "application/json",
        bool chunked = false)
    {
        using var request = new HttpRequestMessage(method, url);
        caller(request.Headers);

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (chunked)
        {
            // No Content-Length: the endpoint must cap the streamed body itself (ReadBodyAsync).
            request.Headers.TransferEncodingChunked = true;
        }

        request.Content = new StringContent(raw, Encoding.UTF8, contentType);
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
            insert into public."user" (id, email) values ('{user}', '{roleCode}@example.test');
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

    /// <summary>
    /// 07.1.1 AC1 + finding S2: the create and the replay are both audited under <c>policy_revision.create</c>,
    /// told apart by the metadata outcome, and both carry the revision's content address as the after-hash; a
    /// refused write (412, 422) is audited by nothing.
    /// </summary>
    [Fact]
    public async Task CreatePolicyRevision_AuditRecordsCreateAndReplay_NotTheRefusedWrites()
    {
        await using var world = await StartAsync();

        var created = await CreateAsync(world, BodyA);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var replay = await CreateAsync(world, BodyAReordered);
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Id().ShouldBe(created.Id());

        // A stale If-Match (412) and an unknown field (422) are both refused before the handler: no audit row.
        (await CreateAsync(world, BodyD, ifMatch: $"\"{Guid.CreateVersion7()}\"")).Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await CreateAsync(world, """{"bogus":true}""")).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var hashHex = created.Text("hash")!["sha256:".Length..];
        var rows = await _fixture.ColumnAsync<string>(
            world.Database,
            $"""
            select action || '|' || coalesce(object_type, '-') || '|' || coalesce(object_id, '-') || '|' ||
                   coalesce(metadata ->> 'outcome', '-') || '|' || coalesce(encode(after_hash, 'hex'), '-') || '|' ||
                   actor_type || '|' || actor_id
              from public.audit_log
             where org_id = '{world.Org}' and action = 'policy_revision.create'
             order by seq
            """);

        rows.ShouldBe(
        [
            $"policy_revision.create|policy_revision|{created.Id():D}|created|{hashHex}|user|{world.User:D}",
            $"policy_revision.create|policy_revision|{created.Id():D}|unchanged|{hashHex}|user|{world.User:D}",
        ]);
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

    /// <summary>
    /// 07.1.1 finding S1: the cheap bounds pre-check refuses an array past its member's <c>maxItems</c> and a
    /// number literal longer than the canonical form allows, before the schema engine runs, and writes nothing.
    /// </summary>
    [Fact]
    public async Task CreatePolicyRevision_ArrayOverMaxItems_AndLongNumber_Are422_NoRow()
    {
        await using var world = await StartAsync();

        var suites = string.Join(",", Enumerable.Range(0, 1001).Select(index => $"\"suite-{index}\""));
        var tooMany = await CreateAsync(world, $"{{\"required_suites\":[{suites}]}}");
        tooMany.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        tooMany.ErrorCode.ShouldBe("policy_invalid");
        Detail(tooMany).GetProperty("path").GetString().ShouldBe("/required_suites");
        Detail(tooMany).GetProperty("reason").GetString().ShouldBe("too_many_items");

        // 40 digits: longer than the 32-character canonical number form (RFC 8785 §3.2.2.3).
        var longNumber = await CreateAsync(world, """{"flaky_budget":1234567890123456789012345678901234567890}""");
        longNumber.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        longNumber.ErrorCode.ShouldBe("policy_invalid");
        Detail(longNumber).GetProperty("path").GetString().ShouldBe("/flaky_budget");
        Detail(longNumber).GetProperty("reason").GetString().ShouldBe("number_too_long");

        // A long number under a member the schema does not declare keeps the unknown-field refusal (AC3).
        var undeclared = await CreateAsync(world, """{"bogus":1234567890123456789012345678901234567890}""");
        undeclared.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        undeclared.ErrorCode.ShouldBe("policy_unknown_field");

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

    /// <summary>
    /// 07.1.1 AC2: <c>policy:write</c> is release_approver-only — a member holding only that role writes, a viewer
    /// is refused 403 — and an <c>If-Match</c> naming the current head admits a new body (201) and a replay of the
    /// head body (200).
    /// </summary>
    [Fact]
    public async Task Permissions_ReleaseApproverWrites_ViewerCannot_AndIfMatchOnHeadSucceeds()
    {
        await using var world = await StartAsync();
        var approver = TenancyApiFactory.Resolved(user: await AddMemberAsync(world, "release_approver"), orgHeader: world.Org);
        var viewer = TenancyApiFactory.Resolved(user: await AddMemberAsync(world, "viewer"), orgHeader: world.Org);

        var first = await CreateAsync(world, BodyA, caller: approver);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        (await CreateAsync(world, BodyB, caller: viewer)).Status.ShouldBe(HttpStatusCode.Forbidden);

        // If-Match naming the current head: a new body appends (201), the head's own body replays (200).
        var next = await CreateAsync(world, BodyB, caller: approver, ifMatch: $"\"{first.Id()}\"");
        next.Status.ShouldBe(HttpStatusCode.Created, next.ErrorMessage);
        next.Body.GetProperty("revision_no").GetInt32().ShouldBe(2);

        var replay = await CreateAsync(world, BodyB, caller: approver, ifMatch: $"\"{next.Id()}\"");
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Id().ShouldBe(next.Id());

        (await RevisionCountAsync(world)).ShouldBe(2);
    }

    /// <summary>
    /// A project-scoped service credential cannot touch policy revisions at all: <c>policy:read</c> and
    /// <c>policy:write</c> are outside the service-role ceiling (<c>PermissionEvaluator.ServiceRoleCeiling</c>), so
    /// the C2 stage refuses the token as malformed with 403 <c>forbidden</c> before the handler runs. The refusal
    /// is identical for an existing revision of another project and an unknown id, so it is no existence oracle.
    /// </summary>
    [Fact]
    public async Task ProjectScopedServiceCredential_IsRefusedByTheServiceRoleCeiling_OnReadAndWrite()
    {
        await using var world = await StartAsync();
        var projectB = await CreateProjectAsync(world.Client, world.User, world.Org, "other");
        var created = await CreateAsync(world, BodyA);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var readScopedToB = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: projectB, servicePermissions: ["policy:read"]);
        var writeScopedToB = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: projectB, servicePermissions: ["policy:write"]);

        // policy:read is outside the service-role ceiling: the token is malformed, so it holds nothing.
        var existing = await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}", readScopedToB);
        var unknown = await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{Guid.CreateVersion7()}", readScopedToB);
        existing.Status.ShouldBe(HttpStatusCode.Forbidden);
        unknown.Status.ShouldBe(HttpStatusCode.Forbidden);
        existing.ErrorCode.ShouldBe("forbidden");
        // The same code and message for an existing and an unknown id: the refusal is no existence oracle.
        existing.ErrorCode.ShouldBe(unknown.ErrorCode);
        existing.ErrorMessage.ShouldBe(unknown.ErrorMessage);

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions", readScopedToB))
            .Status.ShouldBe(HttpStatusCode.Forbidden);

        var write = await CreateAsync(world, BodyB, caller: writeScopedToB);
        write.Status.ShouldBe(HttpStatusCode.Forbidden);
        write.ErrorCode.ShouldBe("forbidden");

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

    /// <summary>
    /// 07.1.1 finding S3: a revision whose project is soft-deleted is invisible on both read paths, with the same
    /// 404 code and message as an unknown id (the <c>OrchestratorSql.RevisionById</c> precedent).
    /// </summary>
    [Fact]
    public async Task SoftDeletedProject_HidesItsRevisions_AsTheSame404AsAnUnknownId()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, BodyA);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        await _fixture.ExecuteAsync(world.Database, $"update public.project set status = 'deleted' where id = '{world.Project}'");

        var hidden = await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}", world.Admin);
        var unknown = await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{Guid.CreateVersion7()}", world.Admin);
        hidden.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        hidden.ErrorCode.ShouldBe(unknown.ErrorCode);
        hidden.ErrorMessage.ShouldBe(unknown.ErrorMessage);

        var list = await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions", world.Admin);
        list.Status.ShouldBe(HttpStatusCode.NotFound);
        list.ErrorCode.ShouldBe(unknown.ErrorCode);
        list.ErrorMessage.ShouldBe(unknown.ErrorMessage);
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

    /// <summary>
    /// 07.1.1 finding CR5: the POST response body is the stored <c>jsonb</c> rendering, so a replay answers with
    /// the same bytes a later GET returns, even though the replay request reordered keys and added whitespace.
    /// </summary>
    [Fact]
    public async Task CreatePolicyRevision_ReplayBodyEqualsTheStoredRendering()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, BodyA);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var replay = await CreateAsync(world, BodyAReordered);
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);

        var read = await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}", world.Admin);
        read.Status.ShouldBe(HttpStatusCode.OK);

        // Both are the database's rendering of the same jsonb value: byte-identical, not merely semantically equal.
        var replayBody = replay.Body.GetProperty("body").GetRawText();
        replayBody.ShouldBe(read.Body.GetProperty("body").GetRawText());
        replayBody.ShouldNotBe(BodyAReordered);
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

        // The production list statements (PolicyRevisionListSql), with the handler's own parameter names bound.
        var firstPage = await ExplainAsync(connection, transaction, PolicyRevisionListSql.FirstPage, command =>
        {
            command.Parameters.AddWithValue("org_id", world.Org);
            command.Parameters.AddWithValue("project_id", world.Project);
            command.Parameters.AddWithValue("limit", 51);
        });
        firstPage.ShouldContain("policy_revision_revision_no_key");

        var nextPage = await ExplainAsync(connection, transaction, PolicyRevisionListSql.NextPage, command =>
        {
            command.Parameters.AddWithValue("org_id", world.Org);
            command.Parameters.AddWithValue("project_id", world.Project);
            command.Parameters.AddWithValue("cursor_revision_no", 50);
            command.Parameters.AddWithValue("cursor_id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("limit", 51);
        });
        nextPage.ShouldContain("policy_revision_revision_no_key");
    }

    /// <summary>Runs <c>EXPLAIN (FORMAT JSON)</c> over one production statement with its real parameters bound.</summary>
    private static async Task<string> ExplainAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Action<NpgsqlCommand> bind)
    {
        await using var command = new NpgsqlCommand("explain (format json) " + sql, connection, transaction);
        bind(command);

        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // ------------------------------------------------------------------ transport cap (00 §8.6, s9-plan)

    /// <summary>
    /// The 64 KiB transport cap: a body of exactly <c>MaxPolicyBytes</c> is read and stored, one byte more is
    /// refused <c>413</c> before parsing, and the streamed (no <c>Content-Length</c>) path caps the same way.
    /// </summary>
    [Fact]
    public async Task CreatePolicyRevision_BodyAtTheCap_IsAccepted_OneByteOver_Is413()
    {
        await using var world = await StartAsync();

        var atCap = CapBody();
        atCap.Length.ShouldBe(64 * 1024);

        var accepted = await CreateAsync(world, atCap);
        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
        (await RevisionCountAsync(world)).ShouldBe(1);

        var overCap = await CreateAsync(world, atCap + " ");
        overCap.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        overCap.ErrorCode.ShouldBe("payload_too_large");

        // The same one-byte-over body with no Content-Length: the endpoint must cap the stream it reads.
        var streamed = await SendRawAsync(
            world.Client, HttpMethod.Post, $"/v1/projects/{world.Project}/policies", world.Admin, atCap + " ", chunked: true);
        streamed.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        streamed.ErrorCode.ShouldBe("payload_too_large");

        (await RevisionCountAsync(world)).ShouldBe(1);
    }

    /// <summary>A schema-valid <c>policy-revision.v1</c> body of exactly 64 KiB (all-ASCII, so chars are bytes).</summary>
    private static string CapBody()
    {
        var items = new List<string>();
        for (var i = 0; i < 322; i++)
        {
            items.Add(i.ToString("D3", CultureInfo.InvariantCulture) + new string('a', 197));
        }

        items.Add("zzz" + new string('b', 143));

        return "{\"required_suites\":[" + string.Join(",", items.Select(item => "\"" + item + "\"")) + "]}";
    }

    // ------------------------------------------------------------------ malformed bodies

    /// <summary>An empty body and a non-object root are <c>422 policy_invalid</c>, never a 500 and never a row.</summary>
    [Fact]
    public async Task CreatePolicyRevision_EmptyBody_AndArrayRoot_Are422()
    {
        await using var world = await StartAsync();

        var empty = await CreateAsync(world, string.Empty);
        empty.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        empty.ErrorCode.ShouldBe("policy_invalid");
        Detail(empty).GetProperty("reason").GetString().ShouldBe("invalid_json");

        var array = await CreateAsync(world, "[1,2]");
        array.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        array.ErrorCode.ShouldBe("policy_invalid");

        (await RevisionCountAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// A lone UTF-16 surrogate, a duplicate member name and numbers a double cannot hold exactly are all
    /// <c>422 policy_invalid</c> before any statement (I-JSON, RFC 7493 §2.2; RFC 8785 §3.2.3).
    /// </summary>
    [Fact]
    public async Task CreatePolicyRevision_LoneSurrogate_DuplicateKeys_AndInexactNumbers_Are422()
    {
        await using var world = await StartAsync();

        var surrogate = await CreateAsync(world, """{"required_suites":["\ud800"]}""");
        surrogate.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        surrogate.ErrorCode.ShouldBe("policy_invalid");
        Detail(surrogate).GetProperty("reason").GetString().ShouldBe("invalid_value");

        var duplicate = await CreateAsync(world, """{"flaky_budget":3,"flaky_budget":4}""");
        duplicate.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        duplicate.ErrorCode.ShouldBe("policy_invalid");

        foreach (var body in new[]
        {
            """{"flaky_budget":1e400}""",
            """{"flaky_budget":0.1000000000000000001}""",
            """{"flaky_budget":9007199254740993}""",
        })
        {
            var inexact = await CreateAsync(world, body);
            inexact.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{body}: {inexact.ErrorMessage}");
            inexact.ErrorCode.ShouldBe("policy_invalid");
        }

        (await RevisionCountAsync(world)).ShouldBe(0);
    }

    /// <summary>The media type is not part of the contract (00 §9.2 has no 415): the raw body is read regardless.</summary>
    [Fact]
    public async Task CreatePolicyRevision_WrongContentType_StillReadsTheRawBody()
    {
        await using var world = await StartAsync();

        var answer = await SendRawAsync(
            world.Client, HttpMethod.Post, $"/v1/projects/{world.Project}/policies", world.Admin, BodyA, contentType: "text/plain");

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        (await RevisionCountAsync(world)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ If-Match

    /// <summary>A malformed <c>If-Match</c>, and one naming a head when no revision exists, are both 412.</summary>
    [Fact]
    public async Task CreatePolicyRevision_MalformedIfMatch_AndIfMatchOnTheFirstRevision_Are412()
    {
        await using var world = await StartAsync();

        var malformed = await CreateAsync(world, BodyA, ifMatch: "not-a-revision-id");
        malformed.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        malformed.ErrorCode.ShouldBe("precondition_failed");
        Detail(malformed).GetProperty("reason").GetString().ShouldBe("invalid");

        // No revision exists yet, so any cached head is stale: 412, nothing written.
        var first = await CreateAsync(world, BodyA, ifMatch: $"\"{Guid.CreateVersion7()}\"");
        first.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        first.ErrorCode.ShouldBe("precondition_failed");
        Detail(first).GetProperty("reason").GetString().ShouldBe("stale");
        (await RevisionCountAsync(world)).ShouldBe(0);

        // Without the header the same body is the first revision.
        (await CreateAsync(world, BodyA)).Status.ShouldBe(HttpStatusCode.Created);
        (await RevisionCountAsync(world)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ list bounds

    /// <summary>The <c>limit</c> bounds are inclusive (1..200) and a cursor this endpoint did not issue is 422.</summary>
    [Fact]
    public async Task ListPolicyRevisions_LimitBounds_AndCursorTampering_Are422()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, BodyA);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions?limit=1", world.Admin))
            .Status.ShouldBe(HttpStatusCode.OK);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions?limit=200", world.Admin))
            .Status.ShouldBe(HttpStatusCode.OK);

        foreach (var limit in new[] { "0", "201", "-1", "+1", "1.0", "abc" })
        {
            var answer = await world.Client.SendAsync(
                HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions?limit={Uri.EscapeDataString(limit)}", world.Admin);
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"limit={limit}");
        }

        foreach (var cursor in new[]
        {
            "not-a-cursor",
            $"00000000-{Guid.CreateVersion7():D}",
            $"zzzzzzzz-{Guid.CreateVersion7():D}",
            "00000001-not-a-uuid",
        })
        {
            var answer = await world.Client.SendAsync(
                HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions?cursor={Uri.EscapeDataString(cursor)}", world.Admin);
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"cursor={cursor}");
        }
    }

    // ------------------------------------------------------------------ read permission

    /// <summary>A credential that does not claim <c>policy:read</c> is refused 403 before the handler (C2 stage 4).</summary>
    [Fact]
    public async Task Read_WithoutPolicyRead_Is403()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, BodyA);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var ci = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["run:create"]);

        var one = await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}", ci);
        one.Status.ShouldBe(HttpStatusCode.Forbidden);
        one.ErrorCode.ShouldBe("forbidden");

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions", ci))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
    }
}
