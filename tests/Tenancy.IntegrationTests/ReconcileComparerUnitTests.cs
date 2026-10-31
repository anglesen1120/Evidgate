using System.Globalization;
using System.Text.RegularExpressions;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.2-T2.2/T2.3 without a database: the pure declared-versus-observed comparer — every 00 §8.5 code it can name from
/// event evidence and from the event flags (<c>stale_epoch</c>, <c>setup_error</c>), the open-shard short circuit, the
/// fail-closed missing declaration, the three bounds a closed shard's declaration is checked against (count, end and the
/// contiguity rule of <c>contracts/events/ordering.md</c> §3), the ordinal reason order, the planned/closed shard counts
/// and the stored-event watermark — plus the pin that every literal is one of the closed list <c>migrations/0049</c>
/// enforces, and the requirement set's freeze bound (<see cref="RequiredArtifactResolver.Compose"/>). The database half
/// (the one grouped aggregate, the run lock) is exercised by the reconcile integration suite.
/// </summary>
public sealed class ReconcileComparerUnitTests
{
    // ---- matching evidence ----------------------------------------------------------------

    [Fact]
    public void Every_Shard_Matching_Has_No_Missing()
    {
        var result = ReconcileComparer.Compare(
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
        var result = ReconcileComparer.Compare([], [Observed("producer-1")]);

        result.Missing.ShouldBeEmpty();
        result.EventsMatch.ShouldBeTrue();
        result.ShardsPlanned.ShouldBe(0);
        result.ShardsClosed.ShouldBe(0);
    }

    [Fact]
    public void A_Closed_Shard_With_No_Stored_Event_And_A_Zero_Declaration_Matches()
    {
        var result = ReconcileComparer.Compare(
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
        var result = ReconcileComparer.Compare(
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
        var result = ReconcileComparer.Compare(
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
        var result = ReconcileComparer.Compare([Planned("producer-1", shardIndex: 7, closed: false)], []);

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
        var result = ReconcileComparer.Compare(
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
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 5, declaredCount: 2)],
            [Observed("producer-1", upTo: 3, min: 3, max: 5)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.Detail.ShouldBe("declared_count=2;observed=3;declared_last_sequence=5;beyond=0");
    }

    [Fact]
    public void Events_Beyond_The_Declared_Last_Sequence_Are_CountMismatch()
    {
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", upTo: 3, beyond: 1, max: 3)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.Detail.ShouldBe("declared_count=3;observed=3;declared_last_sequence=2;beyond=1;max_sequence=3");
    }

    [Fact]
    public void A_Closed_Shard_With_No_Stored_Event_And_A_NonZero_Declaration_Is_CountMismatch()
    {
        var result = ReconcileComparer.Compare(
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
        var result = ReconcileComparer.Compare(
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
        var result = ReconcileComparer.Compare(
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
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: long.MaxValue, declaredCount: 2)],
            [Observed("producer-1", upTo: 2, min: 0, max: long.MaxValue)]);

        result.Missing.ShouldHaveSingleItem().Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
    }

    [Fact]
    public void A_Zero_Based_Contiguous_Shard_Matches()
    {
        // The envelope allows sequence >= 0, so a shard that starts at 0 is contiguous when its count is the declared one.
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", upTo: 3, min: 0, max: 2)]);

        result.Missing.ShouldBeEmpty();
        result.EventsMatch.ShouldBeTrue();
    }

    [Fact]
    public void A_One_Based_Contiguous_Shard_Matches()
    {
        var result = ReconcileComparer.Compare(
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
        var result = ReconcileComparer.Compare(
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
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 1)],
            [Observed("producer-1", upTo: 0, beyond: 1, min: 5, max: 5)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.Detail.ShouldBe("declared_count=1;observed=0;declared_last_sequence=2;beyond=1;max_sequence=5");
    }

    [Fact]
    public void A_Closed_Shard_With_A_Null_Declared_Count_Fails_Closed()
    {
        var result = ReconcileComparer.Compare(
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
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: null, declaredCount: 0)],
            []);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.Detail.ShouldBe("declared_missing");
    }

    // ---- unknown status -------------------------------------------------------------------

    [Fact]
    public void Unknown_Status_With_Matching_Counts_Is_UnknownStatus()
    {
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 1, declaredCount: 2)],
            [Observed("producer-1", upTo: 2, unknown: 1, max: 1)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.UnknownStatus);
        reason.ProducerId.ShouldBe("producer-1");
        reason.Detail.ShouldBe("null_status=1;unfinished=0");
    }

    [Fact]
    public void A_Started_Attempt_Without_A_Finish_Is_UnknownStatus()
    {
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 0, declaredCount: 1)],
            [Observed("producer-1", unfinished: 1)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.UnknownStatus);
        reason.Detail.ShouldBe("null_status=0;unfinished=1");
    }

    [Fact]
    public void Null_Status_And_Unfinished_Attempts_Are_Counted_Together()
    {
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", upTo: 3, unknown: 2, unfinished: 3, max: 2)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.UnknownStatus);
        reason.Detail.ShouldBe("null_status=2;unfinished=3");
    }

    // ---- readiness reasons derived from the event flags (05.3.2-T2.3) ----------------------

    [Fact]
    public void A_Stale_Event_With_Matching_Counts_Is_StaleEpoch()
    {
        // The stale event still fills its sequence slot, so the declared count and the stored range agree: only the flag
        // shows the epoch is older than the run's, and counting it as acknowledged must not hide that.
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 1, declaredCount: 2)],
            [Observed("producer-1", upTo: 2, max: 1, stale: 1)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.StaleEpoch);
        reason.ProducerId.ShouldBe("producer-1");
        reason.Detail.ShouldBe("stale=1");
        result.EventsMatch.ShouldBeFalse();
    }

    [Fact]
    public void A_Blocked_Finish_With_Matching_Counts_Is_SetupError()
    {
        // A blocked finish is a failed setup (00 §8.3): the shard's own counts can still agree and the evidence is not
        // complete.
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", upTo: 3, max: 2, blocked: 2)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.SetupError);
        reason.ProducerId.ShouldBe("producer-1");
        reason.Detail.ShouldBe("blocked=2");
        result.EventsMatch.ShouldBeFalse();
    }

    [Fact]
    public void An_Open_Shard_Is_Not_Compared_For_Stale_Or_Blocked_Events()
    {
        // The open shard is reported once and its events stay unexamined, so the flag counts add no second reason.
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", shardIndex: 4, closed: false)],
            [Observed("producer-1", upTo: 3, beyond: 3, stale: 2, blocked: 1)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.MissingShard);
        reason.Detail.ShouldBe("shard_index=4");
    }

    [Fact]
    public void Stale_And_Setup_Reasons_Do_Not_Hide_The_Count_Mismatch()
    {
        // One closed shard can break every bound at once: each reason is its own entry, and the count mismatch keeps its
        // own detail instead of being folded into the flags.
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", upTo: 2, max: 1, stale: 1, blocked: 1, unknown: 1)]);

        result.Missing.Select(missing => missing.Code).ShouldBe(
            [
                ReconcileMissingCodes.CountMismatch,
                ReconcileMissingCodes.SetupError,
                ReconcileMissingCodes.StaleEpoch,
                ReconcileMissingCodes.UnknownStatus,
            ]);
        result.Missing[1].Detail.ShouldBe("blocked=1");
        result.Missing[2].Detail.ShouldBe("stale=1");
        result.Missing[3].Detail.ShouldBe("null_status=1;unfinished=0");
    }

    [Fact]
    public void A_Closed_Shard_Without_Stale_Or_Blocked_Events_Names_Neither_Reason()
    {
        // The flags are the only source of these two reasons: zero counts add nothing to an otherwise matching shard.
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 1, declaredCount: 2)],
            [Observed("producer-1", upTo: 2, max: 1)]);

        result.Missing.ShouldBeEmpty();
        result.EventsMatch.ShouldBeTrue();
    }

    // ---- code order and producer order ----------------------------------------------------

    [Fact]
    public void A_Shard_Reports_CountMismatch_Before_UnknownStatus()
    {
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", upTo: 2, unknown: 2, unfinished: 1, max: 1)]);

        result.Missing.Select(missing => missing.Code).ShouldBe(
            [ReconcileMissingCodes.CountMismatch, ReconcileMissingCodes.UnknownStatus]);
        result.Missing[0].Detail.ShouldBe(
            "declared_count=3;observed=2;declared_last_sequence=2;beyond=0;max_sequence=1;min_sequence=0;sequence_span=2");
        result.Missing[1].Detail.ShouldBe("null_status=2;unfinished=1");
    }

    [Fact]
    public void Reasons_Are_Ordered_By_Producer_Then_Code()
    {
        var planned = new[]
        {
            Planned("b", shardIndex: 3),
            Planned("A", shardIndex: 1, closed: false),
            Planned("a2", shardIndex: 2, declaredLastSequence: 1, declaredCount: 2),
        };
        var observed = new[]
        {
            Observed("a2", upTo: 1),
            Observed("b", unknown: 1),
            Observed("A", upTo: 4, beyond: 4),
        };

        var result = ReconcileComparer.Compare(planned, observed);

        // Ordinal: "A" (0x41) sorts before "a2" (0x61), which sorts before "b"; a culture-aware order would differ.
        result.Missing.Select(missing => $"{missing.ProducerId}|{missing.Code}").ShouldBe(
            [
                "A|missing_shard",
                "a2|count_mismatch",
                "b|unknown_status",
            ]);
        result.ShardsPlanned.ShouldBe(3);
        result.ShardsClosed.ShouldBe(2);
    }

    [Fact]
    public void Flag_Reasons_Are_Ordered_By_Producer_Then_Code()
    {
        var result = ReconcileComparer.Compare(
            [
                Planned("b", shardIndex: 2, declaredLastSequence: 0, declaredCount: 1),
                Planned("A", shardIndex: 1, declaredLastSequence: 0, declaredCount: 1),
            ],
            [
                Observed("b", stale: 3),
                Observed("A", blocked: 2),
            ]);

        // Ordinal again: "A" before "b", and each shard names its own reason only.
        result.Missing.Select(missing => $"{missing.ProducerId}|{missing.Code}|{missing.Detail}").ShouldBe(
            [
                "A|setup_error|blocked=2",
                "b|stale_epoch|stale=3",
            ]);
    }

    [Fact]
    public void Ordering_Does_Not_Depend_On_Input_Order()
    {
        var planned = new[]
        {
            Planned("c", shardIndex: 3, declaredLastSequence: 1, declaredCount: 2),
            Planned("a", shardIndex: 1, closed: false),
            Planned("b", shardIndex: 2),
        };
        var observed = new[]
        {
            Observed("b", unknown: 1),
            Observed("c", upTo: 1),
            Observed("a"),
        };

        var forward = ReconcileComparer.Compare(planned, observed);
        var reversed = ReconcileComparer.Compare([.. planned.Reverse()], [.. observed.Reverse()]);

        reversed.Missing.ShouldBe(forward.Missing);
        reversed.Missing.Count.ShouldBe(3);
        reversed.ShardsPlanned.ShouldBe(forward.ShardsPlanned);
        reversed.ShardsClosed.ShouldBe(forward.ShardsClosed);
    }

    [Fact]
    public void Compare_Is_Deterministic_For_The_Same_Input()
    {
        var planned = new[]
        {
            Planned("producer-1", shardIndex: 1, declaredLastSequence: 1, declaredCount: 2),
            Planned("producer-2", shardIndex: 2, closed: false),
        };
        var observed = new[] { Observed("producer-1", unknown: 1) };

        var first = ReconcileComparer.Compare(planned, observed);
        var second = ReconcileComparer.Compare(planned, observed);

        second.Missing.ShouldBe(first.Missing);
        second.Missing.Select(missing => missing.Detail).ShouldBe(first.Missing.Select(missing => missing.Detail));
        second.ShardsPlanned.ShouldBe(first.ShardsPlanned);
        second.ShardsClosed.ShouldBe(first.ShardsClosed);
        second.EventsMatch.ShouldBe(first.EventsMatch);
    }

    // ---- scope and totals ----------------------------------------------------------------

    [Fact]
    public void Observed_Rows_Outside_The_Plan_Are_Ignored()
    {
        var result = ReconcileComparer.Compare(
            [Planned("producer-1")],
            [
                Observed("producer-1"),
                Observed("producer-9", upTo: 6, beyond: 6, unknown: 6, unfinished: 6),
            ]);

        result.Missing.ShouldBeEmpty();
        result.ShardsPlanned.ShouldBe(1);
        result.ShardsClosed.ShouldBe(1);
    }

    [Fact]
    public void Event_Watermark_Sums_Every_Observed_Row()
    {
        // Both branches of the aggregate are counted: the events up to each declared high-water mark here.
        var result = ReconcileComparer.Compare(
            [
                Planned("producer-1", shardIndex: 1, declaredLastSequence: 3, declaredCount: 4),
                Planned("producer-2", shardIndex: 2, declaredLastSequence: 0, declaredCount: 1),
            ],
            [
                Observed("producer-1", upTo: 4, max: 3),
                Observed("producer-2", upTo: 1, max: 0),
            ]);

        result.Missing.ShouldBeEmpty();
        result.EventWatermark.ShouldBe(5);
    }

    [Fact]
    public void Event_Watermark_Counts_Beyond_Declared_And_Unplanned_Rows()
    {
        // The watermark is the stored event total, not the compared total: events past a declaration and events of a
        // producer outside the plan are stored events too, so both are summed while neither adds a reason for the
        // unplanned producer.
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 0, declaredCount: 1)],
            [
                Observed("producer-1", upTo: 1, beyond: 2, min: 0, max: 2),
                Observed("producer-9", upTo: 2, beyond: 3),
            ]);

        result.EventWatermark.ShouldBe(8);
        result.Missing.ShouldHaveSingleItem().ProducerId.ShouldBe("producer-1");
    }

    [Fact]
    public void Event_Watermark_Counts_A_Repeated_Producer_Row_Once()
    {
        // The grouped statement answers at most one row per producer, so a repeat is unreachable from the database; the
        // function ignores a repeat the same way it does for the reasons, so those events are never added twice.
        var result = ReconcileComparer.Compare(
            [Planned("producer-1")],
            [Observed("producer-1"), Observed("producer-1", upTo: 2, beyond: 1)]);

        result.EventWatermark.ShouldBe(1);
        result.Missing.ShouldBeEmpty();
    }

    [Fact]
    public void Event_Watermark_Of_A_Run_Without_Events_Is_Zero()
    {
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 0, declaredCount: 0)],
            []);

        result.Missing.ShouldBeEmpty();
        result.EventWatermark.ShouldBe(0);
    }

    [Fact]
    public void Shards_Planned_And_Closed_Are_Counted()
    {
        var result = ReconcileComparer.Compare(
            [
                Planned("producer-1", shardIndex: 1),
                Planned("producer-2", shardIndex: 2, closed: false),
                Planned("producer-3", shardIndex: 3, declaredLastSequence: 0, declaredCount: 0),
            ],
            []);

        result.ShardsPlanned.ShouldBe(3);
        result.ShardsClosed.ShouldBe(2);
        result.Missing.Select(missing => missing.Code).ShouldBe(
            [ReconcileMissingCodes.CountMismatch, ReconcileMissingCodes.MissingShard]);
    }

    // ---- every code is a 0049 literal and means incomplete evidence ------------------------

    [Fact]
    public void Every_Code_Is_A_Migration_Literal_And_Makes_The_Evidence_Incomplete()
    {
        // The migration's closed list is the one authority for these strings: the finisher persists the comparison into
        // evidence_revision, whose CHECK refuses a code outside it. Every literal this module names must be one of the
        // migration's, verbatim; the comparer emits all of them except artifact_missing, which the freeze result
        // contributes. The list is read from the latest file that defines the CHECK, because 0056 re-created it over the
        // 0049 codes plus the plan-coverage code.
        var migrationCodes = MigrationMissingCodes();
        migrationCodes.ShouldBe(
            ["missing_shard", "count_mismatch", "artifact_missing", "build_mismatch", "unknown_status", "setup_error", "stale_epoch", "missing_result"],
            "the migration CHECK's closed code list");

        string[] codeLiterals =
        [
            ReconcileMissingCodes.MissingShard,
            ReconcileMissingCodes.CountMismatch,
            ReconcileMissingCodes.UnknownStatus,
            ReconcileMissingCodes.SetupError,
            ReconcileMissingCodes.StaleEpoch,
            ReconcileMissingCodes.ArtifactMissing,
            ReconcileMissingCodes.MissingResult,
        ];

        codeLiterals.Where(code => !migrationCodes.Contains(code, StringComparer.Ordinal)).ShouldBeEmpty(
            "every reconciler code must be a literal of the migration CHECK");
        codeLiterals.Order(StringComparer.Ordinal).ToArray().ShouldBe(
            ["artifact_missing", "count_mismatch", "missing_result", "missing_shard", "setup_error", "stale_epoch", "unknown_status"],
            "the codes the reconciler names");

        // Each code the comparer itself emits leaves Missing non-empty, so the reconciler can never report matching
        // evidence (EventsMatch) while a shard or a planned execution is incomplete.
        (string Code, PlannedShard[] Planned, ObservedShard[] Observed, PlannedExecution[] Uncovered)[] cases =
        [
            (ReconcileMissingCodes.MissingShard, [Planned("producer-1", closed: false)], [], []),
            (ReconcileMissingCodes.CountMismatch, [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)], [Observed("producer-1", upTo: 2, min: 1, max: 2)], []),
            (ReconcileMissingCodes.UnknownStatus, [Planned("producer-1")], [Observed("producer-1", unknown: 1)], []),
            (ReconcileMissingCodes.SetupError, [Planned("producer-1")], [Observed("producer-1", blocked: 1)], []),
            (ReconcileMissingCodes.StaleEpoch, [Planned("producer-1")], [Observed("producer-1", stale: 1)], []),
            (ReconcileMissingCodes.MissingResult, [Planned("producer-1")], [Observed("producer-1")], [new PlannedExecution(Key(9), "producer-1")]),
        ];

        foreach (var testCase in cases)
        {
            var result = ReconcileComparer.Compare(testCase.Planned, testCase.Observed, testCase.Uncovered);

            result.Missing.ShouldNotBeEmpty($"the {testCase.Code} case must report a reason");
            result.Missing.ShouldContain(
                missing => missing.Code == testCase.Code,
                $"the {testCase.Code} case must name that code");
            result.EventsMatch.ShouldBeFalse($"the {testCase.Code} case is incomplete evidence");
            result.Missing.ShouldAllBe(missing => missing.ProducerId == "producer-1");
            result.Missing.ShouldAllBe(missing => !string.IsNullOrWhiteSpace(missing.Detail));
            result.ShardsPlanned.ShouldBe(1);
        }
    }

    [Fact]
    public void Multiple_Reasons_Across_Shards_Are_All_Reported()
    {
        var result = ReconcileComparer.Compare(
            [
                Planned("producer-1", shardIndex: 1, closed: false),
                Planned("producer-2", shardIndex: 2, declaredLastSequence: 2, declaredCount: 3),
                Planned("producer-3", shardIndex: 3, declaredLastSequence: 1, declaredCount: 2),
            ],
            [
                Observed("producer-2", upTo: 1, max: 0),
                Observed("producer-3", upTo: 2, unknown: 1, max: 1),
            ]);

        result.Missing.Select(missing => $"{missing.ProducerId}|{missing.Code}").ShouldBe(
            [
                "producer-1|missing_shard",
                "producer-2|count_mismatch",
                "producer-3|unknown_status",
            ]);
        result.ShardsPlanned.ShouldBe(3);
        result.ShardsClosed.ShouldBe(2);
    }

    // ---- plan coverage (a planned execution with no recorded attempt) --------------------

    [Fact]
    public void Uncovered_Execution_Is_MissingResult_For_Its_Shard_With_The_Execution_Key()
    {
        var result = ReconcileComparer.Compare(
            [Planned("producer-1")],
            [Observed("producer-1")],
            [new PlannedExecution(Key(7), "producer-1")]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.MissingResult);
        reason.ProducerId.ShouldBe("producer-1", "the reason names the shard whose plan row it is");
        reason.Detail.ShouldBe($"execution_key={Key(7)}", "the plan row's identity, never a payload");
        result.EventsMatch.ShouldBeFalse("counts that agree cannot prove a case that never attempted");
        result.ShardsPlanned.ShouldBe(1);
        result.ShardsClosed.ShouldBe(1);
    }

    [Fact]
    public void Every_Uncovered_Execution_Gets_Its_Own_Entry_Even_When_Every_Count_Agrees()
    {
        // The producer declared one event and stored exactly that one: the event evidence is complete, and the plan
        // still says two executions never reported. One entry per execution, in a stable order.
        var result = ReconcileComparer.Compare(
            [Planned("producer-1")],
            [Observed("producer-1")],
            [new PlannedExecution(Key(2), "producer-1"), new PlannedExecution(Key(1), "producer-1")]);

        result.Missing.Select(reason => reason.Detail).ShouldBe(
            [$"execution_key={Key(1)}", $"execution_key={Key(2)}"],
            "one entry per execution, ordered by the detail");
        result.Missing.ShouldAllBe(reason => reason.Code == ReconcileMissingCodes.MissingResult);
        result.EventsMatch.ShouldBeFalse();
    }

    [Fact]
    public void Covered_Plan_And_Matching_Events_Name_No_Reason()
    {
        var result = ReconcileComparer.Compare(
            [Planned("producer-1")],
            [Observed("producer-1")],
            []);

        result.Missing.ShouldBeEmpty("every planned execution reported an attempt");
        result.EventsMatch.ShouldBeTrue();
    }

    [Fact]
    public void Null_Coverage_Is_The_Event_Comparison_Alone_Not_A_Complete_Plan()
    {
        // The parameter is optional so the comparer can be used without the coverage read; null must read as "not read",
        // never as "nothing missing", which is why it cannot turn an uncovered plan into matching evidence.
        var result = ReconcileComparer.Compare([Planned("producer-1")], [Observed("producer-1")], uncovered: null);

        result.Missing.ShouldBeEmpty();
        result.EventsMatch.ShouldBeTrue("the caller asked for the event comparison only");
    }

    [Fact]
    public void Coverage_Reasons_Join_The_Event_Reasons_In_Producer_Then_Code_Order()
    {
        var result = ReconcileComparer.Compare(
            [
                Planned("producer-1", shardIndex: 1, closed: false),
                Planned("producer-2", shardIndex: 2),
            ],
            [Observed("producer-2")],
            [new PlannedExecution(Key(4), "producer-2"), new PlannedExecution(Key(3), "producer-1")]);

        result.Missing.Select(reason => $"{reason.ProducerId}|{reason.Code}").ShouldBe(
            [
                "producer-1|missing_result",
                "producer-1|missing_shard",
                "producer-2|missing_result",
            ],
            "the total order is producer id, then code, then detail");
    }

    // ---- the requirement set's bound (RequiredArtifactResolver.Compose) --------------------

    [Fact]
    public void RequiredSet_AtTheBound_IsEveryPair()
    {
        var kinds = new[] { "trace", "video" };
        var executions = Enumerable.Range(0, FreezeRequest.MaxRequired / 2).Select(Key).ToArray();

        var set = RequiredArtifactResolver.Compose(executions, kinds);

        set.Unsatisfiable.ShouldBeFalse();
        set.Specs.Count.ShouldBe(FreezeRequest.MaxRequired);
        set.Specs[0].ExecutionKey.ShouldBe(executions[0], "the pairs follow the read's execution order");
        set.Specs.Select(spec => spec.Kind).Distinct().ShouldBe(["trace", "video"]);
        set.Specs.ShouldAllBe(spec => spec.ReasonCode == ReconcileMissingCodes.ArtifactMissing);
    }

    [Fact]
    public void RequiredSet_ProductOverTheBound_IsUnsatisfiableWithoutSpecs()
    {
        var kinds = new[] { "trace", "video" };
        var executions = Enumerable.Range(0, (FreezeRequest.MaxRequired / 2) + 1).Select(Key).ToArray();

        var set = RequiredArtifactResolver.Compose(executions, kinds);

        set.Unsatisfiable.ShouldBeTrue();
        set.Specs.ShouldBeEmpty("an over-bound set is never truncated into a satisfiable-looking subset");
    }

    [Fact]
    public void RequiredSet_ExecutionCountOverTheBound_IsUnsatisfiable()
    {
        // One kind is enough: the executions alone are already past what one FreezeRequest accepts.
        var executions = Enumerable.Range(0, FreezeRequest.MaxRequired + 1).Select(Key).ToArray();

        var set = RequiredArtifactResolver.Compose(executions, ["trace"]);

        set.Unsatisfiable.ShouldBeTrue();
        set.Specs.ShouldBeEmpty();
    }

    [Fact]
    public void RequiredSet_EmptyHalf_IsEmptyAndSatisfiable()
    {
        // A run with no planned execution, or a policy that requires no kind, has nothing to require — and nothing is
        // unsatisfiable about it (the empty set is a legitimate answer, D-S123-Q3).
        var noExecution = RequiredArtifactResolver.Compose([], ["trace"]);
        noExecution.Unsatisfiable.ShouldBeFalse();
        noExecution.Specs.ShouldBeEmpty();

        var noKind = RequiredArtifactResolver.Compose([Key(0)], []);
        noKind.Unsatisfiable.ShouldBeFalse();
        noKind.Specs.ShouldBeEmpty();

        RequiredSet.None.Unsatisfiable.ShouldBeFalse();
        RequiredSet.None.Specs.ShouldBeEmpty();
    }

    /// <summary>An execution key of the 00 §7 shape: 64 lowercase hex characters, one per index.</summary>
    /// <param name="index">The index to encode.</param>
    private static string Key(int index) => index.ToString("x8", CultureInfo.InvariantCulture).PadLeft(64, '0');

    // ---- argument guards -----------------------------------------------------------------

    [Fact]
    public void Compare_Rejects_Null_Lists()
    {
        Should.Throw<ArgumentNullException>(() => ReconcileComparer.Compare(null!, []));
        Should.Throw<ArgumentNullException>(() => ReconcileComparer.Compare([], null!));
    }

    // ---- builders ------------------------------------------------------------------------

    private static PlannedShard Planned(
        string producerId,
        int shardIndex = 1,
        bool closed = true,
        long? declaredLastSequence = 0,
        int? declaredCount = 1) =>
        new(producerId, shardIndex, closed, declaredLastSequence, declaredCount);

    private static ObservedShard Observed(
        string producerId,
        long upTo = 1,
        long beyond = 0,
        long unknown = 0,
        long unfinished = 0,
        long? min = 0,
        long? max = 0,
        long stale = 0,
        long blocked = 0) =>
        new(producerId, upTo, beyond, min, max, unknown, unfinished, stale, blocked);

    /// <summary>
    /// The closed code list of the CHECK as the latest migration leaves it —
    /// <c>migrations/0056_reconcile_missing_result.sql</c>, which re-creates <c>evidence_revision_missing_check</c> over
    /// 0049's list plus the plan-coverage code — read from the <c>missing &lt;@ array[...]</c> term. This is the pin that
    /// this comparer's literals and the persisted CHECK cannot drift apart.
    /// </summary>
    private static string[] MigrationMissingCodes()
    {
        var path = Path.Combine(RepositoryRoot(), "migrations", "0056_reconcile_missing_result.sql");
        File.Exists(path).ShouldBeTrue($"the evidence_revision CHECK migration must be committed at {path}");

        var sql = File.ReadAllText(path);
        var start = sql.IndexOf("missing <@ array[", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1, "0056 must list the codes with 'missing <@ array['");

        var end = sql.IndexOf("]::text[]", start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, "the 0056 code list must close with ']::text[]'");

        return Regex.Matches(sql[start..end], "'([a-z_]+)'")
            .Select(match => match.Groups[1].Value)
            .ToArray();
    }

    /// <summary>The repository root: the directory holding <c>Evidgate.slnx</c> above the test binary.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Evidgate.slnx not found above {AppContext.BaseDirectory}");
    }
}
