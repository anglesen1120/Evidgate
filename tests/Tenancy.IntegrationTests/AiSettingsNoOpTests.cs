using System.Net;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC4 through the routes: a save whose document is what the head already holds is a 200 that appends no version, its
/// identity is the document rather than the request, and returning to an older document is a new version — a revert is
/// not a rollback.
/// </summary>
/// <remarks>
/// The recurring save sends a fresh <c>Idempotency-Key</c>, so a no-op here is the content rule, not the replay rule:
/// two clients that type the same document must converge on one row.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AiSettingsNoOpTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;

    public AiSettingsNoOpTests(PostgresTenancyFixture fixture, TenancyHostPool hosts)
    {
        _fixture = fixture;
        _hosts = hosts;
    }

    /// <summary>AC4: the same document again is 200 with the head's ETag and hash, and no row is appended.</summary>
    [Fact]
    public async Task IdenticalContent_Is200_AndAddsNoVersion()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);
        var document = DocumentWith("Guidance");

        var created = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(document), "\"0\"");
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var auditsAfterCreate = await AiSettingsCalls.AuditCountAsync(_fixture, world);

        var again = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(document), "\"1\"");

        again.Status.ShouldBe(HttpStatusCode.OK, "an unchanged document is not a new version");
        again.ETag.ShouldBe("\"1\"", "the tag stays the head's");
        again.Text("content_hash").ShouldBe(created.Text("content_hash"));
        again.Body.GetProperty("version_no").GetInt32().ShouldBe(1);

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(1, "a no-op appends no row");
        (await AiSettingsCalls.AuditCountAsync(_fixture, world))
            .ShouldBe(auditsAfterCreate + 1, "a no-op is still a successful save, so it is audited once with outcome=noop");
    }

    /// <summary>AC4: two concurrent identical first saves append one row — the loser's tag has moved, so it is 412.</summary>
    [Fact]
    public async Task ConcurrentIdenticalSaves_AppendOneRow()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);
        var body = AiSettingsCalls.Body(DocumentWith("Concurrent"));

        var first = AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\"");
        var second = AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\"");
        var answers = await Task.WhenAll(first, second);

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(1, "one document is one row");
        (await AiSettingsCalls.HeadVersionAsync(_fixture, world)).ShouldBe(1);

        answers.Count(answer => answer.Status == HttpStatusCode.Created)
            .ShouldBe(1, "exactly one save creates the version");
        var loser = answers.Single(answer => answer.Status != HttpStatusCode.Created);
        loser.Status.ShouldBe(HttpStatusCode.PreconditionFailed, "the winner moved the head, so If-Match \"0\" is stale");
        loser.ErrorCode.ShouldBe("precondition_failed");
        answers.ShouldAllBe(answer => answer.Status != HttpStatusCode.InternalServerError);
    }

    /// <summary>AC3: returning to an older document appends a new version; history is never rewritten.</summary>
    [Fact]
    public async Task RevertToOlderContent_AppendsANewVersion()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);
        var first = DocumentWith("First");
        var second = DocumentWith("Second");

        await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(first), "\"0\"");
        await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(second), "\"1\"");

        var reverted = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(first), "\"2\"");

        reverted.Status.ShouldBe(HttpStatusCode.Created, "a revert is a new version, not a rollback");
        reverted.Body.GetProperty("version_no").GetInt32().ShouldBe(3);
        reverted.Text("content_hash").ShouldBe(AiSettingsCalls.HashOf(first));

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(3);
        (await AiSettingsCalls.StoredAsync(_fixture, world, 2)).Document["description"]!.GetValue<string>()
            .ShouldBe("Second", "the version it reverts still reads as it was written");
    }

    /// <summary>The canonical document with one description — the field every case in this suite differs by.</summary>
    private static JsonObject DocumentWith(string description)
    {
        var document = AiSettingsCalls.Document();
        document["description"] = description;

        return document;
    }
}
