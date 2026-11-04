namespace Evidgate.Modules.Authoring.Domain.Changes;

/// <summary>
/// The three states of a queued authoring change (00 §8.11): a change waits in <see cref="Pending"/> and then moves
/// once, to <see cref="Applied"/> or <see cref="Discarded"/>. Both destinations are terminal — applying a change that
/// already left `pending` is a conflict, never a second effect.
/// </summary>
internal enum AuthoringChangeState
{
    /// <summary>Queued and not yet applied or abandoned.</summary>
    Pending,

    /// <summary>The change was written as part of one batch; terminal.</summary>
    Applied,

    /// <summary>The author abandoned the change; terminal.</summary>
    Discarded,
}

/// <summary>
/// The state machine of <see cref="AuthoringChangeState"/> and its wire spelling, in one place: the database checks the
/// three texts, the routes answer them, and the terminal guard of apply and discard is the same rule.
/// </summary>
internal static class AuthoringChangeStates
{
    /// <summary>The wire and database spelling of every state, in enum order.</summary>
    internal static IReadOnlyList<string> WireValues { get; } = ["pending", "applied", "discarded"];

    /// <summary>True for a state a change never leaves: only `pending` may move at all.</summary>
    /// <param name="state">The state to test.</param>
    internal static bool IsTerminal(AuthoringChangeState state) => state is AuthoringChangeState.Applied or AuthoringChangeState.Discarded;

    /// <summary>
    /// True only for the two transitions 00 §8.11 allows: `pending → applied` and `pending → discarded`. Every other
    /// pair — including a state onto itself — is refused, so a replay of a terminal move can never re-run its effect.
    /// </summary>
    /// <param name="from">The state the row holds now.</param>
    /// <param name="to">The state the caller asks for.</param>
    internal static bool CanTransition(AuthoringChangeState from, AuthoringChangeState to) =>
        from is AuthoringChangeState.Pending
        && to is AuthoringChangeState.Applied or AuthoringChangeState.Discarded;

    /// <summary>The database and wire text of a state.</summary>
    /// <param name="state">The state.</param>
    internal static string WireName(AuthoringChangeState state) => state switch
    {
        AuthoringChangeState.Pending => WireValues[0],
        AuthoringChangeState.Applied => WireValues[1],
        AuthoringChangeState.Discarded => WireValues[2],
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "unknown authoring change state"),
    };

    /// <summary>Reads a stored state text; an unknown value is a caller bug, not a refusal.</summary>
    /// <param name="value">The stored `state` text.</param>
    internal static AuthoringChangeState Parse(string value) => value switch
    {
        "pending" => AuthoringChangeState.Pending,
        "applied" => AuthoringChangeState.Applied,
        "discarded" => AuthoringChangeState.Discarded,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown authoring change state"),
    };
}
