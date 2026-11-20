#pragma warning disable CA1861
using System.Globalization;
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The whole page walk of <c>GET /v1/runs/{r}/attempts</c> through the real host (05.3.3, F52 / 00 §9.1): a run
/// of 1 200 attempts walked at <c>limit=50</c> and again at <c>limit=200</c>, with attempts appended to the run
/// between the first and the second page, must serve every pre-existing row exactly once, in ascending
/// <c>(execution_key, attempt_no)</c> order, with the row's own ready-artifact count — so a cursor page never
/// repeats, skips or reorders a row the first page already established.
/// </summary>
public sealed partial class ListRunAttemptsTests
{
    /// <summary>The largest hex execution key, so rows appended with it sort after every seeded key.</summary>
    private const string LastExecutionKey = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    /// <summary>One row as the endpoint serves it: the fields the walk orders and counts by.</summary>
    private sealed record WalkRow(Guid Id, string ExecutionKey, int AttemptNo, int ArtifactCount);

    [Fact]
    public async Task ListRunAttempts_WalkWithRowsAppendedBetweenPages_CoversEveryOriginalRowOnce()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var (world, runId) = await StartWalkRunAsync(dedicated.Name);
        await using var lease = world;

        var originals = await AttemptIdsAsync(world.Database, world.Org, runId);
        originals.Count.ShouldBe(DenseExecutions * DenseAttemptsPerExecution);
        var ready = await ReadyCountsAsync(world.Database, world.Org, runId);

        var secondKey = LastExecutionKey[..^2] + "fe";
        var atFifty = await WalkAsync(world, runId, limit: 50, appendKey: LastExecutionKey);
        var atTwoHundred = await WalkAsync(world, runId, limit: 200, appendKey: secondKey);

