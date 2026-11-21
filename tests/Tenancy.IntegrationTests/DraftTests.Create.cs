using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The create route: one draft row, an ETag over its version, the author taken from the caller's membership,
/// idempotent replay through the request hash, and the content bounds the contract closes.
/// </summary>
public sealed partial class DraftTests
{
    [Fact]
    public async Task Create_StoresTheDraft_AndAnswers201WithTheVersionAsETag()
    {
        await using var world = await StartAsync();
        var membership = await MembershipAsync(world);

        var created = await CreateAsync(world);

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        created.ETag.ShouldBe("\"1\"", "the ETag is the draft's version, quotes included");
        created.Id().ShouldNotBe(Guid.Empty);
        created.Body.GetProperty("project_id").GetGuid().ShouldBe(world.Project);
        created.Body.GetProperty("author_membership_id").GetGuid().ShouldBe(membership);
        created.Body.GetProperty("kind").GetString().ShouldBe("template");
        created.Body.GetProperty("base_commit").GetString().ShouldBe("abcdef1234567");
        created.Body.GetProperty("status").GetString().ShouldBe("draft");
        created.Body.GetProperty("version").GetInt32().ShouldBe(1);
        created.Body.GetProperty("content").GetProperty("files")[0].GetProperty("path").GetString()
            .ShouldBe("tests/checkout.spec.ts");
        created.Body.GetProperty("allow_unverified").GetBoolean().ShouldBeFalse();
        created.Body.GetProperty("created_at").ValueKind.ShouldBe(JsonValueKind.String);
        foreach (var nullMember in new[] { "provenance", "ai_run_id", "dry_run_id", "unverified_reason", "pr_branch",
                     "pr_commit_sha", "pr_url", "pr_number", "linked_case_id", "linked_revision_id" })
        {
            created.Body.GetProperty(nullMember).ValueKind.ShouldBe(JsonValueKind.Null, $"{nullMember} starts absent");
        }

        (await DraftCountAsync(world)).ShouldBe(1);
        (await StoredAsync<int>(world, created.Id(), "version")).ShouldBe(1);
        (await AuditCountAsync(world, created.Id(), "draft.create")).ShouldBe(
            1, "the create and its audit row commit together");
    }

    [Fact]
    public async Task Create_ReplayingTheSameKeyAndBody_AnswersTheStoredResponseAndWritesNoSecondRow()
    {
        await using var world = await StartAsync();
        var key = NewKey();
        var body = Body();

        var first = await CreateAsync(world, body, key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        var replay = await CreateAsync(world, body, key);

        replay.Status.ShouldBe(HttpStatusCode.Created, "a replay answers the original response");
        replay.Id().ShouldBe(first.Id());
        replay.ETag.ShouldBe(first.ETag);
        (await DraftCountAsync(world)).ShouldBe(1, "one key names one draft");
        (await AuditCountAsync(world, first.Id(), "draft.create")).ShouldBe(1, "the replay is not a second mutation");
    }

    [Fact]
    public async Task Create_ReusingTheKeyForAnotherBody_Is409IdempotencyConflict_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var key = NewKey();
        (await CreateAsync(world, Body(), key)).Status.ShouldBe(HttpStatusCode.Created);

        var conflicting = await CreateAsync(world, Body("tests/other.spec.ts"), key);

        conflicting.Status.ShouldBe(HttpStatusCode.Conflict, conflicting.ErrorMessage);
        conflicting.ErrorCode.ShouldBe("idempotency_conflict");
        (await DraftCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Create_EachContentBound_Is422DraftInvalidNamingItsField()
    {
        await using var world = await StartAsync();
        var longPath = new string('a', 513);
        var longRef = new string('b', 513);
        var controlPath = "tests/" + (char)7 + ".ts";

        var invalid = new (string Scenario, string Field, JsonObject Body)[]
        {
            ("no files", "files", new JsonObject { ["kind"] = "template", ["base_commit"] = "abcdef1", ["content"] = Content() }),
            ("201 files", "files", new JsonObject
            {
                ["kind"] = "template", ["base_commit"] = "abcdef1",
                ["content"] = Content([.. Enumerable.Range(0, 201).Select(i => (JsonNode)File($"tests/{i}.ts"))]),
            }),
            ("path over 512 characters", "path", Body(longPath)),
            ("absolute path", "path", Body("/etc/passwd")),
            ("parent segment", "path", Body("tests/../secrets.ts")),
            ("backslash", "path", Body("tests\\checkout.ts")),
            ("control character in the path", "path", Body(controlPath)),
            ("duplicate path", "path", new JsonObject
            {
                ["kind"] = "template", ["base_commit"] = "abcdef1",
                ["content"] = Content(File("tests/one.ts"), File("tests/one.ts", bodyRef: "drafts/two.ts")),
            }),
            ("body_sha without the sha256 prefix", "body_sha", Body(sha: new string('a', 64))),
            ("body_sha with a non-hex character", "body_sha", Body(sha: "sha256:" + new string('g', 64))),
            ("body_ref over 512 characters", "body_ref", Body(bodyRef: longRef)),
            ("base_commit with a non-hex character", "base_commit", new JsonObject
            {
                ["kind"] = "template", ["base_commit"] = "zzzzzzz", ["content"] = Content(File()),
            }),
            ("base_commit shorter than 7", "base_commit", new JsonObject
            {
                ["kind"] = "template", ["base_commit"] = "abc123", ["content"] = Content(File()),
            }),
        };

        foreach (var (scenario, field, body) in invalid)
        {
            var refused = await CreateAsync(world, body);

            ShouldNameField(refused, field, scenario);
        }

        (await DraftCountAsync(world)).ShouldBe(0, "no refused body writes a row");

        // Positive control: the same shape with every member inside its bound is accepted.
        (await CreateAsync(world)).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_BodyOver256KiB_Is413PayloadTooLarge_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var oversized = new JsonObject
        {
            ["kind"] = "template",
            ["base_commit"] = "abcdef1234567",
            ["content"] = Content(File(bodyRef: "drafts/" + new string('x', 300 * 1024))),
        };

        var refused = await CreateAsync(world, oversized);

        refused.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("payload_too_large");
        (await DraftCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_CallerWithoutAnActiveMembership_Is403DraftAuthorRequired()
    {
        await using var world = await StartAsync();

        // A service principal holding draft:write but bound to no membership: the permission stage passes, so the
        // refusal proves the author gate itself, which reads an *active* membership (binding decision (d)).
        var service = TenancyApiFactory.Resolved(serviceOrg: world.Org, servicePermissions: ["draft:write"]);

        var refused = await CreateAsync(world, caller: service);

        refused.Status.ShouldBe(HttpStatusCode.Forbidden, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("draft_author_required");
        (await DraftCountAsync(world)).ShouldBe(0);

        // Positive control: the founder, whose membership is active, creates.
        (await CreateAsync(world)).Status.ShouldBe(HttpStatusCode.Created);
    }
}
