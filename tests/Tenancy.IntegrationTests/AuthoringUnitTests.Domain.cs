using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T1.3, the domain half: the kind ladder and its refusals (AC1), the §6 state machines (AC20/AC21), the
/// <c>content</c> snapshot and its hash (AC5), the sibling-order hash 13.1.2 shares (H6) and the manual case code (§4,
/// AC4). No database: every rule here is pure.
/// </summary>
public sealed partial class AuthoringUnitTests
{
    private const string Steps = """{"steps":[{"no":1,"action":"Open the cart","expected":"The cart is shown"}]}""";

    // ------------------------------------------------------------------ AC1: the kind ladder

    [Fact]
    public void KindLadder_AcceptsAndRefuses()
    {
        var module = new FeatureNodeParent(Guid.CreateVersion7(), FeatureNodeKinds.Module, Deleted: false, SameProject: true);
        var feature = new FeatureNodeParent(Guid.CreateVersion7(), FeatureNodeKinds.Feature, Deleted: false, SameProject: true);
        var category = new FeatureNodeParent(Guid.CreateVersion7(), FeatureNodeKinds.Category, Deleted: false, SameProject: true);
        var subCategory = new FeatureNodeParent(Guid.CreateVersion7(), FeatureNodeKinds.SubCategory, Deleted: false, SameProject: true);

        // The accepted ladder: module at the root, then one kind per level.
        FeatureNodeRules.CheckPlacement(FeatureNodeKinds.Module, null).IsAccepted.ShouldBeTrue();
        FeatureNodeRules.CheckPlacement(FeatureNodeKinds.Feature, module).IsAccepted.ShouldBeTrue();
        FeatureNodeRules.CheckPlacement(FeatureNodeKinds.Category, feature).IsAccepted.ShouldBeTrue();
        FeatureNodeRules.CheckPlacement(FeatureNodeKinds.SubCategory, category).IsAccepted.ShouldBeTrue();

        // A non-module at the root names no parent, which is the kind ladder's answer, not a missing row.
        FeatureNodeRules.CheckPlacement(FeatureNodeKinds.Feature, null).Reason.ShouldBe(PlacementReason.ParentKindInvalid);
        FeatureNodeRules.CheckPlacement(FeatureNodeKinds.Module, module).Reason.ShouldBe(PlacementReason.ParentKindInvalid);
        FeatureNodeRules.CheckPlacement(FeatureNodeKinds.Category, module).Reason.ShouldBe(PlacementReason.ParentKindInvalid);
        FeatureNodeRules.CheckPlacement(FeatureNodeKinds.SubCategory, feature).Reason.ShouldBe(PlacementReason.ParentKindInvalid);

        // The fifth level: nothing hangs under a sub_category.
        FeatureNodeRules.CheckPlacement(FeatureNodeKinds.SubCategory, subCategory).Reason.ShouldBe(PlacementReason.DepthExceeded);

        // Absence and deletion come first, and a parent in another project is indistinguishable from absence (AC10).
        FeatureNodeRules.CheckPlacement(FeatureNodeKinds.Feature, module with { SameProject = false }).Reason
            .ShouldBe(PlacementReason.ParentNotFound);
        FeatureNodeRules.CheckPlacement(FeatureNodeKinds.Feature, module with { Deleted = true }).Reason
            .ShouldBe(PlacementReason.ParentDeleted);

        // AC1's defensive branch: a node never becomes its own ancestor.
        var node = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        FeatureNodeRules.CheckReparent(node, node, []).Reason.ShouldBe(PlacementReason.Cycle);
        FeatureNodeRules.CheckReparent(node, other, [node]).Reason.ShouldBe(PlacementReason.Cycle);
        FeatureNodeRules.CheckReparent(node, other, [Guid.CreateVersion7()]).IsAccepted.ShouldBeTrue();
        FeatureNodeRules.CheckReparent(node, null, []).IsAccepted.ShouldBeTrue();

        // A kind outside the four is a bug in the caller, not a refusal.
        Should.Throw<ArgumentException>(() => FeatureNodeRules.CheckPlacement("epic", module));

        // AC2: a position is a non-negative integer and a new sibling lands at max + 1.
        FeatureNodeRules.IsValidPosition(-1).ShouldBeFalse();
        FeatureNodeRules.IsValidPosition(0).ShouldBeTrue();
        FeatureNodeRules.NextPosition([]).ShouldBe(0);
        FeatureNodeRules.NextPosition([0, 3, 1]).ShouldBe(4);
        FeatureNodeKinds.ItemParentKinds.ShouldBe([FeatureNodeKinds.Category, FeatureNodeKinds.SubCategory]);
    }

