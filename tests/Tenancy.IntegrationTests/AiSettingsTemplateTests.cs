using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC5's catalogue through the routes: the platform templates are read-only, bilingual, carry exactly one default per
/// locale, and a save that names a template version records that provenance as a copy — the saved document is the one
/// the client sent, so a later template is a different document, not a live reference.
/// </summary>
/// <remarks>
/// The catalogue is a platform table with no tenant column, so the only way to write it is a migration: the route refuses
/// every write verb, and the app role holds no write grant on the table at all.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AiSettingsTemplateTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;

    public AiSettingsTemplateTests(PostgresTenancyFixture fixture, TenancyHostPool hosts)
    {
        _fixture = fixture;
        _hosts = hosts;
    }

    /// <summary>AC5: the catalogue answers both locales with one default each and all eight fields in every document.</summary>
    [Fact]
    public async Task Catalogue_Is_Bilingual_WithOneDefaultPerLocale()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);

        var catalogue = await AiSettingsCalls.GetAsync(world.Client, world.As, world.Templates);

        catalogue.Status.ShouldBe(HttpStatusCode.OK, catalogue.ErrorMessage);

        var items = catalogue.Body.GetProperty("items").EnumerateArray().ToList();
        items.ShouldNotBeEmpty();

        foreach (var locale in new[] { "en", "vi" })
        {
            var ofLocale = items.Where(item => item.GetProperty("locale").GetString() == locale).ToList();
            ofLocale.ShouldNotBeEmpty($"the catalogue carries a {locale} template");
            ofLocale.Count(item => item.GetProperty("is_default").GetBoolean())
                .ShouldBe(1, $"exactly one {locale} template is the default");
        }

        foreach (var item in items)
        {
            item.GetProperty("code").GetString().ShouldNotBeNullOrWhiteSpace();
            item.GetProperty("version").GetInt32().ShouldBeGreaterThan(0);
            item.GetProperty("name").GetString().ShouldNotBeNullOrWhiteSpace();
            item.GetProperty("content_hash").GetString()!.ShouldStartWith("sha256:");
            item.GetProperty("content_hash").GetString()!.Length.ShouldBe(71, "sha256: plus 64 hex");

            var document = item.GetProperty("settings");
            document.EnumerateObject().Select(member => member.Name).ShouldBe(
            [
                "description", "environment_url", "languages", "writing_style", "coverage_strategy",
                "priority_criteria", "exclusion_rules", "step_detail",
            ], ignoreOrder: true);
        }
    }

    /// <summary>
    /// AC5/AC11: every seeded template document is storable exactly as published — the migration's own seed text must
    /// pass the save's validation, so a seeded value that looks like a credential is a migration defect.
    /// </summary>
    [Fact]
    public async Task SeededTemplates_SaveTheirOwnDocument_Unchanged()
    {
        // Private: this world outlives the loop, so a lease would defeat the pool for every save world.
        await using var catalogueWorld = await AiSettingsCalls.StartAsync(_fixture);

        var catalogue = await AiSettingsCalls.GetAsync(
            catalogueWorld.Client, catalogueWorld.As, catalogueWorld.Templates);

        catalogue.Status.ShouldBe(HttpStatusCode.OK, catalogue.ErrorMessage);

        var items = catalogue.Body.GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBe(4, "the migration seeds two templates in two locales");

        foreach (var item in items)
        {
            var label = $"{item.GetProperty("code").GetString()}/{item.GetProperty("locale").GetString()}"
                + $" v{item.GetProperty("version").GetInt32()}";
            var settings = JsonNode.Parse(item.GetProperty("settings").GetRawText())!.AsObject();

            await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);
            var saved = await AiSettingsCalls.SaveAsync(
                world.Client, world.As, world, AiSettingsCalls.Body(settings), "\"0\"");

            saved.Status.ShouldBeOneOf(
                [HttpStatusCode.Created, HttpStatusCode.OK], $"{label} must be storable unchanged: {saved.ErrorMessage}");
            saved.Text("content_hash").ShouldBe(AiSettingsCalls.HashOf(settings), label);
        }
    }

    /// <summary>AC5: the catalogue has no write verb — a POST or PUT on it is not a route these callers can reach.</summary>
    [Fact]
    public async Task Catalogue_RefusesWrites()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            var refused = await AiSettingsCalls.SendAsync(
                world.Client, world.As, method, world.Templates, AiSettingsCalls.Body(AiSettingsCalls.Document()), "\"0\"");

            refused.Status.ShouldBeOneOf(
                [HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound],
                $"{method} on the catalogue is not a served operation");
        }
    }

    /// <summary>
    /// AC5: a save naming a template version stores its own document and records the pair; the stored document follows
    /// the request, not the template — which is what makes the provenance a copy rather than a reference.
    /// </summary>
    [Fact]
    public async Task CopyingATemplate_IsNotALiveReference()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);

        var template = AiSettingsCalls.Document();
        template["description"] = "Edited after the copy";

        var copied = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(template, AiSettingsCalls.Template("default", 1)), "\"0\"");

        copied.Status.ShouldBe(HttpStatusCode.Created, copied.ErrorMessage);
        copied.Body.GetProperty("template").GetProperty("code").GetString().ShouldBe("default");
        copied.Body.GetProperty("template").GetProperty("version").GetInt32().ShouldBe(1);

        var stored = await AiSettingsCalls.StoredAsync(_fixture, world, 1);

        stored.TemplateCode.ShouldBe("default");
        stored.TemplateVersion.ShouldBe(1);
        stored.Document["description"]!.GetValue<string>().ShouldBe(
            "Edited after the copy", "the row holds the caller's document, not the template's");

        var detail = await AiSettingsCalls.GetAsync(world.Client, world.As, world.VersionUrl(1));

        detail.Body.GetProperty("template").GetProperty("version").GetInt32()
            .ShouldBe(1, "the provenance is the version that was copied");
    }

    /// <summary>AC5: a code or a version the catalogue does not hold is refused on the pair's own pointer.</summary>
    [Fact]
    public async Task UnknownTemplate_Is422_OnThePair()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);

        foreach (var pair in new[] { AiSettingsCalls.Template("no-such-template", 1), AiSettingsCalls.Template("default", 999) })
        {
            var refused = await AiSettingsCalls.SaveAsync(
                world.Client, world.As, world, AiSettingsCalls.Body(AiSettingsCalls.Document(), pair), "\"0\"");

            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, pair.ToJsonString());
            AiSettingsCalls.Details(refused).ShouldContain(
                ("/template/code", "not_allowed_value"),
                $"an unknown pair is refused on the pair: {string.Join(", ", AiSettingsCalls.Details(refused))}");
        }

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(0);
    }
}
