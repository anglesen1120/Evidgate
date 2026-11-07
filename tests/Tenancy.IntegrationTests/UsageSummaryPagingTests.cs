using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Usage.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The org-wide summary's project paging over the real host and a real PostgreSQL: the walk across pages, the totals
/// that stay org-wide on every page, the refusals of <c>limit</c> and <c>cursor</c>, cursors minted for another org or
/// naming a project the month no longer holds, and the boundaries of an empty, exact and default-sized page.
/// </summary>
/// <remarks>
/// Every project's creation instant is pinned, because <c>(created_at, id)</c> is the keyset: a page boundary that
/// depended on how fast two requests answered could not assert which page a project belongs to. The project filter is
/// proven unchanged here too, since it shares the statement with the page.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsageSummaryPagingTests
{
    private readonly PostgresTenancyFixture _fixture;

    public UsageSummaryPagingTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Summary_PagesOverThreeCalls_ReportEveryProjectOnceInCreationOrder()
    {
        await using var world = await StartAsync();
        var (period, instant) = await world.DatabaseClockAsync();
        var created = await SeedProjectsAsync(world, period, instant, count: 5);

        var walked = new List<Guid>();
        string? cursor = null;
        var calls = 0;
        do
        {
            var answer = await world.GetAsMemberAsync(world.SummaryPageUrl(limit: "2", cursor: cursor));
            answer.Status.ShouldBe(HttpStatusCode.OK);

            // The first page of two projects announces the second one's keyset, which is what the walk hands back.
            if (calls == 0)
            {
                var secondAt = await world.ProjectCreatedAtAsync(created[1]);
                NextCursor(answer).ShouldBe(KeysetCursor.Encode(secondAt, created[1]));
            }

            var ids = ProjectIds(answer);
            ids.Count.ShouldBeLessThanOrEqualTo(2);
            walked.AddRange(ids);
            cursor = NextCursor(answer);
            calls++;
        }
        while (cursor is not null && calls < 10);

        calls.ShouldBe(3);
        walked.ShouldBe(created);
        walked.ShouldBeUnique();

        var unpaged = await world.GetAsMemberAsync(world.SummaryPageUrl(limit: "200"));
        ProjectIds(unpaged).ShouldBe(created);
        NextCursor(unpaged).ShouldBeNull();
    }

    [Fact]
    public async Task Summary_EveryPage_CarriesTheOrgTotalsOverAllProjects()
    {
        await using var world = await StartAsync();
        var (period, instant) = await world.DatabaseClockAsync();
        var created = await SeedProjectsAsync(world, period, instant, count: 3);

        // Roll-up rows only: the organization-wide number is then every project's total, whatever page is asked for.
        await world.SeedRollupAsync(
            new RollupSeed(Guid.CreateVersion7(), created[0], period, UsageLedgerMetricNames.Results, 1m, 1, instant),
            new RollupSeed(Guid.CreateVersion7(), created[1], period, UsageLedgerMetricNames.Results, 2m, 1, instant),
            new RollupSeed(Guid.CreateVersion7(), created[2], period, UsageLedgerMetricNames.Results, 3m, 1, instant));

        string? cursor = null;
        var pages = 0;
        do
        {
            var answer = await world.GetAsMemberAsync(world.SummaryPageUrl(limit: "1", cursor: cursor));
            answer.Status.ShouldBe(HttpStatusCode.OK);
            var results = Metric(answer, UsageLedgerMetricNames.Results);
            results.GetProperty("used").GetDecimal().ShouldBe(6m);
            results.GetProperty("limit").ValueKind.ShouldBe(JsonValueKind.Null);

            // Each page names one project whose own total is a part of that same six, never the whole.
            var only = ProjectIds(answer).ShouldHaveSingleItem();
            results.GetProperty("projects").EnumerateArray().Single()
                .GetProperty("used").GetDecimal().ShouldBe(created.ToList().IndexOf(only) + 1m);

            cursor = NextCursor(answer);
            pages++;
        }
        while (cursor is not null && pages < 10);

        pages.ShouldBe(3);

        // The page-level numbers were never the whole: the unpaged read shows every project's own total, and those
        // totals are exactly the organization-wide figure every page reported.
        var unpaged = await world.GetAsMemberAsync(world.SummaryPageUrl(limit: "200"));
        Metric(unpaged, UsageLedgerMetricNames.Results).GetProperty("projects").EnumerateArray()
            .Sum(project => project.GetProperty("used").GetDecimal()).ShouldBe(6m);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("201")]
    [InlineData("abc")]
    public async Task Summary_LimitOutsideOneToTwoHundred_Is422(string limit)
    {
        await using var world = await StartAsync();

        var answer = await world.GetAsMemberAsync(world.SummaryPageUrl(limit: limit));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe(ApiErrorCode.RequestInvalid);
        Details(answer).ShouldContain(detail => detail.Path == "limit" && detail.Reason == "out_of_range");
        answer.Body.TryGetProperty("metrics", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Summary_MalformedCursor_Is422AndReportsNoRows()
    {
        await using var world = await StartAsync();
        var guid = Guid.CreateVersion7().ToString("D");
        string[] cursors =
        [
            "not-a-cursor",
            "-" + guid,
            guid + "-",
            "zzzzzzzzzzzzzzzz-" + guid,
            "ffffffffffffffff-" + guid,
        ];

        foreach (var cursor in cursors)
        {
            var answer = await world.GetAsMemberAsync(world.SummaryPageUrl(cursor: cursor));

            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            answer.ErrorCode.ShouldBe(ApiErrorCode.RequestInvalid);
            Details(answer).ShouldContain(detail => detail.Path == "cursor" && detail.Reason == "malformed");
            answer.Body.TryGetProperty("metrics", out _).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Summary_CursorMintedForAnotherOrg_ReportsOnlyThisOrgsProjects()
    {
        await using var world = await StartAsync();
        var (period, instant) = await world.DatabaseClockAsync();
        var created = await SeedProjectsAsync(world, period, instant, count: 3);
        var (_, foreignProject) = await world.AddOtherOrgAsync();
        var foreignAt = instant.AddHours(-10);
        await world.PinProjectCreatedAtAsync(foreignProject, foreignAt);

        // The foreign key sorts before every project of this org, so an unfiltered page would start with the foreign
        // project if the tenant boundary were not the scope's own.
        var cursor = KeysetCursor.Encode(foreignAt, foreignProject);
        var answer = await world.GetAsMemberAsync(world.SummaryPageUrl(limit: "200", cursor: cursor));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        var ids = ProjectIds(answer);
        ids.ShouldNotContain(foreignProject);
        ids.ShouldBe(created);
    }

    [Fact]
    public async Task Summary_CursorAtAProjectTheMonthNoLongerHolds_StillPages()
    {
        await using var world = await StartAsync();
        var (period, instant) = await world.DatabaseClockAsync();
        var created = await SeedProjectsAsync(world, period, instant, count: 3);

        var first = await world.GetAsMemberAsync(world.SummaryPageUrl(limit: "1"));
        ProjectIds(first).ShouldBe([created[0]]);
        var cursor = NextCursor(first);
        cursor.ShouldNotBeNull();

        // The project between the cursor and the rest leaves the month, so the continuation names a keyset no row of
        // the page holds: the page after it is still the projects that follow, not an error.
        await world.RemoveProjectFromSummaryAsync(created[1], period);
        var second = await world.GetAsMemberAsync(world.SummaryPageUrl(limit: "2", cursor: cursor));

        second.Status.ShouldBe(HttpStatusCode.OK);
        ProjectIds(second).ShouldBe([created[2]]);
        NextCursor(second).ShouldBeNull();
    }

    [Fact]
    public async Task Summary_CursorPastTheLastProject_IsAnEmptyPage()
    {
        await using var world = await StartAsync();
        var (period, instant) = await world.DatabaseClockAsync();
        await SeedProjectsAsync(world, period, instant, count: 2);
        var beyond = KeysetCursor.Encode(instant.AddDays(1), Guid.CreateVersion7());

        var answer = await world.GetAsMemberAsync(world.SummaryPageUrl(cursor: beyond));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        ProjectIds(answer).ShouldBeEmpty();
        NextCursor(answer).ShouldBeNull();
    }

    [Fact]
    public async Task Summary_ProjectCreatedBetweenPages_AppearsAtTheEnd()
    {
        await using var world = await StartAsync();
        var (period, instant) = await world.DatabaseClockAsync();
        var created = await SeedProjectsAsync(world, period, instant, count: 3);

        var first = await world.GetAsMemberAsync(world.SummaryPageUrl(limit: "2"));
        ProjectIds(first).ShouldBe([created[0], created[1]]);
        var cursor = NextCursor(first);
        cursor.ShouldNotBeNull();

        // A project created after the first page was read sorts after every key that page reported, so the walk
        // reaches it once, at the end, and never repeats or skips the projects it already answered.
        var late = await world.AddProjectAtAsync(instant);
        await world.SeedCounterAsync(
            new CounterSeed(Guid.CreateVersion7(), late, period, UsageLedgerMetricNames.Results, 1m, 10m));

        var second = await world.GetAsMemberAsync(world.SummaryPageUrl(limit: "2", cursor: cursor));
        ProjectIds(second).ShouldBe([created[2], late]);
        NextCursor(second).ShouldBeNull();
    }

    [Fact]
    public async Task Summary_ExactlyTheLimitProjects_HasNoContinuation()
    {
        await using var world = await StartAsync();
        var (period, instant) = await world.DatabaseClockAsync();
        var created = await SeedProjectsAsync(world, period, instant, count: 2);

        var answer = await world.GetAsMemberAsync(world.SummaryPageUrl(limit: "2"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        ProjectIds(answer).ShouldBe(created);
        NextCursor(answer).ShouldBeNull();
    }

    [Fact]
    public async Task Summary_NoProjects_HasNoProjectsAndNoContinuation()
    {
        await using var world = await StartAsync();
        var (period, _) = await world.DatabaseClockAsync();

        var answer = await world.GetAsMemberAsync(world.SummaryPageUrl());

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("period").GetString().ShouldBe(period);
        ProjectIds(answer).ShouldBeEmpty();
        NextCursor(answer).ShouldBeNull();
    }

    [Fact]
    public async Task Summary_ProjectFilter_IgnoresPagingAndHasNoContinuation()
    {
        await using var world = await StartAsync();
        var (period, instant) = await world.DatabaseClockAsync();
        var created = await SeedProjectsAsync(world, period, instant, count: 3);
        var otherAt = await world.ProjectCreatedAtAsync(created[2]);
        var otherCursor = KeysetCursor.Encode(otherAt, created[2]);

        var answer = await world.GetAsMemberAsync(
            world.SummaryPageUrl(project: created[0], limit: "1", cursor: otherCursor));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("project").GetGuid().ShouldBe(created[0]);
        ProjectIds(answer).ShouldBe([created[0]]);
        NextCursor(answer).ShouldBeNull();

        // The paging parameters are still validated on the filtered answer, so a malformed cursor cannot be smuggled
        // past the one route that ignores it.
        var refused = await world.GetAsMemberAsync(
            world.SummaryPageUrl(project: created[0], cursor: "not-a-cursor"));
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe(ApiErrorCode.RequestInvalid);
    }

    [Fact]
    public async Task Summary_LimitTwoHundred_IsAcceptedAndEndsTheWalk()
    {
        await using var world = await StartAsync();
        var (period, instant) = await world.DatabaseClockAsync();
        var created = await SeedProjectsAsync(world, period, instant, count: 1);

        var answer = await world.GetAsMemberAsync(world.SummaryPageUrl(limit: "200"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        ProjectIds(answer).ShouldBe(created);
        NextCursor(answer).ShouldBeNull();
    }

    [Fact]
    public async Task Summary_DefaultLimitWithoutParameters_StopsAtFiftyProjects()
    {
        await using var world = await StartAsync();
        var (period, instant) = await world.DatabaseClockAsync();
        var created = await SeedProjectsAsync(world, period, instant, count: 51);

        var first = await world.GetAsMemberAsync(world.SummaryPageUrl());
        var firstPage = ProjectIds(first);
        firstPage.Count.ShouldBe(50);
        firstPage.ShouldBe(created.Take(50));
        var cursor = NextCursor(first);
        cursor.ShouldNotBeNull();

        var second = await world.GetAsMemberAsync(world.SummaryPageUrl(cursor: cursor));
        ProjectIds(second).ShouldBe(created.Skip(50));
        NextCursor(second).ShouldBeNull();
    }

    /// <summary>
    /// Creates <paramref name="count"/> projects of this world's org, pinned one minute apart from
    /// <paramref name="instant"/>, each with a <c>results</c> counter, and returns them in creation order.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> SeedProjectsAsync(
        UsageReadsWorld world,
        string period,
        DateTimeOffset instant,
        int count)
    {
        var created = new List<Guid>(count);
        var counters = new List<CounterSeed>(count);
        for (var index = 0; index < count; index++)
        {
            var project = await world.AddProjectAtAsync(instant.AddMinutes(index - count));
            created.Add(project);
            counters.Add(new CounterSeed(
                Guid.CreateVersion7(), project, period, UsageLedgerMetricNames.Results, index + 1m, 1000m));
        }

        await world.SeedCounterAsync([.. counters]);
        return created;
    }

    /// <summary>One world over the shared database; its clocks are the database's own.</summary>
    private Task<UsageReadsWorld> StartAsync() =>
        UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

    private static JsonElement Metric(ApiAnswer answer, string wire) =>
        answer.Body.GetProperty("metrics").EnumerateArray()
            .Single(metric => metric.GetProperty("metric").GetString() == wire);

    /// <summary>The project ids one page reports, in page order.</summary>
    private static IReadOnlyList<Guid> ProjectIds(ApiAnswer answer) =>
        [.. Metric(answer, UsageLedgerMetricNames.Results).GetProperty("projects").EnumerateArray()
            .Select(project => project.GetProperty("project_id").GetGuid())];

    private static string? NextCursor(ApiAnswer answer) =>
        answer.Body.GetProperty("next_cursor").GetString();

    private static IReadOnlyList<(string Path, string Reason)> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => (
                detail.GetProperty("path").GetString() ?? string.Empty,
                detail.GetProperty("reason").GetString() ?? string.Empty))];
}
