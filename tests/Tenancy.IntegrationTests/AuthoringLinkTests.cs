using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC11 of 13.1.1 §5 (routes 20 and 21) through the real host and a real PostgreSQL: one link per pair even under a
/// repeat, both directions many-to-many, unlink, the target refusals the plan names, the same-project trigger at its own
/// layer, tenant isolation over row level security, and the permission gate.
/// </summary>
/// <remarks>
/// The fixture is the shared <see cref="PostgresTenancyFixture"/>, like the manual case route suite: each test seeds
/// its own org, project, item and cases as the owner and then calls the routes as a granted caller of that org. The
/// order of the assertions matters — the pair that must survive a duplicate is counted before any unlink runs.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AuthoringLinkTests
{
    private readonly PostgresTenancyFixture _fixture;

    /// <summary>Wires the fixture.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public AuthoringLinkTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// AC11: a link is created once and answered as a no-op when repeated, links are many-to-many in both directions,
    /// unlink removes one link and a missing one is 404, and every documented refusal answers the plan's reason.
    /// </summary>
    [Fact]
    public async Task LinkLifecycle_AndRefusals()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var connectionString = _fixture.AppConnectionString(database);
        await using var api = TenancyApiFactory.GrantAll(connectionString);
        using var client = api.CreateClient();
        var tree = await AuthoringLinkTestData.SeedTreeAsync(_fixture, database);
        var manualCase = await AuthoringLinkTestData.SeedManualCaseAsync(_fixture, database, tree);
        var otherManualCase = await AuthoringLinkTestData.SeedManualCaseAsync(_fixture, database, tree, 2);
        var live = await AuthoringLinkTestData.SeedCatalogCaseAsync(_fixture, database, tree);
        var secondLive = await AuthoringLinkTestData.SeedCatalogCaseAsync(_fixture, database, tree);

        await LinkLifecycleAsync(_fixture, client, database, tree, manualCase, otherManualCase, live, secondLive);
        await TargetRefusalsAsync(_fixture, client, database, tree, manualCase);
        await CrossProjectAndTenantIsolationAsync(_fixture, client, connectionString, database, tree, manualCase, live);
        await TriggerRefusesCrossProjectInsertAsync(_fixture, connectionString, database, tree, manualCase, otherManualCase);
        await ViewerIsRefusedAsync(_fixture, database, connectionString, tree, manualCase, live);
    }

    /// <summary>The 201, the replay, the duplicate, the conflicting key, both link directions and the unlink.</summary>
    private static async Task LinkLifecycleAsync(
        PostgresTenancyFixture fixture,
        HttpClient client,
        string database,
        AuthoringLinkTree tree,
        Guid manualCase,
        Guid otherManualCase,
        Guid live,
        Guid secondLive)
    {
        var key = NewKey();
        var first = await client.SendAsync(
            HttpMethod.Post, tree.LinkPath(manualCase), tree.Caller, new { test_case_id = live }, key);

        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        first.Body.GetProperty("changed").GetBoolean().ShouldBeTrue();
        first.Id("link_id").ShouldNotBe(Guid.Empty);
        var linkId = first.Id("link_id");
        (await AuthoringLinkTestData.LinkCountAsync(fixture, database, tree)).ShouldBe(1);

        var stored = await fixture.ScalarAsync<string>(
            database,
            "select manual_test_case_id::text || ':' || test_case_id::text"
            + $" from public.manual_case_link where id = '{linkId}'");
        stored.ShouldBe($"{manualCase:D}:{live:D}");

        // The same key and body replay the stored answer and write nothing a second time.
        var replay = await client.SendAsync(
            HttpMethod.Post, tree.LinkPath(manualCase), tree.Caller, new { test_case_id = live }, key);

        replay.Status.ShouldBe(first.Status);
        JsonElement.DeepEquals(replay.Body, first.Body).ShouldBeTrue("a replay answers the first body verbatim");
        (await AuthoringLinkTestData.LinkCountAsync(fixture, database, tree)).ShouldBe(1, "a replay inserts nothing");

        // A repeat under a new key is the documented no-op, not an error and not a second row.
        var duplicate = await client.SendAsync(
            HttpMethod.Post, tree.LinkPath(manualCase), tree.Caller, new { test_case_id = live }, NewKey());

        duplicate.Status.ShouldBe(HttpStatusCode.OK);
        duplicate.Body.GetProperty("changed").GetBoolean().ShouldBeFalse();
        (await AuthoringLinkTestData.LinkCountAsync(fixture, database, tree)).ShouldBe(1, "the pair stays one row");

        // A different body under the replayed key is a conflict, and it writes nothing either.
        var conflict = await client.SendAsync(
            HttpMethod.Post, tree.LinkPath(manualCase), tree.Caller, new { test_case_id = secondLive }, key);

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await AuthoringLinkTestData.LinkCountAsync(fixture, database, tree)).ShouldBe(1, "a conflicting key inserts nothing");

        // Many-to-many in both directions: a second catalog case on this manual case, and this case on another case.
        await AuthoringLinkTestData.LinkAsync(client, tree, manualCase, secondLive);
        await AuthoringLinkTestData.LinkAsync(client, tree, otherManualCase, live);
        (await AuthoringLinkTestData.LinkCountAsync(fixture, database, tree)).ShouldBe(3);

        var unlinkPath = tree.LinkPath(manualCase) + "/" + secondLive.ToString("D");
        (await client.SendAsync(HttpMethod.Delete, unlinkPath, tree.Caller, null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.NoContent);
        (await AuthoringLinkTestData.LinkCountAsync(fixture, database, tree)).ShouldBe(2);

        // A pair that is not linked is a 404, never a silent 204 (route 21).
        var absent = await client.SendAsync(HttpMethod.Delete, unlinkPath, tree.Caller, null, NewKey());
        absent.Status.ShouldBe(HttpStatusCode.NotFound);
        absent.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>An archived target and an unknown one are both 422 with their own reason and no catalog echo.</summary>
    private static async Task TargetRefusalsAsync(
        PostgresTenancyFixture fixture,
        HttpClient client,
        string database,
        AuthoringLinkTree tree,
        Guid manualCase)
    {
        var archived = await AuthoringLinkTestData.SeedCatalogCaseAsync(fixture, database, tree);
        await AuthoringLinkTestData.ArchiveAsync(fixture, database, archived);

        var archivedAnswer = await client.SendAsync(
            HttpMethod.Post, tree.LinkPath(manualCase), tree.Caller, new { test_case_id = archived }, NewKey());

        ExpectRefusal(archivedAnswer, "case_archived", archived);
        (await RowCountAsync(fixture, database, tree, archived)).ShouldBe(0, "an archived case gets no link row");

        // The positive control: the same shape with a live case is written, so the refusal is the target's status.
        var live = await AuthoringLinkTestData.SeedCatalogCaseAsync(fixture, database, tree);
        await AuthoringLinkTestData.LinkAsync(client, tree, manualCase, live);

        var unknown = Guid.CreateVersion7();
        var unknownAnswer = await client.SendAsync(
            HttpMethod.Post, tree.LinkPath(manualCase), tree.Caller, new { test_case_id = unknown }, NewKey());

        ExpectRefusal(unknownAnswer, "case_not_found", unknown);
        (await RowCountAsync(fixture, database, tree, unknown)).ShouldBe(0);
    }

    /// <summary>A same-tenant sibling project is refused like an absence, and another tenant cannot see or link.</summary>
    private static async Task CrossProjectAndTenantIsolationAsync(
        PostgresTenancyFixture fixture,
        HttpClient client,
        string connectionString,
        string database,
        AuthoringLinkTree tree,
        Guid manualCase,
        Guid live)
    {
        // Same tenant, sibling project: the catalog lookup is project-exact, so its case is indistinguishable from a
        // missing one (handler path), and another project's manual case is answered by the lock as a 404.
        var sibling = await AuthoringLinkTestData.SeedTreeAsync(fixture, database, tree.Org);
        var siblingCase = await AuthoringLinkTestData.SeedCatalogCaseAsync(fixture, database, sibling);
        var siblingManual = await AuthoringLinkTestData.SeedManualCaseAsync(fixture, database, sibling);

        var crossProject = await client.SendAsync(
            HttpMethod.Post, tree.LinkPath(manualCase), tree.Caller, new { test_case_id = siblingCase }, NewKey());
        ExpectRefusal(crossProject, "case_not_found", siblingCase);

        var foreignManual = await client.SendAsync(
            HttpMethod.Post, tree.LinkPath(siblingManual), tree.Caller, new { test_case_id = live }, NewKey());
        foreignManual.Status.ShouldBe(HttpStatusCode.NotFound);

        // Another tenant: its own link works (positive control), a link to this tenant's catalog case does not.
        var outsider = await AuthoringLinkTestData.SeedTreeAsync(fixture, database);
        var outsiderManual = await AuthoringLinkTestData.SeedManualCaseAsync(fixture, database, outsider);
        var outsiderCase = await AuthoringLinkTestData.SeedCatalogCaseAsync(fixture, database, outsider);
        await AuthoringLinkTestData.LinkAsync(client, outsider, outsiderManual, outsiderCase);

        var stolen = await client.SendAsync(
            HttpMethod.Post, outsider.LinkPath(outsiderManual), outsider.Caller, new { test_case_id = live }, NewKey());
        ExpectRefusal(stolen, "case_not_found", live);

        // Row level security: the outsider's own scope shows its one row and none of this tenant's.
        await using (var scoped = await TenantScope.BeginAsync(connectionString, outsider.Org))
        {
            (await scoped.ScalarAsync<long>("select count(*) from public.manual_case_link")).ShouldBe(1);
            (await scoped.ScalarAsync<long>(
                $"select count(*) from public.manual_case_link where org_id = '{tree.Org}'")).ShouldBe(0);
        }

        // And this tenant's caller cannot address the outsider's project at all.
        (await client.SendAsync(HttpMethod.Get, outsider.CasesPath, tree.Caller))
            .Status.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Get, outsider.CasesPath, outsider.Caller))
            .Status.ShouldBe(HttpStatusCode.OK, "the outsider does read its own project");
    }

    /// <summary>The trigger refuses a same-tenant cross-project link at the table's own layer, and admits a legal row.</summary>
    private static async Task TriggerRefusesCrossProjectInsertAsync(
        PostgresTenancyFixture fixture,
        string connectionString,
        string database,
        AuthoringLinkTree tree,
        Guid manualCase,
        Guid otherManualCase)
    {
        var sibling = await AuthoringLinkTestData.SeedTreeAsync(fixture, database, tree.Org);
        var siblingCase = await AuthoringLinkTestData.SeedCatalogCaseAsync(fixture, database, sibling);
        var ownCase = await AuthoringLinkTestData.SeedCatalogCaseAsync(fixture, database, tree);

        const string Insert = "insert into public.manual_case_link "
            + "(id, org_id, project_id, manual_test_case_id, test_case_id) "
            + "values (@id, @org_id, @project_id, @manual_test_case_id, @test_case_id)";

        await using (var refused = await TenantScope.BeginAsync(connectionString, tree.Org, tree.Project))
        {
            var error = await refused.CaptureErrorAsync(
                Insert,
                ("id", Guid.CreateVersion7()),
                ("org_id", tree.Org),
                ("project_id", tree.Project),
                ("manual_test_case_id", otherManualCase),
                ("test_case_id", siblingCase));

            error.SqlState.ShouldBe("23514");
            error.MessageText.ShouldContain("cross_project_reference");
        }

        // The positive control: the same statement with a case of this project is accepted, so the FK or the policy is
        // not what refused the row above.
        await using (var own = await TenantScope.BeginAsync(connectionString, tree.Org, tree.Project))
        {
            (await own.ExecuteAsync(
                Insert,
                ("id", Guid.CreateVersion7()),
                ("org_id", tree.Org),
                ("project_id", tree.Project),
                ("manual_test_case_id", manualCase),
                ("test_case_id", ownCase))).ShouldBe(1);
        }
    }

    /// <summary>A member with no role holds no permission: 403 <c>forbidden</c>, while the granted caller above wrote.</summary>
    private static async Task ViewerIsRefusedAsync(
        PostgresTenancyFixture fixture,
        string database,
        string connectionString,
        AuthoringLinkTree tree,
        Guid manualCase,
        Guid live)
    {
        var viewer = Guid.CreateVersion7();
        await fixture.ExecuteAsync(
            database,
            $"""
            insert into public."user" (id, email) values ('{viewer}', 'viewer-{viewer:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{Guid.CreateVersion7()}', '{tree.Org}', '{viewer}', 'viewer-{viewer:N}@example.test', 'active')
            """);

        await using var real = TenancyApiFactory.RealResolver(connectionString);
        using var viewerClient = real.CreateClient();
        var refused = await viewerClient.SendAsync(
            HttpMethod.Post,
            tree.LinkPath(manualCase),
            TenancyApiFactory.Resolved(user: viewer, orgHeader: tree.Org),
            new { test_case_id = live },
            NewKey());

        refused.Status.ShouldBe(HttpStatusCode.Forbidden);
        refused.ErrorCode.ShouldBe("forbidden");
    }

    /// <summary>Asserts the AC11 refusal shape: 422 <c>request_invalid</c>, the target's path and reason, no echo.</summary>
    private static void ExpectRefusal(ApiAnswer answer, string reason, Guid testCaseId)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");

        var details = answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().ToArray();
        details.ShouldHaveSingleItem();
        details[0].GetProperty("path").GetString().ShouldBe("/test_case_id");
        details[0].GetProperty("reason").GetString().ShouldBe(reason);
        answer.Body.GetRawText().Contains(testCaseId.ToString("D"), StringComparison.Ordinal)
            .ShouldBeFalse("no echo of catalog internals");
    }

    private static Task<int> RowCountAsync(
        PostgresTenancyFixture fixture, string database, AuthoringLinkTree tree, Guid testCaseId) =>
        fixture.ScalarAsync<int>(
            database,
            "select count(*)::int from public.manual_case_link"
            + $" where org_id = '{tree.Org}' and test_case_id = '{testCaseId}'");
}
