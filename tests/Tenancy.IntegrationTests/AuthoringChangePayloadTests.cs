using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Changes;
using Evidgate.Modules.Authoring.Domain.Changes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The queue payload's pure half: one case per refusal reason, the affected-set derivation of the shared contract,
/// and the payload cap. No database and no host — <see cref="AuthoringChangePayloadReader"/> is the whole subject.
/// </summary>
public sealed class AuthoringChangePayloadTests
{
    private static readonly Guid Target = Guid.CreateVersion7();

    [Fact]
    public void Comment_ValidatesBodyAndRefusesUnknownMembers()
    {
        var (accepted, _) = Read(AuthoringChangeTypes.Comment, Target, """{"body":"Needs a screenshot"}""");
        accepted.ShouldNotBeNull();
        accepted.AffectedItemIds.ShouldBe([Target]);

        AssertRefusal(
            Read(AuthoringChangeTypes.Comment, Target, """{"body":""}"""),
            "/payload/body",
            "comment_body_invalid");
        AssertRefusal(
            Read(AuthoringChangeTypes.Comment, Target, $$"""{"body":"{{new string('x', 2001)}}"}"""),
            "/payload/body",
            "comment_body_invalid");
        AssertRefusal(
            Read(AuthoringChangeTypes.Comment, Target, """{"body":42}"""),
            "/payload/body",
            "comment_body_invalid");
        AssertRefusal(
            Read(AuthoringChangeTypes.Comment, Target, """{"body":"ok","source":"ai"}"""),
            "/payload/source",
            "payload_invalid");
        AssertRefusal(
            Read(AuthoringChangeTypes.Comment, Target, "[]"),
            "/payload",
            "payload_invalid");
    }

    [Fact]
    public void Delete_AcceptsAnEmptyPayloadAndRefusesAnyMember()
    {
        var (accepted, _) = Read(AuthoringChangeTypes.Delete, Target, "{}");
        accepted.ShouldNotBeNull();
        accepted.AffectedItemIds.ShouldBe([Target]);

        AssertRefusal(
            Read(AuthoringChangeTypes.Delete, Target, """{"item_id":"00000000-0000-0000-0000-000000000000"}"""),
            "/payload/item_id",
            "payload_invalid");
    }

    [Fact]
    public void Merge_EnforcesTheSourceBoundsAndSurvivorRules()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var third = Guid.CreateVersion7();

        var (accepted, _) = Read(
            AuthoringChangeTypes.Merge,
            b,
            $$"""{"source_item_ids":["{{a}}","{{b}}","{{third}}"],"survivor_item_id":"{{b}}"}""");
        accepted.ShouldNotBeNull();
        accepted.AffectedItemIds.ShouldBe([a, b, third]);

        // Fewer than two items: the minimum reason, not a generic invalid payload.
        AssertRefusal(
            Read(AuthoringChangeTypes.Merge, b, $$"""{"source_item_ids":["{{b}}"],"survivor_item_id":"{{b}}"}"""),
            "/payload/source_item_ids",
            "merge_sources_min");

        var tooMany = string.Join(',', Enumerable.Range(0, 51).Select(_ => $"\"{Guid.CreateVersion7()}\""));
        AssertRefusal(
            Read(AuthoringChangeTypes.Merge, b, $$"""{"source_item_ids":[{{tooMany}}],"survivor_item_id":"{{b}}"}"""),
            "/payload/source_item_ids",
            "merge_sources_max");

        AssertRefusal(
            Read(AuthoringChangeTypes.Merge, b, $$"""{"source_item_ids":["{{a}}","{{b}}"],"survivor_item_id":"{{third}}"}"""),
            "/payload/survivor_item_id",
            "survivor_not_in_sources");

        // The target must be the survivor the payload names.
        AssertRefusal(
            Read(AuthoringChangeTypes.Merge, a, $$"""{"source_item_ids":["{{a}}","{{b}}"],"survivor_item_id":"{{b}}"}"""),
            "/target_id",
            "payload_invalid");

        // A repeated id is not a two-item merge.
        AssertRefusal(
            Read(AuthoringChangeTypes.Merge, b, $$"""{"source_item_ids":["{{b}}","{{b}}"],"survivor_item_id":"{{b}}"}"""),
            "/payload/source_item_ids/1",
            "payload_invalid");

