using Evidgate.Modules.Authoring.Contracts;

namespace Evidgate.Modules.Authoring.Domain;

/// <summary>The four priorities of a checklist item and of a manual test case (13.1.1 AC3, plan 13 §4.1).</summary>
internal static class AuthoringPriorities
{
    /// <summary>The highest priority.</summary>
    public const string Critical = "critical";

    /// <summary>The second priority.</summary>
    public const string High = "high";

    /// <summary>The third priority.</summary>
    public const string Medium = "medium";

    /// <summary>The lowest priority.</summary>
    public const string Low = "low";

    /// <summary>The closed set, in the order the plan lists it.</summary>
    public static IReadOnlyList<string> Values { get; } = [Critical, High, Medium, Low];

    /// <summary>True when <paramref name="priority"/> is one of the four values.</summary>
    /// <param name="priority">The candidate value.</param>
    public static bool IsValid(string? priority) => priority is not null && Values.Contains(priority, StringComparer.Ordinal);
}

/// <summary>What a requested state transition did (13.1.1 §6, AC20, AC21).</summary>
internal enum AuthoringTransitionOutcome
{
    /// <summary>The move is allowed and writes one version row.</summary>
    Allowed,

    /// <summary>The entity is already in that state: <c>200 changed:false</c>, no version row (AC5's no-op rule).</summary>
    Unchanged,

    /// <summary>The move is refused with <c>409 invalid_transition</c>.</summary>
    Refused,

    /// <summary>The move needs a 1..500 character reason (<c>422 request_invalid</c>, <c>details[].path = /reason</c>).</summary>
    ReasonRequired,
}

/// <summary>The outcome of one transition request.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ToState">The state the move asked for.</param>
internal sealed record AuthoringTransition(AuthoringTransitionOutcome Outcome, string ToState)
{
    /// <summary>True when a version row is written.</summary>
    public bool IsAllowed => Outcome == AuthoringTransitionOutcome.Allowed;

    /// <summary>True for the AC5 no-op answer.</summary>
    public bool IsNoOp => Outcome == AuthoringTransitionOutcome.Unchanged;
}

/// <summary>
/// The <c>checklist_item</c> state machine of 13.1.1 §6 (AC20): <c>manual create → active</c>, <c>ai create → draft</c>,
/// <c>draft → active</c>, <c>{draft, active} → retired</c> with a required reason, and <c>retired</c> terminal for edits
/// (restore is the only way back and it appends a new version that sets <c>active</c>).
/// </summary>
internal static class ChecklistItemStateMachine
{
    /// <summary>Created by 13.3.1 or imported as a draft row; waits for the accept step that flips it to <c>active</c>.</summary>
    public const string Draft = "draft";

    /// <summary>Accepted content of a checklist item.</summary>
    public const string Active = "active";

    /// <summary>Terminal for edits; restore returns a new version.</summary>
    public const string Retired = "retired";

    /// <summary>The closed state set of <c>checklist_item.state</c>.</summary>
    public static IReadOnlyList<string> States { get; } = [Draft, Active, Retired];

    /// <summary>The state a restore writes (§6: "restore appends a new version and sets active").</summary>
    public const string RestoreState = Active;

    /// <summary>Every allowed move, the one table the aggregate and §6 are both read from.</summary>
    public static IReadOnlyList<(string From, string To)> AllowedTransitions { get; } =
        [(Draft, Active), (Draft, Retired), (Active, Retired)];

    /// <summary>The state a new item is created in (AC3): <c>active</c> for an author, <c>draft</c> for 13.3.1's AI output.</summary>
    /// <param name="source">Who created it. An import carries its own <c>state</c> column and does not call this.</param>
    public static string NewItemState(AuthoringSource source) => source == AuthoringSource.Ai ? Draft : Active;

    /// <summary>
    /// Decides a requested move. The reason is mandatory for every move into <c>retired</c> (AC20) and its 1..500
    /// character length is checked by the caller that also owns the version row's reason.
    /// </summary>
    /// <param name="from">The item's current state.</param>
    /// <param name="to">The requested state.</param>
    /// <param name="reason">The reason carried by the command, or null.</param>
    public static AuthoringTransition Decide(string from, string to, string? reason) =>
        AuthoringStateRules.Decide(States, AllowedTransitions, Retired, from, to, reason);

