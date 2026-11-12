// The run list's decision member and its decision filter, end to end: the list copies the newest live verdict of the
// decision route, and the filter pages the keyset order without a skip or a repeat.
using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <c>GET /v1/projects/{p}/runs</c>'s <c>decision</c> member and filter against a real PostgreSQL: every item's
/// verdict is the newest <c>live</c> evaluation Policy names for the run (the same row the decision route serves),
/// <c>null</c> while the run has none, and the filter pages the keyset order for each of <c>pending</c>, <c>PASS</c>,
/// <c>FAIL</c> and <c>INCONCLUSIVE</c> without skipping or repeating a run.
/// </summary>
public sealed partial class ListRunsTests
{
    /// <summary>The evaluation table the statement-budget assertion counts.</summary>
    private const string EvaluationTable = "public.evaluation";

    private const string PolicyHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>
    /// The list copies each run's newest live verdict — later revision over earlier, shadow rows ignored — and the
    /// decision route answers the same value; a run with no live row is <c>null</c> in the list and <c>pending</c>
    /// on the route.
    /// </summary>
    [Fact]
    public async Task ListRuns_Decision_IsTheNewestLiveVerdict_AndNullWhilePending()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var policy = await SeedPolicyAsync(world);
        var revised = await CreateBranchAsync(world, "main", ShaOne);
        var failed = await CreateBranchAsync(world, "feature", ShaOne);
        var inconclusive = await CreateBranchAsync(world, "release", ShaOne);
        var shadowOnly = await CreateBranchAsync(world, "shadow", ShaOne);

        await InsertEvaluationAsync(world, revised, policy, 1, "live", "FAIL");
        await InsertEvaluationAsync(world, revised, policy, 2, "live", "PASS");
        await InsertEvaluationAsync(world, revised, policy, 3, "shadow", "INCONCLUSIVE");
        await InsertEvaluationAsync(world, failed, policy, 1, "live", "FAIL");
        await InsertEvaluationAsync(world, inconclusive, policy, 1, "live", "INCONCLUSIVE");
        await InsertEvaluationAsync(world, shadowOnly, policy, 1, "shadow", "PASS");

        var listed = await DecisionsByRunAsync(world, "limit=50");

        listed[revised].ShouldBe("PASS", "the newest live revision decides, not the earlier one and not the shadow row");
        listed[failed].ShouldBe("FAIL");
        listed[inconclusive].ShouldBe("INCONCLUSIVE");
        listed[shadowOnly].ShouldBeNull("a shadow evaluation is never a run's decision");
        (await RouteDecisionsAsync(world)).ShouldBe(listed, ignoreOrder: true, "the list and the decision route serve one verdict");

        foreach (var run in new[] { revised, failed, inconclusive })
        {
            (await RouteDecisionAsync(world, run)).ShouldBe(listed[run]);
        }

