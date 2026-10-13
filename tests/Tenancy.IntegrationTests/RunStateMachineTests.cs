using Evidgate.Modules.Orchestrator.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.1-T1.1/T1.2 without a database: the 00 §8.1 adjacency table is the single place an edge is decided
/// (00 §5.5 C6, ruling T82), so the accepted edges, the no-op classification (current, already-passed or
/// terminal source) and the rejected forward-skips are pinned as a property over every state pair.
/// <para>
/// The expectation is computed here from the plan's own rules (05.3.1 §6 and T82), independently of the
/// domain table: a change to <c>RunStateMachine</c>'s table that is not also a plan change fails this file.
/// </para>
/// </summary>
public sealed class RunStateMachineTests
{
    /// <summary>The 00 §8.1 vocabulary, the exact set 0031's <c>run_state_check</c> enumerates.</summary>
    private static readonly string[] Vocabulary =
        ["created", "queued", "running", "collecting", "complete", "incomplete", "canceled"];

    /// <summary>§6's canonical adjacency table, written out here so a domain-table change fails this file.</summary>
    private static readonly (string From, string To)[] DeclaredEdges =
    [
        ("created", "queued"),
        ("queued", "running"),
        ("running", "collecting"),
        ("collecting", "complete"),
        ("collecting", "incomplete"),
        ("queued", "incomplete"),
        ("running", "incomplete"),
        ("created", "canceled"),
        ("queued", "canceled"),
        ("running", "canceled"),
        ("collecting", "canceled"),
    ];

    /// <summary>The forward path's positions, in state order (the plan §6 path <c>created → … → collecting</c>).</summary>
    private static readonly Dictionary<string, int> ForwardRank =
        new() { ["created"] = 0, ["queued"] = 1, ["running"] = 2, ["collecting"] = 3 };

    /// <summary>§6: <c>complete</c>, <c>incomplete</c> and <c>canceled</c> have no outgoing edge.</summary>
    private static readonly string[] Terminal = ["complete", "incomplete", "canceled"];

    /// <summary>Every state pair of the vocabulary, the domain of the property cases.</summary>
    private static readonly (string From, string To)[] EveryPair =
        [.. Vocabulary.SelectMany(from => Vocabulary.Select(to => (from, to)))];

    /// <summary>§6's declared edges, for the accepted-edge cases.</summary>
    public static TheoryData<string, string> Declared() => Pairs(DeclaredEdges);

    /// <summary>Every state pair, for the classification property.</summary>
    public static TheoryData<string, string> AllPairs() => Pairs(EveryPair);

    /// <summary>AC1/§6: every declared edge is the applied one.</summary>
    [Theory]
    [MemberData(nameof(Declared))]
    public void RunStateMachine_AcceptsEachDeclaredEdge(string from, string to)
    {
        RunStateMachine.DeclaredEdges.ShouldContain((from, to));
        RunStateMachine.IsKnownState(from).ShouldBeTrue(from);
        RunStateMachine.IsKnownState(to).ShouldBeTrue(to);
        RunStateMachine.Classify(from, to).ShouldBe(RunTransitionDecision.Apply);
    }

    /// <summary>
    /// AC2/AC3 (T82, §9): over every state pair, the classification is exactly the plan's — a declared edge
    /// applies, the current/passed/terminal-source pairs are no-ops, and every other pair is a forward-skip
    /// that is never applied.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllPairs))]
    public void RunStateMachine_RejectsForwardSkips(string from, string to)
    {
        var expected = DeclaredEdges.Contains((from, to))
            ? RunTransitionDecision.Apply
            : IsNoOp((from, to))
                ? RunTransitionDecision.NoOp
                : RunTransitionDecision.ForwardSkip;

        RunStateMachine.Classify(from, to).ShouldBe(expected, $"{from} -> {to}");
    }

    /// <summary>AC2: the named no-op examples of the plan §2 — a passed state, the current state, a terminal source.</summary>
    [Theory]
    [InlineData("running", "queued", "a passed state")]
    [InlineData("running", "created", "a passed state")]
    [InlineData("running", "running", "the current state")]
    [InlineData("queued", "created", "a passed state")]
    [InlineData("queued", "queued", "the current state")]
    [InlineData("collecting", "running", "a passed state")]
    [InlineData("collecting", "collecting", "the current state")]
    [InlineData("complete", "complete", "a terminal source")]
    [InlineData("complete", "running", "a terminal source")]
    [InlineData("incomplete", "queued", "a terminal source")]
    [InlineData("canceled", "collecting", "a terminal source")]
    public void RunStateMachine_NoOpCases(string from, string to, string why)
    {
        RunStateMachine.Classify(from, to).ShouldBe(RunTransitionDecision.NoOp, $"{from} -> {to} is {why}");
    }

    /// <summary>AC3: the named forward-skip examples — <c>running → complete</c> skips <c>collecting</c>.</summary>
    [Theory]
    [InlineData("running", "complete")]
    [InlineData("queued", "collecting")]
    [InlineData("created", "running")]
    [InlineData("created", "complete")]
    [InlineData("queued", "complete")]
    public void RunStateMachine_ForwardSkips_AreRejected(string from, string to) =>
        RunStateMachine.Classify(from, to).ShouldBe(RunTransitionDecision.ForwardSkip, $"{from} -> {to}");

    /// <summary>
    /// The domain vocabulary is exactly the 00 §8.1 set 0031's <c>run_state_check</c> enumerates, in state
    /// order, and the declared-edge table is §6's — so a state or edge added to one list and not the other
    /// fails here (the database's copy of the vocabulary is pinned by
    /// <c>RunCreationShardStateTests.CreateRun_StateCheckConstraint_EnumeratesTheFullVocabulary</c>).
    /// </summary>
    [Fact]
    public void RunStateMachine_AllStates_MatchTheRunStateCheckVocabulary()
    {
        RunStateMachine.AllStates.ShouldBe(Vocabulary);
        RunStateMachine.DeclaredEdges.Count.ShouldBe(DeclaredEdges.Length);
        RunStateMachine.DeclaredEdges.ShouldBe(DeclaredEdges, ignoreOrder: true);
    }

    /// <summary>§6: the three terminal states are terminal, every other vocabulary state is not, and none has an outgoing edge.</summary>
    [Fact]
    public void RunStateMachine_TerminalStates_AreExactlyTheThree()
    {
        foreach (var state in Vocabulary)
        {
            RunStateMachine.IsTerminal(state).ShouldBe(Terminal.Contains(state), state);
        }

        foreach (var state in Terminal)
        {
            foreach (var target in Vocabulary)
            {
                RunStateMachine.Classify(state, target).ShouldBe(RunTransitionDecision.NoOp, $"{state} -> {target}");
            }
        }
    }

    /// <summary>The plan's no-op rule: same state, a forward-path state already passed, or a terminal source.</summary>
    private static bool IsNoOp((string From, string To) pair) =>
        pair.From == pair.To
        || Terminal.Contains(pair.From)
        || (ForwardRank.TryGetValue(pair.To, out var toRank) && toRank < ForwardRank[pair.From]);

    private static TheoryData<string, string> Pairs(IEnumerable<(string From, string To)> pairs)
    {
        var data = new TheoryData<string, string>();
        foreach (var (from, to) in pairs)
        {
            data.Add(from, to);
        }

        return data;
    }
}
