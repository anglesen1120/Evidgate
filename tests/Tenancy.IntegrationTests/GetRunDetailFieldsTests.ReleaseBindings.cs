// The run detail's release bindings and its late-event count, through the real host and a real PostgreSQL.
// Every expectation is read back from the stored rows, so a body that claims a binding the database does not
// hold fails here.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class GetRunDetailFieldsTests
{
    /// <summary>A component digest in the stored shape: <c>sha256:</c> + 64 lower-case hex.</summary>
    private const string WebDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>
    /// Two fresh binding ids, lower first. <c>release_run_pkey</c> is the row id, so a fixed pair would collide
    /// between tests that share the database; the order is what the tied <c>created_at</c> test sorts on.
    /// </summary>
    private static (Guid Low, Guid High) BindingIds()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        return first.CompareTo(second) < 0 ? (first, second) : (second, first);
    }

    /// <summary>The one binding time the tied pair carries, so only the id can decide their order.</summary>
    private static readonly DateTimeOffset TiedBoundAt = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A later binding time, so the sibling row below would sort last were it listed.</summary>
    private static readonly DateTimeOffset LaterBoundAt = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The binding path stored the pair, so the read reports the release's id, its name and the component key of each
    /// binding row — never a recency choice, never the release's digests.
    /// </summary>
    [Fact]
    public async Task GetRun_Releases_ComeFromTheStoredBindingRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await CreateReleaseAsync(world, "detail-bound", "web");
        var runId = await CreateBoundRunAsync(world, releaseId);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var releases = Releases(answer);
        releases.ShouldHaveSingleItem();
        releases[0].ReleaseId.ShouldBe(releaseId);
        releases[0].ReleaseName.ShouldBe("detail-bound");
        releases[0].ComponentKey.ShouldBe("web");
        releases.Select(Row).ShouldBe(await StoredReleasesAsync(world, runId));
    }

    /// <summary>
    /// An unbound run carries the member as an empty array — never null and never absent — and reports zero late
    /// events, so a screen can render "no release" without a second read.
    /// </summary>
    [Fact]
    public async Task GetRun_UnboundRun_ServesAnEmptyReleasesArrayAndZeroLateEvents()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("releases").ValueKind.ShouldBe(JsonValueKind.Array);
        answer.Body.GetProperty("releases").GetArrayLength().ShouldBe(0);
        answer.Body.GetRawText().ShouldContain("\"releases\":[]");
        answer.Body.GetProperty("after_cancel_events").GetInt32().ShouldBe(0);

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.release_run where run_id = '{runId:D}'")).ShouldBe(0);
    }

    /// <summary>
    /// Two bindings of one run whose stored <c>created_at</c> is equal: the id breaks the tie, so the order is not the
    /// insertion order (the higher id was written first) and not the heap order.
    /// </summary>
    [Fact]
    public async Task GetRun_TwoReleases_AreOrderedByBindingCreatedAtThenId()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");

        var (low, high) = BindingIds();
        var second = await SeedReleaseAsync(world, "detail-second", world.Project);
        var first = await SeedReleaseAsync(world, "detail-first", world.Project);
        await SeedBindingAsync(world, second, runId, "web", TiedBoundAt, high);
        await SeedBindingAsync(world, first, runId, "api", TiedBoundAt, low);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(distinct created_at) from public.release_run where run_id = '{runId:D}'")).ShouldBe(1);
        Releases(answer).Count.ShouldBe(2);
        Releases(answer)[0].ReleaseId.ShouldBe(first, "the tied pair sorts by binding id, not by insertion order");
        Releases(answer).Select(Row).ShouldBe(await StoredReleasesAsync(world, runId));
    }

    /// <summary>
    /// A binding row pointed at a sibling project's release — only a writer that bypassed the bind path can store one
    /// — is not listed, and another tenant's read of the bound run stays the one <c>404 not_found</c>.
    /// </summary>
    [Fact]
    public async Task GetRun_SiblingProjectBinding_IsNotListed()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");

        var (low, high) = BindingIds();
        var own = await SeedReleaseAsync(world, "detail-own", world.Project);
        await SeedBindingAsync(world, own, runId, "web", TiedBoundAt, low);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "sibling");
        var foreign = await SeedReleaseAsync(world, "detail-sibling", sibling);
        await SeedBindingAsync(world, foreign, runId, "web", LaterBoundAt, high);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Releases(answer).Select(release => release.ReleaseId).ShouldBe([own]);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.release_run where run_id = '{runId:D}'")).ShouldBe(2,
            "the sibling binding exists, so its absence from the body is the project predicate under test");

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var crossTenant = await GetAsync(world, runId, TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg));
        crossTenant.Status.ShouldBe(HttpStatusCode.NotFound);
        crossTenant.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>
    /// <c>after_cancel_events</c> counts the run's flagged <c>event</c> rows only: the unflagged row beside them is not
    /// counted, a second run of the same database reports zero, and both values match the stored rows.
    /// </summary>
    [Fact]
    public async Task GetRun_AfterCancelEvents_CountsOnlyTheFlaggedEvents()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");
        (await CancelRunTestData.CancelAsync(world, runId)).Status.ShouldBe(HttpStatusCode.Accepted);
        await SeedEventsAsync(world, runId, flagged: 2, unflagged: 1);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("after_cancel_events").GetInt32().ShouldBe(2);
        answer.Body.GetProperty("after_cancel_events").GetInt32().ShouldBe((int)await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.event where run_id = '{runId:D}' and after_cancel"));

        // The control: the same read over a run of the same database that carries no event reports zero.
        var open = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        open.Status.ShouldBe(HttpStatusCode.Created, open.ErrorMessage);
        (await GetAsync(world, open.Id("run_id"))).Body.GetProperty("after_cancel_events").GetInt32().ShouldBe(0);
    }

    /// <summary>
    /// <c>run:read</c> is the only permission the two members need: a caller holding it and not <c>decision:read</c>
    /// still reads the body, and the refusal of the decision route is the positive control for the missing grant.
    /// </summary>
    [Fact]
    public async Task GetRun_ReleaseMembers_NeedOnlyRunRead()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");
        await GrantRunReadOnlyAsync(world);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("releases").ValueKind.ShouldBe(JsonValueKind.Array);
        answer.Body.GetProperty("after_cancel_events").GetInt32().ShouldBe(0);

        var decision = await world.Client.SendAsync(HttpMethod.Get, $"/v1/runs/{runId}/decision", world.Admin);
        decision.Status.ShouldBe(HttpStatusCode.Forbidden, "the caller holds run:read and not decision:read");
    }

    /// <summary>One served <c>releases[]</c> item: the three required members, nothing else.</summary>
    private sealed record ServedRelease(Guid ReleaseId, string ReleaseName, string ComponentKey);

    /// <summary>A stored-row spelling, so a served list and the database can be compared as text.</summary>
    private static string Row(ServedRelease release) =>
        $"{release.ReleaseId:D}|{release.ReleaseName}|{release.ComponentKey}";

    /// <summary>The served <c>releases</c> array, in the order the read reported it.</summary>
    private static List<ServedRelease> Releases(ApiAnswer answer) =>
    [
        .. answer.Body.GetProperty("releases").EnumerateArray().Select(item =>
        {
            item.EnumerateObject().Select(member => member.Name)
                .ShouldBe(["release_id", "release_name", "component_key"], ignoreOrder: true);

            return new ServedRelease(
                item.GetProperty("release_id").GetGuid(),
                item.GetProperty("release_name").GetString()!,
                item.GetProperty("component_key").GetString()!);
        }),
    ];

    /// <summary>The binding rows a run holds, in the read's own order, read as the migration role.</summary>
    private async Task<IReadOnlyList<string>> StoredReleasesAsync(RunCreationTests.World world, Guid runId)
    {
        var rows = await _fixture.ColumnAsync<string>(
            world.Database,
            $"""
            select rr.release_id::text || '|' || rl.name || '|' || rr.component_key
              from public.release_run rr
              join public.release rl on rl.org_id = rr.org_id and rl.id = rr.release_id
             where rr.org_id = '{world.Org:D}' and rr.run_id = '{runId:D}'
             order by rr.created_at, rr.id
            """);

        return [.. rows];
    }

    /// <summary>Creates one active release through the served route; returns its id.</summary>
    private static async Task<Guid> CreateReleaseAsync(RunCreationTests.World world, string name, string key)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["state"] = "active",
            ["components"] = new[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["component_key"] = key,
                    ["digest"] = WebDigest,
                    ["environment_revision_id"] = world.EnvironmentRevision,
                },
            },
        };

        var answer = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{world.Project}/releases", world.Admin, body, NewKey());
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);

        return answer.Id("release_id");
    }

    /// <summary>Creates one run naming the release, so the run-create path writes the binding.</summary>
    private static async Task<Guid> CreateBoundRunAsync(RunCreationTests.World world, Guid releaseId)
    {
        var body = $$"""
            {"manifest_id":"{{world.ManifestId:D}}","components":{"web":"{{WebDigest}}"},"release_id":"{{releaseId:D}}"}
            """;
        var created = await RunCreationTests.CreateRunAsync(world, body);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("run_id");
    }

    /// <summary>Seeds one draft release of a project, as the owner; returns its id.</summary>
    private async Task<Guid> SeedReleaseAsync(RunCreationTests.World world, string name, Guid project)
    {
        var releaseId = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.release (id, org_id, project_id, name, created_by)
            values ('{releaseId:D}', '{world.Org:D}', '{project:D}', '{name}', 'probe');
            """);

        return releaseId;
    }

    /// <summary>Seeds one stored binding row, as the owner, at the given time and id.</summary>
    private async Task SeedBindingAsync(
        RunCreationTests.World world,
        Guid releaseId,
        Guid runId,
        string componentKey,
        DateTimeOffset boundAt,
        Guid bindingId) =>
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.release_run (id, org_id, release_id, run_id, component_key, created_at)
            values ('{bindingId:D}', '{world.Org:D}', '{releaseId:D}', '{runId:D}', '{componentKey}',
                    '{boundAt.UtcDateTime:O}');
            """);

    /// <summary>Seeds <c>event</c> rows for one run: the requested flagged and unflagged ones, under its first slot.</summary>
    private async Task SeedEventsAsync(RunCreationTests.World world, Guid runId, int flagged, int unflagged) =>
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.event
                (id, org_id, run_id, event_id, producer_id, sequence, execution_key, attempt_no, type, payload_hash,
                 after_cancel)
            select gen_random_uuid(), '{world.Org:D}', '{runId:D}', gen_random_uuid(),
                   'shard-1', row_number() over (order by flag desc, n), repeat('a', 64), 1, 'case.started',
                   'sha256:' || repeat('b', 64), flag
              from (select n, n <= {flagged} as flag
                      from generate_series(1, {flagged + unflagged}) as n) rows;
            """);

    /// <summary>Replaces the caller's roles with one holding <c>run:read</c> and <c>project:read</c> only.</summary>
    private async Task GrantRunReadOnlyAsync(RunCreationTests.World world)
    {
        var role = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
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
    }
}
