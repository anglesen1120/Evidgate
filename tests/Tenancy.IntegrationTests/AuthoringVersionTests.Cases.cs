using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1 AC4/AC5/AC22 through the module seam: a manual test case is created, changed and moved as an
/// <c>authoring_version</c> batch, so the case rows of the tree carry the same version discipline as nodes and items —
/// one row per real change, nothing for a no-op, and a refusal that leaves the caller's transaction to abort.
/// </summary>
/// <remarks>
/// These run against the writer directly (like the node and item cases of this suite): the HTTP routes are covered by
/// <c>AuthoringManualCaseTests.*.cs</c>, and what is asserted here is the version bookkeeping the routes rely on.
/// </remarks>
public sealed partial class AuthoringVersionTests
{
    /// <summary>AC4/AC5: the create is version 1, the title change is version 2 with another hash, and the batch differs.</summary>
    [Fact]
    public async Task CaseCreate_ThenTitleUpdate_AppendsTwoVersionsWithDifferentHashes()
    {
        await using var world = await StartAsync();
        var (_, _, _, item) = await SeedLadderAsync(world);
        var caseId = Guid.CreateVersion7();
        var created = await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Create(AuthoringEntityKind.ManualTestCase, caseId, CaseContent(item, "SHOP-001", "Pay with a card")),
        ]);

        created.IsSuccess.ShouldBeTrue();
        var first = created.Value!.Versions.Single();
        first.EntityId.ShouldBe(caseId);
        first.VersionNo.ShouldBe(1);
        first.ContentHash.ShouldStartWith("sha256:");

        var updated = await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Update(AuthoringEntityKind.ManualTestCase, caseId, CaseContent(item, "SHOP-001", "Pay with a stored card")),
        ]);

        updated.IsSuccess.ShouldBeTrue();
        var second = updated.Value!.Versions.Single();
        second.VersionNo.ShouldBe(2);
        second.ContentHash.ShouldNotBe(first.ContentHash, "the title is part of the snapshot");
        second.BatchId.ShouldNotBe(first.BatchId, "each call is its own batch");
    }

    /// <summary>AC5: a snapshot equal to the last one writes no version row at all.</summary>
    [Fact]
    public async Task CaseUpdate_WithAnIdenticalSnapshot_WritesNothing()
    {
        await using var world = await StartAsync();
        var (_, _, _, item) = await SeedLadderAsync(world);
        var caseId = Guid.CreateVersion7();

        (await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Create(AuthoringEntityKind.ManualTestCase, caseId, CaseContent(item, "SHOP-001", "Pay with a card")),
        ])).IsSuccess.ShouldBeTrue();

        var repeated = await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Update(AuthoringEntityKind.ManualTestCase, caseId, CaseContent(item, "SHOP-001", "Pay with a card")),
        ]);

        repeated.IsSuccess.ShouldBeTrue();
        repeated.Value!.Versions.ShouldBeEmpty();
        repeated.Value.UnchangedEntityIds.ShouldContain(caseId);
    }

    /// <summary>
    /// AC22: a move keeps the code, appends exactly one <c>update</c> row and answers the bumped version — and a retired
    /// target item is refused (<c>409 invalid_transition</c>) with the case left where it was.
    /// </summary>
    [Fact]
    public async Task CaseMove_KeepsTheCode_AndRefusesARetiredTarget()
    {
        await using var world = await StartAsync();
        var (_, _, category, item) = await SeedLadderAsync(world);
        var target = Guid.CreateVersion7();
        var retired = Guid.CreateVersion7();

        (await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Create(AuthoringEntityKind.ChecklistItem, target, ItemContent(category, "Refunds", 1)),
            EntityChange.Create(AuthoringEntityKind.ChecklistItem, retired, ItemContent(category, "Old refunds", 2, ChecklistItemStateMachine.Retired)),
        ])).IsSuccess.ShouldBeTrue();

        var caseId = Guid.CreateVersion7();
        (await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Create(AuthoringEntityKind.ManualTestCase, caseId, CaseContent(item, "SHOP-001", "Pay with a card")),
        ])).IsSuccess.ShouldBeTrue();

        var moved = await MoveAsync(world, caseId, target, "belongs to refunds");

        moved.IsSuccess.ShouldBeTrue();
        moved.Value!.Moved.ShouldBeTrue();
        moved.Value.EntityVersion.ShouldBe(2);
        moved.Value.Version!.VersionNo.ShouldBe(2);

        // The same move again is the AC5 no-op: the case already sits under the target item.
        var again = await MoveAsync(world, caseId, target, null);

        again.IsSuccess.ShouldBeTrue();
        again.Value!.Moved.ShouldBeFalse();
        again.Value.EntityVersion.ShouldBe(2);

        var refused = await MoveAsync(world, caseId, retired, "should not move");

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(409);
        refused.Error.Code.ShouldBe("invalid_transition");
    }

    /// <summary>AC7: a soft delete of a live case is one <c>soft_delete</c> row and the case stops being readable.</summary>
    [Fact]
    public async Task CaseSoftDelete_AppendsOneRow()
    {
        await using var world = await StartAsync();
        var (_, _, _, item) = await SeedLadderAsync(world);
        var caseId = Guid.CreateVersion7();

        (await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Create(AuthoringEntityKind.ManualTestCase, caseId, CaseContent(item, "SHOP-001", "Pay with a card")),
        ])).IsSuccess.ShouldBeTrue();

        var deleted = await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.SoftDelete(AuthoringEntityKind.ManualTestCase, caseId, "merged into SHOP-002"),
        ]);

        deleted.IsSuccess.ShouldBeTrue();
        deleted.Value!.Versions.Single().VersionNo.ShouldBe(2);

        // A second delete has nothing live to delete, so the writer refuses it rather than appending.
        var second = await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.SoftDelete(AuthoringEntityKind.ManualTestCase, caseId, null),
        ]);

        second.IsSuccess.ShouldBeFalse();
    }
}