    // ------------------------------------------------------------------ AC20 / AC21: the state machines

    [Fact]
    public void StateMachines_MatchSection6()
    {
        // checklist_item: manual create → active, ai create → draft, draft → active, {draft, active} → retired (reason).
        ChecklistItemStateMachine.NewItemState(AuthoringSource.Manual).ShouldBe(ChecklistItemStateMachine.Active);
        ChecklistItemStateMachine.NewItemState(AuthoringSource.Ai).ShouldBe(ChecklistItemStateMachine.Draft);
        ChecklistItemStateMachine.NewItemState(AuthoringSource.Import).ShouldBe(ChecklistItemStateMachine.Active);

        ChecklistItemStateMachine.Decide(ChecklistItemStateMachine.Draft, ChecklistItemStateMachine.Active, null)
            .IsAllowed.ShouldBeTrue();
        ChecklistItemStateMachine.Decide(ChecklistItemStateMachine.Active, ChecklistItemStateMachine.Active, null)
            .IsNoOp.ShouldBeTrue();
        ChecklistItemStateMachine.Decide(ChecklistItemStateMachine.Active, ChecklistItemStateMachine.Retired, null)
            .Outcome.ShouldBe(AuthoringTransitionOutcome.ReasonRequired);
        ChecklistItemStateMachine.Decide(ChecklistItemStateMachine.Draft, ChecklistItemStateMachine.Retired, "discard")
            .IsAllowed.ShouldBeTrue();
        ChecklistItemStateMachine.Decide(ChecklistItemStateMachine.Retired, ChecklistItemStateMachine.Active, "reopen")
            .Outcome.ShouldBe(AuthoringTransitionOutcome.Refused);
        ChecklistItemStateMachine.CheckEditable(ChecklistItemStateMachine.Retired).IsAllowed.ShouldBeFalse();
        ChecklistItemStateMachine.CheckEditable(ChecklistItemStateMachine.Draft).IsAllowed.ShouldBeTrue();

        // manual_test_case: draft → approved (reason optional), {draft, approved} → retired (reason), retired terminal.
        ManualTestCaseStateMachine.NewCaseState.ShouldBe(ManualTestCaseStateMachine.Draft);
        ManualTestCaseStateMachine.Decide(ManualTestCaseStateMachine.Draft, ManualTestCaseStateMachine.Approved, null)
            .IsAllowed.ShouldBeTrue();
        ManualTestCaseStateMachine.Decide(ManualTestCaseStateMachine.Approved, ManualTestCaseStateMachine.Draft, null)
            .Outcome.ShouldBe(AuthoringTransitionOutcome.Refused);
        ManualTestCaseStateMachine.Decide(ManualTestCaseStateMachine.Draft, ManualTestCaseStateMachine.Retired, null)
            .Outcome.ShouldBe(AuthoringTransitionOutcome.ReasonRequired);
        ManualTestCaseStateMachine.Decide(ManualTestCaseStateMachine.Approved, ManualTestCaseStateMachine.Retired, "old")
            .IsAllowed.ShouldBeTrue();
        ManualTestCaseStateMachine.Decide(ManualTestCaseStateMachine.Retired, ManualTestCaseStateMachine.Draft, "back")
            .Outcome.ShouldBe(AuthoringTransitionOutcome.Refused);
        ManualTestCaseStateMachine.CheckEditable(ManualTestCaseStateMachine.Retired).IsAllowed.ShouldBeFalse();

        // AC8: restore sets a restored item active and a restored case draft.
        ChecklistItemStateMachine.RestoreState.ShouldBe(ChecklistItemStateMachine.Active);
        ManualTestCaseStateMachine.RestoreState.ShouldBe(ManualTestCaseStateMachine.Draft);

        // AC3: the priority set is closed.
        AuthoringPriorities.Values.ShouldBe(["critical", "high", "medium", "low"]);
        AuthoringPriorities.IsValid("blocker").ShouldBeFalse();
    }

