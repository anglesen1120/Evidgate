using Evidgate.Modules.Authoring.Domain.Changes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The change state machine of 13.1.2 §6 as one rule: <c>pending</c> moves once, and both destinations are terminal, so
/// a replayed apply or a second discard can never re-run its effect.
/// </summary>
public sealed class AuthoringChangeStateTests
{
    [Fact]
    public void Terminal_States_Are_Terminal()
    {
        AuthoringChangeStates.IsTerminal(AuthoringChangeState.Pending).ShouldBeFalse();
        AuthoringChangeStates.IsTerminal(AuthoringChangeState.Applied).ShouldBeTrue();
        AuthoringChangeStates.IsTerminal(AuthoringChangeState.Discarded).ShouldBeTrue();

        // The two allowed moves, and every pair the schema's guard refuses — including a state onto itself.
        AuthoringChangeStates.CanTransition(AuthoringChangeState.Pending, AuthoringChangeState.Applied).ShouldBeTrue();
        AuthoringChangeStates.CanTransition(AuthoringChangeState.Pending, AuthoringChangeState.Discarded).ShouldBeTrue();
        AuthoringChangeStates.CanTransition(AuthoringChangeState.Applied, AuthoringChangeState.Pending).ShouldBeFalse();
        AuthoringChangeStates.CanTransition(AuthoringChangeState.Applied, AuthoringChangeState.Discarded).ShouldBeFalse();
        AuthoringChangeStates.CanTransition(AuthoringChangeState.Discarded, AuthoringChangeState.Pending).ShouldBeFalse();
        AuthoringChangeStates.CanTransition(AuthoringChangeState.Discarded, AuthoringChangeState.Applied).ShouldBeFalse();
        AuthoringChangeStates.CanTransition(AuthoringChangeState.Applied, AuthoringChangeState.Applied).ShouldBeFalse();
        AuthoringChangeStates.CanTransition(AuthoringChangeState.Discarded, AuthoringChangeState.Discarded).ShouldBeFalse();

        // Every terminal state is exactly the pair that never transitions, so a new state cannot land half-guarded.
        foreach (var state in Enum.GetValues<AuthoringChangeState>())
        {
            AuthoringChangeStates.IsTerminal(state).ShouldBe(
                !AuthoringChangeStates.CanTransition(state, AuthoringChangeState.Applied)
                && !AuthoringChangeStates.CanTransition(state, AuthoringChangeState.Discarded));
        }
    }

    [Fact]
    public void WireNames_MatchTheDatabaseCheck_AndRoundTrip()
    {
        AuthoringChangeStates.WireValues.ShouldBe(["pending", "applied", "discarded"]);

        foreach (var state in Enum.GetValues<AuthoringChangeState>())
        {
            AuthoringChangeStates.Parse(AuthoringChangeStates.WireName(state)).ShouldBe(state);
        }
    }
}
