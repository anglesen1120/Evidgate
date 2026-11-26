using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class AuthoringIntakeTests(PostgresTenancyFixture fixture)
{
    [Fact]
    public async Task TextLimitsBoundaries()
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        var max = new string('x', 199998) + "e\u0301\U0001F600";
        var key = NewKey();
        var body = new { kind = "text", text = max, feature_node_id = world.Node };
        var created = await world.PostAsync(body, key);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        created.Text("state").ShouldBe("ready");
        var id = created.Id("intake_id");
        var replay = await world.PostAsync(body, key);
        JsonElement.DeepEquals(created.Body, replay.Body).ShouldBeTrue();
        var view = await world.Client.SendAsync(HttpMethod.Get, $"{world.Path}/{id}", world.As);
        view.Status.ShouldBe(HttpStatusCode.OK);
        view.Body.GetProperty("extracted_char_count").GetInt32().ShouldBe(200000);
        view.Body.TryGetProperty("extracted_text", out _).ShouldBeFalse();
        (await fixture.ScalarAsync<string>(world.Database, $"select right(extracted_text, 2) from public.authoring_intake where id = '{id}'")).ShouldBe("é\U0001F600");
        (await fixture.ScalarAsync<int>(world.Database, $"select count(*)::int from public.audit_log where org_id='{world.Org}' and object_id='{id}' and action='authoring.intake.create'")).ShouldBe(1);
        await RefusedTextAsync(world, max + "x", "size_limit_exceeded");
        await RefusedTextAsync(world, "", "empty_input");
        (await fixture.ScalarAsync<int>(world.Database, $"select count(*)::int from public.authoring_intake where org_id='{world.Org}'")).ShouldBe(1);
    }

    [Fact]
    public async Task SecretShapedTextRefused()
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        var clean = await world.PostAsync(new { kind = "text", feature_node_id = world.Node, text = "Check the sign-in screen" });
        clean.Status.ShouldBe(HttpStatusCode.Created);
        const string marker = "NotARealCredential";
        var refused = await world.PostAsync(new { kind = "text", feature_node_id = world.Node, text = "Bearer " + marker });
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.Body.GetRawText().ShouldNotContain(marker);
        (await fixture.ScalarAsync<int>(world.Database, $"select count(*)::int from public.authoring_intake where org_id='{world.Org}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Document_DuplicateArtifact409_UnknownMediaAndOversizeAreRefused()
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        var artifact = await world.ArtifactAsync("requirement_document", "application/pdf", 123);
        var bad = await world.PostAsync(new { kind = "pdf", feature_node_id = world.Node, artifact_id = artifact, media_type = "image/png" });
        Reason(bad).ShouldBe("media_type_not_allowed");
        var large = await world.PostAsync(new { kind = "pdf", feature_node_id = world.Node, artifact_id = artifact, bytes = 25L * 1024 * 1024 + 1 });
        Reason(large).ShouldBe("size_limit_exceeded");
        var body = new { kind = "pdf", feature_node_id = world.Node, artifact_id = artifact };
        var accepted = await world.PostAsync(body);
        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
        accepted.Text("state").ShouldBe("received");
        var view = await world.Client.SendAsync(HttpMethod.Get, $"{world.Path}/{accepted.Id("intake_id")}", world.As);
        view.Text("eta_class").ShouldBe("short");
        var conflict = await world.PostAsync(body);
        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
    }

    [Fact]
    public async Task PayloadTooLarge_PrecedesJsonParsing()
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        var answer = await world.Client.SendRawAsync(HttpMethod.Post, world.Path, world.As, new string('!', 1024 * 1024 + 1));
        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        answer.ErrorCode.ShouldBe("payload_too_large");
    }

    [Fact]
    public async Task ScopeAndPermissions_RefuseForeignAndSiblingResources()
    {
        await using var world = await IntakeApiWorld.StartAsync(fixture);
        await using var other = await IntakeApiWorld.StartAsync(fixture);
        var body = new { kind = "text", feature_node_id = world.Node, text = "Review navigation" };
        var accepted = await world.PostAsync(body);
        accepted.Status.ShouldBe(HttpStatusCode.Created);
        var detail = $"{world.Path}/{accepted.Id("intake_id")}";
        (await other.Client.SendAsync(HttpMethod.Get, detail, other.As)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.PostAsync(new { kind = "text", feature_node_id = other.Node, text = "Foreign" })).Status.ShouldBe(HttpStatusCode.NotFound);
        var viewer = await AddMemberAsync(fixture, world.Database, world.Org, "viewer");
        var caller = TenancyApiFactory.Resolved(user: viewer, orgHeader: world.Org);
        (await world.Client.SendAsync(HttpMethod.Get, detail, caller)).Status.ShouldBe(HttpStatusCode.OK);
        (await world.Client.SendAsync(HttpMethod.Post, world.Path, caller, body, NewKey())).Status.ShouldBe(HttpStatusCode.Forbidden);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("sibling"));
        var scoped = TenancyApiFactory.Caller(org: world.Org, user: world.User, project: sibling);
        var mismatch = await world.Client.SendAsync(HttpMethod.Get, detail, scoped);
        mismatch.Status.ShouldBe(HttpStatusCode.Forbidden);
        mismatch.ErrorCode.ShouldBe("project_scope_mismatch");
        var inScope = TenancyApiFactory.Caller(org: world.Org, user: world.User, project: world.Project);
        (await world.Client.SendAsync(HttpMethod.Get, detail, inScope)).Status.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task RefusedTextAsync(IntakeApiWorld world, string text, string reason) =>
        Reason(await world.PostAsync(new { kind = "text", feature_node_id = world.Node, text })).ShouldBe(reason);

    private static string? Reason(ApiAnswer answer)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        return answer.Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString();
    }
}
