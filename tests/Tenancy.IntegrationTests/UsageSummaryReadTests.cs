using System.Globalization;
using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The tenant usage summary over the real host and a real PostgreSQL: the two counter scopes and their labels, the
/// roll-up fallback of a month with no counter (which never invents a limit), the project filter, the 404 a project
/// outside the org answers, and the service ceiling that keeps a service credential without <c>usage:read</c> out.
/// </summary>
/// <remarks>
/// Every world runs on the same fixed clock, so the period the seeds name and the period the read answers cannot move
/// under a test that straddles a month edge. The viewer deny matrix covers the member-without-<c>usage:read</c> answer
/// for this route like every other route; the service half here is the ceiling the evaluator applies to a service
/// credential that asks for the permission.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsageSummaryReadTests
{
    /// <summary>
    /// The instant every world of this class reports: mid-month, so no run of this suite can cross a rollover and the
    /// seeded period is the period under test.
    /// </summary>
    private static readonly DateTimeOffset Fixed = new(2026, 11, 15, 9, 30, 0, TimeSpan.Zero);

    private readonly PostgresTenancyFixture _fixture;

    public UsageSummaryReadTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static string Period => Fixed.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static string PreviousPeriod => Fixed.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Summary_EmptyProject_MeteredMetricsAreZeroAndUnmeteredNull()
    {
        await using var world = await StartAsync();
        world.Host.Services.GetRequiredService<TimeProvider>().GetUtcNow().ShouldBe(Fixed);

        var answer = await world.GetAsMemberAsync(world.SummaryUrl(world.Project));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("period").GetString().ShouldBe(Period);
        answer.Body.GetProperty("project").GetGuid().ShouldBe(world.Project);

        var results = Metric(answer, UsageLedgerMetricNames.Results);
        results.GetProperty("scope").GetString().ShouldBe(UsageSummaryScopes.Tenant);
        results.GetProperty("used").GetDecimal().ShouldBe(0m);
        results.GetProperty("limit").ValueKind.ShouldBe(JsonValueKind.Null);
        var project = results.GetProperty("projects").EnumerateArray().ShouldHaveSingleItem();
        project.GetProperty("project_id").GetGuid().ShouldBe(world.Project);
        project.GetProperty("scope").GetString().ShouldBe(UsageSummaryScopes.Project);
        project.GetProperty("used").GetDecimal().ShouldBe(0m);

        var egress = Metric(answer, UsageLedgerMetricNames.EgressGb);
        egress.GetProperty("used").ValueKind.ShouldBe(JsonValueKind.Null);
        egress.GetProperty("limit").ValueKind.ShouldBe(JsonValueKind.Null);
        egress.GetProperty("unavailable_reason").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Summary_TenantAndProjectCounters_AreLabelledAndScoped()
    {
        await using var world = await StartAsync();
        await world.SeedCounterAsync(
            new CounterSeed(Guid.CreateVersion7(), null, Period, UsageLedgerMetricNames.Results, 4m, 10m),
            new CounterSeed(Guid.CreateVersion7(), world.Project, Period, UsageLedgerMetricNames.Results, 2m, 5m));

        var answer = await world.GetAsMemberAsync(world.SummaryUrl(world.Project));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        var results = Metric(answer, UsageLedgerMetricNames.Results);
        results.GetProperty("used").GetDecimal().ShouldBe(4m);
        results.GetProperty("limit").GetDecimal().ShouldBe(10m);
        var project = results.GetProperty("projects").EnumerateArray().ShouldHaveSingleItem();
        project.GetProperty("used").GetDecimal().ShouldBe(2m);
        project.GetProperty("limit").GetDecimal().ShouldBe(5m);
    }

    [Fact]
    public async Task Summary_PreviousPeriodCounter_SuppliesNoLimit()
    {
        await using var world = await StartAsync();
        await world.SeedCounterAsync(
            new CounterSeed(Guid.CreateVersion7(), null, PreviousPeriod, UsageLedgerMetricNames.Results, 9m, 10m),
            new CounterSeed(Guid.CreateVersion7(), world.Project, PreviousPeriod, UsageLedgerMetricNames.Results, 1m, 10m));

        var answer = await world.GetAsMemberAsync(world.SummaryUrl(world.Project));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        var results = Metric(answer, UsageLedgerMetricNames.Results);

        // The read reports the month it is in. Carrying the previous limit is the gate's write-time job, so an empty
        // current month answers its roll-up total (none) with no limit rather than a limit the gate never carried.
        results.GetProperty("used").GetDecimal().ShouldBe(0m);
        results.GetProperty("limit").ValueKind.ShouldBe(JsonValueKind.Null);
        results.GetProperty("projects").EnumerateArray().ShouldHaveSingleItem()
            .GetProperty("limit").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Summary_RollupOnlyMonth_UsesRollupTotalsWithNoLimit()
    {
        await using var world = await StartAsync();
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("billing"));
        await world.SeedRollupAsync(
            new RollupSeed(Guid.CreateVersion7(), world.Project, Period, UsageLedgerMetricNames.Results, 3m, 2, Fixed),
            new RollupSeed(Guid.CreateVersion7(), sibling, Period, UsageLedgerMetricNames.Results, 4m, 1, Fixed));

        var answer = await world.GetAsMemberAsync(world.SummaryUrl());

        answer.Status.ShouldBe(HttpStatusCode.OK);
        var results = Metric(answer, UsageLedgerMetricNames.Results);

        // No counter exists for the month, so the organization-wide number is every project's roll-up total and the
        // limit is null: a roll-up records what was measured, never what the gate would allow.
        results.GetProperty("used").GetDecimal().ShouldBe(7m);
        results.GetProperty("limit").ValueKind.ShouldBe(JsonValueKind.Null);
        var projects = results.GetProperty("projects").EnumerateArray()
            .ToDictionary(project => project.GetProperty("project_id").GetGuid());
        projects.Keys.OrderBy(id => id).ShouldBe(new[] { world.Project, sibling }.OrderBy(id => id));
        projects[world.Project].GetProperty("used").GetDecimal().ShouldBe(3m);
        projects[sibling].GetProperty("used").GetDecimal().ShouldBe(4m);
        projects.Values.ShouldAllBe(project => project.GetProperty("limit").ValueKind == JsonValueKind.Null);
        projects.Values.ShouldAllBe(project => project.GetProperty("scope").GetString() == UsageSummaryScopes.Project);
    }

    [Fact]
    public async Task Summary_CounterAndRollup_PrefersTheCounter()
    {
        await using var world = await StartAsync();
        await world.SeedCounterAsync(
            new CounterSeed(Guid.CreateVersion7(), null, Period, UsageLedgerMetricNames.Results, 2m, 10m),
            new CounterSeed(Guid.CreateVersion7(), world.Project, Period, UsageLedgerMetricNames.Results, 1m, 5m));
        await world.SeedRollupAsync(
            new RollupSeed(Guid.CreateVersion7(), world.Project, Period, UsageLedgerMetricNames.Results, 9m, 9, Fixed));

        var answer = await world.GetAsMemberAsync(world.SummaryUrl(world.Project));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        var results = Metric(answer, UsageLedgerMetricNames.Results);

        // The roll-up is the fallback for a scope the month has no counter for; a counter the gate writes is the
        // authority for both scopes, so neither the total nor the limit comes from the roll-up row.
        results.GetProperty("used").GetDecimal().ShouldBe(2m);
        results.GetProperty("limit").GetDecimal().ShouldBe(10m);
        var project = results.GetProperty("projects").EnumerateArray().ShouldHaveSingleItem();
        project.GetProperty("used").GetDecimal().ShouldBe(1m);
        project.GetProperty("limit").GetDecimal().ShouldBe(5m);
    }

    [Fact]
    public async Task Summary_ProjectFilter_ReturnsOnlyThatProject()
    {
        await using var world = await StartAsync();
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("billing"));
        await world.SeedCounterAsync(
            new CounterSeed(Guid.CreateVersion7(), null, Period, UsageLedgerMetricNames.Results, 5m, 20m),
            new CounterSeed(Guid.CreateVersion7(), world.Project, Period, UsageLedgerMetricNames.Results, 2m, 10m),
            new CounterSeed(Guid.CreateVersion7(), sibling, Period, UsageLedgerMetricNames.Results, 3m, 10m));

        var answer = await world.GetAsMemberAsync(world.SummaryUrl(world.Project));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        var results = Metric(answer, UsageLedgerMetricNames.Results);

        // The filter narrows the project rows only: the organization-wide counter still caps every project together.
        results.GetProperty("used").GetDecimal().ShouldBe(5m);
        results.GetProperty("limit").GetDecimal().ShouldBe(20m);
        var project = results.GetProperty("projects").EnumerateArray().ShouldHaveSingleItem();
        project.GetProperty("project_id").GetGuid().ShouldBe(world.Project);
        project.GetProperty("used").GetDecimal().ShouldBe(2m);
    }

    [Fact]
    public async Task Summary_MetricList_IsUniqueAndComplete()
    {
        await using var world = await StartAsync();

        var answer = await world.GetAsMemberAsync(world.SummaryUrl());

        answer.Status.ShouldBe(HttpStatusCode.OK);
        var metrics = answer.Body.GetProperty("metrics").EnumerateArray()
            .Select(metric => metric.GetProperty("metric").GetString()!)
            .ToList();

        metrics.ShouldBeUnique();
        metrics.ShouldBe(UsageLedgerMetricNames.All);
    }

    [Fact]
    public async Task Summary_ForeignProject_Is404NotForbidden()
    {
        await using var world = await StartAsync();
        var (_, otherProject) = await world.AddOtherOrgAsync();

        var answer = await world.GetAsMemberAsync(world.SummaryUrl(otherProject));

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task Summary_ServiceCredentialOutsideCeiling_Is403()
    {
        await using var world = await StartAsync();

        var answer = await world.GetAsServiceAsync(world.SummaryUrl(), Permissions.Usage.Read);

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("forbidden");
    }

    /// <summary>One world whose host reports <see cref="Fixed"/> as the current instant.</summary>
    private Task<UsageReadsWorld> StartAsync()
    {
        var clock = new FakeTimeProvider(Fixed);
        return UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted, clock);
    }

    private static JsonElement Metric(ApiAnswer answer, string wire) =>
        answer.Body.GetProperty("metrics").EnumerateArray()
            .Single(metric => metric.GetProperty("metric").GetString() == wire);
}