    // ------------------------------------------------------------------ AC5: the content snapshot

    [Fact]
    public void ContentSnapshot_ExcludesServerFields()
    {
        // §4 line 207: the exact member sets, and nothing of the row's own bookkeeping.
        AuthoringContent.Members(AuthoringEntityKind.FeatureNode).ShouldBe(["kind", "title", "position", "parent_id"]);
        AuthoringContent.Members(AuthoringEntityKind.ChecklistItem).ShouldBe(
            ["feature_node_id", "title", "description", "priority", "state", "position"]);
        AuthoringContent.Members(AuthoringEntityKind.ManualTestCase).ShouldBe(
            ["checklist_item_id", "code", "title", "description", "precondition", "steps", "priority", "state"]);

        var itemId = Guid.CreateVersion7();
        var snapshot = new ManualTestCaseSnapshot(
            itemId,
            "SHOP-001",
            "Pay with a card",
            null,
            null,
            Steps,
            AuthoringPriorities.High,
            ManualTestCaseStateMachine.Draft);

        var content = JsonNode.Parse(AuthoringContent.Serialize(snapshot))!.AsObject();

        content.Select(member => member.Key).ShouldBe(AuthoringContent.Members(AuthoringEntityKind.ManualTestCase));
        foreach (var serverField in new[] { "id", "org_id", "project_id", "created_at", "updated_at", "deleted_at", "version", "source" })
        {
            content.ContainsKey(serverField).ShouldBeFalse($"{serverField} is a column of the row or of the version, never of content");
        }

        content["steps"]!.ToJsonString().ShouldBe(Steps);

        // A server field in a caller's content is refused, not silently dropped.
        using var withId = JsonDocument.Parse(
            """{"checklist_item_id":"018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e5f","code":"SHOP-001","title":"x","description":null,"precondition":null,"steps":{"steps":[]},"priority":"high","state":"draft","id":"018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e60"}""");
        AuthoringContent.Parse(AuthoringEntityKind.ManualTestCase, withId.RootElement, out var refused, out var details)
            .ShouldBeFalse();
        refused.ShouldBeNull();
        details.ShouldContain(("/content/id", "unknown_member"));

        // NFC normalisation is part of the snapshot: two spellings are one row and one hash (AC14).
        var decomposed = new ChecklistItemSnapshot(itemId, "Ca\u0301ch", null, AuthoringPriorities.Low, ChecklistItemStateMachine.Active, 0);
        var composed = new ChecklistItemSnapshot(itemId, "Cách", null, AuthoringPriorities.Low, ChecklistItemStateMachine.Active, 0);
        AuthoringContent.Serialize(decomposed).ShouldBe(AuthoringContent.Serialize(composed));
    }

