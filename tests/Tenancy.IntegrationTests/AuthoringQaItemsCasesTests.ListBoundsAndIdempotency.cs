using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringQaItemsCasesTests
{
    /// <summary>
    /// The list routes' query bounds: <c>q</c> is a literal, case-insensitive substring (a <c>%</c>, a <c>_</c> and a
    /// <c>\</c> match nothing rather than everything) and is capped at 200 code points, <c>limit</c> is 1..200, and a
    /// cursor this route never issued is <c>422</c>.
    /// </summary>
    [Fact]
    public async Task ListQueryBounds_AreRefusedAndQIsLiteral()
    {
        await using var world = await StartAsync();
        var item = await CreateItemAsync(world, world.Category);
        _ = await CreateCaseAsync(world, item);

        var percent = await world.Client.SendAsync(HttpMethod.Get, $"{world.Cases}?q=%25", world.As);
        percent.Status.ShouldBe(HttpStatusCode.OK, percent.ErrorMessage);
        percent.Body.GetProperty("items").GetArrayLength().ShouldBe(0, "% is a literal character, not a wildcard");

        foreach (var hostile in new[] { "_", "\\" })
        {
            var answer = await world.Client.SendAsync(
                HttpMethod.Get, $"{world.Cases}?q={Uri.EscapeDataString(hostile)}", world.As);
            answer.Status.ShouldBe(HttpStatusCode.OK, hostile);
            answer.Body.GetProperty("items").GetArrayLength().ShouldBe(0, hostile);
        }

        var fullLength = await world.Client.SendAsync(
            HttpMethod.Get, $"{world.Cases}?q={new string('q', 200)}", world.As);
        fullLength.Status.ShouldBe(HttpStatusCode.OK, "200 code points is inside the q bound");

        var tooLong = await world.Client.SendAsync(
            HttpMethod.Get, $"{world.Cases}?q={new string('q', 201)}", world.As);
        tooLong.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(tooLong).ShouldContain(detail => detail.Path == "/q");

        foreach (var limit in new[] { "0", "201", "abc" })
        {
            var refused = await world.Client.SendAsync(HttpMethod.Get, $"{world.Cases}?limit={limit}", world.As);
            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, limit);
            Details(refused).ShouldContain(detail => detail.Path == "/limit", limit);
        }

        (await world.Client.SendAsync(HttpMethod.Get, $"{world.Cases}?limit=200", world.As)).Status.ShouldBe(HttpStatusCode.OK);

        foreach (var list in new[] { world.Cases, world.Items })
        {
            var cursor = await world.Client.SendAsync(HttpMethod.Get, $"{list}?cursor=tampered-cursor", world.As);
            cursor.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, list);
            Details(cursor).ShouldContain(detail => detail.Path == "/cursor", list);
        }

        // The node filter is a uuid or it is refused — never a silently unfiltered page.
        var badNode = await world.Client.SendAsync(HttpMethod.Get, $"{world.Items}?node_id=not-a-uuid", world.As);
        badNode.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(badNode).ShouldContain(detail => detail.Path == "/node_id");
    }

    /// <summary>
    /// AC18/AC10 at the case routes: a replay of one <c>Idempotency-Key</c> with a different body is
    /// <c>409 idempotency_conflict</c>, a key longer than 128 characters is <c>400 request_invalid</c> on
    /// <c>/Idempotency-Key</c>, and a soft-deleted case answers 404 on the detail, the patch, the state move, the item move
    /// and a second delete.
    /// </summary>
    [Fact]
    public async Task IdempotencyConflict_And404OnEveryVerbAfterDelete()
    {
        await using var world = await StartAsync();
        var item = await CreateItemAsync(world, world.Category);
        var manualCase = await CreateCaseAsync(world, item);

        var key = NewKey();
        var body = new
        {
            checklist_item_id = item,
            title = "Replay",
            steps = new[] { new { no = 1, action = "open", expected = "shown" } },
            priority = "high",
        };

        var first = await world.Client.SendAsync(HttpMethod.Post, world.Cases, world.As, body, key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        var replay = await world.Client.SendAsync(HttpMethod.Post, world.Cases, world.As, body, key);
        replay.Status.ShouldBe(first.Status);
        replay.Id("case_id").ShouldBe(first.Id("case_id"), "a replay returns the stored answer");

        var different = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Cases,
            world.As,
            new
            {
                checklist_item_id = item,
                title = "Different",
                steps = new[] { new { no = 1, action = "open", expected = "shown" } },
                priority = "high",
            },
            key);
        different.Status.ShouldBe(HttpStatusCode.Conflict);
        different.ErrorCode.ShouldBe("idempotency_conflict");

        var longKey = await world.Client.SendAsync(HttpMethod.Post, world.Cases, world.As, body, new string('k', 129));
        longKey.Status.ShouldBe(HttpStatusCode.BadRequest);
        longKey.ErrorCode.ShouldBe("request_invalid");
        Details(longKey).ShouldContain(detail => detail.Path == "/Idempotency-Key");

        (await world.Client.SendAsync(HttpMethod.Delete, world.Case(manualCase), world.As, null, NewKey(), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.NoContent);

        foreach (var second in new[]
        {
            await world.Client.SendAsync(HttpMethod.Get, world.Case(manualCase), world.As),
            await world.Client.SendAsync(HttpMethod.Patch, world.Case(manualCase), world.As, new { title = "x" }, NewKey(), "\"2\""),
            await world.Client.SendAsync(HttpMethod.Post, $"{world.Case(manualCase)}/state", world.As, new { state = "approved" }, NewKey(), "\"2\""),
            await world.Client.SendAsync(HttpMethod.Post, $"{world.Case(manualCase)}/item", world.As, new { checklist_item_id = item }, NewKey(), "\"2\""),
            await world.Client.SendAsync(HttpMethod.Delete, world.Case(manualCase), world.As, null, NewKey(), "\"2\""),
        })
        {
            second.Status.ShouldBe(HttpStatusCode.NotFound);
            second.ErrorCode.ShouldBe("not_found");
        }

        // AC21: a retire without a reason is the reason field's 422, and a retired case refuses edits (§6).
        var fresh = await CreateCaseAsync(world, item);
        var noReason = await world.Client.SendAsync(
            HttpMethod.Post, $"{world.Case(fresh)}/state", world.As, new { state = "retired" }, NewKey(), "\"1\"");
        noReason.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(noReason).ShouldContain(detail => detail.Path == "/reason" && detail.Reason == "required");

        (await world.Client.SendAsync(
            HttpMethod.Post, world.Case(fresh) + "/state", world.As, new { state = "retired", reason = "duplicate" }, NewKey(), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.OK);

        var revived = await world.Client.SendAsync(
            HttpMethod.Post, world.Case(fresh) + "/state", world.As, new { state = "draft" }, NewKey(), "\"2\"");
        revived.Status.ShouldBe(HttpStatusCode.Conflict);
        revived.ErrorCode.ShouldBe("invalid_transition");
    }
}
