using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// OI-13-116, the domain half of the hostile-name guard: a member <b>name</b> carrying a lone surrogate
/// (<c>{"\ud800":1}</c>, at any depth, <c>steps</c> included) is a caller error — a <c>request_invalid</c> problem row with
/// a stable path — never the reader's <c>InvalidOperationException</c> surfacing as a 500. String <b>values</b> were
/// already guarded (<c>AuthoringContent.Compose</c> and the <c>steps</c> leaf walk); these are the name sites, which the
/// writer calls for every route and for the 13.1.1-T5 import. No database: <c>AuthoringContent.Parse</c> is pure.
/// </summary>
public sealed class AuthoringContentSurrogateTests
{
    /// <summary>A lone surrogate in a member name at the document's own level is refused, and the raw name is never echoed.</summary>
    [Fact]
    public void ContentParse_RefusesLoneSurrogateMemberNameAtTheRoot()
    {
        // The escape \ud800 is valid JSON text; reading the resulting name is what the reader cannot do.
        var details = Refuse(
            "{\"kind\":\"module\",\"title\":\"Shop\",\"position\":0,\"parent_id\":null,\"\\ud800\":1}",
            AuthoringEntityKind.FeatureNode);

        details.ShouldHaveSingleItem().ShouldBe((AuthoringContent.InvalidMemberNamePath, "invalid_text"));

        // No unpaired surrogate reaches the answer, so serialising the 422 cannot throw a second time.
        details.ShouldAllBe(detail => !detail.Path.Any(char.IsSurrogate));
    }

    /// <summary>
    /// A name that only becomes unstorable after unescaping still reaches the guard: `TryGetProperty` unescapes a stored
    /// name to compare it with an ASCII member, so a probe on <c>"ti\ud800tle"</c> would throw before any name scan that
    /// ran after the required-member check. The name scan is therefore first, and this document is refused, not thrown.
    /// </summary>
    [Fact]
    public void ContentParse_RefusesAnUnstorableNameBeforeAnyNameProbe()
    {
        var details = Refuse(
            "{\"ti\\ud800tle\":1,\"kind\":\"module\",\"title\":\"Shop\",\"position\":0,\"parent_id\":null}",
            AuthoringEntityKind.FeatureNode);

        details.ShouldHaveSingleItem().ShouldBe((AuthoringContent.InvalidMemberNamePath, "invalid_text"));

        // The same when the unstorable name is last, and when it shares the longest ASCII prefix with a member.
        Refuse(
            "{\"kind\":\"module\",\"ti\\ud800tle\":1,\"title\":\"Shop\",\"position\":0,\"parent_id\":null}",
            AuthoringEntityKind.FeatureNode)
            .ShouldHaveSingleItem().ShouldBe((AuthoringContent.InvalidMemberNamePath, "invalid_text"));
        Refuse(
            "{\"kin\\ud800\":1,\"kind\":\"module\",\"title\":\"Shop\",\"position\":0,\"parent_id\":null}",
            AuthoringEntityKind.FeatureNode)
            .ShouldHaveSingleItem().ShouldBe((AuthoringContent.InvalidMemberNamePath, "invalid_text"));
    }

    /// <summary>The same name one level down, inside a step object: <c>steps</c> is the only nested object the parser reads.</summary>
    [Fact]
    public void ContentParse_RefusesLoneSurrogateMemberNameInsideSteps()
    {
        var details = Refuse(
            StepsCase("{\"steps\":[{\"no\":1,\"action\":\"Open\",\"expected\":\"Shown\",\"\\ud800\":1}]}"),
            AuthoringEntityKind.ManualTestCase);

        details.ShouldContain(("/content/steps", "invalid_text"));
    }

    /// <summary>A third level: a surrogate name inside the free-form <c>data</c> member of a step.</summary>
    [Fact]
    public void ContentParse_RefusesLoneSurrogateMemberNameDeepInsideSteps()
    {
        var details = Refuse(
            StepsCase("{\"steps\":[{\"no\":1,\"action\":\"Open\",\"expected\":\"Shown\",\"data\":{\"\\ud800\":1}}]}"),
            AuthoringEntityKind.ManualTestCase);

        details.ShouldContain(("/content/steps", "invalid_text"));
    }

