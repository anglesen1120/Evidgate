using System.Globalization;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The plan-coverage half of the comparer, without a database: a planned execution with no recorded attempt is named
/// <c>missing_result</c> however well the event counts agree — one entry per execution while the bounded read returned
/// them all, plus one counted entry when it stopped at its cap, so a huge plan is never understated and never
/// materialised. The read itself is exercised by the reconcile integration suite.
/// </summary>
public sealed class ReconcileComparerCoverageTests
{
    [Fact]
    public void Uncovered_Execution_Is_MissingResult_For_Its_Shard_With_The_Execution_Key()
    {
        var result = ReconcileComparer.Compare(
            [Planned("producer-1")],
            [Observed("producer-1")],
            Coverage(new PlannedExecution(Key(7), "producer-1")));

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
            Coverage(new PlannedExecution(Key(2), "producer-1"), new PlannedExecution(Key(1), "producer-1")));

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
            ExecutionCoverage.Complete);

        result.Missing.ShouldBeEmpty("every planned execution reported an attempt");
        result.EventsMatch.ShouldBeTrue();
    }

    [Fact]
    public void Event_Half_Alone_Names_No_MissingResult()
    {
        // The event half is what a waiting attempt reports: it reads no coverage, so it can never claim the plan is
        // answered either — an uncovered plan reads as matching events here.
        var result = ReconcileComparer.CompareEvents([Planned("producer-1")], [Observed("producer-1")]);

        result.Missing.ShouldBeEmpty("only the coverage read can see a planned execution nobody attempted");
        result.EventsMatch.ShouldBeTrue();
        result.ShardsPlanned.ShouldBe(1);
        result.ShardsClosed.ShouldBe(1);
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
            Coverage(new PlannedExecution(Key(4), "producer-2"), new PlannedExecution(Key(3), "producer-1")));

        result.Missing.Select(reason => $"{reason.ProducerId}|{reason.Code}").ShouldBe(
            [
                "producer-1|missing_result",
                "producer-1|missing_shard",
                "producer-2|missing_result",
            ],
            "the total order is producer id, then code, then detail");
    }

    [Fact]
    public void Coverage_At_The_Read_Cap_Still_Names_One_Entry_Per_Execution()
    {
        var uncovered = Uncovered(ReconcileSql.UncoveredExecutionLimit);

        var result = ReconcileComparer.Compare(
            [Planned("producer-1")],
            [Observed("producer-1")],
            new ExecutionCoverage(uncovered, uncovered.Length));

        result.Missing.Count.ShouldBe(ReconcileSql.UncoveredExecutionLimit);
        result.Missing.ShouldAllBe(reason => reason.Code == ReconcileMissingCodes.MissingResult);
        result.Missing.ShouldAllBe(reason => reason.ProducerId == "producer-1");
        result.Missing.Select(reason => reason.Detail).ShouldBe(
            uncovered.Select(execution => $"execution_key={execution.ExecutionKey}"),
            "the entries already state every execution, so no counted entry repeats the total");
    }

    [Fact]
    public void Coverage_Over_The_Read_Cap_Names_What_It_Read_And_The_True_Total()
    {
        // One execution more than the read returns: the entries stop at the cap, and the counted entry keeps the plan
        // gap at its true size instead of understating it.
        var uncovered = Uncovered(ReconcileSql.UncoveredExecutionLimit);

        var result = ReconcileComparer.Compare(
            [Planned("producer-1")],
            [Observed("producer-1")],
            new ExecutionCoverage(uncovered, uncovered.Length + 1));

        result.Missing.Count.ShouldBe(ReconcileSql.UncoveredExecutionLimit + 1);
        result.Missing.ShouldAllBe(reason => reason.Code == ReconcileMissingCodes.MissingResult);
        result.EventsMatch.ShouldBeFalse("a truncated read still names the reason");

        var counted = result.Missing.Single(reason => reason.ProducerId is null);
        counted.Detail.ShouldBe(
            string.Create(
                CultureInfo.InvariantCulture,
                $"uncovered_total={ReconcileSql.UncoveredExecutionLimit + 1};listed={ReconcileSql.UncoveredExecutionLimit}"),
            "the true total and how much of it the entries cover");
        result.Missing.Count(reason => reason.ProducerId == "producer-1").ShouldBe(ReconcileSql.UncoveredExecutionLimit);
    }

    // ---- builders ------------------------------------------------------------------------

    /// <summary>An execution key in the shape the plan stores: 64 lowercase hex characters, one per index.</summary>
    /// <param name="index">The index to encode.</param>
    private static string Key(int index) => index.ToString("x8", CultureInfo.InvariantCulture).PadLeft(64, '0');

    /// <summary>The bounded-read answer for the executions a case names, with its total equal to what it lists.</summary>
    /// <param name="uncovered">The executions the read answered.</param>
    private static ExecutionCoverage Coverage(params PlannedExecution[] uncovered) => new(uncovered, uncovered.Length);

    /// <summary>A plan of <paramref name="count"/> executions of one shard, all with no recorded attempt.</summary>
    /// <param name="count">How many executions to name.</param>
    private static PlannedExecution[] Uncovered(int count) =>
        [.. Enumerable.Range(0, count).Select(index => new PlannedExecution(Key(index), "producer-1"))];

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
        long blocked = 0) =>
        new(producerId, upTo, beyond, min, max, unknown, unfinished, blocked);
}
