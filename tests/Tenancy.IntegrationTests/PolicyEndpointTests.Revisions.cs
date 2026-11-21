using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class PolicyEndpointTests
{
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
}
