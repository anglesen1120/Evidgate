using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC5 through the routes: a project with no saved version answers the platform default template as version 0 tagged
/// <c>"0"</c>, the first save with <c>If-Match: "0"</c> turns that into version 1, and the default the head answers does
/// not depend on the request's language.
/// </summary>
/// <remarks>
/// Version 0 is a projection of the catalogue, not a row: <c>settings_id</c> is null and the source says so. The
/// template's own values are compared against the catalogue's default entry for the same locale, which is what makes the
/// projection a real read of the seeded row rather than an empty document.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AiSettingsDefaultsTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AiSettingsDefaultsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>AC5: version 0 is the default template's document, with no row and no author behind it.</summary>
    [Fact]
    public async Task VersionZero_IsThePlatformDefaultTemplate()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);

        var head = await AiSettingsCalls.GetAsync(world.Client, world.As, world.Settings);

        head.Status.ShouldBe(HttpStatusCode.OK, head.ErrorMessage);
        head.ETag.ShouldBe("\"0\"", "the default is the tag a first save quotes");
        head.Body.GetProperty("version_no").GetInt32().ShouldBe(0);
        head.Text("source").ShouldBe("default_template");
        head.Body.GetProperty("settings_id").ValueKind.ShouldBe(JsonValueKind.Null, "version 0 has no row");

        var catalogue = await AiSettingsCalls.GetAsync(world.Client, world.As, world.Templates);
        catalogue.Status.ShouldBe(HttpStatusCode.OK, catalogue.ErrorMessage);

        var english = catalogue.Body.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("code").GetString() == "default"
                && item.GetProperty("locale").GetString() == "en"
                && item.GetProperty("is_default").GetBoolean());

        head.Text("content_hash").ShouldBe(english.GetProperty("content_hash").GetString());
        AiSettingsCalls.SettingsOf(head).ToJsonString()
            .ShouldBe(english.GetProperty("settings").GetRawText(), "the projection is the template's own document");
        AiSettingsCalls.LanguagesOf(head).ShouldBe(["en", "vi"], "the seeded default carries both languages");

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(0, "reading the default writes nothing");
    }

    /// <summary>AC5/AC7: the first save quotes <c>"0"</c> and lands as version 1 with the source set to <c>saved</c>.</summary>
    [Fact]
    public async Task FirstSave_FromVersionZero_IsVersionOne()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);
        var before = await AiSettingsCalls.GetAsync(world.Client, world.As, world.Settings);

        var saved = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(AiSettingsCalls.Document()), before.ETag!);

        saved.Status.ShouldBe(HttpStatusCode.Created, saved.ErrorMessage);
        saved.Body.GetProperty("version_no").GetInt32().ShouldBe(1);
        saved.Text("source").ShouldBe("saved");
        saved.Body.GetProperty("settings_id").GetGuid().ShouldNotBe(Guid.Empty);
        saved.ETag.ShouldBe("\"1\"");

        var head = await AiSettingsCalls.GetAsync(world.Client, world.As, world.Settings);
        head.ETag.ShouldBe("\"1\"", "the head now reads the saved version");
        head.Text("source").ShouldBe("saved");
    }

    /// <summary>AC5: the same project answers the same default to a Vietnamese and an English request.</summary>
    [Fact]
    public async Task Defaults_Are_LocaleIndependent()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);

        Action<System.Net.Http.Headers.HttpRequestHeaders> In(string language) => headers =>
        {
            world.As(headers);
            headers.TryAddWithoutValidation("Accept-Language", language);
        };

        var vietnamese = await AiSettingsCalls.GetAsync(world.Client, In("vi-VN"), world.Settings);
        var english = await AiSettingsCalls.GetAsync(world.Client, In("en-US"), world.Settings);

        vietnamese.Status.ShouldBe(HttpStatusCode.OK, vietnamese.ErrorMessage);
        vietnamese.Body.GetRawText().ShouldBe(
            english.Body.GetRawText(), "the address's language selects nothing: the document is stored, not rendered");
    }
}
