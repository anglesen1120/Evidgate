using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ListRunsTests
{
    [Fact]
    public async Task ListRuns_OrderAndCompleteness_RequireOneExactToken()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        string[] valid = ["order=asc", "order=desc", "completeness=complete", "completeness=incomplete"];
        foreach (var query in valid)
        {
            var answer = await ListAsync(world, query);
            answer.Status.ShouldBe(HttpStatusCode.OK, $"{query}: {answer.ErrorMessage}");
        }

        (string Query, string Path)[] invalid =
        [
            ("order=DESC", "order"), ("order=%20desc", "order"), ("order=newest", "order"), ("order=", "order"),
            ("completeness=Complete", "completeness"), ("completeness=incomplete%20", "completeness"),
            ("completeness=all", "completeness"), ("completeness=", "completeness"),
            ("order=asc&order=desc", "order"), ("completeness=complete&completeness=incomplete", "completeness"),
        ];
        foreach (var (query, path) in invalid)
        {
            AssertInvalidQuery(await ListAsync(world, query), query, path, "invalid_value");
        }
    }

    [Fact]
    public async Task ListRuns_DescendingCursor_RejectsMalformedMarkerAndPayload()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runs = await SeedOrderedDecisionRunsAsync(world, 2);
        const string Query = "order=desc&limit=1";
        var first = await ListAsync(world, Query);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        OrderedIds(first).ShouldHaveSingleItem().ShouldBe(runs[1]);
        var issued = DecisionCursor(first).ShouldNotBeNull();
        issued.ShouldStartWith("d.");
        var control = await ListAsync(world, $"{Query}&cursor={Uri.EscapeDataString(issued)}");
        control.Status.ShouldBe(HttpStatusCode.OK, control.ErrorMessage);
        OrderedIds(control).ShouldHaveSingleItem().ShouldBe(runs[0]);
        DecisionCursor(control).ShouldBeNull();

        var separator = issued.IndexOf('-', StringComparison.Ordinal);
        string[] malformed =
        [
            "d.", "D." + issued[2..], "d." + issued, issued[..^1],
            "d.z" + issued[separator..], "d.7fffffffffffffff" + issued[separator..],
            issued[..^1] + "x",
        ];
        foreach (var cursor in malformed)
        {
            var answer = await ListAsync(world, $"{Query}&cursor={Uri.EscapeDataString(cursor)}");
            AssertInvalidQuery(answer, cursor, "cursor", "malformed");
        }
    }

    [Fact]
    public async Task ListRuns_Descending_InsertAtCursorTimeWithGreaterIdAppearsOnce()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var tied = await SeedOrderedDecisionRunsAsync(world, 2, tied: true);
        var first = await ListAsync(world, "order=desc&limit=1");
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        OrderedIds(first).ShouldHaveSingleItem().ShouldBe(tied[0]);
        var cursor = DecisionCursor(first).ShouldNotBeNull();

        // The maximum UUID sorts after both generated IDs regardless of their random bits.
        var inserted = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        await _fixture.ExecuteAsync(world.Database, $"""
            insert into public.run
              (id, org_id, project_id, manifest_id, policy_revision_id, suite_revision_id, environment_revision_id,
               state, collection_deadline_at, late_window_minutes, created_at, updated_at)
            select '{inserted:D}'::uuid, org_id, project_id, manifest_id, policy_revision_id, suite_revision_id,
                   environment_revision_id, state, collection_deadline_at, late_window_minutes, created_at, updated_at
              from public.run where id = '{tied[0]:D}'
            """);

        var next = await ListAsync(world, $"order=desc&limit=2&cursor={Uri.EscapeDataString(cursor)}");
        next.Status.ShouldBe(HttpStatusCode.OK, next.ErrorMessage);
        OrderedIds(next).ShouldBe(new[] { tied[1], inserted }, ignoreOrder: false);
        DecisionCursor(next).ShouldBeNull();
    }

    private static void AssertInvalidQuery(ApiAnswer answer, string input, string path, string reason)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, input);
        answer.ErrorCode.ShouldBe("request_invalid", input);
        Details(answer).ShouldContain(detail =>
            detail.GetProperty("path").GetString() == path && detail.GetProperty("reason").GetString() == reason);
    }
}