    [Fact]
    public void ContentParse_RefusesDuplicateMembersAndInvalidText()
    {
        var itemId = Guid.CreateVersion7().ToString("D");
        var prefix = "{\"checklist_item_id\":\"" + itemId
            + "\",\"code\":\"SHOP-001\",\"title\":\"x\",\"description\":null,\"precondition\":null,"
            + "\"priority\":\"high\",\"state\":\"draft\",\"steps\":";

        // A duplicate member name inside steps: the node model refuses to build such an object, so the parser answers
        // invalid_json instead of surfacing that exception as a 500.
        using var duplicate = JsonDocument.Parse(
            prefix + "{\"steps\":[{\"no\":1,\"action\":\"a\",\"action\":\"b\",\"expected\":\"c\"}]}}");
        AuthoringContent.Parse(AuthoringEntityKind.ManualTestCase, duplicate.RootElement, out var refusedDuplicate, out var duplicateDetails)
            .ShouldBeFalse();
        refusedDuplicate.ShouldBeNull();
        duplicateDetails.ShouldContain(("/content/steps", "invalid_json"));

        // The one input shape those guards exist for: NFC normalisation refuses text that is not valid UTF-16, so a lone
        // surrogate can never be composed or stored.
        Should.Throw<ArgumentException>(() => { _ = AuthoringText.Normalize("\ud800"); });

        // Whether such an escape reaches the content parser at all depends on the reader; both boundaries answer a
        // problem row and neither may throw. An unpaired surrogate inside steps is invalid_text, never a 500.
        if (TryParse(prefix + "{\"steps\":[{\"no\":1,\"action\":\"\\ud800\",\"expected\":\"c\"}]}}") is { } surrogate)
        {
            using var document = surrogate;
            AuthoringContent.Parse(AuthoringEntityKind.ManualTestCase, document.RootElement, out var refusedSurrogate, out var surrogateDetails)
                .ShouldBeFalse();
            refusedSurrogate.ShouldBeNull();
            surrogateDetails.ShouldContain(("/content/steps", "invalid_text"));
        }

        // The same guard on a plain text member: a title that is not valid UTF-16 is a problem row, not a 500.
        if (TryParse("{\"kind\":\"module\",\"title\":\"\\ud800\",\"position\":0,\"parent_id\":null}") is { } badTitle)
        {
            using var document = badTitle;
            AuthoringContent.Parse(AuthoringEntityKind.FeatureNode, document.RootElement, out var refusedTitle, out var titleDetails)
                .ShouldBeFalse();
            refusedTitle.ShouldBeNull();
            titleDetails.ShouldContain(("/content/title", "invalid_text"));
        }
    }

    /// <summary>Parses a document, or null when the reader itself refuses it (the outer boundary for invalid text).</summary>
    /// <param name="json">The raw document.</param>
    private static JsonDocument? TryParse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [Fact]
    public void ContentHash_Rfc8785Vectors()
    {
        var inputDirectory = Path.Combine(RlsExceptionFile.RepositoryRoot, "fixtures", "rfc8785", "input");
        var outputDirectory = Path.Combine(RlsExceptionFile.RepositoryRoot, "fixtures", "rfc8785", "output");

        var vectors = Directory.GetFiles(inputDirectory, "*.json");
        vectors.ShouldNotBeEmpty();

        foreach (var input in vectors)
        {
            var name = Path.GetFileName(input);
            var canonical = Path.Combine(outputDirectory, name);
            File.Exists(canonical).ShouldBeTrue($"{name}: every input vector has its canonical output");

            // RFC 8785's canonical form is a fixed point: hashing the input and hashing the canonical output agree.
            var expected = CanonicalHash.Compute(File.ReadAllBytes(canonical));
            CanonicalHash.Compute(File.ReadAllBytes(input)).ShouldBe(expected, name);
            expected.ShouldMatch("^sha256:[0-9a-f]{64}$");
        }

        // The authoring hash is that same digest over the canonical content of §4 line 207.
        var snapshot = new FeatureNodeSnapshot(FeatureNodeKinds.Module, "Shop", 0, null);
        var contentHash = AuthoringHashes.ContentHash(snapshot);
        contentHash.ShouldBe(CanonicalHash.Compute(AuthoringContent.Serialize(snapshot)));
        contentHash.ShouldMatch("^sha256:[0-9a-f]{64}$");

        // Equal content, equal hash — whatever order the record was built in.
        var same = new FeatureNodeSnapshot(FeatureNodeKinds.Module, "Shop", 0, null);
        AuthoringHashes.ContentHash(same).ShouldBe(contentHash);

        // A different position is a different snapshot.
        AuthoringHashes.ContentHash(snapshot with { Position = 1 }).ShouldNotBe(contentHash);
    }

    [Fact]
    public void OrderHash_IsStable()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var third = Guid.CreateVersion7();

        var hash = AuthoringHashes.OrderHash([(first, 1), (second, 2), (third, 3)]);

        hash.ShouldStartWith(AuthoringHashes.OrderPrefix);
        hash.ShouldMatch("^order:sha256:[0-9a-f]{64}$");

