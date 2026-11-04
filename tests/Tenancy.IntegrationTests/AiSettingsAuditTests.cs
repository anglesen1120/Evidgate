using System.Net;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC13 through the trail: each successful save — a created version and a no-op alike — appends exactly one
/// <c>audit_log</c> row for <c>authoring.ai_settings.update</c>, carrying the before/after hashes and never a field's text,
/// and a replayed <c>Idempotency-Key</c> appends nothing because the replay wrote nothing.
/// </summary>
/// <remarks>
/// The row is read as the database stores it (<c>public.audit_log</c>), not through an export route: the trail is the
/// record and the hashes are the columns the chain commits. A refused save is asserted to leave the trail untouched in
/// the concurrency suite.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AiSettingsAuditTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AiSettingsAuditTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>AC13: the create row names the settings row, the caller and the new hash, with no text anywhere.</summary>
    [Fact]
    public async Task Save_Writes_OneRow_WithHashesOnly()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);
        var document = AiSettingsCalls.Document();
        document["description"] = "Prose that must never reach the trail";

        var created = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(document, reason: "Initial capture"), "\"0\"");

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        (await AiSettingsCalls.AuditCountAsync(_fixture, world)).ShouldBe(1, "one save, one row");

        var settingsId = created.Body.GetProperty("settings_id").GetGuid();
        var row = await LatestAsync(world);

        row.ObjectType.ShouldBe("project_ai_settings");
        row.ObjectId.ShouldBe(settingsId.ToString("D"), "the row names the version it wrote");
        row.ActorType.ShouldBe("user");
        row.ActorId.ShouldBe(world.User.ToString("D"));
        row.AfterHash.ShouldBe(
            created.Text("content_hash")!["sha256:".Length..], "the audit column holds the digest bytes, the row holds it prefixed");
        row.BeforeHash.ShouldBeNull("version 1 has no before");
        row.Metadata.ShouldContain("created");
        row.Metadata.ShouldContain("Initial capture");
        row.Metadata.ShouldNotContain("Prose that must never reach the trail", Case.Sensitive);
    }

    /// <summary>AC13/AC4: a no-op is audited once with the same hash on both sides, before and after.</summary>
    [Fact]
    public async Task NoOp_IsAudited_WithTheSameHashOnBothSides()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);
        var body = AiSettingsCalls.Body(AiSettingsCalls.Document());

        var created = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\"");
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var noop = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"1\"");
        noop.Status.ShouldBe(HttpStatusCode.OK, noop.ErrorMessage);

        (await AiSettingsCalls.AuditCountAsync(_fixture, world)).ShouldBe(2, "a no-op is a successful save");

        var row = await LatestAsync(world);

        row.Metadata.ShouldContain("noop");
        row.BeforeHash.ShouldBe(row.AfterHash, "nothing changed, so the hashes are equal");
        row.AfterHash.ShouldBe(created.Text("content_hash")!["sha256:".Length..]);
    }

    /// <summary>AC13: the original request replayed answers its stored response and appends no second row.</summary>
    [Fact]
    public async Task ReplayedKey_AuditsNothing()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);
        var key = RegistryApi.NewKey();
        var body = AiSettingsCalls.Body(AiSettingsCalls.Document());

        var first = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\"", key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        var replay = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\"", key);

        replay.Status.ShouldBe(first.Status, "a replay answers the stored response verbatim");
        replay.ETag.ShouldBe(first.ETag);
        replay.Text("content_hash").ShouldBe(first.Text("content_hash"));
        replay.Body.GetProperty("version_no").GetInt32().ShouldBe(first.Body.GetProperty("version_no").GetInt32());

        (await AiSettingsCalls.AuditCountAsync(_fixture, world)).ShouldBe(1, "a replay changed nothing, so it audits nothing");
        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(1);
    }

    /// <summary>The newest save row, as the table stores it.</summary>
    private async Task<(string ObjectType, string ObjectId, string ActorType, string ActorId, string? BeforeHash, string? AfterHash, string Metadata)> LatestAsync(
        AiSettingsCalls.World world)
    {
        var row = await _fixture.ScalarAsync<string>(
            world.Database,
            $"""
            select coalesce(object_type, '-') || '|' || coalesce(object_id, '-') || '|' || actor_type || '|' || actor_id
                   || '|' || coalesce(encode(before_hash, 'hex'), 'null') || '|' || coalesce(encode(after_hash, 'hex'), 'null')
                   || '|' || coalesce(metadata::text, '')
            from public.audit_log
            where org_id = '{world.Org}' and action = '{AiSettingsCalls.AuditAction}'
            order by seq desc limit 1
            """);

        var parts = row.Split('|', 7);

        return (parts[0], parts[1], parts[2], parts[3],
            parts[4] == "null" ? null : parts[4],
            parts[5] == "null" ? null : parts[5],
            parts[6]);
    }
}
