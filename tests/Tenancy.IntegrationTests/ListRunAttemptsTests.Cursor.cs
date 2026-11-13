#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The run-bound cursor of <c>GET /v1/runs/{r}/attempts</c> and the run detail's <c>counts</c> and first page of
/// attempts, through the real host and a real PostgreSQL: a cursor names the run it was minted for, so another run's
/// cursor, another tenant's run id and a non-canonical spelling are all 422 at <c>cursor</c>, and the detail serves
/// constant-size counts plus the first 50 attempts whose <c>next_cursor</c> continues on the attempts route.
/// </summary>
public sealed partial class ListRunAttemptsTests
{
    private const int DetailPageSize = 50;

    /// <summary>A cursor minted for run A is 422 on run B of the same tenant, never a page position in B.</summary>
    [Fact]
    public async Task ListRunAttempts_CursorOfAnotherRun_Is422AtCursor()
    {
        await using var world = await StartAsync();
        var runA = await SeedPlanAsync(world, 1);
        var runB = await SeedPlanAsync(world, 1);
        var executionA = runA.Executions[0];
        await InsertAttemptsAsync(
            world,
            runA.RunId,
            new Attempt(executionA.ExecutionKey, executionA.CaseId, 1, "passed"),
            new Attempt(executionA.ExecutionKey, executionA.CaseId, 2, "passed"));

        var firstPage = await GetAsync(world, runA.RunId, query: "?limit=1");
        var cursor = firstPage.Body.GetProperty("next_cursor").GetString()!;

        var sameRun = await GetAsync(world, runA.RunId, query: $"?cursor={Uri.EscapeDataString(cursor)}");
        var otherRun = await GetAsync(world, runB.RunId, query: $"?cursor={Uri.EscapeDataString(cursor)}");

        sameRun.Status.ShouldBe(HttpStatusCode.OK, sameRun.ErrorMessage);
        otherRun.Status.ShouldBe(HttpStatusCode.UnprocessableContent, otherRun.ErrorMessage);
        otherRun.ErrorCode.ShouldBe("request_invalid");
        DetailPath(otherRun).ShouldBe("cursor");
    }

    /// <summary>A cursor spelling another tenant's run id is the same 422 on the caller's own run, and leaks nothing.</summary>
    [Fact]
    public async Task ListRunAttempts_CursorOfOtherOrgRun_Is422AtCursor()
    {
        await using var world = await StartAsync();
        var own = await SeedPlanAsync(world, 1);
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        await CreateProjectAsync(world.Client, otherUser, otherOrg, Unique.Slug("rival-project"));
        var foreign = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, otherOrg, 1);
        var forged = $"{foreign.RunId:D}-{foreign.Executions[0].ExecutionKey}-1";