    /// <summary>
    /// The 422 the writer builds from those problem rows: <c>request_invalid</c>, status 422, one detail under the change
    /// path — the shape 13.1.1-T2's routes and the import surface, with no 500 anywhere.
    /// </summary>
    [Fact]
    public void Refusal_MapsToThe422RequestInvalidShape()
    {
        using var document = JsonDocument.Parse(
            "{\"kind\":\"module\",\"title\":\"Shop\",\"position\":0,\"parent_id\":null,\"\\ud800\":1}");
        AuthoringContent.Parse(AuthoringEntityKind.FeatureNode, document.RootElement, out _, out var details)
            .ShouldBeFalse();

        // Exactly the mapping AuthoringVersionWriter.AppendCoreAsync applies to a refused content object.
        var error = AuthoringErrors.InvalidMany(
            "the change is not a valid snapshot of the entity",
            [.. details.Select(detail => new ErrorDetail("/changes/0" + detail.Path, detail.Reason))]);

        error.Code.ShouldBe("request_invalid");
        error.Status.ShouldBe(422);
        error.Retryable.ShouldBeFalse();
        error.Details.ShouldNotBeNull();
        error.Details!.ShouldHaveSingleItem()
            .ShouldBe(new ErrorDetail("/changes/0/content/<invalid-member-name>", "invalid_text"));
    }

    /// <summary>A storable astral-plane name is not "invalid text": it reads fine and is refused as an unknown member.</summary>
    [Fact]
    public void ContentParse_EchoesAValidAstralPlaneNameAsUnknownMember()
    {
        // U+1F600, the pair System.Text.Json stores for it.
        using var document = JsonDocument.Parse(
            "{\"kind\":\"module\",\"title\":\"Shop\",\"position\":0,\"parent_id\":null,\"\ud83d\ude00\":1}");
        AuthoringContent.Parse(AuthoringEntityKind.FeatureNode, document.RootElement, out var snapshot, out var details)
            .ShouldBeFalse();

        snapshot.ShouldBeNull();
        details.ShouldHaveSingleItem().ShouldBe(("/content/\ud83d\ude00", "unknown_member"));
    }

    /// <summary>A valid astral-plane string value still parses into the snapshot.</summary>
    [Fact]
    public void ContentParse_AcceptsAValidAstralPlaneTitle()
    {
        using var document = JsonDocument.Parse(
            "{\"kind\":\"module\",\"title\":\"Shop \ud83d\ude00\",\"position\":0,\"parent_id\":null}");
        AuthoringContent.Parse(AuthoringEntityKind.FeatureNode, document.RootElement, out var snapshot, out var details)
            .ShouldBeTrue();

        details.ShouldBeEmpty();
        snapshot.ShouldBeOfType<FeatureNodeSnapshot>().Title.ShouldBe("Shop \ud83d\ude00");
    }

    /// <summary>A complete case document with one substituted <c>steps</c> object, so only the name guard differs.</summary>
    /// <param name="steps">The steps document.</param>
    private static string StepsCase(string steps) =>
        "{\"checklist_item_id\":\"" + Guid.CreateVersion7().ToString("D") + "\",\"code\":\"SHOP-001\","
        + "\"title\":\"Pay with a card\",\"description\":null,\"precondition\":null,"
        + "\"steps\":" + steps + ",\"priority\":\"high\",\"state\":\"draft\"}";

    /// <summary>Parses one document that must be refused without throwing, and returns the problem rows.</summary>
    /// <param name="json">The raw content document.</param>
    /// <param name="kind">The entity the content describes.</param>
    private static IReadOnlyList<(string Path, string Reason)> Refuse(string json, AuthoringEntityKind kind)
    {
        using var document = JsonDocument.Parse(json);
        IReadOnlyList<(string Path, string Reason)>? details = null;
        var thrown = Record.Exception(() =>
            AuthoringContent.Parse(kind, document.RootElement, out _, out details).ShouldBeFalse());

        thrown.ShouldBeNull("a lone surrogate in a member name is a caller error, never an exception");
        details.ShouldNotBeNull();
        details!.ShouldAllBe(detail => !detail.Path.Any(char.IsSurrogate));

        return details;
    }
}
