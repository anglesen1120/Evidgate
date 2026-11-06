// Request URLs are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReportComparisonTests
{
    // ------------------------------------------------------------------ paging

    /// <summary>
    /// AC: cases are ordered by sequence and keyset-paged — five cases read two, two and one, every case exactly once
    /// and in sequence order, with <c>next_cursor</c> null only on the last page.
    /// </summary>
    [Fact]
    public async Task Comparison_ManyCases_KeepSequenceOrderAcrossPagesWithoutGapOrRepeat()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var report = await SeedReportAsync(
            world,
            runId,
            events: [.. Enumerable.Range(1, 5).Select(sequence => new EventSeed(sequence, $"case-{sequence}", "passed"))]);

        var walked = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 3; page++)
        {
            var answer = await GetAsync(world, runId, report.Id, limit: 2, cursor: cursor);
            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
            walked.AddRange(CaseRefs(answer));
            var next = answer.Body.GetProperty("next_cursor");
            if (page < 2)
            {
                next.ValueKind.ShouldBe(JsonValueKind.String);
                cursor = next.GetString();
            }
            else
            {
                next.ValueKind.ShouldBe(JsonValueKind.Null);
            }
        }

        walked.ShouldBe([.. Enumerable.Range(1, 5).Select(sequence => ExecutionKeyFor($"case-{sequence}"))]);
    }

    /// <summary>
    /// AC: a cursor is a bare sequence number, not a report-scoped token — a cursor another report's page issued keeps
    /// its meaning as a sequence for this report's route, so the page holds this report's cases above that sequence
    /// and never the other report's rows.
    /// </summary>
    [Fact]
    public async Task Comparison_CursorFromAnotherReport_IsJustASequenceForThisReportsCases()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var first = await SeedReportAsync(
            world,
            runId,
            events: [new EventSeed(1, "first-1", "passed"), new EventSeed(2, "first-2", "passed"), new EventSeed(3, "first-3", "passed")]);
        var second = await SeedReportAsync(
            world,
            runId,
            events: [new EventSeed(1, "second-1", "passed"), new EventSeed(2, "second-2", "passed"), new EventSeed(3, "second-3", "passed")]);

        var firstPage = await GetAsync(world, runId, first.Id, limit: 2);
        firstPage.Status.ShouldBe(HttpStatusCode.OK, firstPage.ErrorMessage);
        var cursor = firstPage.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldBe("2");

        var answer = await GetAsync(world, runId, second.Id, cursor: cursor);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        CaseRefs(answer).ShouldBe([ExecutionKeyFor("second-3")], "the cursor is a sequence of this report's own page");
        answer.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>AC: <c>limit</c> 1 and 200 are the accepted bounds; 0 and 201 are 422 <c>request_invalid</c> at <c>limit</c>.</summary>
    [Theory]
    [InlineData(1, HttpStatusCode.OK)]
    [InlineData(200, HttpStatusCode.OK)]
    [InlineData(0, HttpStatusCode.UnprocessableEntity)]
    [InlineData(201, HttpStatusCode.UnprocessableEntity)]
    public async Task Comparison_LimitBoundary_IsOneToTwoHundred(int limit, HttpStatusCode expected)
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, runId, events: [new EventSeed(1, "probe", "passed")]);

        var answer = await GetAsync(world, runId, report.Id, limit: limit);

        answer.Status.ShouldBe(expected, answer.ErrorMessage);
        if (expected == HttpStatusCode.UnprocessableEntity)
        {
            answer.ErrorCode.ShouldBe("request_invalid");
            FieldPath(answer).ShouldBe("limit");
        }
    }

    /// <summary>AC: a cursor this endpoint never issued — not a canonical decimal, negative, or two of them — is 422 at <c>cursor</c>.</summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("01")]
    [InlineData("1,2")]
    [InlineData("999999999999999999999999")]
    public async Task Comparison_CursorThatIsNotACanonicalSequence_IsUnprocessable(string cursor)
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, runId, events: [new EventSeed(1, "probe", "passed")]);

        var answer = await GetAsync(world, runId, report.Id, cursor: cursor);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        FieldPath(answer).ShouldBe("cursor");
    }

    /// <summary>AC: the read is the report row and the case page — two statements, never one per case.</summary>
    [Fact]
    public async Task Comparison_ReadsTheReportAndItsPageInTwoStatements()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var report = await SeedReportAsync(
            world,
            runId,
            events:
            [
                new EventSeed(1, "probe", "passed"),
                new EventSeed(2, "probe-two", "failed"),
                new EventSeed(3, "probe-three", "skipped"),
            ]);

        world.Api.Statements.Clear();
        var answer = await GetAsync(world, runId, report.Id);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Cases(answer).Length.ShouldBe(3);
        // The read is one statement for the report row and one for the page, whatever the page holds: a statement
        // per case would show up as more than one `public.event` read.
        var statements = world.Api.Statements.Snapshot()
            .Where(statement => statement.Contains("public.raw_report", StringComparison.Ordinal)
                || statement.Contains("public.event", StringComparison.Ordinal))
            .ToArray();
        statements.Length.ShouldBe(2, string.Join(Environment.NewLine, statements));
        statements.Count(statement => statement.Contains("public.raw_report", StringComparison.Ordinal)).ShouldBe(1);
        statements.Count(statement => statement.Contains("public.event", StringComparison.Ordinal)).ShouldBe(1);
    }

    private static string? FieldPath(ApiAnswer answer) =>
        answer.Body.TryGetProperty("error", out var error) && error.TryGetProperty("details", out var details) && details.GetArrayLength() > 0
            ? details[0].GetProperty("path").GetString()
            : null;
}
