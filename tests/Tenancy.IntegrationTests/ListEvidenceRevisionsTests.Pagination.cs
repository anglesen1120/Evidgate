using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The page size the route promises: <c>limit=1</c> is one row and a cursor on a run that holds more, so a limit the
/// query ignored would show up as extra rows rather than as a passing test.
/// </summary>
public sealed partial class ListEvidenceRevisionsTests
{
    /// <summary>
    /// AC (00 §9.1): three revisions and <c>limit=1</c> answer the newest row alone plus a cursor; the cursor
    /// continues at the next row, so the walk repeats nothing and skips nothing.
    /// </summary>
    [Fact]
    public async Task ListEvidenceRevisions_LimitOne_AnswersOneRowAndACursor()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertRevisionsAsync(
            world,
            runId,
            new Revision(1, "complete"),
            new Revision(2, "incomplete", "{artifact_missing}"),
            new Revision(3, "complete"));

        var first = await GetAsync(world, runId, "?limit=1");

        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        RevisionNumbers(first).ShouldHaveSingleItem().ShouldBe(3, "limit=1 is one row, never the run's three");
        var cursor = first.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrEmpty("a page with rows left must hand back a cursor");

        var second = await GetAsync(world, runId, $"?limit=1&cursor={Uri.EscapeDataString(cursor!)}");

        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        RevisionNumbers(second).ShouldHaveSingleItem().ShouldBe(2, "the cursor continues at the row below the first page");
        second.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.String, "one more revision is left");
    }
}
