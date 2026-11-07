using System.Globalization;
using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Usage.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The summary's org-wide project counts over a real PostgreSQL: what another tenant's counters and an earlier period's
/// stamps must not contribute, and that a project which refused after it warned is counted as blocked alone.
/// </summary>
/// <remarks>
/// The counts come from the counters only, so a project whose month holds a roll-up and no counter cannot move them;
/// the page and the counts share one statement, and these cases pin the parts of its filter the walk cannot show.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsageSummaryProjectsStatusTests
{
    private readonly PostgresTenancyFixture _fixture;

    public UsageSummaryProjectsStatusTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Summary_ProjectsStatus_IgnoresAnotherOrgsBlockedProject()
    {
        await using var world = await StartAsync();
        var (period, instant) = await world.DatabaseClockAsync();
        var warned = await world.AddProjectAtAsync(instant.AddMinutes(-3));
        var blocked = await world.AddProjectAtAsync(instant.AddMinutes(-2));
        var (otherOrg, foreignProject) = await world.AddOtherOrgAsync();

        await world.SeedCounterAsync(
            new CounterSeed(
                Guid.CreateVersion7(), warned, period, UsageLedgerMetricNames.Results, 9m, 10m,
                WarnedAt: instant.AddHours(-1)),
            new CounterSeed(
                Guid.CreateVersion7(), blocked, period, UsageLedgerMetricNames.Results, 10m, 10m,
                BlockedAt: instant.AddMinutes(-30)),
            new CounterSeed(
                Guid.CreateVersion7(), foreignProject, period, UsageLedgerMetricNames.Results, 10m, 10m,
                BlockedAt: instant.AddMinutes(-30), Org: otherOrg));

        var answer = await world.GetAsMemberAsync(world.SummaryPageUrl());

        answer.Status.ShouldBe(HttpStatusCode.OK);
        var status = Status(answer, UsageLedgerMetricNames.Results);
        status.BlockedCount.ShouldBe(1L);
        status.WarnedCount.ShouldBe(1L);
    }

    [Fact]
    public async Task Summary_ProjectsStatus_IgnoresAnEarlierPeriodsStamps()
    {
        await using var world = await StartAsync();
        var (period, instant) = await world.DatabaseClockAsync();
        var current = await world.AddProjectAtAsync(instant.AddMinutes(-2));
        var stale = await world.AddProjectAtAsync(instant.AddMinutes(-3));
        var previous = Previous(period);

        await world.SeedCounterAsync(
            new CounterSeed(Guid.CreateVersion7(), current, period, UsageLedgerMetricNames.Results, 1m, 10m),
            new CounterSeed(
                Guid.CreateVersion7(), stale, previous, UsageLedgerMetricNames.Results, 10m, 10m,
                WarnedAt: instant.AddMonths(-1), BlockedAt: instant.AddMonths(-1)));

        var answer = await world.GetAsMemberAsync(world.SummaryPageUrl());

        answer.Status.ShouldBe(HttpStatusCode.OK);

        // The stale project is not a scope of this period either, so its stamp cannot reach the counts.
        ProjectIds(answer).ShouldBe([current]);
        var status = Status(answer, UsageLedgerMetricNames.Results);
        status.BlockedCount.ShouldBe(0L);
        status.WarnedCount.ShouldBe(0L);
    }

    [Fact]
    public async Task Summary_ProjectsStatus_CountsAProjectBlockedOnlyWhenItAlsoWarned()
    {
        await using var world = await StartAsync();
        var (period, instant) = await world.DatabaseClockAsync();
        var project = await world.AddProjectAtAsync(instant.AddMinutes(-2));

        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), project, period, UsageLedgerMetricNames.Results, 10m, 10m,
            WarnedAt: instant.AddHours(-2), BlockedAt: instant.AddMinutes(-30)));

        var answer = await world.GetAsMemberAsync(world.SummaryPageUrl());

        answer.Status.ShouldBe(HttpStatusCode.OK);
        var status = Status(answer, UsageLedgerMetricNames.Results);
        status.BlockedCount.ShouldBe(1L);
        status.WarnedCount.ShouldBe(0L);
    }

    /// <summary>One world over the shared database; its clocks are the database's own.</summary>
    private Task<UsageReadsWorld> StartAsync() =>
        UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

    /// <summary>The month before <paramref name="period"/>, as the counters spell it.</summary>
    private static string Previous(string period) =>
        DateTime.ParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture)
            .AddMonths(-1)
            .ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static JsonElement Metric(ApiAnswer answer, string wire) =>
        answer.Body.GetProperty("metrics").EnumerateArray()
            .Single(metric => metric.GetProperty("metric").GetString() == wire);

    /// <summary>The project ids one page reports, in page order.</summary>
    private static IReadOnlyList<Guid> ProjectIds(ApiAnswer answer) =>
        [.. Metric(answer, UsageLedgerMetricNames.Results).GetProperty("projects").EnumerateArray()
            .Select(project => project.GetProperty("project_id").GetGuid())];

    /// <summary>One metric's org-wide project counts as the wire carries them.</summary>
    private static UsageSummaryProjectsStatusDto Status(ApiAnswer answer, string wire)
    {
        var status = Metric(answer, wire).GetProperty("projects_status");
        return new UsageSummaryProjectsStatusDto(
            status.GetProperty("blocked_count").GetInt64(),
            status.GetProperty("warned_count").GetInt64());
    }
}
