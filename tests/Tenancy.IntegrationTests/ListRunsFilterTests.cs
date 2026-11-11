// Request bodies are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The F43/S143 run-list read (00 §9.3, <c>GET /v1/projects/{p}/runs</c>) through the real host and a real
/// PostgreSQL: the four additive filters <c>state</c>, <c>decision</c>, <c>rerun_scope</c> and
/// <c>parent_run_id</c> are exact, AND-combined SQL predicates applied <em>before</em> the page is cut, and every
/// item carries <c>completeness</c> (from the run's current <c>evidence_revision</c>), <c>parent_run_id</c> and
/// <c>rerun_scope</c>. <c>decision</c> accepts only <c>pending</c> — no <c>evaluation</c> row exists before
/// 07.2.2 — so any other value is 422 <c>request_invalid</c> rather than a silently empty page, and
/// <c>completeness</c> is null (never a fabricated <c>incomplete</c>) while a run has no evidence revision.
/// </summary>
[Trait("Category", "Docker")]
public sealed class ListRunsFilterTests : IClassFixture<TenancyHostPool>
{
    private const string ShaOne = "1111111111111111111111111111111111111111";

    /// <summary>A well-formed manifest hash for the revisions these tests insert (00 §7 RFC 8785 address).</summary>
    private static readonly string ManifestHash = "sha256:" + new string('e', 64);

    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;
    public ListRunsFilterTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    /// <summary>F43: <c>state</c> selects exactly the runs in that state and no other.</summary>
    [Fact]
    public async Task ListRunsFilter_State_MatchesExactly()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var queued = await CreateRunAsync(world);
        var complete = await CreateRunAsync(world);
        var canceled = await CreateRunAsync(world);
        await SetStateAsync(world, complete, "complete");
        await SetStateAsync(world, canceled, "canceled");

        (await IdsAsync(world, "state=complete")).ShouldBe(new[] { complete });
        (await IdsAsync(world, "state=canceled")).ShouldBe(new[] { canceled });
        (await IdsAsync(world, "state=queued")).ShouldBe(new[] { queued });
        (await IdsAsync(world, "state=running")).ShouldBeEmpty();

