using System.Net;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class AiSettingsRequestShapeTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) : IClassFixture<TenancyHostPool>
{
    [Theory]
    [InlineData("root", "/unexpected")]
    [InlineData("settings", "/settings/unexpected")]
    [InlineData("criteria", "/settings/priority_criteria/unexpected")]
    [InlineData("template", "/template/unexpected")]
    public async Task Save_UnknownMember_RefusesTheExactPointer(string location, string path)
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture, hosts);
        var body = ValidBody();
        var parent = location switch
        {
            "root" => body,
            "settings" => body["settings"]!.AsObject(),
            "criteria" => body["settings"]!["priority_criteria"]!.AsObject(),
            "template" => body["template"]!.AsObject(),
            _ => throw new InvalidOperationException("unknown test location"),
        };
        parent["unexpected"] = "ignored text would lose user guidance";
        var before = await AiSettingsCalls.IdempotencyCountAsync(fixture, world);

        var refused = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\"");

        AiSettingsCalls.ShouldRefuse(refused, path, "unknown_field");
        await AssertNoWritesAsync(world, before);
        parent.Remove("unexpected").ShouldBeTrue();
        (await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\""))
            .Status.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("1.5")]
    [InlineData("3000000000")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task TemplateVersion_NonPositiveOrNonInteger_Is422Not500(string json)
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture, hosts);
        var body = ValidBody();
        body["template"]!["version"] = JsonNode.Parse(json);
        var before = await AiSettingsCalls.IdempotencyCountAsync(fixture, world);

        var refused = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\"");

        AiSettingsCalls.ShouldRefuse(refused, "/template/version", "invalid");
        await AssertNoWritesAsync(world, before);
        body["template"]!["version"] = 1;
        (await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\""))
            .Status.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("[\"en\",\"en\"]", "duplicate_value")]
    [InlineData("[\"vi\",\"vi\"]", "duplicate_value")]
    [InlineData("[\"en\",\"vi\",\"en\"]", "duplicate_value")]
    [InlineData("[\"en\",\" en \"]", "duplicate_value")]
    [InlineData("[]", "required")]
    [InlineData("[\"fr\"]", "not_allowed_value")]
    [InlineData("[\"en\",null]", "invalid")]
    [InlineData("null", "invalid")]
    public async Task Languages_InvalidSet_Is422WithoutWrites(string json, string reason)
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture, hosts);
        var body = ValidBody();
        body["settings"]!["languages"] = JsonNode.Parse(json);
        var before = await AiSettingsCalls.IdempotencyCountAsync(fixture, world);

        var refused = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\"");

        AiSettingsCalls.ShouldRefuse(refused, "/settings/languages", reason);
        await AssertNoWritesAsync(world, before);
        body["settings"]!["languages"] = new JsonArray("en");
        (await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\""))
            .Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Languages_ReorderedUniqueSet_HasTheSameHashAndNoNewVersion()
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture, hosts);
        var body = ValidBody();
        body["settings"]!["languages"] = new JsonArray("vi", "en");
        var first = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\"");

        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        AiSettingsCalls.LanguagesOf(first).ShouldBe(["en", "vi"]);
        body["settings"]!["languages"] = new JsonArray("en", "vi");
        var reordered = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"1\"");

        reordered.Status.ShouldBe(HttpStatusCode.OK, reordered.ErrorMessage);
        reordered.Text("content_hash").ShouldBe(first.Text("content_hash"));
        reordered.ETag.ShouldBe(first.ETag);
        (await AiSettingsCalls.SettingsCountAsync(fixture, world)).ShouldBe(1);
    }

    [Theory]
    [InlineData("description")]
    [InlineData("environment_url")]
    [InlineData("priority_criteria")]
    [InlineData("reason")]
    public async Task Save_NulInText_IsRefusedBeforePersistence(string field)
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture, hosts);
        var body = ValidBody();
        var path = field switch
        {
            "reason" => "/reason",
            "priority_criteria" => "/settings/priority_criteria/critical",
            _ => "/settings/" + field,
        };
        var parent = field switch
        {
            "reason" => body,
            "priority_criteria" => body["settings"]!["priority_criteria"]!.AsObject(),
            _ => body["settings"]!.AsObject(),
        };
        var member = field == "priority_criteria" ? "critical" : field;
        parent[member] = field == "environment_url" ? "https://example.test/\0" : "guidance\0text";
        var before = await AiSettingsCalls.IdempotencyCountAsync(fixture, world);
        var refused = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\"");

        AiSettingsCalls.ShouldRefuse(refused, path, "invalid_text");
        await AssertNoWritesAsync(world, before);
        parent[member] = field == "environment_url" ? "https://example.test/" : "guidance text";
        (await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\""))
            .Status.ShouldBe(HttpStatusCode.Created);
    }

    private static JsonObject ValidBody()
    {
        var document = AiSettingsCalls.Document();
        document["description"] = "Checkout guidance";
        return AiSettingsCalls.Body(document, AiSettingsCalls.Template("default", 1));
    }

    private async Task AssertNoWritesAsync(AiSettingsCalls.World world, long idempotencyBefore)
    {
        (await AiSettingsCalls.SettingsCountAsync(fixture, world)).ShouldBe(0);
        (await AiSettingsCalls.AuditCountAsync(fixture, world)).ShouldBe(0);
        (await AiSettingsCalls.IdempotencyCountAsync(fixture, world)).ShouldBe(idempotencyBefore);
    }
}
