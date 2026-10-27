using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.2-T2.2 without a database: the pure declared-versus-observed comparer — every 00 §8.5 code it can name from
/// event evidence, the open-shard short circuit, the fail-closed missing declaration, the ordinal reason order and the
/// planned/closed shard counts. The database half (the one grouped aggregate, the run lock) is exercised by the
/// reconcile integration suite.
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
                Observed("producer-2", total: 4, upTo: 4, max: 3),
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
    public void TotalCount_Alone_Is_Not_Compared()
    {
        // CountUpToDeclared + CountBeyondDeclared == TotalCount by construction of the aggregate, so the comparer reads
        // the two declared boundaries only and never trusts the total on its own.
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 0, declaredCount: 1)],
            [Observed("producer-1", total: 99, upTo: 1)]);

        result.Missing.ShouldBeEmpty();
    }

    // ---- missing shard --------------------------------------------------------------------

    [Fact]
    public void Open_Shard_Is_One_MissingShard_And_Its_Events_Are_Not_Compared()
    {
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", shardIndex: 2, closed: false, declaredLastSequence: null, declaredCount: null)],
            [Observed("producer-1", total: 5, upTo: 5, beyond: 5, unknown: 3, unfinished: 2)]);

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
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", total: 2, upTo: 2, max: 1)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.ProducerId.ShouldBe("producer-1");
        reason.Detail.ShouldBe("declared_count=3;observed=2;declared_last_sequence=2;beyond=0");
    }

    [Fact]
    public void Count_Over_The_Declaration_Is_CountMismatch()
    {
        // Sequences 0, 1 and 2 fit under the declared high-water mark 5 while only 2 events were declared.
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 5, declaredCount: 2)],
            [Observed("producer-1", total: 3, upTo: 3, max: 2)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.Detail.ShouldBe("declared_count=2;observed=3;declared_last_sequence=5;beyond=0");
    }

    [Fact]
    public void Events_Beyond_The_Declared_Last_Sequence_Are_CountMismatch()
    {
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", total: 4, upTo: 3, beyond: 1, max: 3)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.CountMismatch);
        reason.Detail.ShouldBe("declared_count=3;observed=3;declared_last_sequence=2;beyond=1");
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
            [Observed("producer-1", total: 2, upTo: 2, unknown: 1, max: 1)]);

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
            [Observed("producer-1", total: 3, upTo: 3, unknown: 2, unfinished: 3, max: 2)]);

        var reason = result.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.UnknownStatus);
        reason.Detail.ShouldBe("null_status=2;unfinished=3");
    }

    // ---- code order and producer order ----------------------------------------------------

    [Fact]
    public void A_Shard_Reports_CountMismatch_Before_UnknownStatus()
    {
        var result = ReconcileComparer.Compare(
            [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)],
            [Observed("producer-1", total: 2, upTo: 2, unknown: 2, unfinished: 1, max: 1)]);

        result.Missing.Select(missing => missing.Code).ShouldBe(
            [ReconcileMissingCodes.CountMismatch, ReconcileMissingCodes.UnknownStatus]);
        result.Missing[0].Detail.ShouldBe("declared_count=3;observed=2;declared_last_sequence=2;beyond=0");
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
            Observed("A", total: 4, upTo: 4, beyond: 4),
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
            Observed("c", total: 1, upTo: 1),
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
                Observed("producer-9", total: 6, upTo: 6, beyond: 6, unknown: 6, unfinished: 6),
            ]);

        result.Missing.ShouldBeEmpty();
        result.ShardsPlanned.ShouldBe(1);
        result.ShardsClosed.ShouldBe(1);
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

    // ---- every code means incomplete evidence --------------------------------------------

    [Fact]
    public void Every_Code_Makes_The_Evidence_Incomplete()
    {
        // The three codes this comparer can name from event evidence; each must leave Missing non-empty so the
        // reconciler can never report matching evidence (EventsMatch) while a shard is incomplete.
        (string Code, PlannedShard[] Planned, ObservedShard[] Observed)[] cases =
        [
            (ReconcileMissingCodes.MissingShard, [Planned("producer-1", closed: false)], []),
            (ReconcileMissingCodes.CountMismatch, [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)], [Observed("producer-1", total: 2, upTo: 2, max: 1)]),
            (ReconcileMissingCodes.UnknownStatus, [Planned("producer-1")], [Observed("producer-1", unknown: 1)]),
        ];

        foreach (var testCase in cases)
        {
            var result = ReconcileComparer.Compare(testCase.Planned, testCase.Observed);

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
                Observed("producer-2", total: 1, upTo: 1, max: 0),
                Observed("producer-3", total: 2, upTo: 2, unknown: 1, max: 1),
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
        long total = 1,
        long upTo = 1,
        long beyond = 0,
        long unknown = 0,
        long unfinished = 0,
        long? min = 0,
        long? max = 0) =>
        new(producerId, total, upTo, beyond, min, max, unknown, unfinished);
}
