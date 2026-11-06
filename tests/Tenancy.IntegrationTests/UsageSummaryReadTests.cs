using System.Globalization;
using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Usage.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The tenant usage summary over the real host and a real PostgreSQL: the two counter scopes and their labels, a month
/// with no counter (a roll-over never invents a limit), the 404 a project outside the org answers, and the service
/// ceiling that keeps a service credential without <c>usage:read</c> out.
/// </summary>
/// <remarks>
/// The period is the server's current month, so the seeds name the month the run is in rather than a pinned one. The
/// viewer deny matrix covers the member-without-<c>usage:read</c> answer for this route like every other route; the
/// service half here is the ceiling the evaluator applies to a service credential that asks for the permission.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsageSummaryReadTests
{
    private readonly PostgresTenancyFixture _fixture;

    public UsageSummaryReadTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static string CurrentPeriod => DateTimeOffset.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static string PreviousPeriod =>
        DateTimeOffset.UtcNow.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Summary_EmptyProject_MeteredMetricsAreZeroAndUnmeteredNull()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

        var answer = await world.GetAsMemberAsync(world.SummaryUrl(world.Project));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("period").GetString().ShouldBe(CurrentPeriod);
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
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        var period = CurrentPeriod;
        await world.SeedCounterAsync(
            new CounterSeed(Guid.CreateVersion7(), null, period, UsageLedgerMetricNames.Results, 4m, 10m),
            new CounterSeed(Guid.CreateVersion7(), world.Project, period, UsageLedgerMetricNames.Results, 2m, 5m));

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
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
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
    public async Task Summary_ForeignProject_Is404NotForbidden()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        var (_, otherProject) = await world.AddOtherOrgAsync();

        var answer = await world.GetAsMemberAsync(world.SummaryUrl(otherProject));

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task Summary_ServiceCredentialOutsideCeiling_Is403()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

        var answer = await world.GetAsServiceAsync(world.SummaryUrl(), Permissions.Usage.Read);

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static JsonElement Metric(ApiAnswer answer, string wire) =>
        answer.Body.GetProperty("metrics").EnumerateArray()
            .Single(metric => metric.GetProperty("metric").GetString() == wire);
}
