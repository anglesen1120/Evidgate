using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ListRunsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListRuns_Decision_CustomRoleWithoutDecisionReadCannotReadOrInferVerdict(bool filtered)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var run = await CreateBranchAsync(world, "main", ShaOne);
        var policy = await SeedPolicyAsync(world);
        await InsertEvaluationAsync(world, run, policy, 1, "live", "PASS");
        var role = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(world.Database, $"""
            insert into public.role (id, org_id, code, name, is_builtin, kind)
            values ('{role:D}', '{world.Org:D}', 'run_reader', 'Run reader', false, 'member');
            insert into public.role_permission (id, org_id, role_id, permission_code)
            values (gen_random_uuid(), '{world.Org:D}', '{role:D}', 'run:read'),
                   (gen_random_uuid(), '{world.Org:D}', '{role:D}', 'project:read');
            delete from public.membership_role
             where org_id = '{world.Org:D}' and membership_id in
               (select id from public.membership where org_id = '{world.Org:D}' and user_id = '{world.User:D}');
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), org_id, id, '{role:D}' from public.membership
             where org_id = '{world.Org:D}' and user_id = '{world.User:D}';
            """);

        var denied = await world.Client.SendAsync(HttpMethod.Get, $"/v1/runs/{run}/decision", world.Admin);
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        var query = filtered ? "decision=pass" : "limit=1";
        var restricted = await ListAsync(world, query);

        await _fixture.ExecuteAsync(world.Database, $"""
            insert into public.role_permission (id, org_id, role_id, permission_code)
            values (gen_random_uuid(), '{world.Org:D}', '{role:D}', 'decision:read');
            """);
        var allowed = await ListAsync(world, query);
        allowed.Status.ShouldBe(HttpStatusCode.OK, allowed.ErrorMessage);
        Items(allowed).ShouldHaveSingleItem().GetProperty("decision").GetString().ShouldBe("PASS");
        (await RouteDecisionAsync(world, run)).ShouldBe("PASS");

        if (filtered)
        {
            restricted.Status.ShouldBe(HttpStatusCode.Forbidden, "filter membership discloses a protected verdict");
        }
        else
        {
            restricted.Body.ToString().ShouldNotContain("\"PASS\"");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(200)]
    public async Task ListRuns_DecisionFilter_TiedTimestampsRetainEveryMatchingId(int limit)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runs = await SeedOrderedDecisionRunsAsync(world, limit == 1 ? 51 : 201, tied: true);
        var policy = await SeedPolicyAsync(world);
        var matching = new[] { runs[0], runs[19], runs[20], runs[^2], runs[^1] };
        foreach (var run in matching)
        {
            await InsertEvaluationAsync(world, run, policy, 1, "live", "PASS");
        }

        (await PagedIdsAsync(world, $"decision=pass&limit={limit}"))
            .ShouldBe(matching, ignoreOrder: false);
        (await PagedIdsAsync(world, $"decision=pending&limit={limit}"))
            .ShouldBe(runs.Except(matching).ToArray(), ignoreOrder: false);
    }

    [Fact]
    public async Task ListRuns_DecisionFilter_NonemptyBudgetPageResumesAfterLastScannedRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runs = await SeedOrderedDecisionRunsAsync(world, 1003);
        var policy = await SeedPolicyAsync(world);
        foreach (var index in new[] { 10, 999, 1001, 1002 })
        {
            await InsertEvaluationAsync(world, runs[index], policy, 1, "live", "PASS");
        }

        world.Api.Statements.Clear();
        var first = await ListAsync(world, "decision=pass&limit=3");
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        OrderedIds(first).ShouldBe(new[] { runs[10], runs[999] }, ignoreOrder: false);
        EvaluationStatementCount(world).ShouldBe(20, "1000 scanned rows use twenty batches, not one read per row");
        var cursor = DecisionCursor(first);
        cursor.ShouldNotBeNullOrEmpty();

        var second = await ListAsync(world, $"decision=pass&limit=3&cursor={Uri.EscapeDataString(cursor)}");
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        OrderedIds(second).ShouldBe(new[] { runs[1001], runs[1002] }, ignoreOrder: false);
        DecisionCursor(second).ShouldBeNull();
    }

    [Fact]
    public async Task ListRuns_DecisionFilter_LimitOneRetainsMatchesOnBothSidesOfScanBudget()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runs = await SeedOrderedDecisionRunsAsync(world, 1001);
        var policy = await SeedPolicyAsync(world);
        await InsertEvaluationAsync(world, runs[999], policy, 1, "live", "PASS");
        await InsertEvaluationAsync(world, runs[1000], policy, 1, "live", "PASS");

        var first = await ListAsync(world, "decision=pass&limit=1");
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        OrderedIds(first).ShouldBe(new[] { runs[999] });
        var cursor = DecisionCursor(first);
        cursor.ShouldNotBeNullOrEmpty();
        var second = await ListAsync(world, $"decision=pass&limit=1&cursor={Uri.EscapeDataString(cursor)}");
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        OrderedIds(second).ShouldBe(new[] { runs[1000] });
        DecisionCursor(second).ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(49)]
    [InlineData(50)]
    [InlineData(1000)]
    [InlineData(1001)]
    public async Task ListRuns_DecisionFilter_NoMatchesTerminatesAndLastRunCanBecomeAMatch(int count)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runs = await SeedOrderedDecisionRunsAsync(world, count);
        world.Api.Statements.Clear();
        var first = await ListAsync(world, "decision=pass&limit=1");
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        Items(first).ShouldBeEmpty();
        EvaluationStatementCount(world).ShouldBe((Math.Min(count, 1000) + 49) / 50);
        var cursor = DecisionCursor(first);
        if (count >= 1000)
        {
            cursor.ShouldNotBeNullOrEmpty();
            var second = await ListAsync(world, $"decision=pass&limit=1&cursor={Uri.EscapeDataString(cursor)}");
            second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
            Items(second).ShouldBeEmpty();
            DecisionCursor(second).ShouldBeNull();
        }
        else
        {
            cursor.ShouldBeNull();
        }

        var control = count == 0 ? await CreateBranchAsync(world, "control", ShaOne) : runs[^1];
        var policy = await SeedPolicyAsync(world);
        await InsertEvaluationAsync(world, control, policy, 1, "live", "PASS");
        (await PagedIdsAsync(world, "decision=pass&limit=1")).ShouldBe(new[] { control });
    }

    [Theory]
    [InlineData("pass")]
    [InlineData("pending")]
    public async Task ListRuns_DecisionFilter_ForeignTenantCannotReadOrProbeEvaluatedProject(string filter)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runs = await SeedOrderedDecisionRunsAsync(world, 2);
        var policy = await SeedPolicyAsync(world);
        await InsertEvaluationAsync(world, runs[0], policy, 1, "live", "PASS");
        var own = await ListAsync(world, $"decision={filter}");
        own.Status.ShouldBe(HttpStatusCode.OK, own.ErrorMessage);
        OrderedIds(own).ShouldBe(new[] { filter == "pass" ? runs[0] : runs[1] });

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var other = TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg);
        world.Api.Statements.Clear();
        var foreign = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/projects/{world.Project}/runs?decision={filter}", other);
        var unknown = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/projects/{Guid.CreateVersion7()}/runs?decision={filter}", other);

        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        foreign.ErrorCode.ShouldBe(unknown.ErrorCode);
        foreign.ErrorMessage.ShouldBe(unknown.ErrorMessage);
        EvaluationStatementCount(world).ShouldBe(0, "foreign and nonexistent projects are rejected before decision reads");
    }

    [Fact]
    public async Task ListRuns_DecisionDecoration_MaximumPageUsesOneEvaluationStatement()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runs = await SeedOrderedDecisionRunsAsync(world, 201);
        var policy = await SeedPolicyAsync(world);
        await InsertEvaluationAsync(world, runs[0], policy, 1, "live", "PASS");
        await InsertEvaluationAsync(world, runs[199], policy, 1, "live", "FAIL");
        await InsertEvaluationAsync(world, runs[200], policy, 1, "live", "INCONCLUSIVE");
        world.Api.Statements.Clear();

        var page = await ListAsync(world, "limit=200");
        page.Status.ShouldBe(HttpStatusCode.OK, page.ErrorMessage);
        OrderedIds(page).ShouldBe(runs[..200], ignoreOrder: false);
        Items(page)[0].GetProperty("decision").GetString().ShouldBe("PASS");
        Items(page)[199].GetProperty("decision").GetString().ShouldBe("FAIL");
        Items(page)[1].GetProperty("decision").ValueKind.ShouldBe(JsonValueKind.Null);
        EvaluationStatementCount(world).ShouldBe(1);
        var cursor = DecisionCursor(page);
        cursor.ShouldNotBeNullOrEmpty();
        var last = await ListAsync(world, $"limit=200&cursor={Uri.EscapeDataString(cursor)}");
        OrderedIds(last).ShouldBe(runs[200..]);
        Items(last).ShouldHaveSingleItem().GetProperty("decision").GetString().ShouldBe("INCONCLUSIVE");
        DecisionCursor(last).ShouldBeNull();
    }

    [Fact]
    public async Task ListRuns_DecisionFilter_HigherLiveRevisionWinsOverLaterInsertedOlderAndShadowRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var run = await CreateBranchAsync(world, "main", ShaOne);
        var policy = await SeedPolicyAsync(world);
        await InsertEvaluationAsync(world, run, policy, 3, "live", "FAIL");
        await InsertEvaluationAsync(world, run, policy, 2, "live", "PASS");
        await InsertEvaluationAsync(world, run, policy, 4, "shadow", "PASS");

        (await IdsAsync(world, "decision=fail")).ShouldBe(new[] { run });
        (await IdsAsync(world, "decision=pass")).ShouldBeEmpty();
        (await IdsAsync(world, "decision=pending")).ShouldBeEmpty();
        (await RouteDecisionAsync(world, run)).ShouldBe("FAIL");
    }

    [Fact]
    public async Task ListRuns_DecisionFilter_ShadowOnlyPassRemainsPending()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var run = await CreateBranchAsync(world, "main", ShaOne);
        var policy = await SeedPolicyAsync(world);
        await InsertEvaluationAsync(world, run, policy, 1, "shadow", "PASS");

        (await IdsAsync(world, "decision=pending")).ShouldBe(new[] { run });
        (await IdsAsync(world, "decision=pass")).ShouldBeEmpty();
        await InsertEvaluationAsync(world, run, policy, 1, "live", "FAIL");
        (await IdsAsync(world, "decision=pending")).ShouldBeEmpty();
        (await IdsAsync(world, "decision=fail")).ShouldBe(new[] { run });
    }

    [Fact]
    public async Task ListRuns_DecisionFilter_RevisionFlipBetweenPagesUsesLatestUnseenVerdicts()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runs = await SeedOrderedDecisionRunsAsync(world, 4);
        var policy = await SeedPolicyAsync(world);
        for (var index = 0; index < runs.Length; index++)
        {
            await InsertEvaluationAsync(world, runs[index], policy, 1, "live", index == 2 ? "FAIL" : "PASS");
        }

        var first = await ListAsync(world, "decision=pass&limit=1");
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        OrderedIds(first).ShouldBe(new[] { runs[0] });
        var cursor = DecisionCursor(first);
        cursor.ShouldNotBeNullOrEmpty();
        await InsertEvaluationAsync(world, runs[1], policy, 2, "live", "FAIL");
        await InsertEvaluationAsync(world, runs[2], policy, 2, "live", "PASS");

        var second = await ListAsync(world, $"decision=pass&limit=1&cursor={Uri.EscapeDataString(cursor)}");
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        OrderedIds(second).ShouldBe(new[] { runs[2] });
        var next = DecisionCursor(second);
        next.ShouldNotBeNullOrEmpty();
        var third = await ListAsync(world, $"decision=pass&limit=1&cursor={Uri.EscapeDataString(next)}");
        third.Status.ShouldBe(HttpStatusCode.OK, third.ErrorMessage);
        OrderedIds(third).ShouldBe(new[] { runs[3] });
        DecisionCursor(third).ShouldBeNull();
        (await PagedIdsAsync(world, "decision=fail&limit=1")).ShouldBe(new[] { runs[1] });
    }

    [Fact]
    public async Task ListRuns_Decision_NewerUnevaluatedEvidenceKeepsStoredVerdictUntilEvaluationArrives()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var run = await CreateBranchAsync(world, "main", ShaOne);
        var policy = await SeedPolicyAsync(world);
        await AppendDecisionEvidenceAsync(world, run, 1, "complete", "all_passed");
        await InsertEvaluationAsync(world, run, policy, 1, "live", "PASS");
        await AppendDecisionEvidenceAsync(world, run, 2, "incomplete", "no_results");

        var page = await ListAsync(world, "decision=pass");
        page.Status.ShouldBe(HttpStatusCode.OK, page.ErrorMessage);
        var item = Items(page).ShouldHaveSingleItem();
        item.GetProperty("decision").GetString().ShouldBe("PASS");
        item.GetProperty("completeness").GetString().ShouldBe("incomplete");
        (await RouteDecisionAsync(world, run)).ShouldBe("PASS");
        await InsertEvaluationAsync(world, run, policy, 2, "live", "INCONCLUSIVE");
        (await IdsAsync(world, "decision=pass")).ShouldBeEmpty();
        (await IdsAsync(world, "decision=inconclusive")).ShouldBe(new[] { run });
    }

    private async Task<Guid[]> SeedOrderedDecisionRunsAsync(RunCreationTests.World world, int count, bool tied = false)
    {
        if (count == 0)
        {
            return [];
        }

        var template = await CreateBranchAsync(world, "main", ShaOne);
        await _fixture.ExecuteAsync(world.Database,
            $"update public.run set created_at = timestamptz '2026-01-01T00:00:00Z' where id = '{template:D}'");
        await _fixture.ExecuteAsync(world.Database, $"""
            insert into public.run
              (id, org_id, project_id, manifest_id, policy_revision_id, suite_revision_id, environment_revision_id,
               state, collection_deadline_at, late_window_minutes, created_at, updated_at)
            select gen_random_uuid(), r.org_id, r.project_id, r.manifest_id, r.policy_revision_id,
                   r.suite_revision_id, r.environment_revision_id, r.state, r.collection_deadline_at,
                   r.late_window_minutes, r.created_at + (clone * {(tied ? 0 : 1)} * interval '1 second'), r.updated_at
              from public.run r cross join generate_series(1, {count - 1}) as clone
             where r.id = '{template:D}'
            """);
        var runs = (await OrderedIdsAsync(world)).Select(Guid.Parse).ToArray();
        runs.Length.ShouldBe(count);
        return runs;
    }

    private async Task AppendDecisionEvidenceAsync(
        RunCreationTests.World world, Guid run, int revision, string completeness, string outcome)
    {
        var id = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        await scope.ExecuteAsync("""
            insert into public.evidence_revision
              (id, org_id, run_id, revision_no, completeness, missing, outcome, event_watermark, manifest_hash)
            values (@id, @org_id, @run_id, @revision, @completeness,
                    case when @completeness = 'complete' then array[]::text[] else array['missing_result']::text[] end,
                    @outcome, 0, @manifest_hash)
            """, ("id", id), ("org_id", world.Org), ("run_id", run),
            ("revision", revision), ("completeness", completeness), ("outcome", outcome),
            ("manifest_hash", "sha256:" + new string('c', 64)));
        await scope.ExecuteAsync(
            "update public.run set current_evidence_revision_id = @id where org_id = @org_id and id = @run_id",
            ("id", id), ("org_id", world.Org), ("run_id", run));
        await scope.CommitAsync();
    }

    private static string? DecisionCursor(ApiAnswer answer) => answer.Body.GetProperty("next_cursor").GetString();

    private static int EvaluationStatementCount(RunCreationTests.World world) =>
        world.Api.Statements.Snapshot().Count(statement => statement.Contains(EvaluationTable, StringComparison.Ordinal));
}
