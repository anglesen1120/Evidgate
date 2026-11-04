using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC12 of 13.1.1 §5 (route 12's <c>covered</c> filter): the two sides of the filter are a complement, every row carries
/// the live-link count and the ids behind it, and an archived catalog case is excluded so a case linked only to one is
/// uncovered. This is authoring coverage — a link an author asserted — never 03.1.4's execution coverage.
/// </summary>
/// <remarks>
/// A dedicated database, because the index assertion in the partial file depends on database-wide planner statistics.
/// The rows are seeded directly as the owner (the link and case routes' own paths are proven by the other suites); the
/// reads go through the real host as a granted caller of the seeded org.
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class AuthoringCoverageTests
{
    private readonly PostgresTenancyFixture _fixture;

    /// <summary>Wires the fixture.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public AuthoringCoverageTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// AC12: <c>covered=false</c> lists exactly the cases no live link reaches and <c>covered=true</c> the complement;
    /// each item's <c>linked_live_count</c> and <c>linked_case_ids</c> agree, and a link to an archived catalog case
    /// counts for neither.
    /// </summary>
    [Fact]
    public async Task Uncovered_CountsExcludeArchivedCatalogCases()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var connectionString = _fixture.AppConnectionString(database);
        await using var api = TenancyApiFactory.GrantAll(connectionString);
        using var client = api.CreateClient();
        var tree = await AuthoringLinkTestData.SeedTreeAsync(_fixture, database);
        var cases = await SeedCasesAsync(_fixture, database, tree);
        var catalog = await SeedCatalogAsync(_fixture, database, tree);
        await SeedLinksAsync(_fixture, database, tree, cases, catalog);

        // Archive after the links exist: a link to a case that later turns archived must stop counting, not vanish.
        await ArchiveCatalogAsync(_fixture, database, catalog.Archived);

        var uncovered = await PageAsync(client, tree, covered: false);
        var covered = await PageAsync(client, tree, covered: true);

        AssertComplement(uncovered, covered, cases);
        await AssertCountsAsync(_fixture, database, uncovered, covered, cases, catalog);

        // A second, densely linked project so the planner has real statistics for the coverage probe.
        var planTree = await AuthoringLinkTestData.SeedTreeAsync(_fixture, database, tree.Org);
        await SeedDenseProjectAsync(_fixture, database, planTree);
        await AssertIndexServedAsync(_fixture, database, planTree);
    }

    /// <summary>The two sides partition every live case of the project, with no overlap.</summary>
    private static void AssertComplement(CoverageRow[] uncovered, CoverageRow[] covered, Guid[] cases)
    {
        var uncoveredIds = uncovered.Select(row => row.CaseId).ToHashSet();
        var coveredIds = covered.Select(row => row.CaseId).ToHashSet();

        uncoveredIds.ShouldBe([cases[1], cases[3]], ignoreOrder: true);
        coveredIds.ShouldBe([cases[0], cases[2], cases[4]], ignoreOrder: true);
        uncoveredIds.Overlaps(coveredIds).ShouldBeFalse("the two sides are a complement, never a partial partition");
        uncoveredIds.Union(coveredIds).Count().ShouldBe(cases.Length);
    }

    /// <summary>Every count and id set agrees with what was seeded, and the archived links are excluded.</summary>
    private static async Task AssertCountsAsync(
        PostgresTenancyFixture fixture,
        string database,
        CoverageRow[] uncovered,
        CoverageRow[] covered,
        Guid[] cases,
        (Guid[] Live, Guid[] Archived) catalog)
    {
        var byCase = uncovered.Concat(covered).ToDictionary(row => row.CaseId);

        byCase[cases[0]].LinkedLiveCount.ShouldBe(1);
        byCase[cases[0]].LinkedCaseIds.ShouldBe([catalog.Live[0]]);
        byCase[cases[2]].LinkedLiveCount.ShouldBe(2);
        byCase[cases[2]].LinkedCaseIds.ShouldBe(new[] { catalog.Live[1], catalog.Live[2] }.OrderBy(Canonical).ToArray());
        byCase[cases[4]].LinkedLiveCount.ShouldBe(1);
        byCase[cases[4]].LinkedCaseIds.ShouldBe([catalog.Live[0]]);

        // Linked only to an archived case: one row exists, yet the case is uncovered and its id list is empty.
        (await LinkRowsAsync(fixture, database, cases[1])).ShouldBe(1);
        byCase[cases[1]].LinkedLiveCount.ShouldBe(0);
        byCase[cases[1]].LinkedCaseIds.ShouldBeEmpty();

        // Never linked at all.
        (await LinkRowsAsync(fixture, database, cases[3])).ShouldBe(0);
        byCase[cases[3]].LinkedLiveCount.ShouldBe(0);
        byCase[cases[3]].LinkedCaseIds.ShouldBeEmpty();

        // The live ids of a case are ascending, and an archived id never rides along.
        byCase[cases[2]].LinkedCaseIds.ShouldBe(byCase[cases[2]].LinkedCaseIds.OrderBy(Canonical).ToArray());
        byCase[cases[4]].LinkedCaseIds.ShouldNotContain(catalog.Archived[1]);
    }

    /// <summary>
    /// The server's uuid ordering: PostgreSQL compares the 16 bytes, which the canonical hex form sorts identically.
    /// <see cref="Guid.CompareTo(Guid)"/> does not — it compares the first fields signed — so it cannot be used here.
    /// </summary>
    private static string Canonical(Guid id) => id.ToString("D");

    /// <summary>The page of route 12 for one side of the filter, with room for every seeded case.</summary>
    private static async Task<CoverageRow[]> PageAsync(HttpClient client, AuthoringLinkTree tree, bool covered)
    {
        var answer = await client.SendAsync(
            HttpMethod.Get,
            tree.CasesPath + (covered ? "?covered=true" : "?covered=false") + "&limit=50",
            tree.Caller);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        return answer.Body.GetProperty("items").EnumerateArray()
            .Select(item => new CoverageRow(
                item.GetProperty("case_id").GetGuid(),
                item.GetProperty("linked_live_count").GetInt32(),
                item.GetProperty("linked_case_ids").EnumerateArray().Select(id => id.GetGuid()).ToArray()))
            .ToArray();
    }

    private static Task<int> LinkRowsAsync(PostgresTenancyFixture fixture, string database, Guid manualCase) =>
        fixture.ScalarAsync<int>(
            database,
            $"select count(*)::int from public.manual_case_link where manual_test_case_id = '{manualCase}'");

    /// <summary>Five manual cases: linked to one live case, to one archived, to two live, to none, and to both.</summary>
    private static async Task<Guid[]> SeedCasesAsync(PostgresTenancyFixture fixture, string database, AuthoringLinkTree tree)
    {
        var cases = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToArray();
        var rows = cases.Select((id, index) =>
            $"('{id}', '{tree.Org}', '{tree.Project}', '{tree.Item}', 'COV-00{index + 1}', 'Case {index + 1}',"
            + " '{\"steps\":[]}'::jsonb, 'high', 'draft', 'manual')");

        await fixture.ExecuteAsync(
            database,
            "insert into public.manual_test_case "
            + "(id, org_id, project_id, checklist_item_id, code, title, steps, priority, state, source) values "
            + string.Join(",\n", rows));

        return cases;
    }

    /// <summary>Three live catalog cases and two that this test archives after their links exist.</summary>
    private static async Task<(Guid[] Live, Guid[] Archived)> SeedCatalogAsync(
        PostgresTenancyFixture fixture, string database, AuthoringLinkTree tree)
    {
        var live = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).ToArray();
        var archived = Enumerable.Range(0, 2).Select(_ => Guid.CreateVersion7()).ToArray();
        var rows = live.Concat(archived).Select((id, index) =>
            $"('{id}', '{tree.Org}', '{tree.Project}', '{Guid.CreateVersion7()}', 'catalog-{index}', 'playwright', 'abcdef0')");

        await fixture.ExecuteAsync(
            database,
            "insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit) values "
            + string.Join(",\n", rows));

        return (live, archived);
    }

    /// <summary>Moves the cases to <c>archived</c> through the catalog's own status column.</summary>
    private static Task ArchiveCatalogAsync(PostgresTenancyFixture fixture, string database, Guid[] cases) =>
        fixture.ExecuteAsync(
            database,
            "update public.test_case set status = 'archived' where id in ("
            + string.Join(", ", cases.Select(id => $"'{id}'"))
            + ")");

    /// <summary>The link rows behind the five cases; seeded as the owner, since write paths have their own suite.</summary>
    private static async Task SeedLinksAsync(
        PostgresTenancyFixture fixture,
        string database,
        AuthoringLinkTree tree,
        Guid[] cases,
        (Guid[] Live, Guid[] Archived) catalog)
    {
        var pairs = new[]
        {
            (cases[0], catalog.Live[0]),
            (cases[1], catalog.Archived[0]),
            (cases[2], catalog.Live[1]),
            (cases[2], catalog.Live[2]),
            (cases[4], catalog.Live[0]),
            (cases[4], catalog.Archived[1]),
        };

        var rows = pairs.Select(pair =>
            $"('{Guid.CreateVersion7()}', '{tree.Org}', '{tree.Project}', '{pair.Item1}', '{pair.Item2}')");

        await fixture.ExecuteAsync(
            database,
            "insert into public.manual_case_link "
            + "(id, org_id, project_id, manual_test_case_id, test_case_id) values "
            + string.Join(",\n", rows));
    }

    /// <summary>A project of 300 manual cases with two live links each, so the plan has a real choice to make.</summary>
    private static async Task SeedDenseProjectAsync(PostgresTenancyFixture fixture, string database, AuthoringLinkTree tree)
    {
        await fixture.ExecuteAsync(
            database,
            $$"""
            insert into public.manual_test_case
                (id, org_id, project_id, checklist_item_id, code, title, steps, priority, state, source)
            select gen_random_uuid(), '{{tree.Org}}', '{{tree.Project}}', '{{tree.Item}}',
                   'BULK-' || lpad(g::text, 3, '0'), 'Bulk ' || g, '{"steps":[]}'::jsonb, 'high', 'draft', 'manual'
              from generate_series(1, 300) g;
            insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
            select gen_random_uuid(), '{{tree.Org}}', '{{tree.Project}}', gen_random_uuid(), 'bulk-' || g, 'playwright', 'abcdef0'
              from generate_series(1, 2) g;
            insert into public.manual_case_link (id, org_id, project_id, manual_test_case_id, test_case_id)
            select gen_random_uuid(), m.org_id, m.project_id, m.id, c.id
              from public.manual_test_case m
              cross join (select id from public.test_case where project_id = '{{tree.Project}}') c
             where m.project_id = '{{tree.Project}}';
            """);
        await fixture.ExecuteAsync(
            database,
            "analyze public.manual_test_case; analyze public.checklist_item; "
            + "analyze public.test_case; analyze public.manual_case_link;");
    }

    /// <summary>One page of the covered/uncovered read, with the ids and counts the response carries.</summary>
    private sealed record CoverageRow(Guid CaseId, int LinkedLiveCount, Guid[] LinkedCaseIds);
}
