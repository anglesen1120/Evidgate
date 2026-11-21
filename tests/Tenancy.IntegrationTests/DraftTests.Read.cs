using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The two read routes: a keyset page over <c>(created_at, id)</c> that runs one statement, and one draft whose ETag
/// is its version. The tenant and the project are both part of the read, so a foreign id and an unknown one answer
/// identically.
/// </summary>
public sealed partial class DraftTests
{
    [Fact]
    public async Task List_WalksThreePagesInKeysetOrder()
    {
        await using var world = await StartAsync();
        var created = new List<Guid>();
        foreach (var path in new[] { "one.ts", "two.ts", "three.ts", "four.ts", "five.ts" })
        {
            created.Add(await CreateDraftAsync(world, "tests/" + path));
        }

        // The page order is the database's own keyset order, so a tie on created_at cannot make this expectation guess.
        var expected = await _fixture.ColumnAsync<string>(
            world.Database,
            $"""
             select id::text from public.draft
              where org_id = '{world.Org}' and project_id = '{world.Project}'
              order by created_at, id
             """);
        expected.Count.ShouldBe(5);

        var seen = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 3; page++)
        {
            var url = $"{world.Collection}?limit=2" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var answer = await world.Client.SendAsync(HttpMethod.Get, url, world.Admin);

            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
            var items = answer.Body.GetProperty("items").EnumerateArray().ToList();
            items.Count.ShouldBe(page < 2 ? 2 : 1);
            seen.AddRange(items.Select(item => item.GetProperty("id").GetGuid().ToString("D")));

            var next = answer.Body.GetProperty("next_cursor");
            if (page < 2)
            {
                next.ValueKind.ShouldBe(JsonValueKind.String, "a page that is not the last carries a cursor");
                cursor = next.GetString();
            }
            else
            {
                next.ValueKind.ShouldBe(JsonValueKind.Null, "the last page carries no cursor");
            }
        }

        seen.ShouldBe(expected, "every draft once, in (created_at, id) order");
        seen.Count.ShouldBe(created.Count);
    }

    [Fact]
    public async Task List_RunsOneStatementOverTheDraftTable()
    {
        await using var world = await StartAsync();
        await CreateDraftAsync(world);
        await CreateDraftAsync(world, "tests/second.ts");

        var before = world.Api.Statements.Snapshot().Count;

        var answer = await world.Client.SendAsync(HttpMethod.Get, world.Collection, world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var statements = world.Api.Statements.Snapshot();
        statements.Count(statement => statement.Contains("from public.draft", StringComparison.Ordinal))
            .ShouldBe(1, "the page is one Dapper statement, as the index plan assumes");
        statements.Count.ShouldBeGreaterThan(before, "the counter observed the request");
    }

    [Fact]
    public async Task List_SeesOnlyTheProjectsDrafts()
    {
        await using var world = await StartAsync();
        var mine = await CreateDraftAsync(world);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("sibling"));
        var other = await SeedAsync(world, project: sibling);

        var answer = await world.Client.SendAsync(HttpMethod.Get, world.Collection, world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var ids = answer.Body.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToList();
        ids.ShouldBe([mine]);
        ids.ShouldNotContain(other);
    }

    [Fact]
    public async Task Get_AnswersTheDraftWithItsVersionAsETag()
    {
        await using var world = await StartAsync();
        var id = await CreateDraftAsync(world);

        var answer = await GetAsync(world, id);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.ETag.ShouldBe("\"1\"");
        answer.Id().ShouldBe(id);
        answer.Body.GetProperty("version").GetInt32().ShouldBe(1);
        answer.Body.GetProperty("status").GetString().ShouldBe("draft");
        answer.Body.GetProperty("content").GetProperty("files")[0].GetProperty("body_ref").GetString()
            .ShouldBe("drafts/checkout.ts");
    }

    [Fact]
    public async Task Get_ForeignProjectForeignTenantAndUnknown_AreTheSame404()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject, otherUser) = await OtherTenantAsync(world);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("sibling"));

        var answers = new[]
        {
            await GetAsync(world, Guid.CreateVersion7()),
            await GetAsync(world, await SeedAsync(world, project: sibling)),
            await GetAsync(world, await SeedForAsync(world, otherOrg, otherProject, otherUser)),
        };

        foreach (var answer in answers)
        {
            answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
            answer.ErrorCode.ShouldBe("not_found");
        }

        answers.Select(answer => answer.ErrorMessage ?? string.Empty).Distinct(StringComparer.Ordinal).Count().ShouldBe(
            1, "an unknown id, another project's draft and another tenant's draft answer identically");
        (await DraftCountAsync(world)).ShouldBe(0, "a refused read writes nothing");
    }

    [Fact]
    public async Task DraftOfAnotherProject_ForANarrowedMember_Is403ProjectScopeMismatch()
    {
        await using var world = await StartAsync();
        var id = await CreateDraftAsync(world);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("sibling"));
        var narrowed = TenancyApiFactory.Caller(org: world.Org, user: world.User, project: sibling);

        var read = await GetAsync(world, id, narrowed);
        read.Status.ShouldBe(HttpStatusCode.Forbidden, read.ErrorMessage);
        read.ErrorCode.ShouldBe("project_scope_mismatch");

        var patch = await PatchAsync(world, id, "\"1\"", caller: narrowed);
        patch.Status.ShouldBe(HttpStatusCode.Forbidden, patch.ErrorMessage);
        patch.ErrorCode.ShouldBe("project_scope_mismatch");

        var list = await world.Client.SendAsync(HttpMethod.Get, world.Collection, narrowed);
        list.Status.ShouldBe(HttpStatusCode.Forbidden, list.ErrorMessage);
        list.ErrorCode.ShouldBe("project_scope_mismatch");

        (await StoredAsync<int>(world, id, "version")).ShouldBe(1, "the narrowed member changed nothing");
    }

    [Fact]
    public async Task List_StatusFilter_KeepsOnlyThatState()
    {
        await using var world = await StartAsync();
        var draft = await CreateDraftAsync(world);
        var passed = await SeedAsync(world, "dry_passed");

        var answer = await world.Client.SendAsync(HttpMethod.Get, $"{world.Collection}?status=dry_passed", world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var ids = answer.Body.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToList();
        ids.ShouldBe([passed]);
        ids.ShouldNotContain(draft);

        var unknown = await world.Client.SendAsync(HttpMethod.Get, $"{world.Collection}?status=nonsense", world.Admin);
        unknown.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, unknown.ErrorMessage);
        unknown.ErrorCode.ShouldBe("request_invalid");
    }

    [Fact]
    public async Task List_BadLimit_Is422AtLimit()
    {
        await using var world = await StartAsync();

        foreach (var limit in new[] { "0", "201", "abc" })
        {
            var refused = await world.Client.SendAsync(HttpMethod.Get, $"{world.Collection}?limit={limit}", world.Admin);

            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"?limit={limit}: {refused.ErrorMessage}");
            refused.ErrorCode.ShouldBe("request_invalid");
            Details(refused).ShouldContain(
                detail => detail.Path == "limit",
                $"?limit={limit} names the member: {string.Join(", ", Details(refused))}");
        }
    }
}