        (await RouteDecisionAsync(world, shadowOnly)).ShouldBeNull();
    }

    /// <summary>
    /// Each verdict filter and <c>pending</c> page the keyset order with a small limit: concatenating the pages
    /// yields every matching run exactly once, in the database's own <c>(created_at, id)</c> order.
    /// </summary>
    [Fact]
    public async Task ListRuns_DecisionFilter_PagesWithoutSkipOrRepeat()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var policy = await SeedPolicyAsync(world);
        var verdicts = new List<string?> { "PASS", "FAIL", "PASS", null, "INCONCLUSIVE", "FAIL", null, "PASS", "FAIL", "INCONCLUSIVE", null, "PASS" };
        var runs = new List<Guid>(verdicts.Count);
        for (var index = 0; index < verdicts.Count; index++)
        {
            var run = await CreateBranchAsync(world, $"branch-{index}", ShaOne);
            runs.Add(run);
            if (verdicts[index] is { } verdict)
            {
                await InsertEvaluationAsync(world, run, policy, 1, "live", verdict);
            }
        }

        var ordered = (await OrderedIdsAsync(world)).Select(Guid.Parse).ToArray();
        ordered.Length.ShouldBe(runs.Count);

        foreach (var decision in new[] { "pending", "pass", "fail", "inconclusive" })
        {
            var expected = ordered.Where(run => Matches(verdicts[runs.IndexOf(run)], decision)).ToArray();
            var paged = await PagedIdsAsync(world, $"decision={decision}&limit=2");

            paged.ShouldBe(expected, ignoreOrder: false, $"decision={decision} pages the keyset order");
            paged.Distinct().Count().ShouldBe(paged.Length, $"decision={decision} repeats no run");
        }
    }

    /// <summary>
    /// The filter token is not the stored value: <c>decision=pass</c> selects the run whose item serves
    /// <c>"decision":"PASS"</c>, and the stored spelling as a filter is refused rather than answered.
    /// </summary>
    [Fact]
    public async Task ListRuns_DecisionFilter_TokenMapsToTheStoredVerdict()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var policy = await SeedPolicyAsync(world);
        var passing = await CreateBranchAsync(world, "main", ShaOne);
        var failing = await CreateBranchAsync(world, "next", ShaOne);
        await InsertEvaluationAsync(world, passing, policy, 1, "live", "PASS");
        await InsertEvaluationAsync(world, failing, policy, 1, "live", "FAIL");

        var passPage = await ListAsync(world, "decision=pass&limit=50");
        passPage.Status.ShouldBe(HttpStatusCode.OK, passPage.ErrorMessage);
        var item = Items(passPage).ShouldHaveSingleItem();
        item.GetProperty("run_id").GetGuid().ShouldBe(passing);
        item.GetProperty("decision").GetString().ShouldBe("PASS");

        (await ListAsync(world, "decision=FAIL&limit=50")).Status
            .ShouldBe(HttpStatusCode.UnprocessableEntity, "the filter vocabulary is lower snake");
    }

    /// <summary>The filter is AND-combined with a column-backed filter, so a canceled run never matches <c>state=queued</c>.</summary>
    [Fact]
    public async Task ListRuns_DecisionFilter_CombinedWithState()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var policy = await SeedPolicyAsync(world);
        var queued = await CreateBranchAsync(world, "main", ShaOne);
        var canceled = await CreateBranchAsync(world, "feature", ShaOne);
        await InsertEvaluationAsync(world, queued, policy, 1, "live", "PASS");
        await InsertEvaluationAsync(world, canceled, policy, 1, "live", "PASS");
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set state = 'canceled' where id = '{canceled:D}'");

        (await IdsAsync(world, "decision=pass")).ShouldBe(new[] { queued, canceled }, ignoreOrder: true);
        (await IdsAsync(world, "decision=pass&state=queued")).ShouldBe(new[] { queued }, ignoreOrder: true);
        (await IdsAsync(world, "decision=fail&state=queued")).ShouldBeEmpty();
    }

    /// <summary>
    /// The scan is bounded: a page whose filter matches almost nothing stops after the documented row budget and
    /// answers a short (here empty) page whose <c>next_cursor</c> is the last scanned row, so the following page
    /// reaches the match without skipping or repeating a run.
    /// </summary>
    [Fact]
    public async Task ListRuns_DecisionScan_IsBounded_AndShortPageAdvances()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var policy = await SeedPolicyAsync(world);
        var template = await CreateBranchAsync(world, "main", ShaOne);

        // One thousand and one clones after the template: the first page's budget cannot reach the only verdict.
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.run
              (id, org_id, project_id, manifest_id, policy_revision_id, suite_revision_id, environment_revision_id,
               state, collection_deadline_at, late_window_minutes, created_at, updated_at)
            select gen_random_uuid(), r.org_id, r.project_id, r.manifest_id, r.policy_revision_id,
                   r.suite_revision_id, r.environment_revision_id, r.state, r.collection_deadline_at,
                   r.late_window_minutes, timestamptz '2027-01-01T00:00:00Z' + (clone * interval '1 second'),
                   r.updated_at
              from public.run r
             cross join generate_series(1, 1001) as clone
             where r.id = '{template:D}'
            """);
        var evaluated = await NewestRunIdAsync(world);
        await InsertEvaluationAsync(world, evaluated, policy, 1, "live", "PASS");

        var first = await ListAsync(world, "decision=pass&limit=1");
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        Items(first).ShouldBeEmpty("the scan budget ends before the one verdict, so the page is short");
        var cursor = first.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrEmpty("a short page still points at the position the scan reached");

        var second = await ListAsync(world, $"decision=pass&limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        OrderedIds(second).ShouldBe(new[] { evaluated }, ignoreOrder: false);
        second.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);

        // The pending scan matches the clones too, so the budget never hides a row from a later page either.
        var pendingFirst = await ListAsync(world, "decision=pending&limit=1");
        Items(pendingFirst).ShouldHaveSingleItem()
            .GetProperty("decision").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// Another project of the same org never shows this project's verdicts: the decoration names only the page's own
    /// run ids, and the page itself is cut by the project predicate.
    /// </summary>
    [Fact]
    public async Task ListRuns_Decision_AnotherProjectsRunNeverMatches()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var policy = await SeedPolicyAsync(world);
        var mine = await CreateBranchAsync(world, "main", ShaOne);
        await InsertEvaluationAsync(world, mine, policy, 1, "live", "PASS");

        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "other");
        var parents = await SiblingProjectParents.SeedAsync(_fixture, world.Database, world.Org, sibling);
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.run
              (id, org_id, project_id, manifest_id, policy_revision_id, suite_revision_id, environment_revision_id,
               state, collection_deadline_at, late_window_minutes, created_at, updated_at)
            select gen_random_uuid(), r.org_id, '{sibling:D}', '{parents.Manifest:D}', null,
                   '{parents.SuiteRevision:D}', '{parents.EnvironmentRevision:D}', r.state, r.collection_deadline_at,
                   r.late_window_minutes, r.created_at, r.updated_at
              from public.run r
             where r.id = '{mine:D}'
            """);

        var siblingPass = await world.Client.SendAsync(
            HttpMethod.Get,
            $"/v1/projects/{sibling}/runs?decision=pass",
            world.Admin);
        siblingPass.Status.ShouldBe(HttpStatusCode.OK, siblingPass.ErrorMessage);
        Items(siblingPass).ShouldBeEmpty("the verdict belongs to the other project's run");

        // The same project still answers it, so the empty page is the project boundary and not a broken filter.
        (await IdsAsync(world, "decision=pass")).ShouldBe(new[] { mine }, ignoreOrder: false);
        var siblingAll = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{sibling}/runs", world.Admin);
        Items(siblingAll).ShouldHaveSingleItem()
            .GetProperty("decision").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>A page's verdicts cost one Policy statement, never one read per row.</summary>
    [Fact]
    public async Task ListRuns_DecisionDecoration_IsOneBatchStatement()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var policy = await SeedPolicyAsync(world);
        var first = await CreateBranchAsync(world, "main", ShaOne);
        var second = await CreateBranchAsync(world, "feature", ShaOne);
        var third = await CreateBranchAsync(world, "release", ShaOne);
        await InsertEvaluationAsync(world, first, policy, 1, "live", "PASS");
        await InsertEvaluationAsync(world, second, policy, 1, "live", "FAIL");
        await InsertEvaluationAsync(world, third, policy, 1, "live", "INCONCLUSIVE");

        world.Api.Statements.Clear();
        var answer = await ListAsync(world, "limit=10");
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Items(answer).Count.ShouldBe(3);

        world.Api.Statements.Snapshot()
            .Count(statement => statement.Contains(EvaluationTable, StringComparison.Ordinal))
            .ShouldBe(1, "three rows are one batch read, never three");
    }

    /// <summary>One page's verdict per run, plus the positive control that an unevaluated run carries a null member.</summary>
    private static async Task<Dictionary<Guid, string?>> DecisionsByRunAsync(RunCreationTests.World world, string query)
    {
        var answer = await ListAsync(world, query);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        var listed = new Dictionary<Guid, string?>();
        foreach (var item in Items(answer))
        {
            listed[item.GetProperty("run_id").GetGuid()] = item.GetProperty("decision").ValueKind == JsonValueKind.Null
                ? null
                : item.GetProperty("decision").GetString();
        }

        return listed;
    }

    /// <summary>Every verdict the decision route serves for the project's runs, keyed by run.</summary>
    private async Task<Dictionary<Guid, string?>> RouteDecisionsAsync(RunCreationTests.World world)
    {
        var listed = new Dictionary<Guid, string?>();
        foreach (var run in await OrderedIdsAsync(world))
        {
            listed[Guid.Parse(run)] = await RouteDecisionAsync(world, Guid.Parse(run));
        }

        return listed;
    }

    /// <summary>The decision route's verdict for one run; <c>pending</c> maps to null, the list's spelling of "not yet".</summary>
    private static async Task<string?> RouteDecisionAsync(RunCreationTests.World world, Guid runId)
    {
        var answer = await world.Client.SendAsync(HttpMethod.Get, $"/v1/runs/{runId}/decision", world.Admin);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        return answer.Text("status") == "pending" ? null : answer.Text("decision");
    }

    /// <summary>Walks every page of one filtered list and returns the run ids in the order they were served.</summary>
    private static async Task<Guid[]> PagedIdsAsync(RunCreationTests.World world, string query)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 100; page++)
        {
            var suffix = cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}";
            var answer = await ListAsync(world, query + suffix);
            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
            ids.AddRange(OrderedIds(answer));

            cursor = answer.Body.GetProperty("next_cursor").ValueKind == JsonValueKind.Null
                ? null
                : answer.Body.GetProperty("next_cursor").GetString();
            if (cursor is null)
            {
                return [.. ids];
            }
        }

        throw new InvalidOperationException($"the paging of {query} did not terminate");
    }

    /// <summary>The database's own spelling of the filter: a null verdict is <c>pending</c>.</summary>
    /// <summary>
    /// The contract's filter tokens mapped to the stored verdicts, written out here so a mapping slip on either
    /// side fails the test instead of agreeing with itself.
    /// </summary>
    private static string? StoredDecision(string decision) => decision switch
    {
        "pending" => null,
        "pass" => "PASS",
        "fail" => "FAIL",
        "inconclusive" => "INCONCLUSIVE",
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "not a decision filter token"),
    };

    private static bool Matches(string? stored, string decision) =>
        string.Equals(stored, StoredDecision(decision), StringComparison.Ordinal);

    /// <summary>One revision of the world's project, written by the app role so evaluation rows can point at it.</summary>
    private async Task<Guid> SeedPolicyAsync(RunCreationTests.World world)
    {
        var revision = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            """
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            select @id, @org_id, @project_id, coalesce(max(revision_no), 0) + 1, @hash, '{}', @created_by
              from public.policy_revision where org_id = @org_id and project_id = @project_id
            """,
            ("id", revision),
            ("org_id", world.Org),
            ("project_id", world.Project),
            ("hash", PolicyHash),
            ("created_by", world.User))).ShouldBe(1);
        await scope.CommitAsync();

        return revision;
    }

    /// <summary>One immutable <c>evaluation</c> row of a run's evidence revision, written by the app role.</summary>
    private async Task<Guid> InsertEvaluationAsync(
        RunCreationTests.World world,
        Guid runId,
        Guid policyRevisionId,
        int evidenceRevision,
        string mode,
        string decision)
    {
        var id = Guid.CreateVersion7();
        var complete = string.Equals(decision, "PASS", StringComparison.Ordinal);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        await scope.ExecuteAsync(
            """
            insert into public.evaluation
              (id, org_id, run_id, evidence_revision, policy_revision_id, policy_hash, manifest_hash, completeness,
               missing, outcome, decision, reasons, evaluator_version, mode, exceptions_hash, exceptions_as_of)
            values
              (@id, @org_id, @run_id, @evidence_revision, @policy_revision_id, @policy_hash, @manifest_hash,
               @completeness, array[]::text[], @outcome, @decision, array[]::text[], 'v1', @mode, @exceptions_hash, now())
            """,
            ("id", id),
            ("org_id", world.Org),
            ("run_id", runId),
            ("evidence_revision", evidenceRevision),
            ("policy_revision_id", policyRevisionId),
            ("policy_hash", PolicyHash),
            ("manifest_hash", "sha256:" + new string('c', 64)),
            ("completeness", complete ? "complete" : "incomplete"),
            ("outcome", complete ? "all_passed" : "has_failures"),
            ("decision", decision),
            ("mode", mode),
            ("exceptions_hash", "sha256:" + new string('d', 64)));
        await scope.CommitAsync();

        return id;
    }

    /// <summary>The id of the project's run with the greatest <c>created_at</c>, i.e. the last row the keyset scan reaches.</summary>
    private async Task<Guid> NewestRunIdAsync(RunCreationTests.World world) =>
        Guid.Parse(await _fixture.ScalarAsync<string>(
            world.Database,
            $"select id::text from public.run where project_id = '{world.Project:D}' order by created_at desc, id desc limit 1"));
}
