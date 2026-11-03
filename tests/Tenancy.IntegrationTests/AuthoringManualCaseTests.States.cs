using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1 §6 (CR-13-1) and AC21 (route 17): the <c>manual_test_case</c> state machine — <c>[*] → draft</c>,
/// <c>draft → approved</c>, <c>{draft, approved} → retired</c> with a 1..500 character reason, <c>retired</c> terminal,
/// and any edit of a <c>retired</c> case refused.
/// </summary>
public sealed partial class AuthoringManualCaseTests
{
    /// <summary>AC21: the four legal moves, the refused ones, and the reason rule of a retirement.</summary>
    [Fact]
    public async Task StateMatrix_AllowsOnlyTheFourEdges()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var (org, project, item, _) = await SeedTreeAsync(database);
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        var cases = new (string From, string To, string? Reason, HttpStatusCode Status)[]
        {
            ("draft", "draft", null, HttpStatusCode.OK),
            ("draft", "approved", null, HttpStatusCode.OK),
            ("approved", "retired", "superseded by SHOP-002", HttpStatusCode.OK),
            ("draft", "retired", "no longer part of the suite", HttpStatusCode.OK),
            ("draft", "approved", new string('r', 501), HttpStatusCode.UnprocessableEntity),
            ("retired", "approved", null, HttpStatusCode.Conflict),
            ("retired", "draft", "back to draft", HttpStatusCode.Conflict),
            ("approved", "draft", "back to draft", HttpStatusCode.Conflict),
        };

