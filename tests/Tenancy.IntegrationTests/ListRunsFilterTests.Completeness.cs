using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The <c>completeness</c> filter of the run list: an exact match on the run's <em>current</em> evidence revision,
/// so a run without one (null join row) is excluded by both values, the pointer moving to a later revision moves the
/// match, and another tenant's revisions never reach the page.
/// </summary>
public sealed partial class ListRunsFilterTests
{
    [Fact]
    public async Task ListRunsFilter_Completeness_SelectsTheCurrentRevisionAndExcludesRunsWithoutOne()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var noRevision = await CreateRunAsync(world);
        var complete = await CreateRunAsync(world);
        var incomplete = await CreateRunAsync(world);
        await AppendEvidenceAsync(world, complete, [("complete", 1)]);
        await AppendEvidenceAsync(world, incomplete, [("incomplete", 1)]);

        // Control: without the filter the run that has no revision is listed, so its absence below is the filter's.
        (await IdsAsync(world, "limit=50")).ShouldBe(new[] { noRevision, complete, incomplete }, ignoreOrder: true);
        (await IdsAsync(world, "completeness=complete")).ShouldBe(new[] { complete });
        (await IdsAsync(world, "completeness=incomplete")).ShouldBe(new[] { incomplete });

        // AND-combined with another filter, in both directions of the list.
        (await IdsAsync(world, "completeness=incomplete&state=queued")).ShouldBe(new[] { incomplete });
        (await IdsAsync(world, "completeness=incomplete&state=complete")).ShouldBeEmpty();
        (await IdsAsync(world, "completeness=incomplete&order=desc")).ShouldBe(new[] { incomplete });

        // The current revision decides: a later incomplete revision moves the run to the other value.
        await AppendEvidenceAsync(world, complete, [("incomplete", 2)]);
        (await IdsAsync(world, "completeness=complete")).ShouldBeEmpty();
        (await IdsAsync(world, "completeness=incomplete")).ShouldBe(new[] { complete, incomplete }, ignoreOrder: true);
    }

    [Fact]
    public async Task ListRunsFilter_Completeness_OtherTenantRevisionNeverMatches()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var mine = await CreateRunAsync(world);
        await using var second = await StartSecondTenantAsync(world.Database, "rival");
        var theirs = await CreateRunAsync(second);
        await AppendEvidenceAsync(world, mine, [("incomplete", 1)]);
        await AppendEvidenceAsync(second, theirs, [("incomplete", 1)]);

        // Both runs satisfy the predicate, so only the tenant scope separates each page.
        (await IdsAsync(world, "completeness=incomplete")).ShouldBe(new[] { mine });
        (await IdsAsync(second, "completeness=incomplete")).ShouldBe(new[] { theirs });
        (await IdsAsync(world, "completeness=complete")).ShouldBeEmpty();
        (await IdsAsync(second, "completeness=complete")).ShouldBeEmpty();
    }

    [Fact]
    public async Task ListRunsFilter_Completeness_PagesTiesInBothOrdersWithoutRepeatOrGap()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        // Groups share a created_at; within a group the order is the id's, in both directions of the list.
        (int Group, string? Completeness)[] seeds =
        [
            (1, "complete"), (1, "incomplete"), (2, null), (3, "complete"), (3, "incomplete"),
            (4, "complete"), (4, "incomplete"), (5, null),
        ];
        var runs = new List<(Guid Id, int Group, string? Completeness)>();
        foreach (var (group, completeness) in seeds)
        {
            var id = await CreateRunAsync(world);
            await SetCreatedAtAsync(world, id, $"2026-06-0{group}T00:00:00Z");
            if (completeness is not null)
            {
                await AppendEvidenceAsync(world, id, [(completeness, 1)]);
            }

            runs.Add((id, group, completeness));
        }

        // Lower-case uuid text orders exactly as PostgreSQL's bytewise uuid comparison does.
        static string Key(Guid id) => id.ToString("D");
        foreach (var order in new[] { "asc", "desc" })
        {
            var ordered = order == "asc"
                ? runs.OrderBy(run => run.Group).ThenBy(run => Key(run.Id), StringComparer.Ordinal)
                : runs.OrderByDescending(run => run.Group).ThenBy(run => Key(run.Id), StringComparer.Ordinal);
            foreach (var completeness in new[] { "complete", "incomplete" })
            {
                var expected = ordered.Where(run => run.Completeness == completeness).Select(run => run.Id).ToArray();
                expected.Length.ShouldBe(3);
                foreach (var limit in new[] { 1, 2 })
                {
                    var query = $"completeness={completeness}&order={order}";
                    (await WalkAsync(world, query, limit)).ShouldBe(expected, ignoreOrder: false, $"{query}&limit={limit}");
                }
            }
        }
    }

    /// <summary>Follows <c>next_cursor</c> to the end: every page but the last is full and the last carries none.</summary>
    private static async Task<Guid[]> WalkAsync(RunCreationTests.World world, string query, int limit)
    {
        var seen = new List<Guid>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var suffix = cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}";
            var answer = await ListAsync(world, $"{query}&limit={limit}{suffix}");
            answer.Status.ShouldBe(HttpStatusCode.OK, $"{query}&limit={limit}: {answer.ErrorMessage}");
            var ids = OrderedIds(answer);
            seen.AddRange(ids);
            var next = answer.Body.GetProperty("next_cursor");
            if (next.ValueKind == JsonValueKind.Null)
            {
                ids.Length.ShouldBeGreaterThan(0, "a terminal page of a non-empty filter is never empty");
                seen.Distinct().Count().ShouldBe(seen.Count, "no row is served twice");
                return [.. seen];
            }

            ids.Length.ShouldBe(limit, "a page with a continuation is full");
            cursors.Add(next.GetString()!).ShouldBeTrue("a cursor is never issued twice");
            cursor = next.GetString();
        }

        throw new InvalidOperationException($"{query}&limit={limit} did not end within 10 pages");
    }
}
