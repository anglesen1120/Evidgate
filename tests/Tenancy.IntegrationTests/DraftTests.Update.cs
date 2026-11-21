using System.Net;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The update route: If-Match over the draft's version (a stale tag is 412 naming the current version, never a lost
/// update), one winner under real concurrency, and the state machine's own 409.
/// </summary>
public sealed partial class DraftTests
{
    [Fact]
    public async Task Update_StaleEtag_Is412NamingTheCurrentVersion_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var id = await CreateDraftAsync(world);
        var before = await StoredAsync<string>(world, id, "content #>> '{files,0,path}'");

        foreach (var stale in new[] { "\"2\"", "\"3\"", "\"99\"" })
        {
            var refused = await PatchAsync(world, id, stale);

            ShouldBeStale(refused, currentVersion: 1, $"If-Match {stale} is not the head");
        }

        // Version 0 is not an ETag this endpoint ever issued: the request is invalid, not stale.
        var zero = await PatchAsync(world, id, "\"0\"");
        zero.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, zero.ErrorMessage);
        zero.ErrorCode.ShouldBe("request_invalid");
        Details(zero).ShouldContain(detail => detail.Path == "If-Match" && detail.Reason == "invalid");

        (await StoredAsync<int>(world, id, "version")).ShouldBe(1, "a refused patch bumps nothing");
        (await StoredAsync<string>(world, id, "content #>> '{files,0,path}'"))
            .ShouldBe(before, "the content is untouched");
        (await AuditCountAsync(world, id, "draft.update")).ShouldBe(0, "a refused patch is not audited");
    }

    [Fact]
    public async Task Update_CurrentEtag_Is200_AndVersionIncrementsByExactlyOne()
    {
        await using var world = await StartAsync();
        var id = await CreateDraftAsync(world);

        var first = await PatchAsync(world, id, "\"1\"", UpdateBody("tests/first.spec.ts"));

        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        first.ETag.ShouldBe("\"2\"");
        first.Body.GetProperty("version").GetInt32().ShouldBe(2);
        first.Body.GetProperty("content").GetProperty("files")[0].GetProperty("path").GetString()
            .ShouldBe("tests/first.spec.ts");
        (await StoredAsync<int>(world, id, "version")).ShouldBe(2);
        (await AuditCountAsync(world, id, "draft.update")).ShouldBe(1);

        var second = await PatchAsync(world, id, "\"2\"", UpdateBody("tests/second.spec.ts"));

        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        second.ETag.ShouldBe("\"3\"");
        (await StoredAsync<int>(world, id, "version")).ShouldBe(3, "one update, one version step");

        var read = await GetAsync(world, id);
        read.ETag.ShouldBe("\"3\"");
        read.Body.GetProperty("content").GetProperty("files")[0].GetProperty("path").GetString()
            .ShouldBe("tests/second.spec.ts", "the read answers the committed content");
    }

    [Fact]
    public async Task Update_ConcurrentPatchesWithTheSameEtag_HaveExactlyOneWinner()
    {
        await using var world = await StartAsync();
        var id = await CreateDraftAsync(world);

        for (var round = 1; round <= 5; round++)
        {
            var etag = $"\"{round}\"";

            var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(async index =>
            {
                using var client = world.Api.CreateClient();

                return await client.SendAsync(
                    HttpMethod.Patch, World.Draft(id), world.Admin,
                    UpdateBody($"tests/round-{round}-{index}.spec.ts"), NewKey(), etag);
            }));

            answers.Count(answer => answer.Status == HttpStatusCode.OK).ShouldBe(
                1, $"round {round}: one writer wins the version");
            foreach (var loser in answers.Where(answer => answer.Status != HttpStatusCode.OK))
            {
                ShouldBeStale(loser, currentVersion: round + 1, $"round {round}: the losing writers are stale");
            }

            (await StoredAsync<int>(world, id, "version")).ShouldBe(
                round + 1, $"round {round}: exactly one version step, never one per request");
        }

        (await AuditCountAsync(world, id, "draft.update")).ShouldBe(5, "one audit row per winning update");
    }

    [Fact]
    public async Task Update_OnANonDraftStatus_Is409DraftStateConflict()
    {
        await using var world = await StartAsync();
        var id = await SeedAsync(world, "dry_passed");
        var before = await StoredAsync<string>(world, id, "content #>> '{files,0,path}'");
        var version = await StoredAsync<int>(world, id, "version");

        // The ETag names the seeded row's head, so the refusal proved here is the state guard, not a stale tag.
        var refused = await PatchAsync(world, id, $"\"{version}\"");

        refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("draft_state_conflict");
        (await StoredAsync<int>(world, id, "version")).ShouldBe(version);
        (await StoredAsync<string>(world, id, "content #>> '{files,0,path}'")).ShouldBe(before);
        (await AuditCountAsync(world, id, "draft.update")).ShouldBe(0);

        // Positive control: the same patch on the same body at status 'draft' is accepted.
        var draft = await CreateDraftAsync(world);
        (await PatchAsync(world, draft, "\"1\"")).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Update_WithoutIfMatch_Is422RequestInvalidNamingTheHeader()
    {
        await using var world = await StartAsync();
        var id = await CreateDraftAsync(world);

        var refused = await PatchAsync(world, id, etag: null);

        // The catalog precedent answers a missing precondition as 422 and names the header, not a 400:
        // the request is well-formed JSON the caller can fix by re-sending the ETag it read.
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("request_invalid");
        Details(refused).ShouldContain(
            detail => detail.Path == "If-Match" && detail.Reason == "required",
            $"the 422 names the header: {string.Join(", ", Details(refused))}");
        (await StoredAsync<int>(world, id, "version")).ShouldBe(1);
    }

    [Fact]
    public async Task Update_WithoutIdempotencyKey_Is400RequestInvalid_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var id = await CreateDraftAsync(world);

        var refused = await world.Client.SendAsync(
            HttpMethod.Patch, World.Draft(id), world.Admin, UpdateBody(), idempotencyKey: null, ifMatch: "\"1\"");

        refused.Status.ShouldBe(HttpStatusCode.BadRequest, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("request_invalid");
        (await StoredAsync<int>(world, id, "version")).ShouldBe(1);
    }

    [Fact]
    public async Task Update_ReplaysTheStoredResponse_AndAReusedKeyForAnotherBodyIs409()
    {
        await using var world = await StartAsync();
        var id = await CreateDraftAsync(world);
        var key = NewKey();
        var body = UpdateBody("tests/replayed.spec.ts");

        var first = await PatchAsync(world, id, "\"1\"", body, key);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        first.ETag.ShouldBe("\"2\"");

        // The same key and body is the same request, even though its If-Match names the version before the first
        // write: the pipeline answers the stored response instead of applying the edit twice.
        var replay = await PatchAsync(world, id, "\"1\"", body, key);
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.ETag.ShouldBe("\"2\"");
        (await StoredAsync<int>(world, id, "version")).ShouldBe(2, "a replay is not a second update");
        (await AuditCountAsync(world, id, "draft.update")).ShouldBe(1, "a replay is not a second mutation");

        var conflicting = await PatchAsync(world, id, "\"2\"", UpdateBody("tests/other.spec.ts"), key);
        conflicting.Status.ShouldBe(HttpStatusCode.Conflict, conflicting.ErrorMessage);
        conflicting.ErrorCode.ShouldBe("idempotency_conflict");
        (await StoredAsync<int>(world, id, "version")).ShouldBe(2);
        (await StoredAsync<string>(world, id, "content #>> '{files,0,path}'")).ShouldBe("tests/replayed.spec.ts");
    }

    [Fact]
    public async Task Update_ContentThatIsMissingNullOrCarriesNoFile_Is422DraftInvalid()
    {
        await using var world = await StartAsync();
        var id = await CreateDraftAsync(world);

        var bodies = new (JsonObject Body, string Field)[]
        {
            (new JsonObject(), "content"),
            (new JsonObject { ["content"] = null }, "content"),
            (new JsonObject { ["content"] = new JsonObject { ["files"] = null } }, "content.files"),
            (new JsonObject { ["content"] = new JsonObject { ["files"] = new JsonArray((JsonNode?)null) } }, "content.files"),
        };

        foreach (var (body, field) in bodies)
        {
            var refused = await PatchAsync(world, id, "\"1\"", body);

            // A missing or null content is named as the content member; a null files member or a null element is
            // named inside it. All four answer 422 rather than reaching the row.
            ShouldNameField(refused, field, $"PATCH body {body.ToJsonString()} must be refused");
        }

        (await StoredAsync<int>(world, id, "version")).ShouldBe(1, "no invalid body was applied");
        (await AuditCountAsync(world, id, "draft.update")).ShouldBe(0);
    }

    [Fact]
    public async Task Update_UnknownForeignProjectAndForeignTenant_AreTheSame404()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject, otherUser) = await OtherTenantAsync(world);
        var unknown = Guid.CreateVersion7();
        var foreignTenant = await SeedForAsync(world, otherOrg, otherProject, otherUser);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("sibling"));
        var foreignProject = await SeedAsync(world, project: sibling);

        // A project-scoped credential cannot reach another project's draft: the handler answers the same 404 as a
        // missing id, so the id cannot be probed. An unrestricted member would read it; that is the positive control.
        var narrowed = TenancyApiFactory.Caller(org: world.Org, user: world.User, project: world.Project);
        (await GetAsync(world, foreignProject)).Status.ShouldBe(HttpStatusCode.OK, "the draft exists");

        var answers = new[]
        {
            await PatchAsync(world, unknown, caller: narrowed),
            await PatchAsync(world, foreignTenant, caller: narrowed),
            await PatchAsync(world, foreignProject, caller: narrowed),
        };

        foreach (var answer in answers)
        {
            answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
            answer.ErrorCode.ShouldBe("not_found");
        }

        answers.Select(answer => answer.ErrorMessage ?? string.Empty).Distinct(StringComparer.Ordinal).Count().ShouldBe(
            1, "an unknown id, another project's draft and another tenant's draft answer identically");
        (await StoredAsync<int>(world, foreignProject, "version")).ShouldBe(1, "a refused patch changed nothing");
    }

    /// <summary>A draft of another tenant, filed by that tenant's own founder through the create route.</summary>
    private static async Task<Guid> SeedForAsync(World world, Guid org, Guid project, Guid user)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{project}/drafts",
            TenancyApiFactory.Resolved(user: user, orgHeader: org), Body(), NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id();
    }
}
