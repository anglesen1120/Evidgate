using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Saving a selection of a ready intake's extracted text as a <c>spec_section</c> requirement through the real host and
/// PostgreSQL: the stored body and metadata, the refusals that must write nothing, scoping, replay and the audit row.
/// </summary>
[Trait("Category", "Docker")]
public sealed class IntakeSpecSectionTests(PostgresTenancyFixture fixture)
{
    [Theory]
    [InlineData(null, null, "A😀BéC")]
    [InlineData(1, 3, "😀B")]
    [InlineData(1, null, "😀BéC")]
    [InlineData(null, 2, "A😀")]
    public async Task Save_WholeOrCodePointRange_PreservesSelectedTextAndMetadata(int? start, int? end, string expected)
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        var intake = await TextAsync(world, "A😀Be\u0301C");
        var parent = await world.Client.SendAsync(HttpMethod.Post, Requirements(world), world.As,
            new { kind = "epic", title = "Parent" }, NewKey());
        parent.Status.ShouldBe(HttpStatusCode.Created, parent.ErrorMessage);
        var body = Selection(start, end);
        body["feature_node_id"] = world.Node;
        body["parent_requirement_id"] = parent.Id("requirement_id");
        body["external_ref"] = "SPEC:1";
        body["reason"] = "Selected specification";

