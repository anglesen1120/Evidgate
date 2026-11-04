using System.Net;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC1's length unit and AC1/AC14's normalisation through the route: a cap counts code points (an astral emoji is one),
/// NFC composition and the leading/trailing trim happen before the cap and before the hash, so a decomposed or padded
/// spelling of a stored document is the same document — same <c>content_hash</c>, one version, a 200 no-op.
/// </summary>
/// <remarks>
/// Normalisation is also the probe that the Api host really has ICU: with invariant globalization the composition is a
/// silent no-op, and the decomposed save below would store two code points per accented letter and hash the decomposed
/// text. This suite therefore pins the behaviour where it matters — on the stored row — not on a helper.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AiSettingsTextTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AiSettingsTextTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>AC1: the unit is the code point, so 250 emoji fit the 250-cap and 251 do not.</summary>
    [Fact]
    public async Task CodePoints_AreTheLengthUnit()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);

        var over = AiSettingsCalls.Document();
        over["writing_style"] = string.Concat(Enumerable.Repeat("\U0001F600", AiSettingsCalls.WritingStyleLimit + 1));

        AiSettingsCalls.ShouldRefuse(
            await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(over), "\"0\""),
            "/settings/writing_style",
            "too_long");

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(0);

        var atLimit = AiSettingsCalls.Document();
        atLimit["writing_style"] = string.Concat(Enumerable.Repeat("\U0001F600", AiSettingsCalls.WritingStyleLimit));

        var accepted = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(atLimit), "\"0\"");

        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);

        var stored = await AiSettingsCalls.StoredAsync(_fixture, world, 1);
        var storedText = stored.Document["writing_style"]!.GetValue<string>();

        storedText.ShouldBe(atLimit["writing_style"]!.GetValue<string>(), "the surrogate pairs are stored whole");
        storedText.Length.ShouldBe(AiSettingsCalls.WritingStyleLimit * 2, "250 code points are 500 UTF-16 units");
        storedText.EnumerateRunes().Count().ShouldBe(AiSettingsCalls.WritingStyleLimit);
    }

    /// <summary>
    /// AC1/AC14: a decomposed Vietnamese value is stored composed and hashes as the composed text, the ends are trimmed,
    /// and re-saving the composed spelling is a 200 no-op — normalisation is stable and happens before the hash.
    /// </summary>
    [Fact]
    public async Task Normalisation_ComposesNfc_TrimsEnds_AndHashesTheNormalisedForm()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);
        const string Composed = "\u00e1";
        const string Decomposed = "a\u0301";
        var composedText = string.Concat(Enumerable.Repeat(Composed, AiSettingsCalls.WritingStyleLimit));
        var decomposedText = string.Concat(Enumerable.Repeat(Decomposed, AiSettingsCalls.WritingStyleLimit));

        var document = AiSettingsCalls.Document();
        document["writing_style"] = decomposedText;
        document["description"] = "  Guidance: check the cart total  ";

        var created = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(document), "\"0\"");

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        created.Text("source").ShouldBe("saved");

        var stored = await AiSettingsCalls.StoredAsync(_fixture, world, 1);

        stored.Document["writing_style"]!.GetValue<string>().ShouldBe(
            composedText, "the decomposed pairs are composed before they are stored");
        stored.Document["description"]!.GetValue<string>().ShouldBe(
            "Guidance: check the cart total", "leading and trailing whitespace is trimmed, the inside is kept");

        var expected = AiSettingsCalls.Document();
        expected["writing_style"] = composedText;
        expected["description"] = "Guidance: check the cart total";

        stored.ContentHash.ShouldBe(
            AiSettingsCalls.HashOf(expected), "the stored hash covers the normalised document, not the wire form");
        created.Text("content_hash").ShouldBe(stored.ContentHash);

        // The composed spelling of the same document is the same document: 200, the same ETag, no second row.
        var replay = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(expected), "\"1\"");

        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.ETag.ShouldBe("\"1\"");
        replay.Text("content_hash").ShouldBe(stored.ContentHash);
        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(1);
    }
}
