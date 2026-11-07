using System.Globalization;
using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Usage.Contracts;
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
/// Every world names its period and instant from the database's own clock, which is the clock the summary answers from,
/// so a seed and the read that answers it cannot disagree about the month under a host clock that has moved on. The
/// viewer deny matrix covers the member-without-<c>usage:read</c> answer for this route like every other route; the
/// service half here is the ceiling the evaluator applies to a service credential that asks for the permission.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsageSummaryReadTests
{
    private readonly PostgresTenancyFixture _fixture;

    public UsageSummaryReadTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Summary_EmptyProject_MeteredMetricsAreZeroAndUnmeteredNull()
    {
        await using var world = await StartAsync();
        var (period, _) = await world.DatabaseClockAsync();

        var answer = await world.GetAsMemberAsync(world.SummaryUrl(world.Project));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("period").GetString().ShouldBe(period);
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
        var (period, _) = await world.DatabaseClockAsync();
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
        await using var world = await StartAsync();
        var (period, instant) = await world.DatabaseClockAsync();
        var previous = Month(instant.AddMonths(-1));
        await world.SeedCounterAsync(
            new CounterSeed(Guid.CreateVersion7(), null, previous, UsageLedgerMetricNames.Results, 9m, 10m),
            new CounterSeed(Guid.CreateVersion7(), world.Project, previous, UsageLedgerMetricNames.Results, 1m, 10m));

        var answer = await world.GetAsMemberAsync(world.SummaryUrl(world.Project));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("period").GetString().ShouldBe(period);
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
        var (period, instant) = await world.DatabaseClockAsync();
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("billing"));
        await world.SeedRollupAsync(
            new RollupSeed(Guid.CreateVersion7(), world.Project, period, UsageLedgerMetricNames.Results, 3m, 2, instant),
            new RollupSeed(Guid.CreateVersion7(), sibling, period, UsageLedgerMetricNames.Results, 4m, 1, instant));

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
        var (period, instant) = await world.DatabaseClockAsync();
        await world.SeedCounterAsync(
            new CounterSeed(Guid.CreateVersion7(), null, period, UsageLedgerMetricNames.Results, 2m, 10m),
            new CounterSeed(Guid.CreateVersion7(), world.Project, period, UsageLedgerMetricNames.Results, 1m, 5m));
        await world.SeedRollupAsync(
            new RollupSeed(Guid.CreateVersion7(), world.Project, period, UsageLedgerMetricNames.Results, 9m, 9, instant));

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
        var (period, _) = await world.DatabaseClockAsync();
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("billing"));
        await world.SeedCounterAsync(
            new CounterSeed(Guid.CreateVersion7(), null, period, UsageLedgerMetricNames.Results, 5m, 20m),
            new CounterSeed(Guid.CreateVersion7(), world.Project, period, UsageLedgerMetricNames.Results, 2m, 10m),
            new CounterSeed(Guid.CreateVersion7(), sibling, period, UsageLedgerMetricNames.Results, 3m, 10m));

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

    [Fact]
    public async Task Summary_MemberWithoutUsageRead_Is403()
    {
        await using var world = await StartAsync();

        var answer = await world.GetAsMemberWithRoleAsync(_fixture, "viewer", world.SummaryUrl());

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("forbidden");
    }

    /// <summary>A member naming an org it does not belong to is refused by the tenant pipeline (403), before the handler; 404 is for object ids.</summary>
    [Fact]
    public async Task Summary_ForeignOrgNamedInTheRoute_IsRefusedByTheTenantPipeline()
    {
        await using var world = await StartAsync();
        var (otherOrg, _) = await world.AddOtherOrgAsync();

        var answer = await world.Client.SendAsync(
            HttpMethod.Get,
            $"/v1/orgs/{otherOrg:D}/usage/summary",
            TenancyApiFactory.Resolved(user: world.User, orgHeader: otherOrg));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Summary_UnknownProjectInOwnOrg_IsTheSame404AsAForeignProject()
    {
        await using var world = await StartAsync();
        var (_, otherProject) = await world.AddOtherOrgAsync();

        var unknown = await world.GetAsMemberAsync(world.SummaryUrl(Guid.CreateVersion7()));
        var foreign = await world.GetAsMemberAsync(world.SummaryUrl(otherProject));

        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        foreign.Status.ShouldBe(unknown.Status);
        foreign.ErrorCode.ShouldBe(unknown.ErrorCode);
    }

    [Fact]
    public async Task Summary_ServiceCredentialBoundToAnotherProject_IsRefusedByTheCeiling()
    {
        await using var world = await StartAsync();
        var otherProject = await RegistryApi.CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("sibling"));

        var answer = await world.GetAsServiceAsync(world.SummaryUrl(otherProject), Permissions.Usage.Read);

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>One world over the shared database; its period comes from that database's own clock.</summary>
    private Task<UsageReadsWorld> StartAsync() =>
        UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

    private static string Month(DateTimeOffset instant) =>
        instant.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static JsonElement Metric(ApiAnswer answer, string wire) =>
        answer.Body.GetProperty("metrics").EnumerateArray()
            .Single(metric => metric.GetProperty("metric").GetString() == wire);
}
