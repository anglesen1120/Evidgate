using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC2/AC3/AC6 through the routes: every save appends exactly one version whose number is the previous head plus one,
/// the document the row holds hashes to the row's <c>content_hash</c>, the history lists newest first without the
/// document, and the detail returns the version as it was written.
/// </summary>
/// <remarks>
/// The hash is recomputed here from the row's own columns (01.3.2's RFC 8785 canonicalisation), so a save that stored a
/// different document than the one it hashed fails — the response body alone cannot prove that.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AiSettingsVersionTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AiSettingsVersionTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>AC2/AC3: one save, one row, numbered from 1, hashed over the stored document and attributed to the caller.</summary>
    [Fact]
    public async Task Appends_ExactlyOneVersion_PerSave()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);

        var first = DocumentWith("First guidance");
        var created = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(first), "\"0\"");

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        created.Body.GetProperty("version_no").GetInt32().ShouldBe(1, "the first save is version 1");
        created.Text("source").ShouldBe("saved");
        created.ETag.ShouldBe("\"1\"");
        created.Text("content_hash").ShouldBe(AiSettingsCalls.HashOf(first));

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(1);

        var stored = await AiSettingsCalls.StoredAsync(_fixture, world, 1);
        var founder = await AiSettingsCalls.MembershipAsync(_fixture, world, world.User);

        stored.ContentHash.ShouldBe(AiSettingsCalls.HashOf(stored.Document), "the hash covers the stored document");
        stored.Document.ToJsonString().ShouldBe(first.ToJsonString());
        stored.AuthorMembershipId.ShouldBe(founder, "the row records the saver's membership");
        stored.Reason.ShouldBeNull("no reason was sent");

        var second = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(DocumentWith("Second guidance"), reason: "Review feedback"), "\"1\"");

        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);
        second.Body.GetProperty("version_no").GetInt32().ShouldBe(2, "the next version is the head plus one");
        second.ETag.ShouldBe("\"2\"");

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(2);

        var historyFirst = await AiSettingsCalls.StoredAsync(_fixture, world, 1);
        historyFirst.ContentHash.ShouldBe(AiSettingsCalls.HashOf(first), "the earlier version is not rewritten");
        (await AiSettingsCalls.StoredAsync(_fixture, world, 2)).Reason.ShouldBe("Review feedback");

        (await AiSettingsCalls.HeadVersionAsync(_fixture, world)).ShouldBe(2);
    }

    /// <summary>AC3: the history detail returns the version as written, with its own ETag, and 404s an unknown number.</summary>
    [Fact]
    public async Task HistoryDetail_Returns_TheStoredVersion()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);
        var first = DocumentWith("First guidance");
        var second = DocumentWith("Second guidance");

        await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(first), "\"0\"");
        await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(second), "\"1\"");

        var one = await AiSettingsCalls.GetAsync(world.Client, world.As, world.VersionUrl(1));
        one.Status.ShouldBe(HttpStatusCode.OK, one.ErrorMessage);
        one.ETag.ShouldBe("\"1\"");
        one.Body.GetProperty("version_no").GetInt32().ShouldBe(1);
        AiSettingsCalls.SettingsOf(one).ToJsonString().ShouldBe(first.ToJsonString());

        var two = await AiSettingsCalls.GetAsync(world.Client, world.As, world.VersionUrl(2));
        two.Status.ShouldBe(HttpStatusCode.OK, two.ErrorMessage);
        two.ETag.ShouldBe("\"2\"");
        two.Body.GetProperty("version_no").GetInt32().ShouldBe(2);
        AiSettingsCalls.SettingsOf(two).ToJsonString().ShouldBe(second.ToJsonString());

        var missing = await AiSettingsCalls.GetAsync(world.Client, world.As, world.VersionUrl(99));
        missing.Status.ShouldBe(HttpStatusCode.NotFound);
        missing.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>AC6: the list is newest first, carries the summary fields only, and its cursor pages without overlap.</summary>
    [Fact]
    public async Task VersionPage_Lists_NewestFirst_WithoutTheDocument()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);

        for (var i = 0; i < 3; i++)
        {
            var saved = await AiSettingsCalls.SaveAsync(
                world.Client, world.As, world, AiSettingsCalls.Body(DocumentWith($"Guidance {i}")), $"\"{i}\"");
            saved.Status.ShouldBe(HttpStatusCode.Created, saved.ErrorMessage);
        }

        var page = await AiSettingsCalls.GetAsync(world.Client, world.As, world.Versions);
        page.Status.ShouldBe(HttpStatusCode.OK, page.ErrorMessage);

        var items = page.Body.GetProperty("items").EnumerateArray().ToList();
        items.Select(item => item.GetProperty("version_no").GetInt32()).ShouldBe([3, 2, 1]);
        page.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null, "three versions fit the default page");

        foreach (var item in items)
        {
            item.TryGetProperty("settings", out _).ShouldBeFalse("a summary never carries the document");
            item.GetProperty("content_hash").GetString().ShouldNotBeNullOrWhiteSpace();
            item.GetProperty("author_membership_id").GetGuid().ShouldBe(await AiSettingsCalls.MembershipAsync(_fixture, world, world.User));
            item.GetProperty("created_at").GetString().ShouldNotBeNullOrWhiteSpace();
            item.GetProperty("template").ValueKind.ShouldBe(JsonValueKind.Null, "no template was named");
            item.GetProperty("reason").ValueKind.ShouldBe(JsonValueKind.Null, "no reason was sent");
        }

        var firstPage = await AiSettingsCalls.GetAsync(world.Client, world.As, $"{world.Versions}?limit=2");
        var cursor = firstPage.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace("a page that fills its limit with rows left over has a cursor");

        var secondPage = await AiSettingsCalls.GetAsync(world.Client, world.As, $"{world.Versions}?limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        secondPage.Status.ShouldBe(HttpStatusCode.OK, secondPage.ErrorMessage);
        secondPage.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("version_no").GetInt32())
            .ShouldBe([1]);
        secondPage.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>AC6: an out-of-range limit is 422, a non-integer limit is 422 on its own pointer, and an unissued cursor is 422.</summary>
    [Fact]
    public async Task VersionPage_Refuses_BadLimitAndCursor()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);

        (await AiSettingsCalls.GetAsync(world.Client, world.As, $"{world.Versions}?limit=201")).Status
            .ShouldBe(HttpStatusCode.UnprocessableEntity, "200 is the maximum page size");

        var malformed = await AiSettingsCalls.GetAsync(world.Client, world.As, $"{world.Versions}?limit=abc");
        malformed.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, "a non-integer limit is a validation failure, not a 400");
        malformed.ErrorCode.ShouldBe("request_invalid");
        AiSettingsCalls.Details(malformed).ShouldContain(
            ("/limit", "out_of_range"), $"the answer blames the query member: {string.Join(", ", AiSettingsCalls.Details(malformed))}");

        var garbage = await AiSettingsCalls.GetAsync(world.Client, world.As, $"{world.Versions}?cursor=not-a-cursor");
        garbage.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, "an unissued cursor is refused");
        garbage.ErrorCode.ShouldBe("request_invalid");
    }

    /// <summary>The canonical document with one description — the field every version in this suite differs by.</summary>
    private static JsonObject DocumentWith(string description)
    {
        var document = AiSettingsCalls.Document();
        document["description"] = description;

        return document;
    }
}
