using System.Net;
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

        foreach (var stale in new[] { "\"0\"", "\"2\"", "\"99\"" })
        {
            var refused = await PatchAsync(world, id, stale);

            ShouldBeStale(refused, $"If-Match {stale} is not the head");
        }

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
                ShouldBeStale(loser, $"round {round}: the losing writers are stale");
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

        var refused = await PatchAsync(world, id, "\"1\"");

        refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("draft_state_conflict");
        (await StoredAsync<int>(world, id, "version")).ShouldBe(1);
        (await StoredAsync<string>(world, id, "content #>> '{files,0,path}'")).ShouldBe(before);
        (await AuditCountAsync(world, id, "draft.update")).ShouldBe(0);

        // Positive control: the same patch on the same body at status 'draft' is accepted.
        var draft = await CreateDraftAsync(world);
        (await PatchAsync(world, draft, "\"1\"")).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Update_WithoutIfMatch_Is400RequestInvalidNamingTheHeader()
    {
        await using var world = await StartAsync();
        var id = await CreateDraftAsync(world);

        var refused = await PatchAsync(world, id, etag: null);

        // The header is checked with the body shape in the handler, after authorization: a malformed request is the
        // 400 the catalog precedent uses for a request the caller could fix, not the 422 of a body rule.
        refused.Status.ShouldBe(HttpStatusCode.BadRequest, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("request_invalid");
        Details(refused).ShouldContain(
            detail => detail.Path == "/If-Match", $"the 422 names the header: {string.Join(", ", Details(refused))}");
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
    public async Task Update_UnknownForeignProjectAndForeignTenant_AreTheSame404()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject, otherUser) = await OtherTenantAsync(world);
        var unknown = Guid.CreateVersion7();
        var foreignTenant = await SeedForAsync(world, otherOrg, otherProject, otherUser);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("sibling"));
        var foreignProject = await SeedAsync(world, project: sibling);

        var answers = new[]
        {
            await PatchAsync(world, unknown),
            await PatchAsync(world, foreignTenant),
            await PatchAsync(world, foreignProject),
        };

        foreach (var answer in answers)
        {
            answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
            answer.ErrorCode.ShouldBe("not_found");
        }

        answers.Select(answer => answer.ErrorMessage ?? string.Empty).Distinct(StringComparer.Ordinal).Count().ShouldBe(
            1, "an unknown id, another project's draft and another tenant's draft answer identically");
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