        AssertWalk(atFifty, originals, ready, [LastExecutionKey]);
        AssertWalk(atTwoHundred, originals, ready, [LastExecutionKey, secondKey]);
    }

    /// <summary>A run of <see cref="DenseExecutions"/> planned executions with four attempts each and ready artifacts on a subset.</summary>
    private async Task<(World World, Guid RunId)> StartWalkRunAsync(string database)
    {
        var world = await DedicatedWorldAsync(database);

        try
        {
            var plan = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, database, world.Org, DenseExecutions);

            await SeedExecutionAttemptsAsync(database, world.Org, plan.RunId, DenseAttemptsPerExecution);
            await SeedReadyArtifactsAsync(database, world.Org, world.Project, plan.RunId);

            return (world, plan.RunId);
        }
        catch
        {
            // A world that never returns owns nothing: a failed arrange must not leave the host's connections
            // behind for the assembly's database cleanup.
            await world.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Walks the run from the first page to the page whose <c>next_cursor</c> is null, appending two attempts under
    /// <paramref name="appendKey"/> after the first page so the rest of the walk has to account for them.
    /// </summary>
    private async Task<IReadOnlyList<WalkRow>> WalkAsync(World world, Guid runId, int limit, string appendKey)
    {
        var walked = new List<WalkRow>();
        string? cursor = null;
        var appended = false;

        for (var page = 0; page < 60; page++)
        {
            var query = $"?limit={limit}" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var answer = await GetAsync(world, runId, query: query);
            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
            var rows = Items(answer).EnumerateArray().Select(Row).ToList();
            walked.AddRange(rows);

            if (!appended)
            {
                appended = true;
                await AppendAsync(world, runId, appendKey);
            }

            var next = answer.Body.GetProperty("next_cursor").GetString();
            if (next is not null)
            {
                // A page with a next cursor is full: the statement asks for limit + 1 and drops the lookahead row.
                rows.Count.ShouldBe(limit, $"page {page} has a next cursor, so it must serve the whole limit");
            }
            else
            {
                (rows.Count <= limit).ShouldBeTrue($"the last page serves at most the limit, {rows.Count} > {limit}");
            }

            cursor = next;
            if (cursor is null)
            {
                break;
            }
        }

        cursor.ShouldBeNull("the last page's next_cursor ends the walk");

        return walked;
    }

    private static WalkRow Row(JsonElement item) =>
        new(item.GetProperty("attempt_id").GetGuid(),
            item.GetProperty("execution_key").GetString()!,
            item.GetProperty("attempt_no").GetInt32(),
            item.GetProperty("artifact_count").GetInt32());

    /// <summary>Appends one planned execution and its two attempts to the run, as the migration role.</summary>
    private async Task AppendAsync(World world, Guid runId, string executionKey)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));

        (await scope.ExecuteAsync(
            """
            insert into public.planned_execution
              (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id)
            select gen_random_uuid(), @org_id, @run_id, @execution_key, p.case_revision_id, @variant_hash, 'staging', 'shard-1'
              from public.planned_execution p
             where p.org_id = @org_id and p.run_id = @run_id
             limit 1
            """,
            ("org_id", world.Org), ("run_id", runId), ("execution_key", executionKey),
            ("variant_hash", DenseVariantHash))).ShouldBe(1);

        (await scope.ExecuteAsync(
            """
            insert into public.case_attempt
              (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status, started_at)
            select gen_random_uuid(), @org_id, @run_id, @execution_key, tr.case_id, g, 'passed', 'passed', now()
              from public.planned_execution p
              join public.test_revision tr on tr.org_id = p.org_id and tr.id = p.case_revision_id
              cross join generate_series(1, 2) g
             where p.org_id = @org_id and p.run_id = @run_id and p.execution_key = @execution_key
            """,
            ("org_id", world.Org), ("run_id", runId), ("execution_key", executionKey))).ShouldBe(2);

        await scope.CommitAsync();
    }

    /// <summary>Every attempt id of the run, in the page's own order.</summary>
    private async Task<IReadOnlyList<Guid>> AttemptIdsAsync(string database, Guid org, Guid runId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));

        return await scope.ScalarAsync<Guid[]>(
            """
            select coalesce(array_agg(a.id order by a.execution_key, a.attempt_no), '{}'::uuid[])
              from public.case_attempt a
             where a.org_id = @org_id and a.run_id = @run_id
            """,
            ("org_id", org), ("run_id", runId));
    }

    /// <summary>The run's <c>ready</c> artifact count per slot, straight from the table the aggregate reads.</summary>
    private async Task<IReadOnlyDictionary<(string ExecutionKey, int AttemptNo), int>> ReadyCountsAsync(
        string database, Guid org, Guid runId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        var json = await scope.ScalarAsync<string>(
            """
            select coalesce(jsonb_object_agg(execution_key || '/' || attempt_no, total), '{}'::jsonb)::text
              from (
                  select ar.execution_key, ar.attempt_no, count(*)::int as total
                    from public.artifact ar
                   where ar.org_id = @org_id and ar.run_id = @run_id and ar.state = 'ready'
                   group by ar.execution_key, ar.attempt_no
              ) counted
            """,
            ("org_id", org), ("run_id", runId));

        using var document = JsonDocument.Parse(json);

        return document.RootElement.EnumerateObject().ToDictionary(
            property => Slot(property.Name),
            property => property.Value.GetInt32());
    }

    private static (string ExecutionKey, int AttemptNo) Slot(string key) =>
        (key[..key.LastIndexOf('/')],
            int.Parse(key[(key.LastIndexOf('/') + 1)..], CultureInfo.InvariantCulture));

    /// <summary>
    /// Every pre-existing row exactly once, no row twice, strictly ascending order, the row's own artifact count,
    /// and the two attempts appended under each key present (they sort after the seeded keys, so the walk must
    /// reach them from a cursor).
    /// </summary>
    private static void AssertWalk(
        IReadOnlyList<WalkRow> walked,
        IReadOnlyList<Guid> originals,
        IReadOnlyDictionary<(string ExecutionKey, int AttemptNo), int> ready,
        IReadOnlyList<string> appendedKeys)
    {
        walked.Select(row => row.Id).Distinct().Count().ShouldBe(walked.Count, "a page walk may serve no row twice");
        foreach (var id in originals)
        {
            walked.Count(row => row.Id == id).ShouldBe(1, $"{id} must be served exactly once");
        }

        for (var index = 1; index < walked.Count; index++)
        {
            var previous = walked[index - 1];
            var current = walked[index];
            var ordered = string.CompareOrdinal(previous.ExecutionKey, current.ExecutionKey) < 0
                || (string.Equals(previous.ExecutionKey, current.ExecutionKey, StringComparison.Ordinal)
                    && previous.AttemptNo < current.AttemptNo);
            ordered.ShouldBeTrue($"{current.ExecutionKey}/{current.AttemptNo} must follow {previous.ExecutionKey}/{previous.AttemptNo}");
        }

        foreach (var row in walked)
        {
            row.ArtifactCount.ShouldBe(ready.GetValueOrDefault((row.ExecutionKey, row.AttemptNo)), $"{row.ExecutionKey}/{row.AttemptNo}");
        }

        foreach (var key in appendedKeys)
        {
            walked.Count(row => string.Equals(row.ExecutionKey, key, StringComparison.Ordinal)).ShouldBe(2, $"{key} was appended mid-walk");
        }
    }
}