        var answer = await SaveAsync(world, intake, body);

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        var id = answer.Id("requirement_id");
        answer.ETag.ShouldBe("\"1\"");
        var detail = await world.Client.SendAsync(HttpMethod.Get, $"{Requirements(world)}/{id}", world.As);
        detail.Text("kind").ShouldBe("spec_section");
        detail.Text("title").ShouldBe("Section");
        detail.Id("feature_node_id").ShouldBe(world.Node);
        detail.Id("parent_requirement_id").ShouldBe(parent.Id("requirement_id"));
        detail.Text("external_ref").ShouldBe("SPEC:1");
        (await Scalar<string>(world, $"select body from public.requirement_version where requirement_id = '{id}'")).ShouldBe(expected);
        (await Scalar<string>(world, $"select source from public.requirement_version where requirement_id = '{id}'")).ShouldBe("import");
        (await Scalar<string>(world, $"select reason from public.requirement_version where requirement_id = '{id}'")).ShouldBe("Selected specification");
    }

    [Theory]
    [InlineData(0, 0, "/end")]
    [InlineData(2, 1, "/end")]
    [InlineData(0, 5, "/end")]
    [InlineData(-1, 1, "/start")]
    [InlineData(0, -1, "/end")]
    [InlineData(4, null, "/start")]
    [InlineData(int.MaxValue, int.MaxValue, "/start")]
    public async Task Save_InvalidRange_RefusesWithoutWrites(int start, int? end, string path)
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        var intake = await TextAsync(world, "A😀BC");
        var before = await SnapshotAsync(world);

        var answer = await SaveAsync(world, intake, Selection(start, end));

        AssertInvalid(answer, path, "range_invalid");
        (await SnapshotAsync(world)).ShouldBe(before);
        (await SaveAsync(world, intake, Selection(0, 4))).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData(20000, true)]
    [InlineData(20001, false)]
    public async Task Save_NormalizedBodyCap_AcceptsBoundaryAndRefusesOverflow(int length, bool accepted)
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        var intake = await TextAsync(world, string.Concat(Enumerable.Repeat("e\u0301", length)));
        var before = await SnapshotAsync(world);
        var answer = await SaveAsync(world, intake, Selection());
        if (accepted)
        {
            answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
            (await Scalar<int>(world, $"select char_length(body) from public.requirement_version where requirement_id = '{answer.Id("requirement_id")}'"))
                .ShouldBe(length);
        }
        else
        {
            AssertInvalid(answer, "/end", "length");
            (await SnapshotAsync(world)).ShouldBe(before);
            (await SaveAsync(world, intake, Selection(0, 20000))).Status.ShouldBe(HttpStatusCode.Created);
        }
    }

    [Fact]
    public async Task Save_WhitespaceSelection_RefusesWithoutWrites()
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        var intake = await TextAsync(world, "A    B");
        var before = await SnapshotAsync(world);
        var answer = await SaveAsync(world, intake, Selection(1, 4));
        AssertInvalid(answer, "/start", "empty_selection");
        (await SnapshotAsync(world)).ShouldBe(before);
        (await SaveAsync(world, intake, Selection())).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("received")]
    [InlineData("extracting")]
    [InlineData("rejected")]
    [InlineData("image_ready")]
    public async Task Save_IntakeWithoutReadyText_ConflictsWithoutWrites(string state)
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        var image = state == "image_ready";
        var artifact = await world.ArtifactAsync(image ? "requirement_image" : "requirement_document",
            image ? "image/png" : "application/pdf", 123);
        object request = image
            ? new { kind = "image", artifact_id = artifact }
            : new { kind = "pdf", feature_node_id = world.Node, artifact_id = artifact };
        var created = await world.PostAsync(request);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var intake = created.Id("intake_id");
        if (state is "extracting" or "rejected")
        {
            var failure = state == "rejected" ? "'extraction_failed'" : "null";
            await fixture.ExecuteAsync(world.Database,
                $"update public.authoring_intake set state = '{state}', failure_code = {failure} where id = '{intake}'");
        }
        var before = await SnapshotAsync(world);
        var answer = await SaveAsync(world, intake, Selection());
        answer.Status.ShouldBe(HttpStatusCode.Conflict, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("invalid_transition");
        (await SnapshotAsync(world)).ShouldBe(before);
        (await SaveAsync(world, await TextAsync(world, "Ready text"), Selection())).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Save_ForeignProjectOrTenantIntake_IsIndistinguishableFromMissing()
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        var intake = await TextAsync(world, "Stored text");
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("sibling"));
        var rivalUser = Guid.CreateVersion7();
        var rivalOrg = await CreateOrgAsync(world.Client, rivalUser, Unique.Slug("rival"));
        var rivalProject = await CreateProjectAsync(world.Client, rivalUser, rivalOrg, Unique.Slug("rival-doc"));
        var before = await SnapshotAsync(world);
        foreach (var project in new[] { sibling, rivalProject })
        {
            var caller = project == sibling ? world.As : TenancyApiFactory.Resolved(user: rivalUser, orgHeader: rivalOrg);
            var prefix = $"/v1/projects/{project}/authoring/intakes";
            var foreign = await world.Client.SendAsync(HttpMethod.Post, $"{prefix}/{intake}/spec-section", caller, Selection(), NewKey());
            var missing = await world.Client.SendAsync(HttpMethod.Post, $"{prefix}/{Guid.CreateVersion7()}/spec-section", caller, Selection(), NewKey());
            foreign.Status.ShouldBe(HttpStatusCode.NotFound, foreign.ErrorMessage);
            missing.Status.ShouldBe(HttpStatusCode.NotFound);
            foreign.ErrorCode.ShouldBe(missing.ErrorCode);
            foreign.ErrorMessage.ShouldBe(missing.ErrorMessage);
        }
        (await SnapshotAsync(world)).ShouldBe(before);
        (await SaveAsync(world, intake, Selection())).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Save_IdempotentReplay_CreatesOneVersionAndAudit()
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        var intake = await TextAsync(world, "Stored text");
        var key = NewKey();
        var first = await SaveAsync(world, intake, Selection(), key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        var before = await SnapshotAsync(world);
        var replay = await SaveAsync(world, intake, Selection(), key);
        replay.Status.ShouldBe(HttpStatusCode.Created);
        JsonElement.DeepEquals(first.Body, replay.Body).ShouldBeTrue();
        (await SnapshotAsync(world)).ShouldBe(before);
        (await Scalar<long>(world, $"select count(*) from public.requirement where org_id = '{world.Org}'")).ShouldBe(1);
        (await Scalar<long>(world, $"select count(*) from public.requirement_version where org_id = '{world.Org}'")).ShouldBe(1);
        (await Scalar<long>(world, $"select count(*) from public.audit_log where org_id = '{world.Org}' and action = 'authoring.requirement.create'")).ShouldBe(1);
        (await Scalar<bool>(world, $"select reason is null from public.requirement_version where requirement_id = '{first.Id("requirement_id")}'")).ShouldBeTrue();
    }

    [Fact]
    public async Task Save_ViewerCannotWrite_WhileOwnerCan()
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        var intake = await TextAsync(world, "Stored text");
        var user = Guid.CreateVersion7();
        var member = Guid.CreateVersion7();
        await fixture.ExecuteAsync(world.Database, $"""
            insert into public."user" (id, email) values ('{user}', 'section-{user:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{member}', '{world.Org}', '{user}', 'section-{user:N}@example.test', 'active');
            """);
        var role = await Scalar<Guid>(world, "select id from public.role where org_id is null and code = 'viewer'");
        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/orgs/{world.Org}/memberships/{member}/roles", world.As,
            new { role_id = role }, NewKey())).Status.ShouldBe(HttpStatusCode.OK);
        var viewer = TenancyApiFactory.Resolved(user: user, orgHeader: world.Org);
        (await world.Client.SendAsync(HttpMethod.Get, $"{world.Path}/{intake}", viewer)).Status.ShouldBe(HttpStatusCode.OK);
        var before = await SnapshotAsync(world);
        var denied = await world.Client.SendAsync(HttpMethod.Post, $"{world.Path}/{intake}/spec-section", viewer, Selection(), NewKey());
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await SnapshotAsync(world)).ShouldBe(before);
        (await SaveAsync(world, intake, Selection())).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Save_ForeignFeatureNode_RefusesWithoutWrites()
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        var intake = await TextAsync(world, "Stored text");
        var project = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("other"));
        var node = await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/authoring/nodes", world.As,
            new { kind = "module", title = "Other" }, NewKey());
        node.Status.ShouldBe(HttpStatusCode.Created, node.ErrorMessage);
        var body = Selection();
        body["feature_node_id"] = node.Id("node_id");
        var before = await SnapshotAsync(world);
        AssertInvalid(await SaveAsync(world, intake, body), "/feature_node_id", "not_found");
        (await SnapshotAsync(world)).ShouldBe(before);
        body["feature_node_id"] = world.Node;
        (await SaveAsync(world, intake, body)).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("title", null, "/title", "invalid_type")]
    [InlineData("title", "", "/title", "length")]
    [InlineData("unexpected", "value", "/unexpected", "unknown_field")]
    [InlineData("start", "1", "/start", "invalid_type")]
    [InlineData("end", null, "/end", "invalid_type")]
    public async Task Save_InvalidMember_RefusesWithoutWrites(string member, string? value, string path, string reason)
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        var intake = await TextAsync(world, "Stored text");
        var body = Selection();
        body[member] = value;
        var before = await SnapshotAsync(world);
        AssertInvalid(await SaveAsync(world, intake, body), path, reason);
        (await SnapshotAsync(world)).ShouldBe(before);
        (await SaveAsync(world, intake, Selection())).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Save_MissingIdempotencyKey_Is400WithoutWrites()
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        var intake = await TextAsync(world, "Stored text");
        var before = await SnapshotAsync(world);
        var answer = await world.Client.SendAsync(HttpMethod.Post, $"{world.Path}/{intake}/spec-section", world.As, Selection());
        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
        (await SnapshotAsync(world)).ShouldBe(before);
        (await SaveAsync(world, intake, Selection())).Status.ShouldBe(HttpStatusCode.Created);
    }

    private static Dictionary<string, object?> Selection(int? start = null, int? end = null)
    {
        var body = new Dictionary<string, object?> { ["title"] = "Section" };
        if (start is { } from) { body["start"] = from; }
        if (end is { } to) { body["end"] = to; }
        return body;
    }

    private static async Task<Guid> TextAsync(IntakeApiWorld world, string text)
    {
        var answer = await world.PostAsync(new { kind = "text", feature_node_id = world.Node, text });
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        return answer.Id("intake_id");
    }

    private static Task<ApiAnswer> SaveAsync(IntakeApiWorld world, Guid intake, object body, string? key = null) =>
        world.Client.SendAsync(HttpMethod.Post, $"{world.Path}/{intake}/spec-section", world.As, body, key ?? NewKey());

    private static string Requirements(IntakeApiWorld world) => $"/v1/projects/{world.Project}/authoring/requirements";

    private Task<T> Scalar<T>(IntakeApiWorld world, string sql) => fixture.ScalarAsync<T>(world.Database, sql);

    private Task<string> SnapshotAsync(IntakeApiWorld world) => Scalar<string>(world, $"""
        select jsonb_build_array(
            (select count(*) from public.requirement where org_id = '{world.Org}'),
            (select count(*) from public.requirement_version where org_id = '{world.Org}'),
            (select count(*) from public.audit_log where org_id = '{world.Org}'))::text
        """);

    private static void AssertInvalid(ApiAnswer answer, string path, string reason)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        var details = answer.Body.GetProperty("error").GetProperty("details").EnumerateArray();
        details.ShouldContain(detail => detail.GetProperty("path").GetString() == path && detail.GetProperty("reason").GetString() == reason);
    }
}
