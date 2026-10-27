using System.Text.RegularExpressions;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.2-T2.2 without a database: the pure declared-versus-observed comparer — every 00 §8.5 code it can name from
/// event evidence, the open-shard short circuit, the fail-closed missing declaration, the three bounds a closed shard's
/// declaration is checked against (count, end and the contiguity rule of <c>contracts/events/ordering.md</c> §3), the
/// ordinal reason order and the planned/closed shard counts — plus the pin that the three literals are the event-evidence
/// subset of the closed list <c>migrations/0049</c> enforces. The database half (the one grouped aggregate, the run lock)
/// is exercised by the reconcile integration suite.
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
            "declared_count=3;observed=3;declared_last_sequence=3;beyond=0;max_sequence=2;min_sequence=0;contiguous_count=4");
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
            "declared_count=5;observed=5;declared_last_sequence=5;beyond=0;min_sequence=0;contiguous_count=6");
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
            "declared_count=3;observed=2;declared_last_sequence=2;beyond=0;max_sequence=1;min_sequence=0;contiguous_count=3");
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
    public void Every_Code_Is_A_0049_Literal_And_Makes_The_Evidence_Incomplete()
    {
        // The migration's closed list is the one authority for these strings: T2.4 persists the comparison into
        // evidence_revision, whose CHECK refuses a code outside it. This comparer names the event-evidence subset only
        // (T2.3/T2.4 own artifact_missing, setup_error and stale_epoch), and each of its literals must be one of the
        // migration's, verbatim.
        var migrationCodes = MigrationMissingCodes();
        migrationCodes.ShouldBe(
            ["missing_shard", "count_mismatch", "artifact_missing", "build_mismatch", "unknown_status", "setup_error", "stale_epoch"],
            "the 0049 CHECK's closed code list");

        string[] codeLiterals =
        [
            ReconcileMissingCodes.MissingShard,
            ReconcileMissingCodes.CountMismatch,
            ReconcileMissingCodes.UnknownStatus,
        ];

        // Each literal is a migration literal, and together they are exactly the event-evidence subset.
        codeLiterals.Where(code => !migrationCodes.Contains(code, StringComparer.Ordinal)).ShouldBeEmpty(
            "every comparer code must be a 0049 literal");
        codeLiterals.Order(StringComparer.Ordinal).ToArray().ShouldBe(
            ["count_mismatch", "missing_shard", "unknown_status"],
            "the event-evidence codes of 05.3.2-T2.2");

        // Each code leaves Missing non-empty, so the reconciler can never report matching evidence (EventsMatch) while
        // a shard is incomplete.
        (string Code, PlannedShard[] Planned, ObservedShard[] Observed)[] cases =
        [
            (ReconcileMissingCodes.MissingShard, [Planned("producer-1", closed: false)], []),
            (ReconcileMissingCodes.CountMismatch, [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)], [Observed("producer-1", upTo: 2, min: 1, max: 2)]),
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
        long? max = 0) =>
        new(producerId, upTo, beyond, min, max, unknown, unfinished);

    /// <summary>
    /// The closed code list of <c>migrations/0049_orchestration_evidence_revision.sql</c>, read from the
    /// <c>missing &lt;@ array[...]</c> term of <c>evidence_revision_missing_check</c> — the pin that this comparer's
    /// literals and the persisted CHECK cannot drift apart.
    /// </summary>
    private static string[] MigrationMissingCodes()
    {
        var path = Path.Combine(RepositoryRoot(), "migrations", "0049_orchestration_evidence_revision.sql");
        File.Exists(path).ShouldBeTrue($"the evidence_revision migration must be committed at {path}");

        var sql = File.ReadAllText(path);
        var start = sql.IndexOf("missing <@ array[", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1, "0049 must list the codes with 'missing <@ array['");

        var end = sql.IndexOf("]::text[]", start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, "the 0049 code list must close with ']::text[]'");

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
