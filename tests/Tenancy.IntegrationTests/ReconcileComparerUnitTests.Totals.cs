using System.Globalization;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReconcileComparerUnitTests
{
    // ---- scope and totals ----------------------------------------------------------------

    [Fact]
    public void Observed_Rows_Outside_The_Plan_Are_Ignored()
    {
        var result = ReconcileComparer.CompareEvents(
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
        var result = ReconcileComparer.CompareEvents(
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
        var result = ReconcileComparer.CompareEvents(
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
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1")],
            [Observed("producer-1"), Observed("producer-1", upTo: 2, beyond: 1)]);

        result.EventWatermark.ShouldBe(1);
        result.Missing.ShouldBeEmpty();
    }

    [Fact]
    public void Event_Watermark_Of_A_Run_Without_Events_Is_Zero()
    {
        var result = ReconcileComparer.CompareEvents(
            [Planned("producer-1", declaredLastSequence: 0, declaredCount: 0)],
            []);

        result.Missing.ShouldBeEmpty();
        result.EventWatermark.ShouldBe(0);
    }

    [Fact]
    public void Shards_Planned_And_Closed_Are_Counted()
    {
        var result = ReconcileComparer.CompareEvents(
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
        (string Code, PlannedShard[] Planned, ObservedShard[] Observed, ExecutionCoverage Coverage)[] cases =
        [
            (ReconcileMissingCodes.MissingShard, [Planned("producer-1", closed: false)], [], ExecutionCoverage.Complete),
            (ReconcileMissingCodes.CountMismatch, [Planned("producer-1", declaredLastSequence: 2, declaredCount: 3)], [Observed("producer-1", upTo: 2, min: 1, max: 2)], ExecutionCoverage.Complete),
            (ReconcileMissingCodes.UnknownStatus, [Planned("producer-1")], [Observed("producer-1", unknown: 1)], ExecutionCoverage.Complete),
            (ReconcileMissingCodes.SetupError, [Planned("producer-1")], [Observed("producer-1", blocked: 1)], ExecutionCoverage.Complete),
            (ReconcileMissingCodes.StaleEpoch, [Planned("producer-1")], [Observed("producer-1", stale: 1)], ExecutionCoverage.Complete),
            (ReconcileMissingCodes.MissingResult, [Planned("producer-1")], [Observed("producer-1")], new ExecutionCoverage([new PlannedExecution(Key(9), "producer-1")], 1)),
        ];

        foreach (var testCase in cases)
        {
            var result = ReconcileComparer.Compare(testCase.Planned, testCase.Observed, testCase.Coverage);

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
        var result = ReconcileComparer.CompareEvents(
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
}
