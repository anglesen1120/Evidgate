using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReconcileComparerUnitTests
{
    // ---- matching evidence ----------------------------------------------------------------

    [Fact]
    public void Every_Shard_Matching_Has_No_Missing()
    {
        var result = ReconcileComparer.CompareEvents(
            [
                Planned("producer-1", shardIndex: 1),
                Planned("producer-2", shardIndex: 2, declaredLastSequence: 3, declaredCount: 4),
            ],
            [
                Observed("producer-2", upTo: 4, max: 3),
                Observed("producer-1"),
            ]);

        result.Missing.ShouldBeEmpty();
        result.EventsMatch.ShouldBeTrue();
        result.ShardsPlanned.ShouldBe(2);
        result.ShardsClosed.ShouldBe(2);
    }

    [Fact]
    public void Empty_Plan_Has_No_Missing()
    {
        var result = ReconcileComparer.CompareEvents([], [Observed("producer-1")]);

        result.Missing.ShouldBeEmpty();
        result.EventsMatch.ShouldBeTrue();
        result.ShardsPlanned.ShouldBe(0);
        result.ShardsClosed.ShouldBe(0);
    }

    [Fact]
    public void A_Closed_Shard_With_No_Stored_Event_And_A_Zero_Declaration_Matches()
    {
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 0, declaredCount: 0)],
            []);

        result.Missing.ShouldBeEmpty();
        result.ShardsClosed.ShouldBe(1);
    }

    [Fact]
    public void A_Zero_Declaration_With_A_Stored_Event_Is_CountMismatch()
    {
        // A declaration of zero events matches only when nothing at all was stored, so one stored event — even the
        // shard's own sequence 0 — is a disagreement.
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 0, declaredCount: 0)],
            [Observed("producer-1", upTo: 1, min: 0, max: 0)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.Detail.ShouldBe("declared_count=0;observed=1;declared_last_sequence=0;beyond=0");
    }

    // ---- missing shard --------------------------------------------------------------------

    [Fact]
    public void Open_Shard_Is_One_MissingShard_And_Its_Events_Are_Not_Compared()
    {
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", shardIndex: 2, closed: false, declaredLastSequence: null, declaredCount: null)],
            [Observed("producer-1", upTo: 5, beyond: 5, unknown: 3, unfinished: 2)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.MissingShard);
        reason.ProducerId.ShouldBe("producer-1");
        reason.Detail.ShouldBe("shard_index=2");
        result.EventsMatch.ShouldBeFalse();
        result.ShardsPlanned.ShouldBe(1);
        result.ShardsClosed.ShouldBe(0);
    }

    [Fact]
    public void Open_Shard_Without_Observed_Events_Is_Still_A_MissingShard()
    {
        var result = ReconcileComparer.CompareEvents([Planned("producer-1", shardIndex: 7, closed: false)], []);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.MissingShard);
        reason.Detail.ShouldBe("shard_index=7");
    }

    // ---- count mismatch -------------------------------------------------------------------

    [Fact]
    public void Count_Short_Of_The_Declaration_Is_CountMismatch()
    {
        // Two contiguous events (1, 2) end exactly on the declared high-water mark: the range is clean, only the count
        // is one short of the declared three.
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", upTo: 2, min: 1, max: 2)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.ProducerId.ShouldBe("producer-1");
        reason.Detail.ShouldBe("declared_count=3;observed=2;declared_last_sequence=2;beyond=0");
    }

    [Fact]
    public void Count_Over_The_Declaration_Is_CountMismatch()
    {
        // Sequences 3, 4 and 5 are contiguous and end on the declared high-water mark 5, while only 2 events were
        // declared: the declaration undercounts its own clean range.
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 5, declaredCount: 2)],
            [Observed("producer-1", upTo: 3, min: 3, max: 5)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.Detail.ShouldBe("declared_count=2;observed=3;declared_last_sequence=5;beyond=0");
    }

    [Fact]
    public void Events_Beyond_The_Declared_Last_Sequence_Are_CountMismatch()
    {
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", upTo: 3, beyond: 1, max: 3)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.Detail.ShouldBe("declared_count=3;observed=3;declared_last_sequence=2;beyond=1;max_sequence=3");
    }

    [Fact]
    public void A_Closed_Shard_With_No_Stored_Event_And_A_NonZero_Declaration_Is_CountMismatch()
    {
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 1, declaredCount: 2)],
            []);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.Detail.ShouldBe("declared_count=2;observed=0;declared_last_sequence=1;beyond=0");
    }

    // ---- the bounds a count cannot see (contracts/events/ordering.md §3) ------------------

    [Fact]
    public void A_Sequence_Gap_With_An_Equal_Count_Is_CountMismatch()
    {
        // Declared three events ending at sequence 3, stored 0, 1 and 2: the count agrees and the range still has a
        // hole where 3 should be — the case a count-only comparison could not see.
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 3, declaredCount: 3)],
            [Observed("producer-1", upTo: 3, min: 0, max: 2)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.Detail.ShouldBe(
            "declared_count=3;observed=3;declared_last_sequence=3;beyond=0;max_sequence=2;min_sequence=0;sequence_span=3");
        result.EventsMatch.ShouldBeFalse();
    }

    [Fact]
    public void A_Sequence_Gap_Below_The_Declared_Last_Is_CountMismatch()
    {
        // Declared five events ending at 5, stored 0, 1, 2, 3 and 5: the count and the end agree, but 4 was never stored.
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 5, declaredCount: 5)],
            [Observed("producer-1", upTo: 5, min: 0, max: 5)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.Detail.ShouldBe(
            "declared_count=5;observed=5;declared_last_sequence=5;beyond=0;min_sequence=0;sequence_span=5");
    }

    [Fact]
    public void A_Huge_Declared_Last_Sequence_Cannot_Wrap_The_Gap_Check()
    {
        // Declared two events ending at long.MaxValue, stored 0 and long.MaxValue: not contiguous, and a `last - min + 1`
        // would wrap to long.MinValue and skip the hole check.
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: long.MaxValue, declaredCount: 2)],
            [Observed("producer-1", upTo: 2, min: 0, max: long.MaxValue)]);

        result.Missing.ShouldHaveSingleItem().Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
    }

    [Fact]
    public void A_Zero_Based_Contiguous_Shard_Matches()
    {
        // The envelope allows sequence >= 0, so a shard that starts at 0 is contiguous when its count is the declared one.
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", upTo: 3, min: 0, max: 2)]);

        result.Missing.ShouldBeEmpty();
        result.EventsMatch.ShouldBeTrue();
    }

    [Fact]
    public void A_One_Based_Contiguous_Shard_Matches()
    {
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 5, declaredCount: 5)],
            [Observed("producer-1", upTo: 5, min: 1, max: 5)]);

        result.Missing.ShouldBeEmpty();
        result.EventsMatch.ShouldBeTrue();
    }

    [Fact]
    public void An_Unwritten_Prefix_Is_Not_A_Gap()
    {
        // The producer simply started at 3: a hole below the lowest stored sequence is not detectable from these columns
        // and is never reported (ordering.md §3).
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 5, declaredCount: 3)],
            [Observed("producer-1", upTo: 3, min: 3, max: 5)]);

        result.Missing.ShouldBeEmpty();
        result.EventsMatch.ShouldBeTrue();
    }

    [Fact]
    public void Events_Above_The_Declared_Last_Are_Not_A_Contiguity_Gap()
    {
        // Every stored event is past the declared high-water mark, so the range [min stored, declared last] is empty: the
        // count bound and the end bound name the disagreement, and the contiguity bound adds nothing.
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 1)],
            [Observed("producer-1", upTo: 0, beyond: 1, min: 5, max: 5)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.Detail.ShouldBe("declared_count=1;observed=0;declared_last_sequence=2;beyond=1;max_sequence=5");
    }

    [Fact]
    public void A_Closed_Shard_With_A_Null_Declared_Count_Fails_Closed()
    {
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 0, declaredCount: null)],
            [Observed("producer-1", unknown: 2)]);

        var countReason = result.Missing.Single(missing => missing.Code == ReconcileMissingCodes.CountMismatch);
        countReason.ProducerId.ShouldBe("producer-1");
        countReason.Detail.ShouldBe("declared_missing");

        // The status evidence does not depend on the declaration, so it is still named.
        result.Missing.ShouldContain(missing => missing.Code == ReconcileMissingCodes.UnknownStatus);
    }

    [Fact]
    public void The_Declared_Missing_Detail_Token_Is_Stable()
    {
        // 05.3.2-T2.2's fail-closed detail is machine-readable; the constant and the emitted detail stay identical.
        ReconcileComparer.DeclaredMissingDetail.ShouldBe("declared_missing");
    }

    [Fact]
    public void A_Closed_Shard_With_A_Null_Declared_Last_Sequence_Fails_Closed()
    {
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: null, declaredCount: 0)],
            []);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.Detail.ShouldBe("declared_missing");
    }
}
