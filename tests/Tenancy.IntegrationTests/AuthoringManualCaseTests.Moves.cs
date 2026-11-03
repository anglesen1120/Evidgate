using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1 AC22 (route 19): a manual test case moves between the items of its project, keeps its <c>code</c>, appends
/// exactly one <c>update</c> version, answers 200 with the unchanged version when it already sits under the target, and
/// refuses a missing, deleted, other-project or retired target — writing nothing.
/// </summary>
public sealed partial class AuthoringManualCaseTests
{
    /// <summary>AC22: the case is re-parented with one version bump and the same code.</summary>
    [Fact]
    public async Task MoveBetweenItems_ReparentsWithOneUpdateVersion()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var (org, project, item, other) = await SeedTreeAsync(database);
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        var created = await client.SendAsync(HttpMethod.Post, CasePath(project), caller, CaseBody(item, "Pay with a card"), NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);
        var caseId = created.Body.GetProperty("case_id").GetGuid();
        var code = created.Text("code");
        var movePath = CasePath(project) + "/" + caseId.ToString("D") + "/item";

        var moved = await client.SendAsync(
            HttpMethod.Post,
            movePath,
            caller,
            Element("""{"checklist_item_id":"__ITEM__","reason":"the case belongs to refunds"}"""
                .Replace("__ITEM__", other.ToString("D"), StringComparison.Ordinal)),
            NewKey(),
            "\"1\"");

        moved.Status.ShouldBe(HttpStatusCode.OK, moved.ErrorMessage);
        moved.Body.GetProperty("version").GetInt32().ShouldBe(2);
        moved.Body.GetProperty("checklist_item_id").GetGuid().ShouldBe(other);
        moved.ETag.ShouldBe("\"2\"");

        (await _fixture.ScalarAsync<string>(database, $"select code from public.manual_test_case where id = '{caseId}'"))
            .ShouldBe(code, "the code never changes on a move (AC4/AC22)");
        (await _fixture.ScalarAsync<Guid>(
            database,
            $"select checklist_item_id from public.manual_test_case where id = '{caseId}'"))
            .ShouldBe(other);
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.authoring_version where entity_id = '{caseId}' and change_kind = 'update'"))
            .ShouldBe(1, "one move is one update version");

        // A move onto the item it already sits under writes nothing and answers the unchanged version (AC5).
        var noop = await client.SendAsync(
            HttpMethod.Post,
            movePath,
            caller,
            Element("""{"checklist_item_id":"__ITEM__"}""".Replace("__ITEM__", other.ToString("D"), StringComparison.Ordinal)),
            NewKey(),
            "\"2\"");

