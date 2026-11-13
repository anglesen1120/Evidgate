// The run detail's release bindings at the read's own ceiling, beside a run whose history is unknown, and through the
// statement the host actually executed: a real PostgreSQL and the real host, so the served list is what one statement
// returned rather than a shape this file built for itself.
using System.Net;
using System.Text.Json;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class GetRunDetailFieldsTests
{
    /// <summary>
    /// The binding ceiling the run detail read applies. The contract declares no bound, so this pins the
    /// implementation constant: changing it must be a deliberate edit of this file too.
    /// </summary>
    private const int ServedBindingBound = 500;

    /// <summary>Component keys one seeded release contributes, so a count beyond one release's keys is still storable.</summary>
    private const int KeysPerRelease = 100;

    /// <summary>The bulk bindings' fixed base, one minute apart each, so their order never depends on the wall clock.</summary>
    private static readonly DateTimeOffset BulkBoundAt = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A binding time after every bulk row, so the row past the ceiling is also the newest binding.</summary>
    private static readonly DateTimeOffset NewestBoundAt = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One more binding than the read serves: the table keeps all 501 rows, the body carries exactly
    /// <see cref="ServedBindingBound"/> of them in the binding order (<c>created_at</c>, then id), and the single row the
    /// ceiling drops is the newest one — never the oldest, which is what a ceiling applied after sorting the wrong way
    /// would keep.
    /// </summary>
    [Fact]
    public async Task GetRun_MoreBindingsThanTheCeiling_ServesTheOldestRowsAndDropsTheNewest()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        ReleaseSql.MaxBoundReleases.ShouldBe(ServedBindingBound, "the read's ceiling is the pinned implementation value");
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");

        await SeedBulkBindingsAsync(world, runId, ServedBindingBound);
        var newest = await SeedReleaseAsync(world, "detail-newest", world.Project);
        await SeedBindingAsync(world, newest, runId, "web", NewestBoundAt, Guid.CreateVersion7());

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var stored = await StoredReleasesAsync(world, runId);
        stored.Count.ShouldBe(ServedBindingBound + 1, "the table keeps every binding; the ceiling is the read's");
        var served = Releases(answer);
        served.Count.ShouldBe(ServedBindingBound);

        // The rows the read kept are the oldest ones in the binding order, compared against the stored rows themselves.
        served.Select(Row).ShouldBe(stored.Take(ServedBindingBound).ToList());
        served.Select(release => release.ReleaseName).ShouldNotContain(
            "detail-newest",
            "the row past the ceiling is the newest binding");
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.release_run where run_id = '{runId:D}' and release_id = '{newest:D}'")).ShouldBe(
            1, "the dropped binding exists, so its absence from the body is the ceiling under test");
    }

    /// <summary>
    /// The whole detail is one statement. The host records every command its data source executes, so exactly one recorded
    /// statement may touch the run row, its state history or its bindings, and that one must carry all three; a second
    /// statement would be a second snapshot, where a cancel committing between two reads serves the old state beside the
    /// bindings and history written after it.
    /// </summary>
    [Fact]
    public async Task GetRun_BoundRun_ReadsTheBindingsInTheStatementThatReadsTheRun()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await CreateReleaseAsync(world, "detail-statement", "web");
        var runId = await CreateBoundRunAsync(world, releaseId);

        world.Api.Statements.Clear();
        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Releases(answer).ShouldHaveSingleItem().ReleaseId.ShouldBe(releaseId);

        var captured = world.Api.Statements.Snapshot();
        captured.ShouldNotBeEmpty("an empty log would make the count below pass vacuously");
        var detailReads = captured
            .Where(statement =>
                statement.Contains("public.run r", StringComparison.Ordinal)
                || statement.Contains("public.run_state_event", StringComparison.Ordinal)
                || statement.Contains("public.release_run", StringComparison.Ordinal))
            .ToList();
        detailReads.ShouldHaveSingleItem();
        detailReads[0].ShouldContain("public.run r", Case.Sensitive);
        detailReads[0].ShouldContain("public.run_state_event", Case.Sensitive);
        detailReads[0].ShouldContain("public.release_run", Case.Sensitive);
    }

    /// <summary>
    /// A run with no creation marker — old enough to predate the history table — reports its unknown history as null and
    /// still serves the releases it is bound to: the history and the bindings are two subqueries of the one statement,
    /// and neither answer may suppress the other.
    /// </summary>
    [Fact]
    public async Task GetRun_RunWithoutTheCreationMarker_StillServesTheBoundReleases()
    {
        // Dedicated: disabling the append-only guard is DDL and takes a table-wide lock no sibling class may share.
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, database: owned.Name, hosts: _hosts);
        var releaseId = await CreateReleaseAsync(world, "detail-legacy", "web");
        var runId = await CreateBoundRunAsync(world, releaseId);
        await RemoveStateHistoryAsync(world, runId);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("state_history").ValueKind.ShouldBe(JsonValueKind.Null);
        var releases = Releases(answer);
        releases.ShouldHaveSingleItem();
        releases[0].ReleaseId.ShouldBe(releaseId);
        releases[0].ReleaseName.ShouldBe("detail-legacy");
        releases.Select(Row).ShouldBe(await StoredReleasesAsync(world, runId));
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run_state_event where run_id = '{runId:D}'")).ShouldBe(
            0, "the run holds no history row at all, so the null above is the absent marker");
    }

    /// <summary>
    /// Another tenant's read of a bound run is the one <c>404 not_found</c> and its body names neither the bindings
    /// member nor the release the run holds: a refusal that echoed the release would make the binding a second existence
    /// oracle, beside the run id itself.
    /// </summary>
    [Fact]
    public async Task GetRun_AnotherTenantsRun_RefusesWithoutNamingItsReleases()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await CreateReleaseAsync(world, "detail-private", "web");
        var runId = await CreateBoundRunAsync(world, releaseId);

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival-snapshot"));
        var refused = await GetAsync(world, runId, TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg));

        refused.Status.ShouldBe(HttpStatusCode.NotFound);
        refused.ErrorCode.ShouldBe("not_found");
        refused.Body.GetRawText().ShouldNotContain("releases");
        refused.Body.GetRawText().ShouldNotContain(releaseId.ToString());

        // The control: the same run read inside its own tenant carries the binding, so the absence above is the tenant
        // scope and not a run without bindings.
        var own = await GetAsync(world, runId);
        own.Status.ShouldBe(HttpStatusCode.OK, own.ErrorMessage);
        Releases(own).ShouldHaveSingleItem().ReleaseId.ShouldBe(releaseId);
    }

    /// <summary>
    /// Seeds <paramref name="count"/> binding rows of one run at one-minute steps from <see cref="BulkBoundAt"/>, spread
    /// over as many drafted releases as the binding key needs (<see cref="KeysPerRelease"/> component keys each).
    /// </summary>
    private async Task SeedBulkBindingsAsync(RunCreationTests.World world, Guid runId, int count)
    {
        var releaseIds = new List<string>();
        for (var index = 0; index * KeysPerRelease < count; index++)
        {
            var name = $"detail-bulk-{index}";
            var releaseId = await SeedReleaseAsync(world, name, world.Project);
            releaseIds.Add($"'{releaseId:D}'::uuid");
        }

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.release_run (id, org_id, release_id, run_id, component_key, created_at)
            select gen_random_uuid(), '{world.Org:D}', ids[1 + ((n - 1) / {KeysPerRelease})], '{runId:D}',
                   'k' || lpad(((n - 1) % {KeysPerRelease})::text, 3, '0'),
                   '{BulkBoundAt.UtcDateTime:O}'::timestamptz + (n || ' minutes')::interval
              from generate_series(1, {count}) as n
             cross join (select array[{string.Join(", ", releaseIds)}] as ids) picked;
            """);
    }

    /// <summary>
    /// Deletes a run's history as the owner, with the table's append-only guard disabled for the one transaction and
    /// restored before the commit, so a failed arrange cannot leave the table writable.
    /// </summary>
    private async Task RemoveStateHistoryAsync(RunCreationTests.World world, Guid runId)
    {
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        await owner.ExecuteAsync("alter table public.run_state_event disable trigger run_state_event_immutable");
        (await owner.ExecuteAsync($"delete from public.run_state_event where run_id = '{runId:D}'")).ShouldBe(
            1, "a run created through the route holds its creation marker alone");
        await owner.ExecuteAsync("alter table public.run_state_event enable trigger run_state_event_immutable");
        await owner.CommitAsync();
    }
}
