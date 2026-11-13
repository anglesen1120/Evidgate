using System.Net;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC6's precondition through the routes: a stale or unparsable <c>If-Match</c> is refused before anything is written,
/// <c>"0"</c> is the tag of the platform default and stops being one after the first save, and the save without its
/// <c>Idempotency-Key</c> is a client error rather than a write.
/// </summary>
/// <remarks>
/// "Nothing was written" is checked in every table this story touches — the settings rows, the audit trail and the
/// idempotency records — because the last one is the table a replayed key would otherwise leak into: the pipeline's
/// begin row rolls back with the failed transaction (01.5.1-T6).
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AiSettingsConcurrencyTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;

    public AiSettingsConcurrencyTests(PostgresTenancyFixture fixture, TenancyHostPool hosts)
    {
        _fixture = fixture;
        _hosts = hosts;
    }

    /// <summary>AC6: a stale tag is 412 <c>precondition_failed</c> naming the head, and writes nothing anywhere.</summary>
    [Fact]
    public async Task StaleIfMatch_Is412_AndWritesNothing()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);
        var head = DocumentWith("Head guidance");

        await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(head), "\"0\"");

        var storedBefore = (await AiSettingsCalls.StoredAsync(_fixture, world, 1)).ContentHash;
        var auditsBefore = await AiSettingsCalls.AuditCountAsync(_fixture, world);
        var idempotencyBefore = await AiSettingsCalls.IdempotencyCountAsync(_fixture, world);

        foreach (var stale in new[] { "\"0\"", "\"2\"", "\"99\"" })
        {
            var refused = await AiSettingsCalls.SaveAsync(
                world.Client, world.As, world, AiSettingsCalls.Body(DocumentWith("Stale write")), stale);

            refused.Status.ShouldBe(HttpStatusCode.PreconditionFailed, $"If-Match {stale} is not the head");
            refused.ErrorCode.ShouldBe("precondition_failed");

            var details = AiSettingsCalls.Details(refused);
            details.ShouldContain(
                ("version", "1"),
                $"the 412 names the document's version and the head it holds: {string.Join(", ", details)}");
        }

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(1, "a refused save appends nothing");
        (await AiSettingsCalls.StoredAsync(_fixture, world, 1)).ContentHash.ShouldBe(storedBefore, "the head is untouched");
        (await AiSettingsCalls.AuditCountAsync(_fixture, world)).ShouldBe(auditsBefore, "a refused save is not audited");
        (await AiSettingsCalls.IdempotencyCountAsync(_fixture, world))
            .ShouldBe(idempotencyBefore, "the begin row rolls back with the failure, so the key may be retried");
    }

    /// <summary>AC6: the header is required and must parse — a missing tag is <c>required</c>, junk is <c>invalid</c>.</summary>
    [Fact]
    public async Task MissingOrUnparsableIfMatch_Is422()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);
        var body = AiSettingsCalls.Body(DocumentWith("Unconditional"));

        var absent = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body);

        absent.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, "a save without If-Match is refused, not treated as upsert");
        AiSettingsCalls.Details(absent).ShouldContain(("If-Match", "required"));

        foreach (var junk in new[] { "abc", "1.5", "\"x\"", "version-1" })
        {
            var refused = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, junk);

            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"If-Match '{junk}' is not a version tag");
            AiSettingsCalls.Details(refused).ShouldContain(
                ("If-Match", "invalid"), $"the answer blames the header's pointer: {string.Join(", ", AiSettingsCalls.Details(refused))}");
        }

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(0);
        (await AiSettingsCalls.AuditCountAsync(_fixture, world)).ShouldBe(0);
    }

    /// <summary>
    /// AC6/AC13: <c>"0"</c> is the default document's tag — the first save is accepted with it, and every later save
    /// carrying it is stale because the head is now 1.
    /// </summary>
    [Fact]
    public async Task ZeroIfMatch_IsAcceptedBeforeFirstSave_AndStaleAfter()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);

        var created = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(DocumentWith("First")), "\"0\"");

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        created.ETag.ShouldBe("\"1\"");

        var again = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(DocumentWith("Second")), "\"0\"");

        again.Status.ShouldBe(HttpStatusCode.PreconditionFailed, "\"0\" tagged the default, not the saved head");
        AiSettingsCalls.Details(again).ShouldContain(("version", "1"));
        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(1);
    }

    /// <summary>
    /// AC6: a weak validator and a bare number are the tag <c>"0"</c> — <c>W/"0"</c> saves version 1 and the unquoted
    /// <c>1</c> saves version 2, because the comparison is the version, not the header spelling.
    /// </summary>
    [Fact]
    public async Task WeakAndBareIfMatch_AreTheSameVersion()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);

        var first = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(DocumentWith("Weak tag")), "W/\"0\"");

        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        first.ETag.ShouldBe("\"1\"");

        var second = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(DocumentWith("Bare tag")), "1");

        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);
        second.ETag.ShouldBe("\"2\"");
    }

    /// <summary>AC4/AC13: a save without <c>Idempotency-Key</c> is 400 <c>request_invalid</c> on the header's pointer.</summary>
    [Fact]
    public async Task MissingIdempotencyKey_Is400()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);

        var refused = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(DocumentWith("No key")), "\"0\"", withIdempotencyKey: false);

        refused.Status.ShouldBe(HttpStatusCode.BadRequest);
        refused.ErrorCode.ShouldBe("request_invalid");
        AiSettingsCalls.Details(refused).ShouldContain(("/Idempotency-Key", "required"));
        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(0);
    }

    /// <summary>The canonical document with one description — the field every case in this suite differs by.</summary>
    private static JsonObject DocumentWith(string description)
    {
        var document = AiSettingsCalls.Document();
        document["description"] = description;

        return document;
    }
}
