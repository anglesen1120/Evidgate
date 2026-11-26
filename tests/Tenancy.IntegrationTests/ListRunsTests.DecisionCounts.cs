using System.Globalization;
using System.Net;
using Evidgate.Modules.Orchestrator.Application.Queries;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <c>GET /v1/projects/{p}/runs/decision-counts</c> through the real host and PostgreSQL: the counts match the
/// seeded verdict mix, a run is counted once under its newest live evaluation, <c>since</c> is inclusive, the scan
/// cap sets <c>truncated</c>, and the project and permission refusals disclose nothing.
/// </summary>
public sealed partial class ListRunsTests
{
    private static readonly DateTimeOffset SeedStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DecisionCounts_MatchTheSeededMixAndTheRunsWithoutAVerdict()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runs = await SeedOrderedDecisionRunsAsync(world, 8);
        var policy = await SeedPolicyAsync(world);
        await InsertEvaluationAsync(world, runs[0], policy, 1, "live", "PASS");
        await InsertEvaluationAsync(world, runs[1], policy, 1, "live", "PASS");
        await InsertEvaluationAsync(world, runs[2], policy, 1, "live", "FAIL");
        await InsertEvaluationAsync(world, runs[3], policy, 1, "live", "INCONCLUSIVE");
        await InsertEvaluationAsync(world, runs[4], policy, 1, "live", "INCONCLUSIVE");
        await InsertEvaluationAsync(world, runs[5], policy, 1, "shadow", "PASS");