        foreach (var (from, to, reason, status) in cases)
        {
            // Each row gets its own case: the state machine is a path, so a "from" state is reached the way a caller
            // reaches it — the legal moves of §6 — and the tag is the version those moves left behind. A direct SQL
            // write would bump `version` through the 0004 trigger without a version row, and the writer's
            // `version_no == row.version` guard would then refuse the next legitimate edit.
            var seeded = await client.SendAsync(HttpMethod.Post, CasePath(project), caller, CaseBody(item, "Pay with a card"), NewKey());
            seeded.Status.ShouldBe(HttpStatusCode.Created);
            var caseId = seeded.Body.GetProperty("case_id").GetGuid();
            var statePath = CasePath(project) + "/" + caseId.ToString("D") + "/state";
            var current = seeded.Body.GetProperty("version").GetInt32();

            if (!string.Equals(from, "draft", StringComparison.Ordinal))
            {
                var toFrom = string.Equals(from, "approved", StringComparison.Ordinal)
                    ? """{"state":"approved"}"""
                    : """{"state":"retired","reason":"superseded by SHOP-001"}""";
                var reached = await client.SendAsync(
                    HttpMethod.Post, statePath, caller, Element(toFrom), NewKey(), "\"" + current + "\"");
                reached.Status.ShouldBe(HttpStatusCode.OK, $"{from} is a legal first move");
                current = reached.Body.GetProperty("version").GetInt32();
            }

            var body = reason is null
                ? $$"""{"state":"{{to}}"}"""
                : $$"""{"state":"{{to}}","reason":"{{reason}}"}""";

            var answer = await client.SendAsync(
                HttpMethod.Post, statePath, caller, Element(body), NewKey(), "\"" + current + "\"");

            answer.Status.ShouldBe(status, $"{from} → {to}");

            if (status == HttpStatusCode.OK)
            {
                answer.Body.GetProperty("state").GetString().ShouldBe(to);
                answer.Body.GetProperty("version").GetInt32().ShouldBe(to == from ? current : current + 1);
            }
            else if (status == HttpStatusCode.UnprocessableEntity)
            {
                answer.ErrorCode.ShouldBe("request_invalid");
                answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
                    .ShouldContain(detail => detail.GetProperty("path").GetString() == "/reason"
                        && detail.GetProperty("reason").GetString() == "length_invalid");
            }
            else
            {
                answer.ErrorCode.ShouldBe("invalid_transition");
            }
        }
    }

    /// <summary>AC21: a retirement without a reason is 422 on <c>/reason</c> and changes nothing.</summary>
    [Fact]
    public async Task RetireWithoutReason_422()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var (org, project, item, _) = await SeedTreeAsync(database);
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        var created = await client.SendAsync(HttpMethod.Post, CasePath(project), caller, CaseBody(item, "Pay with a card"), NewKey());
        var caseId = created.Body.GetProperty("case_id").GetGuid();
        var statePath = CasePath(project) + "/" + caseId.ToString("D") + "/state";

        foreach (var body in (string[])["""{"state":"retired"}""", """{"state":"retired","reason":""}"""])
        {
            var refused = await client.SendAsync(
                HttpMethod.Post, statePath, caller, Element(body), NewKey(), "\"1\"");

            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
            refused.ErrorCode.ShouldBe("request_invalid");
            refused.Body.GetProperty("error").GetProperty("details").EnumerateArray()
                .ShouldContain(detail => detail.GetProperty("path").GetString() == "/reason",
                    "the reason member, not the state one, is what is missing");
        }

        (await _fixture.ScalarAsync<string>(database, $"select state from public.manual_test_case where id = '{caseId}'"))
            .ShouldBe("draft");
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.authoring_version where entity_id = '{caseId}'"))
            .ShouldBe(1);
    }

    /// <summary>§6: every edit of a retired case is 409 <c>invalid_transition</c> — PATCH, state and delete alike.</summary>
    [Fact]
    public async Task RetiredCase_RefusesEveryEdit()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var (org, project, item, _) = await SeedTreeAsync(database);
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        var created = await client.SendAsync(HttpMethod.Post, CasePath(project), caller, CaseBody(item, "Pay with a card"), NewKey());
        var caseId = created.Body.GetProperty("case_id").GetGuid();
        var path = CasePath(project) + "/" + caseId.ToString("D");

        await _fixture.ExecuteAsync(
            database,
            $"update public.manual_test_case set state = 'retired' where id = '{caseId}'");

        // The 0004 version trigger bumps the row on that direct write, so the tag is what the row holds now.
        var retiredVersion = await _fixture.ScalarAsync<int>(
            database,
            $"select version from public.manual_test_case where id = '{caseId}'");
        var tag = "\"" + retiredVersion + "\"";

        var patched = await client.SendAsync(
            HttpMethod.Patch, path, caller, Element("""{"title":"Renamed"}"""), NewKey(), tag);
        patched.Status.ShouldBe(HttpStatusCode.Conflict);
        patched.ErrorCode.ShouldBe("invalid_transition");

        var moved = await client.SendAsync(
            HttpMethod.Post, path + "/state", caller, Element("""{"state":"approved"}"""), NewKey(), tag);
        moved.Status.ShouldBe(HttpStatusCode.Conflict);
        moved.ErrorCode.ShouldBe("invalid_transition");

        var deleted = await client.SendAsync(
            HttpMethod.Delete, path, caller, Element("""{"reason":"cleanup"}"""), NewKey(), tag);
        deleted.Status.ShouldBe(HttpStatusCode.Conflict);
        deleted.ErrorCode.ShouldBe("invalid_transition");

        (await _fixture.ScalarAsync<string>(database, $"select title from public.manual_test_case where id = '{caseId}'"))
            .ShouldBe("Pay with a card");
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.authoring_version where entity_id = '{caseId}'"))
            .ShouldBe(1);
    }

    /// <summary>Route 16: a live case is soft-deleted, answers 204 and keeps its row.</summary>
    [Fact]
    public async Task Delete_SoftDeletesAndAnswers204()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var (org, project, item, _) = await SeedTreeAsync(database);
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        var created = await client.SendAsync(HttpMethod.Post, CasePath(project), caller, CaseBody(item, "Pay with a card"), NewKey());
        var caseId = created.Body.GetProperty("case_id").GetGuid();
        var path = CasePath(project) + "/" + caseId.ToString("D");

        var deleted = await client.SendAsync(
            HttpMethod.Delete,
            path,
            caller,
            Element("""{"reason":"merged into SHOP-002"}"""),
            NewKey(),
            "\"1\"");

        deleted.Status.ShouldBe(HttpStatusCode.NoContent);
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.manual_test_case where id = '{caseId}' and deleted_at is not null"))
            .ShouldBe(1);
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.authoring_version where entity_id = '{caseId}' and change_kind = 'delete'"))
            .ShouldBe(1, "AC5's change_kind vocabulary is {create, update, delete, restore, import}");

        // A deleted case is not readable by id any more.
        (await client.SendAsync(HttpMethod.Get, path, caller)).Status.ShouldBe(HttpStatusCode.NotFound);
    }
}
