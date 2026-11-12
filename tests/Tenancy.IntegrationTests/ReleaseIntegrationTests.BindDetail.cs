// The read side of the binding: the release detail's `bound_runs` section (ordered by created_at, id over the release
// index), the reader the policy module consumes, and the shape guarantees that keep a "latest run" join impossible.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseIntegrationTests
{
    /// <summary>One row of the detail's <c>bound_runs</c> section.</summary>
    private sealed record BoundRun(Guid RunId, string ComponentKey, DateTimeOffset BoundAt);

    /// <summary>The detail answer's <c>bound_runs</c> section, in the order the body carries it.</summary>
    private static IReadOnlyList<BoundRun> BoundRunsOf(ApiAnswer answer) =>
    [
        .. answer.Body.GetProperty("bound_runs").EnumerateArray().Select(item => new BoundRun(
            item.GetProperty("run_id").GetGuid(),
            item.GetProperty("component_key").GetString()!,
            item.GetProperty("bound_at").GetDateTimeOffset())),
    ];

    /// <summary>Seeds one <c>release_run</c> row with an explicit <c>created_at</c>, as the migration role.</summary>
    private async Task SeedBindingAsync(
        RunCreationTests.World world,
        Guid releaseId,
        Guid runId,
        string componentKey,
        DateTimeOffset createdAt) =>
        await _fixture.ExecuteAsync(
            world.Database,
            $"insert into public.release_run (id, org_id, release_id, run_id, component_key, created_at) "
            + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{releaseId:D}', '{runId:D}', '{componentKey}', "
            + $"'{createdAt.UtcDateTime:yyyy-MM-ddTHH:mm:ss.ffffffZ}')").ConfigureAwait(false);

    [Fact]
    public async Task ReleaseDetail_BoundRuns_AreOrderedByCreatedAtThenId()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "detail-bound-runs", "active", ("web", 'a'), ("api", 'b'), ("worker", 'c'));
        var runId = await RunWithAsync(world, Components(("web", 'a'), ("api", 'b'), ("worker", 'c')));

        // Two rows share a timestamp, so the id is the only tie-breaker; the third is later and must come last.
        var shared = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await SeedBindingAsync(world, releaseId, runId, "worker", shared.AddMinutes(5));
        await SeedBindingAsync(world, releaseId, runId, "web", shared);
        await SeedBindingAsync(world, releaseId, runId, "api", shared);

        var answer = await GetReleaseAsync(world, releaseId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var expected = await _fixture.ColumnAsync<string>(
            world.Database,
            $"select component_key from public.release_run where org_id = '{world.Org:D}' "
            + $"and release_id = '{releaseId:D}' order by created_at, id");
        expected.Count.ShouldBe(3);
        expected[^1].ShouldBe("worker", "the later binding is last whatever the key order is");

        var bound = BoundRunsOf(answer);
        bound.Select(entry => entry.ComponentKey).ShouldBe(expected);
        bound.ShouldAllBe(entry => entry.RunId == runId);
        bound[^1].BoundAt.ShouldBeGreaterThan(bound[0].BoundAt);
        bound[0].BoundAt.ShouldBe(bound[1].BoundAt);
    }

    [Fact]
    public async Task ReleaseDetail_BoundRunsSection_IsOneStatementWhateverTheBindingCount()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "detail-statement-budget", "active", ("web", 'a'), ("api", 'b'), ("worker", 'c'));
        var runId = await RunWithAsync(world, Components(("web", 'a'), ("api", 'b'), ("worker", 'c')));
        var boundAt = DateTimeOffset.UtcNow;
        await SeedBindingAsync(world, releaseId, runId, "web", boundAt);
        await SeedBindingAsync(world, releaseId, runId, "api", boundAt);
        await SeedBindingAsync(world, releaseId, runId, "worker", boundAt);

        world.Api.Statements.Clear();
        (await GetReleaseAsync(world, releaseId)).Status.ShouldBe(HttpStatusCode.OK);
        var statements = world.Api.Statements.Snapshot()
            .Where(statement => statement.Contains("public.release", StringComparison.Ordinal))
            .ToList();

        statements.Count(statement => statement.Contains("public.release_run", StringComparison.Ordinal))
            .ShouldBe(1, "the bound runs are one statement, never one per row: " + string.Join(Environment.NewLine, statements));
        statements.Count.ShouldBeLessThanOrEqualTo(3, string.Join(Environment.NewLine, statements));
    }

    [Fact]
    public async Task ReleaseDetail_CarriesTheBoundRunOfEachExplicitAndRunCreateBind()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "detail-after-bind", "active", ("web", 'a'));
        var explicitRun = await RunWithAsync(world, Components(("web", 'a')));
        var created = await RunCreationTests.CreateRunAsync(
            world,
            RunBodyWith(world.ManifestId, Components(("web", 'a')), releaseId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        (await BindAsync(world, releaseId, explicitRun)).Status.ShouldBe(HttpStatusCode.Created);

        var bound = BoundRunsOf(await GetReleaseAsync(world, releaseId));
        bound.Select(entry => entry.RunId).ShouldBe([created.Id("run_id"), explicitRun], ignoreOrder: true);
        bound.Select(entry => entry.ComponentKey).ShouldBe(["web", "web"]);
    }

    /// <summary>The binding lives only in <c>release_run</c>: the run row itself has no release pointer to drift.</summary>
    [Fact]
    public async Task RunTable_HasNoReleaseIdColumn()
    {
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from information_schema.columns "
            + "where table_schema = 'public' and table_name = 'run' and column_name like '%release%'"))
            .ShouldBe(0, "a run is bound through release_run, never through a column on the run");
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from information_schema.columns "
            + "where table_schema = 'public' and table_name = 'release_run' "
            + "and column_name in ('org_id', 'release_id', 'run_id', 'component_key', 'created_at')"))
            .ShouldBe(5);
    }
}