        // Sorted pairs: the caller's read order never changes the ETag.
        AuthoringHashes.OrderHash([(third, 3), (first, 1), (second, 2)]).ShouldBe(hash);

        // One moved sibling is a different order.
        AuthoringHashes.OrderHash([(first, 1), (second, 2), (third, 4)]).ShouldNotBe(hash);
        AuthoringHashes.OrderHash([(first, 1), (second, 2)]).ShouldNotBe(hash);
    }

    // ------------------------------------------------------------------ §4 / AC4: the code

    [Fact]
    public void AuthoringCode_FormatAndParse()
    {
        AuthoringCode.TryFromNumber("SHOP", 1, out var first).ShouldBeTrue();
        first!.Value.ShouldBe("SHOP-001");

        AuthoringCode.TryFromNumber("SHOP", 12, out var twelve).ShouldBeTrue();
        twelve!.Value.ShouldBe("SHOP-012");

        // Three digits is a minimum, not a maximum: 10 digits are still one code.
        AuthoringCode.TryFromNumber("SHOP", 1_000_000_000, out var largest).ShouldBeTrue();
        largest!.Value.ShouldBe("SHOP-1000000000");

        AuthoringCode.TryFromNumber("SHOP", 1_000_000_001, out _).ShouldBeFalse("the 10⁹ cap is the last code");
        AuthoringCode.TryFromNumber("SHOP", 0, out _).ShouldBeFalse();
        AuthoringCode.TryFromNumber("shop", 1, out _).ShouldBeFalse("the prefix grammar is upper case");
        AuthoringCode.TryFromNumber("SHOPPING1", 1, out _).ShouldBeFalse("eight characters is the longest prefix");

        // Parse is strict and round-trips the allocated value.
        AuthoringCode.TryParse("SHOP-001", out var parsed).ShouldBeTrue();
        parsed!.Prefix.ShouldBe("SHOP");
        parsed.Number.ShouldBe(1);
        parsed.Value.ShouldBe("SHOP-001");

        AuthoringCode.TryParse(first.Value, out var roundTrip).ShouldBeTrue();
        roundTrip!.Number.ShouldBe(first.Number);

        AuthoringCode.TryParse("SHOP-01", out _).ShouldBeFalse("two digits is not a code");
        AuthoringCode.TryParse("SHOP-00123456789", out _).ShouldBeFalse("eleven digits is not a code");
        AuthoringCode.TryParse("1SHOP-001", out _).ShouldBeFalse();
        AuthoringCode.TryParse("SHOP_001", out _).ShouldBeFalse();
        AuthoringCode.TryParse(null, out _).ShouldBeFalse();

        // A trailing newline is refused: the patterns are anchored with \z, not $, which .NET matches before a final
        // newline. A code is an identifier that reaches URLs and the wire unchanged.
        AuthoringCode.TryParse("SHOP-007\n", out _).ShouldBeFalse("a trailing newline is not part of a code");
        AuthoringCode.IsValidPrefix("SHOP\n").ShouldBeFalse("a trailing newline is not part of a prefix");

        // The grammar of OI-13-101, shared with the column CHECK, anchored so no trailing newline slips through.
        AuthoringCode.PrefixPattern.ShouldBe(@"\A[A-Z][A-Z0-9]{1,7}\z");
        AuthoringCode.FormatPattern.ShouldBe(@"\A[A-Z][A-Z0-9]{1,7}-[0-9]{3,10}\z");
        AuthoringCode.IsValidPrefix("A1").ShouldBeTrue();
        AuthoringCode.IsValidPrefix("A").ShouldBeFalse();
        AuthoringCode.MaximumNumber.ShouldBe(1_000_000_000);

        // A code never formats itself with fewer than three digits, whatever the culture.
        AuthoringCode.TryFromNumber("SHOP", 7, out var seven).ShouldBeTrue();
        seven!.Value.ShouldBe("SHOP-007");
        seven.Value.Length.ShouldBe(8);
        int.Parse(seven.Value[(seven.Value.IndexOf('-', StringComparison.Ordinal) + 1)..], CultureInfo.InvariantCulture)
            .ShouldBe(7);
    }
}
