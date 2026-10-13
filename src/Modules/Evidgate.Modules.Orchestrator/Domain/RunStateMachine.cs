namespace Evidgate.Modules.Orchestrator.Domain;

/// <summary>
/// The 00 §8.1 run state vocabulary, the same closed set 0031's <c>run_state_check</c> enumerates
/// (<c>created</c>, <c>queued</c>, <c>running</c>, <c>collecting</c>, <c>complete</c>,
/// <c>incomplete</c>, <c>canceled</c>).
/// </summary>
internal static class RunStates
{
    /// <summary>The run's first state; never persisted — 05.2.1-T4 inserts the run as <c>queued</c>.</summary>
    internal const string Created = "created";

    /// <summary>The run is accepted and its producer slots are committed.</summary>
    internal const string Queued = "queued";

    /// <summary>The first accepted event (CI mode) or first claim (agent mode) arrived.</summary>
    internal const string Running = "running";

    /// <summary>The first finalize arrived; the reconciler is collecting evidence (05.3.2-T1).</summary>
    internal const string Collecting = "collecting";

    /// <summary>Terminal: the reconciler found complete evidence with no block (05.3.2-T2).</summary>
    internal const string Complete = "complete";

    /// <summary>Terminal: the collection deadline passed without complete evidence (05.3.2-T3).</summary>
    internal const string Incomplete = "incomplete";

    /// <summary>Terminal: a cancel request was accepted (05.4.1-T1).</summary>
    internal const string Canceled = "canceled";
}

/// <summary>How a requested run transition is classified against the 00 §8.1 adjacency table (05.3.1 T82).</summary>
internal enum RunTransitionDecision
{
    /// <summary>A declared forward edge between non-terminal states: apply it.</summary>
    Apply,

    /// <summary>The run already holds the target, has passed it on the forward path, or is terminal: return the current state, write nothing.</summary>
    NoOp,

    /// <summary>An undeclared edge to a state the run has not passed (e.g. <c>running → complete</c>): refuse it.</summary>
    ForwardSkip,
}

/// <summary>
/// The 00 §8.1 run state machine as an explicit adjacency table (05.3.1-T1.1, ruling T82) — the single
/// source of truth every caller consults, so no caller re-implements state logic (00 §5.5 C6).
/// <para>
/// The forward path is <c>created → queued → running → collecting</c>; <c>complete</c>,
/// <c>incomplete</c> and <c>canceled</c> are terminal (no outgoing edge). A request for the current
/// state or a state already passed on the forward path is a no-op returning the current state, and so
/// is any request out of a terminal state (AC2/AC3): only an undeclared forward-skip between
/// non-terminal states is a <see cref="RunTransitionDecision.ForwardSkip"/> (AC3).
/// </para>
/// </summary>
internal static class RunStateMachine
{
    /// <summary>The 1-based position of each non-terminal state on the forward path, in state order.</summary>
    private static readonly Dictionary<string, int> ForwardRank = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [RunStates.Created] = 0,
        [RunStates.Queued] = 1,
        [RunStates.Running] = 2,
        [RunStates.Collecting] = 3,
    };

    /// <summary>The terminal states: no outgoing edge, so a request out of one is always a no-op.</summary>
    private static readonly HashSet<string> TerminalStates = new HashSet<string>(StringComparer.Ordinal)
    {
        RunStates.Complete,
        RunStates.Incomplete,
        RunStates.Canceled,
    };

    /// <summary>Every declared edge of 00 §8.1 (feature plan 05.3.1 §6), the whole adjacency table.</summary>
    internal static readonly IReadOnlySet<(string From, string To)> DeclaredEdges = new HashSet<(string, string)>
    {
        (RunStates.Created, RunStates.Queued),
        (RunStates.Queued, RunStates.Running),
        (RunStates.Running, RunStates.Collecting),
        (RunStates.Collecting, RunStates.Complete),
        (RunStates.Collecting, RunStates.Incomplete),
        (RunStates.Queued, RunStates.Incomplete),
        (RunStates.Running, RunStates.Incomplete),
        (RunStates.Created, RunStates.Canceled),
        (RunStates.Queued, RunStates.Canceled),
        (RunStates.Running, RunStates.Canceled),
        (RunStates.Collecting, RunStates.Canceled),
    };

    /// <summary>The 00 §8.1 vocabulary in state order (the property tests enumerate it).</summary>
    internal static readonly IReadOnlyList<string> AllStates =
    [
        RunStates.Created,
        RunStates.Queued,
        RunStates.Running,
        RunStates.Collecting,
        RunStates.Complete,
        RunStates.Incomplete,
        RunStates.Canceled,
    ];

    /// <summary>True when <paramref name="state"/> is one of the 00 §8.1 states.</summary>
    internal static bool IsKnownState(string state) =>
        ForwardRank.ContainsKey(state) || TerminalStates.Contains(state);

    /// <summary>True when <paramref name="state"/> has no outgoing edge.</summary>
    internal static bool IsTerminal(string state) => TerminalStates.Contains(state);

    /// <summary>
    /// Classifies the requested <c>from → to</c> edge for a run currently in <paramref name="from"/>
    /// (both known states). Same state, a forward-path state already passed, or a terminal source is a
    /// no-op; a declared edge is applied; anything else is an undeclared forward-skip.
    /// </summary>
    internal static RunTransitionDecision Classify(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal) || TerminalStates.Contains(from))
        {
            return RunTransitionDecision.NoOp;
        }

        // `from` is on the forward path. A target on that path with a lower rank is a state the run
        // already passed, so the request is a no-op rather than a backward move.
        if (ForwardRank.TryGetValue(to, out var toRank) && toRank < ForwardRank[from])
        {
            return RunTransitionDecision.NoOp;
        }

        return DeclaredEdges.Contains((from, to))
            ? RunTransitionDecision.Apply
            : RunTransitionDecision.ForwardSkip;
    }
}
