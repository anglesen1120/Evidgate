using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReconcileComparerUnitTests
{
    // ---- unknown status -------------------------------------------------------------------

    [Fact]
    public void Unknown_Status_With_Matching_Counts_Is_UnknownStatus()
    {
        var result = ReconcileComparer.CompareEvents(
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
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 0, declaredCount: 1)],
            [Observed("producer-1", unfinished: 1)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.UnknownStatus);
        reason.Detail.ShouldBe("null_status=0;unfinished=1");
    }

    [Fact]
    public void Null_Status_And_Unfinished_Attempts_Are_Counted_Together()
    {
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", upTo: 3, unknown: 2, unfinished: 3, max: 2)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.UnknownStatus);
        reason.Detail.ShouldBe("null_status=2;unfinished=3");
    }

    // ---- readiness reasons derived from the event flags (05.3.2-T2.3) ----------------------

    [Fact]
    public void A_Blocked_Finish_With_Matching_Counts_Is_SetupError()
    {
        // A blocked finish is a failed setup (00 §8.3): the shard's own counts can still agree and the evidence is not
        // complete.
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", upTo: 3, max: 2, blocked: 2)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.SetupError);
        reason.ProducerId.ShouldBe("producer-1");
        reason.Detail.ShouldBe("blocked=2");
        result.EventsMatch.ShouldBeFalse();
    }

    [Fact]
    public void An_Open_Shard_Is_Not_Compared_For_Blocked_Events()
    {
        // The open shard is reported once and its events stay unexamined, so the flag count adds no second reason.
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", shardIndex: 4, closed: false)],
            [Observed("producer-1", upTo: 3, beyond: 3, blocked: 1)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.MissingShard);
        reason.Detail.ShouldBe("shard_index=4");
    }

    [Fact]
    public void A_Stale_Event_On_A_Closed_Shard_Is_StaleEpoch()
    {
        // Foreign-epoch evidence is stored, never dropped, and it cannot leave the shard complete: one entry however
        // many of its events are stale, and the shard's own counts may still agree.
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", upTo: 3, max: 2, stale: 2)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.StaleEpoch);
        reason.ProducerId.ShouldBe("producer-1");
        reason.Detail.ShouldBe("stale=2");
        result.EventsMatch.ShouldBeFalse();
    }

    [Fact]
    public void An_Open_Shard_Is_Not_Compared_For_Stale_Events()
    {
        // The open shard is reported once and its events stay unexamined, so the stale count adds no second reason.
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", shardIndex: 4, closed: false)],
            [Observed("producer-1", upTo: 3, beyond: 3, stale: 3)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.MissingShard);
        reason.Detail.ShouldBe("shard_index=4");
    }

    [Fact]
    public void A_Closed_Shard_Without_Stale_Events_Names_No_EpochReason()
    {
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1")],
            [Observed("producer-1")]);

        result.Missing.ShouldBeEmpty();
        result.Missing.Any(reason => reason.Code == ReconcileMissingCodes.StaleEpoch)
            .ShouldBeFalse("zero stale events name no epoch reason");
    }

    [Fact]
    public void Stale_Evidence_Does_Not_Hide_The_Count_Mismatch()
    {
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", upTo: 2, max: 1, blocked: 1, stale: 1)]);

        result.Missing.Select(missing => missing.Code).ShouldBe(
            [
                ReconcileMissingCodes.CountMismatch,
                ReconcileMissingCodes.SetupError,
                ReconcileMissingCodes.StaleEpoch,
            ]);
        result.Missing[1].Detail.ShouldBe("blocked=1");
        result.Missing[2].Detail.ShouldBe("stale=1");
    }

    [Fact]
    public void The_Setup_Reason_Does_Not_Hide_The_Count_Mismatch()
    {
        // One closed shard can break every bound at once: each reason is its own entry, and the count mismatch keeps its
        // own detail instead of being folded into the flag.
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", upTo: 2, max: 1, blocked: 1, unknown: 1)]);

        result.Missing.Select(missing => missing.Code).ShouldBe(
            [
                ReconcileMissingCodes.CountMismatch,
                ReconcileMissingCodes.SetupError,
                ReconcileMissingCodes.UnknownStatus,
            ]);
        result.Missing[1].Detail.ShouldBe("blocked=1");
        result.Missing[2].Detail.ShouldBe("null_status=1;unfinished=0");
    }

    [Fact]
    public void A_Closed_Shard_Without_Blocked_Events_Names_No_Flag_Reason()
    {
        // The flag is the only source of the setup reason: a zero count adds nothing to an otherwise matching shard.
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 1, declaredCount: 2)],
            [Observed("producer-1", upTo: 2, max: 1)]);

        result.Missing.ShouldBeEmpty();
        result.EventsMatch.ShouldBeTrue();
    }

    // ---- code order and producer order ----------------------------------------------------

    [Fact]
    public void A_Shard_Reports_CountMismatch_Before_UnknownStatus()
    {
        var result = ReconcileComparer.CompareEvents(
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

        var result = ReconcileComparer.CompareEvents(planned, observed);

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
        var result = ReconcileComparer.CompareEvents(
            [
                Planned("b", shardIndex: 2, declaredLastSequence: 0, declaredCount: 1),
                Planned("A", shardIndex: 1, declaredLastSequence: 0, declaredCount: 1),
            ],
            [
                Observed("b", unknown: 3),
                Observed("A", blocked: 2),
            ]);

        // Ordinal again: "A" before "b", and each shard names its own reason only.
        result.Missing.Select(missing => $"{missing.ProducerId}|{missing.Code}|{missing.Detail}").ShouldBe(
            [
                "A|setup_error|blocked=2",
                "b|unknown_status|null_status=3;unfinished=0",
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

        var forward = ReconcileComparer.CompareEvents(planned, observed);
        var reversed = ReconcileComparer.CompareEvents([.. planned.Reverse()], [.. observed.Reverse()]);

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

        var first = ReconcileComparer.CompareEvents(planned, observed);
        var second = ReconcileComparer.CompareEvents(planned, observed);

        second.Missing.ShouldBe(first.Missing);
        second.Missing.Select(missing => missing.Detail).ShouldBe(first.Missing.Select(missing => missing.Detail));
        second.ShardsPlanned.ShouldBe(first.ShardsPlanned);
        second.ShardsClosed.ShouldBe(first.ShardsClosed);
        second.EventsMatch.ShouldBe(first.EventsMatch);
    }
}