        var answer = await DecisionCountsAsync(world, $"since={Stamp(SeedStart)}");

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        ExpectCounts(answer, pass: 2, fail: 1, inconclusive: 2, noDecision: 3, scanned: 8, truncated: false);
        answer.Body.GetProperty("since").GetDateTimeOffset().ShouldBe(SeedStart);
    }

    [Fact]
    public async Task DecisionCounts_ARunWithTwoLiveEvaluationsCountsOnlyItsLatest()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var run = await CreateBranchAsync(world, "main", ShaOne);
        var policy = await SeedPolicyAsync(world);
        await InsertEvaluationAsync(world, run, policy, 1, "live", "PASS");
        await InsertEvaluationAsync(world, run, policy, 2, "live", "FAIL");
        await InsertEvaluationAsync(world, run, policy, 3, "shadow", "PASS");

        var answer = await DecisionCountsAsync(world, string.Empty);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        ExpectCounts(answer, pass: 0, fail: 1, inconclusive: 0, noDecision: 0, scanned: 1, truncated: false);
    }

    [Fact]
    public async Task DecisionCounts_WithoutSinceLooksBackThirtyDays()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var recent = await CreateBranchAsync(world, "main", ShaOne);
        var old = await CreateBranchAsync(world, "old", ShaOne);
        await _fixture.ExecuteAsync(
            world.Database, $"update public.run set created_at = now() - interval '31 days' where id = '{old:D}'");
        var policy = await SeedPolicyAsync(world);
        await InsertEvaluationAsync(world, recent, policy, 1, "live", "FAIL");
        await InsertEvaluationAsync(world, old, policy, 1, "live", "PASS");

        var before = DateTimeOffset.UtcNow;
        var answer = await DecisionCountsAsync(world, string.Empty);
        var after = DateTimeOffset.UtcNow;

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        ExpectCounts(answer, pass: 0, fail: 1, inconclusive: 0, noDecision: 0, scanned: 1, truncated: false);
        answer.Body.GetProperty("since").GetDateTimeOffset()
            .ShouldBeInRange(before - TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1), after - TimeSpan.FromDays(30) + TimeSpan.FromSeconds(1));
        var widened = await DecisionCountsAsync(world, $"since={Stamp(DateTimeOffset.UtcNow - TimeSpan.FromDays(40))}");
        ExpectCounts(widened, pass: 1, fail: 1, inconclusive: 0, noDecision: 0, scanned: 2, truncated: false);
    }

    [Fact]
    public async Task DecisionCounts_SinceIsInclusiveAtTheRunCreationInstant()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runs = await SeedOrderedDecisionRunsAsync(world, 3);
        var policy = await SeedPolicyAsync(world);
        await InsertEvaluationAsync(world, runs[0], policy, 1, "live", "FAIL");
        await InsertEvaluationAsync(world, runs[1], policy, 1, "live", "PASS");
        var boundary = SeedStart.AddSeconds(1);

        var atBoundary = await DecisionCountsAsync(world, $"since={Stamp(boundary)}");
        var oneTickLater = await DecisionCountsAsync(world, $"since={Stamp(boundary.AddTicks(10))}");
        var oneTickEarlier = await DecisionCountsAsync(world, $"since={Stamp(boundary.AddTicks(-10))}");

        ExpectCounts(atBoundary, pass: 1, fail: 0, inconclusive: 0, noDecision: 1, scanned: 2, truncated: false);
        ExpectCounts(oneTickLater, pass: 0, fail: 0, inconclusive: 0, noDecision: 1, scanned: 1, truncated: false);
        ExpectCounts(oneTickEarlier, pass: 1, fail: 0, inconclusive: 0, noDecision: 1, scanned: 2, truncated: false);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(201, false)]
    [InlineData(405, true)]
    public async Task DecisionCounts_ReadsEveryChunkOfRunsIncludingTiedCreationTimes(int count, bool tied)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runs = await SeedOrderedDecisionRunsAsync(world, count, tied);
        var policy = await SeedPolicyAsync(world);
        await InsertEvaluationAsync(world, runs[0], policy, 1, "live", "PASS");
        await InsertEvaluationAsync(world, runs[count / 2], policy, 1, "live", "FAIL");
        await InsertEvaluationAsync(world, runs[^1], policy, 1, "live", "INCONCLUSIVE");

        var answer = await DecisionCountsAsync(world, $"since={Stamp(SeedStart)}");

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        ExpectCounts(answer, pass: 1, fail: 1, inconclusive: 1, noDecision: count - 3, scanned: count, truncated: false);
    }

    [Fact]
    public async Task DecisionCounts_ACapFullOfRunsIsNotTruncated_OneMoreRunIs()
    {
        await using var exact = await RunCreationTests.StartAsync(_fixture, services: UseScanCap(3), hosts: _hosts);
        await SeedOrderedDecisionRunsAsync(exact, 3);
        await using var over = await RunCreationTests.StartAsync(_fixture, slug: "over", services: UseScanCap(3), hosts: _hosts);
        var runs = await SeedOrderedDecisionRunsAsync(over, 4);
        var policy = await SeedPolicyAsync(over);
        await InsertEvaluationAsync(over, runs[0], policy, 1, "live", "PASS");
        await InsertEvaluationAsync(over, runs[3], policy, 1, "live", "FAIL");

        var atCap = await DecisionCountsAsync(exact, $"since={Stamp(SeedStart)}");
        var pastCap = await DecisionCountsAsync(over, $"since={Stamp(SeedStart)}");

        ExpectCounts(atCap, pass: 0, fail: 0, inconclusive: 0, noDecision: 3, scanned: 3, truncated: false);
        // The newest three are read, so the oldest run's PASS is the one left out.
        ExpectCounts(pastCap, pass: 0, fail: 1, inconclusive: 0, noDecision: 2, scanned: 3, truncated: true);
    }

    [Fact]
    public async Task DecisionCounts_AnEmptyProjectAnswersZeroCounts()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var answer = await DecisionCountsAsync(world, string.Empty);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        ExpectCounts(answer, pass: 0, fail: 0, inconclusive: 0, noDecision: 0, scanned: 0, truncated: false);
    }

    [Theory]
    [InlineData("since=")]
    [InlineData("since=yesterday")]
    [InlineData("since=2026-13-45T00:00:00Z")]
    public async Task DecisionCounts_ABadSinceIsRequestInvalid(string query)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var answer = await DecisionCountsAsync(world, query);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        Details(answer).ShouldContain(detail => detail.GetProperty("path").GetString()!.Contains("since", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DecisionCounts_ForeignAndUnknownProjectsAreTheSameNotFoundBeforeAnyRead()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var other = TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg);
        world.Api.Statements.Clear();

        var foreign = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/projects/{world.Project}/runs/decision-counts", other);
        var unknown = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/projects/{Guid.CreateVersion7()}/runs/decision-counts", other);

        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        foreign.ErrorCode.ShouldBe(unknown.ErrorCode);
        foreign.ErrorMessage.ShouldBe(unknown.ErrorMessage);
        EvaluationStatementCount(world).ShouldBe(0, "a refused project reads no verdict");
    }

    [Fact]
    public async Task DecisionCounts_ACallerWithoutDecisionReadIsForbiddenUntilGrantedAndUnknownProjectsStillAre404()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var run = await CreateBranchAsync(world, "main", ShaOne);
        var policy = await SeedPolicyAsync(world);
        await InsertEvaluationAsync(world, run, policy, 1, "live", "PASS");
        var role = await RestrictToRunReadAsync(world);

        var denied = await DecisionCountsAsync(world, string.Empty);
        var unknown = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/projects/{Guid.CreateVersion7()}/runs/decision-counts", world.Admin);
        await _fixture.ExecuteAsync(world.Database, $"""
            insert into public.role_permission (id, org_id, role_id, permission_code)
            values (gen_random_uuid(), '{world.Org:D}', '{role:D}', 'decision:read');
            """);
        var allowed = await DecisionCountsAsync(world, string.Empty);

        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        denied.Body.ToString().ShouldNotContain("\"pass\"");
        unknown.Status.ShouldBe(HttpStatusCode.NotFound, "the project check precedes the second permission");
        allowed.Status.ShouldBe(HttpStatusCode.OK, allowed.ErrorMessage);
        ExpectCounts(allowed, pass: 1, fail: 0, inconclusive: 0, noDecision: 0, scanned: 1, truncated: false);
    }

    private static Task<ApiAnswer> DecisionCountsAsync(RunCreationTests.World world, string query) =>
        world.Client.SendAsync(
            HttpMethod.Get,
            $"/v1/projects/{world.Project}/runs/decision-counts{(query.Length == 0 ? string.Empty : "?" + query)}",
            world.Admin);

    private static Action<IServiceCollection> UseScanCap(int rows) =>
        services => services.AddSingleton(new RunDecisionCountsScanCap(rows));

    /// <summary>Seven-digit fractions keep a 100 ns tick visible in the query (PostgreSQL keeps microseconds).</summary>
    private static string Stamp(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static void ExpectCounts(
        ApiAnswer answer, int pass, int fail, int inconclusive, int noDecision, int scanned, bool truncated)
    {
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var body = answer.Body;
        body.GetProperty("pass").GetInt32().ShouldBe(pass);
        body.GetProperty("fail").GetInt32().ShouldBe(fail);
        body.GetProperty("inconclusive").GetInt32().ShouldBe(inconclusive);
        body.GetProperty("no_decision").GetInt32().ShouldBe(noDecision);
        body.GetProperty("scanned").GetInt32().ShouldBe(scanned);
        body.GetProperty("truncated").GetBoolean().ShouldBe(truncated);
    }
}