        var answer = await GetAsync(world, own.RunId, query: $"?cursor={Uri.EscapeDataString(forged)}");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableContent, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        DetailPath(answer).ShouldBe("cursor");
    }

    /// <summary>The pre-binding spelling, an uppercase run id and a truncated run id are not cursors this endpoint issues.</summary>
    [Theory]
    [InlineData("KEY-1")]
    [InlineData("RUNUPPER-KEY-1")]
    [InlineData("RUN-KEY-0")]
    [InlineData("RUN-KEY-01")]
    [InlineData("RUN_KEY_1")]
    [InlineData("RUN-SHORT-1")]
    public async Task ListRunAttempts_NonCanonicalRunBoundCursor_Is422AtCursor(string shape)
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var key = plan.Executions[0].ExecutionKey;
        var cursor = shape
            .Replace("RUNUPPER", plan.RunId.ToString("D").ToUpperInvariant(), StringComparison.Ordinal)
            .Replace("RUN-SHORT", plan.RunId.ToString("D")[..35], StringComparison.Ordinal)
            .Replace("RUN_KEY", $"{plan.RunId:D}_{key}", StringComparison.Ordinal)
            .Replace("RUN-KEY", $"{plan.RunId:D}-{key}", StringComparison.Ordinal)
            .Replace("KEY", key, StringComparison.Ordinal);

        var answer = await GetAsync(world, plan.RunId, query: $"?cursor={Uri.EscapeDataString(cursor)}");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableContent, answer.ErrorMessage);
        DetailPath(answer).ShouldBe("cursor");
    }

    /// <summary>Exact page size: a page that holds every row has no next cursor; one row short of that has one.</summary>
    [Fact]
    public async Task ListRunAttempts_ExactPageSize_HasNoNextCursor()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var execution = plan.Executions[0];
        await InsertAttemptsAsync(
            world,
            plan.RunId,
            new Attempt(execution.ExecutionKey, execution.CaseId, 1, "passed"),
            new Attempt(execution.ExecutionKey, execution.CaseId, 2, "passed"));

        var exact = await GetAsync(world, plan.RunId, query: "?limit=2");
        var short1 = await GetAsync(world, plan.RunId, query: "?limit=1");

        Items(exact).GetArrayLength().ShouldBe(2);
        exact.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
        Items(short1).GetArrayLength().ShouldBe(1);
        short1.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.String);
    }

    /// <summary>The run detail counts attempts by normalised status and serves the first page of them.</summary>
    [Fact]
    public async Task GetRun_ReturnsCountsByNormalisedStatusAndFirstPage()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 3);
        await InsertAttemptsAsync(
            world,
            plan.RunId,
            new Attempt(plan.Executions[0].ExecutionKey, plan.Executions[0].CaseId, 1, "passed"),
            new Attempt(plan.Executions[1].ExecutionKey, plan.Executions[1].CaseId, 1, "failed"),
            new Attempt(plan.Executions[1].ExecutionKey, plan.Executions[1].CaseId, 2, "failed"),
            new Attempt(plan.Executions[2].ExecutionKey, plan.Executions[2].CaseId, 1, "skipped"));

        var answer = await GetRunAsync(world, plan.RunId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var counts = answer.Body.GetProperty("counts");
        counts.GetProperty("passed").GetInt32().ShouldBe(1);
        counts.GetProperty("failed").GetInt32().ShouldBe(2);
        counts.GetProperty("skipped").GetInt32().ShouldBe(1);
        counts.GetProperty("timed_out").GetInt32().ShouldBe(0);
        counts.GetProperty("error").GetInt32().ShouldBe(0);
        counts.GetProperty("blocked").GetInt32().ShouldBe(0);
        var page = answer.Body.GetProperty("executions_page");
        page.GetProperty("items").GetArrayLength().ShouldBe(4);
        page.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>A run without attempts reports zero counts and an empty first page, never a 404 or a null member.</summary>
    [Fact]
    public async Task GetRun_WithoutAttempts_HasZeroCountsAndAnEmptyPage()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);

        var answer = await GetRunAsync(world, plan.RunId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("counts").GetProperty("passed").GetInt32().ShouldBe(0);
        var page = answer.Body.GetProperty("executions_page");
        page.GetProperty("items").GetArrayLength().ShouldBe(0);
        page.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// More attempts than the detail's page: the page holds 50 and its <c>next_cursor</c> continues on the attempts
    /// route with the one remaining row, while the counts still cover every attempt.
    /// </summary>
    [Fact]
    public async Task GetRun_MoreThanOnePage_NextCursorContinuesOnTheAttemptsRoute()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var execution = plan.Executions[0];
        var attempts = Enumerable.Range(1, DetailPageSize + 1)
            .Select(no => new Attempt(execution.ExecutionKey, execution.CaseId, no, "passed"))
            .ToArray();
        await InsertAttemptsAsync(world, plan.RunId, attempts);

        var detail = await GetRunAsync(world, plan.RunId);

        detail.Status.ShouldBe(HttpStatusCode.OK, detail.ErrorMessage);
        detail.Body.GetProperty("counts").GetProperty("passed").GetInt32().ShouldBe(DetailPageSize + 1);
        var page = detail.Body.GetProperty("executions_page");
        page.GetProperty("items").GetArrayLength().ShouldBe(DetailPageSize);
        var cursor = page.GetProperty("next_cursor").GetString()!;

        var rest = await GetAsync(world, plan.RunId, query: $"?cursor={Uri.EscapeDataString(cursor)}");

        rest.Status.ShouldBe(HttpStatusCode.OK, rest.ErrorMessage);
        Items(rest).GetArrayLength().ShouldBe(1);
        Items(rest)[0].GetProperty("attempt_no").GetInt32().ShouldBe(DetailPageSize + 1);
        rest.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static Task<ApiAnswer> GetRunAsync(World world, Guid runId) =>
        world.Client.SendAsync(HttpMethod.Get, $"/v1/runs/{runId}", world.Admin);
}