        AssertRefusal(
            Read(AuthoringChangeTypes.Merge, b, $$"""{"source_item_ids":["{{a}}","{{b}}"],"survivor_item_id":"{{b}}","keep":true}"""),
            "/payload/keep",
            "payload_invalid");
    }

    [Fact]
    public void Split_EnforcesItemBoundsAssignmentsAndEntryShape()
    {
        var first = Guid.CreateVersion7();
        var caseId = Guid.CreateVersion7();

        var (accepted, _) = Read(
            AuthoringChangeTypes.Split,
            Target,
            $$"""
            {"new_items":[
                {"title":"Log in","priority":"high"},
                {"title":"Log out","description":"also covers timeout"}
             ],
             "case_assignments":[{"case_id":"{{caseId}}","item_index":1}]}
            """);
        accepted.ShouldNotBeNull();
        accepted.AffectedItemIds.ShouldBe([Target]);

        AssertRefusal(
            Read(AuthoringChangeTypes.Split, Target, """{"new_items":[{"title":"Only one"}]}"""),
            "/payload/new_items",
            "split_items_min");

        var tooMany = string.Join(
            ',',
            Enumerable.Range(0, 21).Select(index => $$"""{"title":"Item {{index}}"}"""));
        AssertRefusal(
            Read(AuthoringChangeTypes.Split, Target, $$"""{"new_items":[{{tooMany}}]}"""),
            "/payload/new_items",
            "split_items_max");

        AssertRefusal(
            Read(AuthoringChangeTypes.Split, Target, """{"new_items":[{"title":""},{"title":"B"}]}"""),
            "/payload/new_items/0/title",
            "payload_invalid");

        AssertRefusal(
            Read(AuthoringChangeTypes.Split, Target, """{"new_items":[{"title":"A","priority":"urgent"},{"title":"B"}]}"""),
            "/payload/new_items/0/priority",
            "payload_invalid");

        AssertRefusal(
            Read(AuthoringChangeTypes.Split, Target, """{"new_items":[{"title":"A"},{"title":"B"}],"case_assignments":[{"case_id":"00000000-0000-0000-0000-000000000000","item_index":0}]}"""),
            "/payload/case_assignments/0/case_id",
            "payload_invalid");

        AssertRefusal(
            Read(
                AuthoringChangeTypes.Split,
                Target,
                $$"""{"new_items":[{"title":"A"},{"title":"B"}],"case_assignments":[{"case_id":"{{caseId}}","item_index":2}]}"""),
            "/payload/case_assignments/0/item_index",
            "payload_invalid");

        AssertRefusal(
            Read(
                AuthoringChangeTypes.Split,
                Target,
                $$"""
                {"new_items":[{"title":"A"},{"title":"B"}],
                 "case_assignments":[{"case_id":"{{caseId}}","item_index":0},{"case_id":"{{caseId}}","item_index":1}]}
                """),
            "/payload/case_assignments/1/case_id",
            "payload_invalid");
    }

    [Fact]
    public void Payload_OverTheByteCap_IsRefused()
    {
        var padding = new string('x', AuthoringChangeLimits.PayloadMaxBytes);

        AssertRefusal(
            Read(AuthoringChangeTypes.Comment, Target, $$"""{"body":"ok","pad":"{{padding}}"}"""),
            "/payload",
            "payload_invalid");
    }

    [Fact]
    public void AffectedSet_MatchesTheChangeType()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        Read(AuthoringChangeTypes.Comment, Target, """{"body":"note"}""").Payload!.AffectedItemIds
            .ShouldBe([Target]);
        Read(AuthoringChangeTypes.Delete, Target, "{}").Payload!.AffectedItemIds
            .ShouldBe([Target]);
        Read(AuthoringChangeTypes.Split, Target, """{"new_items":[{"title":"A"},{"title":"B"}]}""").Payload!.AffectedItemIds
            .ShouldBe([Target]);
        Read(
            AuthoringChangeTypes.Merge,
            b,
            $$"""{"source_item_ids":["{{a}}","{{b}}"],"survivor_item_id":"{{b}}"}""").Payload!.AffectedItemIds
            .ShouldBe([a, b]);

        // The change type and the target kind are the closed vocabularies, not free text.
        Read("commentary", Target, """{"body":"note"}""").Refusal!.Details![0].Path.ShouldBe("/change_type");
    }

    private static (AuthoringChangePayload? Payload, Error? Refusal) Read(string changeType, Guid target, string json) =>
        AuthoringChangePayloadReader.Read(changeType, target, JsonDocument.Parse(json).RootElement);

    private static void AssertRefusal(
        (AuthoringChangePayload? Payload, Error? Refusal) outcome,
        string path,
        string reason)
    {
        outcome.Payload.ShouldBeNull();
        outcome.Refusal.ShouldNotBeNull();

        var detail = outcome.Refusal.Details.ShouldNotBeNull().ShouldHaveSingleItem();
        detail.Path.ShouldBe(path);
        detail.Reason.ShouldBe(reason);
    }
}