        // The row still reports its own state, so the filter is not a client-side label.
        var item = Items(await ListAsync(world, "state=complete")).ShouldHaveSingleItem();
        item.GetProperty("state").GetString().ShouldBe("complete");
    }

    /// <summary>
    /// F43: <c>decision=pending</c> matches every run of a project whose runs store no evaluation, so every item's
    /// <c>decision</c> member is <c>null</c> and the page is the unfiltered one, in the same order.
    /// </summary>
    [Fact]
    public async Task ListRunsFilter_DecisionPending_ReturnsEveryRun()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await CreateRunAsync(world);
        await CreateRunAsync(world);
        await CreateRunAsync(world);

        var unfiltered = await ListAsync(world, "limit=50");
        unfiltered.Status.ShouldBe(HttpStatusCode.OK, unfiltered.ErrorMessage);

        var pending = await ListAsync(world, "decision=pending&limit=50");

        pending.Status.ShouldBe(HttpStatusCode.OK, pending.ErrorMessage);
        OrderedIds(pending).Length.ShouldBe(3);
        OrderedIds(pending).ShouldBe(OrderedIds(unfiltered), ignoreOrder: false);
        pending.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
        Items(pending).ShouldAllBe(item => item.GetProperty("decision").ValueKind == JsonValueKind.Null);
    }

    /// <summary>
    /// F43, openapi <c>RunDecision</c>: the vocabulary is <c>pending</c> plus the three lower-snake verdict tokens,
    /// matched case-exactly, so every other value — including the stored upper-case verdict itself — is 422
    /// <c>request_invalid</c> (<c>decision</c>, <c>invalid_value</c>), never an empty page, which would read as
    /// "no run has that verdict".
    /// </summary>
    [Fact]
    public async Task ListRunsFilter_Decision_OutOfVocabulary_422()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await CreateRunAsync(world);

        foreach (var value in new[] { "PASS", "Fail", "Inconclusive", "Pending", "pendingly", "unknown", "pass ", string.Empty })
        {
            var answer = await ListAsync(world, "decision=" + Uri.EscapeDataString(value));
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, value);
            answer.ErrorCode.ShouldBe("request_invalid");
            Details(answer).ShouldContain(detail =>
                detail.GetProperty("path").GetString() == "decision"
                && detail.GetProperty("reason").GetString() == "invalid_value");
        }

        // Every admitted value answers 200, so the loop above is not passing on a broken route; this world stores no
        // evaluation, so the verdict filters answer empty pages rather than 422.
        foreach (var value in new[] { "pending", "pass", "fail", "inconclusive" })
        {
            var answer = await ListAsync(world, "decision=" + value);
            answer.Status.ShouldBe(HttpStatusCode.OK, value);
        }
    }

    /// <summary>
    /// F43: <c>rerun_scope</c> is an exact match on <c>run.rerun_scope</c>; an original run stores null and matches
    /// neither value, and the row serves the column it filtered on together with its parent.
    /// </summary>
    [Fact]
    public async Task ListRunsFilter_RerunScope_MatchesExactly()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var original = await CreateRunAsync(world);
        var full = await CreateRunAsync(world);
        var failedSubset = await CreateRunAsync(world);
        await SetRerunAsync(world, full, original, "full");
        await SetRerunAsync(world, failedSubset, original, "failed_subset");

        (await IdsAsync(world, "rerun_scope=full")).ShouldBe(new[] { full });
        (await IdsAsync(world, "rerun_scope=failed_subset")).ShouldBe(new[] { failedSubset });

        var fullItem = Items(await ListAsync(world, "rerun_scope=full")).ShouldHaveSingleItem();
        fullItem.GetProperty("rerun_scope").GetString().ShouldBe("full");
        fullItem.GetProperty("parent_run_id").GetGuid().ShouldBe(original);

        // An original run is null on both members and is never matched by either scope.
        var originalItem = Items(await ListAsync(world, "limit=50")).Single(i => i.GetProperty("run_id").GetGuid() == original);
        originalItem.GetProperty("rerun_scope").ValueKind.ShouldBe(JsonValueKind.Null);
        originalItem.GetProperty("parent_run_id").ValueKind.ShouldBe(JsonValueKind.Null);
        (await IdsAsync(world, "rerun_scope=full")).ShouldNotContain(original);
        (await IdsAsync(world, "rerun_scope=failed_subset")).ShouldNotContain(original);
    }

    /// <summary>
    /// F43: <c>parent_run_id</c> is an exact match on <c>run.parent_run_id</c>; an unknown parent is an empty page
    /// (200, like any filter that matches nothing) and a present but non-UUID value is 422 <c>request_invalid</c>.
    /// </summary>
    [Fact]
    public async Task ListRunsFilter_ParentRunId_MatchesExactly()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var parent = await CreateRunAsync(world);
        var otherParent = await CreateRunAsync(world);
        var child = await CreateRunAsync(world);
        var otherChild = await CreateRunAsync(world);
        await SetRerunAsync(world, child, parent, "full");
        await SetRerunAsync(world, otherChild, otherParent, "failed_subset");

        (await IdsAsync(world, $"parent_run_id={parent:D}")).ShouldBe(new[] { child });
        (await IdsAsync(world, $"parent_run_id={otherParent:D}")).ShouldBe(new[] { otherChild });

        var none = await ListAsync(world, $"parent_run_id={Guid.CreateVersion7():D}");
        none.Status.ShouldBe(HttpStatusCode.OK, none.ErrorMessage);
        Items(none).ShouldBeEmpty();

        foreach (var raw in new[] { "not-a-uuid", string.Empty, "018f0000-0000-7000-8000" })
        {
            var answer = await ListAsync(world, "parent_run_id=" + Uri.EscapeDataString(raw));
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, raw);
            answer.ErrorCode.ShouldBe("request_invalid");
            Details(answer).ShouldContain(detail =>
                detail.GetProperty("path").GetString() == "parent_run_id"
                && detail.GetProperty("reason").GetString() == "invalid_value");
        }
    }

    /// <summary>
    /// F43: a present but out-of-vocabulary <c>state</c> is 422 <c>request_invalid</c> (<c>state</c>,
    /// <c>invalid_value</c>) — the closed seven of 00 §8.1, matched case-sensitively.
    /// </summary>
    [Fact]
    public async Task ListRunsFilter_BadState_422()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await CreateRunAsync(world);

        foreach (var value in new[] { "COMPLETE", "Complete", "unknown", "queued ", " pending", string.Empty })
        {
            var answer = await ListAsync(world, "state=" + Uri.EscapeDataString(value));
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, value);
            answer.ErrorCode.ShouldBe("request_invalid");
            Details(answer).ShouldContain(detail =>
                detail.GetProperty("path").GetString() == "state"
                && detail.GetProperty("reason").GetString() == "invalid_value");
        }

        // Every one of the seven declared states is accepted.
        foreach (var state in new[] { "created", "queued", "running", "collecting", "complete", "incomplete", "canceled" })
        {
            (await ListAsync(world, "state=" + state)).Status.ShouldBe(HttpStatusCode.OK, state);
        }
    }

    /// <summary>
    /// F43: the filters are AND-combined, so a page is the rows that satisfy every present predicate, and a pair no
    /// row satisfies is a successful empty page rather than a 404.
    /// </summary>
    [Fact]
    public async Task ListRunsFilter_Combined_And()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var parent = await CreateRunAsync(world);
        var otherParent = await CreateRunAsync(world);
        var both = await CreateRunAsync(world);
        var scopeOnly = await CreateRunAsync(world);
        var stateOnly = await CreateRunAsync(world);
        var otherParentMatch = await CreateRunAsync(world);
        var identified = await CreateNamedRunAsync(world, "release-train");

        await SetStateAsync(world, both, "complete");
        await SetRerunAsync(world, both, parent, "full");
        await SetStateAsync(world, scopeOnly, "running");
        await SetRerunAsync(world, scopeOnly, parent, "full");
        await SetStateAsync(world, stateOnly, "complete");
        await SetRerunAsync(world, stateOnly, parent, "failed_subset");
        await SetStateAsync(world, otherParentMatch, "complete");
        await SetRerunAsync(world, otherParentMatch, otherParent, "full");
        await SetStateAsync(world, identified, "complete");

        (await IdsAsync(world, $"state=complete&rerun_scope=full&parent_run_id={parent:D}")).ShouldBe(new[] { both });
        (await IdsAsync(world, "state=running&rerun_scope=failed_subset")).ShouldBeEmpty();
        (await IdsAsync(world, $"rerun_scope=full&parent_run_id={otherParent:D}")).ShouldBe(new[] { otherParentMatch });

        // An identity filter combines with a new one the same way.
        (await IdsAsync(world, "ref_name=release-train&state=complete")).ShouldBe(new[] { identified });
        (await IdsAsync(world, "ref_name=release-train&state=running")).ShouldBeEmpty();
    }

    /// <summary>
    /// F43, openapi <c>RunSummary.completeness</c>: the member is the <c>evidence_revision.completeness</c> of the
    /// run's <em>current</em> revision — null while the run has no revision, never a fabricated <c>incomplete</c> —
    /// and it follows the pointer when a later revision is frozen.
    /// </summary>
    [Fact]
    public async Task ListRunsFilter_Completeness_FollowsTheCurrentEvidenceRevision()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var pending = await CreateRunAsync(world);
        var complete = await CreateRunAsync(world);
        var incomplete = await CreateRunAsync(world);

        await AppendEvidenceAsync(world, complete, [("complete", 1)]);
        await AppendEvidenceAsync(world, incomplete, [("incomplete", 1)]);

        var items = Items(await ListAsync(world, "limit=50"));
        foreach (var item in items)
        {
            item.TryGetProperty("completeness", out _).ShouldBeTrue("every row carries the member");
        }

        items.Single(i => i.GetProperty("run_id").GetGuid() == pending)
            .GetProperty("completeness").ValueKind.ShouldBe(JsonValueKind.Null, "no revision is null, never incomplete");
        items.Single(i => i.GetProperty("run_id").GetGuid() == complete)
            .GetProperty("completeness").GetString().ShouldBe("complete");
        items.Single(i => i.GetProperty("run_id").GetGuid() == incomplete)
            .GetProperty("completeness").GetString().ShouldBe("incomplete");

        // The member is the CURRENT revision's: a second revision moves the verdict.
        await AppendEvidenceAsync(world, complete, [("incomplete", 2)]);
        Items(await ListAsync(world, $"state=queued&limit=50"))
            .Single(i => i.GetProperty("run_id").GetGuid() == complete)
            .GetProperty("completeness").GetString().ShouldBe("incomplete");
    }

    /// <summary>
    /// 00 §9.1 with filters: the predicates narrow the rows the page is cut from — so a page of two holds two
    /// matching rows even when non-matching rows sit between them in <c>(created_at, id)</c> order — the keyset
    /// cursor keeps them narrowed on the following page, and <c>limit</c> stays bounded at 1..200.
    /// </summary>
    [Fact]
    public async Task ListRunsFilter_Pagination_AcrossFilteredPages()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var ids = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            ids.Add(await CreateRunAsync(world));
        }

        // Alternate the state and pin a created_at per row so the page order is the database's, not the clock's.
        for (var index = 0; index < ids.Count; index++)
        {
            await SetStateAsync(world, ids[index], index % 2 == 0 ? "complete" : "running");
            await SetCreatedAtAsync(world, ids[index], $"2026-05-0{index + 1}T00:00:00Z");
        }

        var matching = new[] { ids[0], ids[2], ids[4] };

        // limit=2 over an alternating list: the two oldest MATCHING rows, though a non-matching row lies between.
        var pageOne = await ListAsync(world, "state=complete&limit=2");
        pageOne.Status.ShouldBe(HttpStatusCode.OK, pageOne.ErrorMessage);
        OrderedIds(pageOne).ShouldBe(matching[..2], ignoreOrder: false);

        var cursor = pageOne.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrEmpty();

        var pageTwo = await ListAsync(world, $"state=complete&limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        pageTwo.Status.ShouldBe(HttpStatusCode.OK, pageTwo.ErrorMessage);
        OrderedIds(pageTwo).ShouldBe(matching[2..], ignoreOrder: false);
        pageTwo.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
        OrderedIds(pageOne).Concat(OrderedIds(pageTwo)).ShouldBe(matching, ignoreOrder: false);
        OrderedIds(pageOne).Concat(OrderedIds(pageTwo)).Distinct().Count().ShouldBe(3);

        // The cursor is a position only: replayed under the other state it stays just a position.
        var crossed = await ListAsync(world, $"state=running&cursor={Uri.EscapeDataString(cursor!)}");
        crossed.Status.ShouldBe(HttpStatusCode.OK, crossed.ErrorMessage);
        OrderedIds(crossed).ShouldBe(new[] { ids[3] }, ignoreOrder: false);

        // 00 §9.1 boundaries: 1 and 200 are served, 201 is 422 out_of_range, with the filter present.
        (await ListAsync(world, "state=complete&limit=1")).Status.ShouldBe(HttpStatusCode.OK);
        (await ListAsync(world, "state=complete&limit=200")).Status.ShouldBe(HttpStatusCode.OK);

        var tooLarge = await ListAsync(world, "state=complete&limit=201");
        tooLarge.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        tooLarge.ErrorCode.ShouldBe("request_invalid");
        Details(tooLarge).ShouldContain(detail =>
            detail.GetProperty("path").GetString() == "limit"
            && detail.GetProperty("reason").GetString() == "out_of_range");
    }

    /// <summary>
    /// 00 §5.4 with a filter present: another tenant's run never appears in the page, even when it satisfies the
    /// same predicate — each tenant's filtered page is its own rows — and naming the foreign project is the same
    /// 404 <c>not_found</c> as an unknown one, never an empty page.
    /// </summary>
    [Fact]
    public async Task ListRunsFilter_OtherTenantRun_Absent()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var mine = await CreateRunAsync(world);
        await using var second = await StartSecondTenantAsync(world.Database, "rival");
        var theirs = await CreateRunAsync(second);

        // Both runs are `queued`, so the predicate alone would match both: only the tenant scope separates them.
        (await IdsAsync(world, "state=queued")).ShouldBe(new[] { mine });
        (await IdsAsync(second, "state=queued")).ShouldBe(new[] { theirs });

        var foreignProject = await second.Client.SendAsync(
            HttpMethod.Get,
            $"/v1/projects/{world.Project}/runs?state=queued",
            second.Admin);
        var unknownProject = await world.Client.SendAsync(
            HttpMethod.Get,
            $"/v1/projects/{Guid.CreateVersion7()}/runs?state=queued",
            world.Admin);

        foreignProject.Status.ShouldBe(HttpStatusCode.NotFound);
        foreignProject.ErrorCode.ShouldBe(unknownProject.ErrorCode);
        foreignProject.ErrorMessage.ShouldBe(unknownProject.ErrorMessage);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>One run of the world's manifest with a branch identity named <c>main</c>; returns its id.</summary>
    private static Task<Guid> CreateRunAsync(RunCreationTests.World world) => CreateNamedRunAsync(world, "main");

    /// <summary>One run of the world's manifest with a named branch identity; returns its id.</summary>
    private static async Task<Guid> CreateNamedRunAsync(RunCreationTests.World world, string refName)
    {
        var created = await RunCreationTests.CreateRunAsync(
            world,
            $$$"""{"manifest_id":"{{{world.ManifestId}}}","ref":{"kind":"branch","name":"{{{refName}}}","head_sha":"{{{ShaOne}}}"}}""");
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        return created.Id("run_id");
    }

    /// <summary>The run's state, walked through the edges the state machine allows as the migration role: nothing served drives it here.</summary>
    private Task SetStateAsync(RunCreationTests.World world, Guid runId, string state) =>
        RunStateWalk.MoveAsync(_fixture, world.Database, runId, state);

    /// <summary>The rerun link (0031's nullable <c>parent_run_id</c>/<c>rerun_scope</c>); 05.5.1 writes it in production.</summary>
    private Task SetRerunAsync(RunCreationTests.World world, Guid runId, Guid parentRunId, string scope) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set parent_run_id = '{parentRunId:D}', rerun_scope = '{scope}' where id = '{runId:D}'");

    /// <summary>Pins <c>created_at</c> so the page order is fixed for the run.</summary>
    private Task SetCreatedAtAsync(RunCreationTests.World world, Guid runId, string instant) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set created_at = timestamptz '{instant}' where id = '{runId:D}'");

    /// <summary>
    /// Appends the run's evidence revisions as the app role and moves <c>run.current_evidence_revision_id</c> to the
    /// last one, in one transaction — the reconciler's own order (D-S115-Q1). Each entry is
    /// <c>(completeness, revision_no)</c>, gap-free from 1.
    /// </summary>
    private async Task AppendEvidenceAsync(
        RunCreationTests.World world,
        Guid runId,
        (string Completeness, int RevisionNo)[] revisions)
    {
        const string InsertRevision =
            """
            insert into public.evidence_revision
              (id, org_id, run_id, revision_no, completeness, missing, outcome, event_watermark, manifest_hash)
            values (@id, @org_id, @run_id, @revision_no, @completeness, '{}', 'all_passed', 0, @manifest_hash)
            """;

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        Guid? latest = null;
        foreach (var (completeness, revisionNo) in revisions)
        {
            var id = Guid.CreateVersion7();
            (await scope.ExecuteAsync(
                InsertRevision,
                ("id", id),
                ("org_id", world.Org),
                ("run_id", runId),
                ("revision_no", revisionNo),
                ("completeness", completeness),
                ("manifest_hash", ManifestHash))).ShouldBe(1);
            latest = id;
        }

        (await scope.ExecuteAsync(
            "update public.run set current_evidence_revision_id = @revision_id where org_id = @org_id and id = @run_id",
            ("revision_id", latest!.Value),
            ("org_id", world.Org),
            ("run_id", runId))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>
    /// A second tenant in the SAME database (<see cref="RunCreationTests.StartAsync"/> mints a database, so its own
    /// steps are repeated here): another org, project, suite revision, environment revision and manifest, so its
    /// runs are real rows the first tenant's queries must not see.
    /// </summary>
    private async Task<RunCreationTests.World> StartSecondTenantAsync(string database, string slug)
    {
        var api = _hosts.RealResolver(database);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug(slug));
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var revisions = await ManifestTests.SeedCasesAsync(_fixture, api, org, project, database, 2);
        var admin = TenancyApiFactory.Resolved(user: user, orgHeader: org);

        var suite = await client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{project}/suites", admin, new { name = "Smoke", slug = "smoke" }, NewKey());
        suite.Status.ShouldBe(HttpStatusCode.Created, suite.ErrorMessage);
        var revision = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/suites/{suite.Id()}/revisions",
            admin,
            new { members = revisions.Select(id => new { case_revision_id = id }).ToArray() },
            NewKey());
        revision.Status.ShouldBe(HttpStatusCode.Created, revision.ErrorMessage);

        var environment = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{project}/environments/staging/revisions",
            admin,
            new { base_urls = new { web = "https://staging.example.test" }, dependency_versions = new { postgres = "18.0" } },
            NewKey());
        environment.Status.ShouldBe(HttpStatusCode.Created, environment.ErrorMessage);

        var manifest = RunCreationTests.Manifest(revision.Id("suite_revision_id"), revisions, variants: 2, shards: 2);
        manifest["environment_revision_id"] = environment.Id("revision_id").ToString();
        var manifestId = await RunCreationTests.UploadAsync(client, admin, project, manifest);

        return new RunCreationTests.World(
            api,
            client,
            database,
            user,
            org,
            project,
            revision.Id("suite_revision_id"),
            environment.Id("revision_id"),
            manifestId,
            revisions);
    }

    private static Task<ApiAnswer> ListAsync(RunCreationTests.World world, string query) =>
        world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/runs?{query}", world.Admin);

    private static async Task<Guid[]> IdsAsync(RunCreationTests.World world, string query)
    {
        var answer = await ListAsync(world, query);
        answer.Status.ShouldBe(HttpStatusCode.OK, $"{query}: {answer.ErrorMessage}");
        return OrderedIds(answer);
    }

    private static Guid[] OrderedIds(ApiAnswer answer) =>
        [.. Items(answer).Select(item => item.GetProperty("run_id").GetGuid())];

    private static List<JsonElement> Items(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("items").EnumerateArray()];

    private static List<JsonElement> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()];
}