    /// <summary>
    /// Refuses an edit of a <c>retired</c> item and accepts one in any other state (AC20: "any edit of a
    /// <c>retired</c> item are <c>409 invalid_transition</c>").
    /// </summary>
    /// <param name="state">The item's current state.</param>
    public static AuthoringTransition CheckEditable(string state) =>
        AuthoringStateRules.CheckEditable(States, Retired, state);
}

/// <summary>
/// The <c>manual_test_case</c> state machine of 13.1.1 §6 (AC21): <c>draft → approved</c> with an optional reason,
/// <c>{draft, approved} → retired</c> with a required reason, <c>retired</c> terminal for edits and every move out of
/// it refused, and a restore that sets <c>draft</c>.
/// </summary>
internal static class ManualTestCaseStateMachine
{
    /// <summary>Created (by a person or by 13.3.1) and not reviewed yet; 13.5.1 never generates from it.</summary>
    public const string Draft = "draft";

    /// <summary>Reviewed; the only state 13.5.1's approved-cases read service returns.</summary>
    public const string Approved = "approved";

    /// <summary>Terminal for edits; restore returns a new version in <c>draft</c>.</summary>
    public const string Retired = "retired";

    /// <summary>The closed state set of <c>manual_test_case.state</c>.</summary>
    public static IReadOnlyList<string> States { get; } = [Draft, Approved, Retired];

    /// <summary>The state a restore writes (§6, AC21: "restore sets <c>draft</c>").</summary>
    public const string RestoreState = Draft;

    /// <summary>Every allowed move.</summary>
    public static IReadOnlyList<(string From, string To)> AllowedTransitions { get; } =
        [(Draft, Approved), (Draft, Retired), (Approved, Retired)];

    /// <summary>Every manual test case starts in <c>draft</c>, whether a person or 13.3.1 created it.</summary>
    public const string NewCaseState = Draft;

    /// <summary>
    /// Decides a requested move. The reason is optional for <c>draft → approved</c> and mandatory for every move into
    /// <c>retired</c> (AC21).
    /// </summary>
    /// <param name="from">The case's current state.</param>
    /// <param name="to">The requested state.</param>
    /// <param name="reason">The reason carried by the command, or null.</param>
    public static AuthoringTransition Decide(string from, string to, string? reason) =>
        AuthoringStateRules.Decide(States, AllowedTransitions, Retired, from, to, reason);

    /// <summary>Refuses an edit of a <c>retired</c> case and accepts one in any other state (AC21, AC22).</summary>
    /// <param name="state">The case's current state.</param>
    public static AuthoringTransition CheckEditable(string state) =>
        AuthoringStateRules.CheckEditable(States, Retired, state);
}

/// <summary>
/// The one decision procedure the two machines share, so the tables above are the only statement of §6 and the
/// aggregate cannot drift from the plan: same state is the AC5 no-op, <c>retired</c> is terminal, a move outside the
/// table is refused, and a move into <c>retired</c> without a reason is <see cref="AuthoringTransitionOutcome.ReasonRequired"/>.
/// </summary>
internal static class AuthoringStateRules
{
    internal static AuthoringTransition Decide(
        IReadOnlyList<string> states,
        IReadOnlyList<(string From, string To)> allowed,
        string retired,
        string from,
        string to,
        string? reason)
    {
        if (!states.Contains(from, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{from}' is not a state of this entity", nameof(from));
        }

        if (!states.Contains(to, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{to}' is not a state of this entity", nameof(to));
        }

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return new AuthoringTransition(AuthoringTransitionOutcome.Unchanged, to);
        }

        if (string.Equals(from, retired, StringComparison.Ordinal)
            || !allowed.Contains((from, to)))
        {
            return new AuthoringTransition(AuthoringTransitionOutcome.Refused, to);
        }

        if (string.Equals(to, retired, StringComparison.Ordinal) && string.IsNullOrEmpty(reason))
        {
            return new AuthoringTransition(AuthoringTransitionOutcome.ReasonRequired, to);
        }

        return new AuthoringTransition(AuthoringTransitionOutcome.Allowed, to);
    }

    internal static AuthoringTransition CheckEditable(IReadOnlyList<string> states, string retired, string state)
    {
        if (!states.Contains(state, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{state}' is not a state of this entity", nameof(state));
        }

        return new AuthoringTransition(
            string.Equals(state, retired, StringComparison.Ordinal)
                ? AuthoringTransitionOutcome.Refused
                : AuthoringTransitionOutcome.Allowed,
            state);
    }
}