        noop.Status.ShouldBe(HttpStatusCode.OK);
        noop.Body.GetProperty("version").GetInt32().ShouldBe(2);
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.authoring_version where entity_id = '{caseId}'"))
            .ShouldBe(2, "the create and the one real move");
    }

    /// <summary>AC22: every refused target writes nothing and names its reason.</summary>
    [Fact]
    public async Task MoveBetweenItems_Refusals_WriteNothing()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var (org, project, item, other) = await SeedTreeAsync(database);
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        var created = await client.SendAsync(HttpMethod.Post, CasePath(project), caller, CaseBody(item, "Pay with a card"), NewKey());
        var caseId = created.Body.GetProperty("case_id").GetGuid();
        var movePath = CasePath(project) + "/" + caseId.ToString("D") + "/item";

        // A retired target item of the same project.
        var retiredItem = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.checklist_item (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            select '{retiredItem}', '{org}', '{project}', feature_node_id, 'Old refunds', 'low', 'retired', 'manual', 2
              from public.checklist_item where id = '{other}'
            """);

        // An item of another project (same org), so the target is legal as a row but not as a parent of this case.
        var foreignProject = Guid.CreateVersion7();
        var foreignNode = Guid.CreateVersion7();
        var foreignItem = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.project (id, org_id, name, slug)
            values ('{foreignProject}', '{org}', 'other project', 'other');
            insert into public.feature_node (id, org_id, project_id, kind, title, position)
            values ('{foreignNode}', '{org}', '{foreignProject}', 'module', 'Other', 0);
            insert into public.checklist_item (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            values ('{foreignItem}', '{org}', '{foreignProject}', '{foreignNode}', 'Elsewhere', 'low', 'active', 'manual', 0);
            """);

        var refusals = new (string Because, Guid Target, HttpStatusCode Status, string Code, string? Reason)[]
        {
            ("unknown target", Guid.CreateVersion7(), HttpStatusCode.UnprocessableEntity, "authoring_parent_invalid", "parent_not_found"),
            ("other project's target", foreignItem, HttpStatusCode.UnprocessableEntity, "authoring_parent_invalid", "parent_not_found"),
            ("retired target", retiredItem, HttpStatusCode.Conflict, "invalid_transition", null),
        };

        foreach (var (because, target, status, code, reason) in refusals)
        {
            var refused = await client.SendAsync(
                HttpMethod.Post,
                movePath,
                caller,
                Element("""{"checklist_item_id":"__ITEM__"}""".Replace("__ITEM__", target.ToString("D"), StringComparison.Ordinal)),
                NewKey(),
                "\"1\"");

            refused.Status.ShouldBe(status, because);
            refused.ErrorCode.ShouldBe(code, because);

            if (reason is not null)
            {
                refused.Body.GetProperty("error").GetProperty("details").EnumerateArray()
                    .ShouldContain(detail => detail.GetProperty("reason").GetString() == reason, because);
            }
        }

        // A retired case cannot move either: the move is an edit of the case (§6).
        await _fixture.ExecuteAsync(
            database,
            $"update public.manual_test_case set state = 'retired' where id = '{caseId}'");

        // The 0004 version trigger bumps the row on that direct write, so the tag is what the row holds now.
        var retiredVersion = await _fixture.ScalarAsync<int>(
            database,
            $"select version from public.manual_test_case where id = '{caseId}'");

        var retiredCase = await client.SendAsync(
            HttpMethod.Post,
            movePath,
            caller,
            Element("""{"checklist_item_id":"__ITEM__"}""".Replace("__ITEM__", other.ToString("D"), StringComparison.Ordinal)),
            NewKey(),
            "\"" + retiredVersion + "\"");

        retiredCase.Status.ShouldBe(HttpStatusCode.Conflict);
        retiredCase.ErrorCode.ShouldBe("invalid_transition");

        (await _fixture.ScalarAsync<Guid>(
            database,
            $"select checklist_item_id from public.manual_test_case where id = '{caseId}'"))
            .ShouldBe(item, "no refusal re-parented the case");
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.authoring_version where entity_id = '{caseId}'"))
            .ShouldBe(1, "and none appended a version");
    }

    /// <summary>AC19/AC22: two concurrent moves of one case at the same version — one wins, the other is 412.</summary>
    [Fact]
    public async Task MoveBetweenItems_Concurrent_412()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var (org, project, item, other) = await SeedTreeAsync(database);
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        var created = await client.SendAsync(HttpMethod.Post, CasePath(project), caller, CaseBody(item, "Pay with a card"), NewKey());
        var caseId = created.Body.GetProperty("case_id").GetGuid();
        var movePath = CasePath(project) + "/" + caseId.ToString("D") + "/item";

        // A third item, so the two moves do not queue on the same target row.
        var third = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.checklist_item (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            select '{third}', '{org}', '{project}', feature_node_id, 'Chargebacks', 'low', 'active', 'manual', 3
              from public.checklist_item where id = '{other}'
            """);

        var answers = await Task.WhenAll(new[] { other, third }.Select(target => client.SendAsync(
            HttpMethod.Post,
            movePath,
            caller,
            Element("""{"checklist_item_id":"__ITEM__"}""".Replace("__ITEM__", target.ToString("D"), StringComparison.Ordinal)),
            NewKey(),
            "\"1\"")));

        var seen = string.Join(" | ", answers.Select(answer => $"{answer.Status}:{answer.Body}"));
        answers.Count(answer => answer.Status == HttpStatusCode.OK).ShouldBe(1, seen);
        answers.Count(answer => answer.Status == HttpStatusCode.PreconditionFailed).ShouldBe(1, seen);
        answers.Single(answer => answer.Status == HttpStatusCode.PreconditionFailed)
            .ErrorCode.ShouldBe("precondition_failed", seen);

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.authoring_version where entity_id = '{caseId}' and change_kind = 'update'"))
            .ShouldBe(1, "the loser wrote nothing");
        (await _fixture.ScalarAsync<int>(database, $"select version from public.manual_test_case where id = '{caseId}'"))
            .ShouldBe(2);
    }
}
