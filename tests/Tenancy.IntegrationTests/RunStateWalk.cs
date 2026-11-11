using Evidgate.Modules.Orchestrator.Domain;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Moves a seeded run to a target state through the edges the database guard allows, one UPDATE per edge, so a
/// fixture never raw-writes an edge the state machine forbids (a seeded run starts in <c>created</c>).
/// </summary>
internal static class RunStateWalk
{
    private static readonly string[] Forward =
    [
        RunStates.Created,
        RunStates.Queued,
        RunStates.Running,
        RunStates.Collecting,
    ];

    /// <summary>
    /// The states to write, in order, to get from <paramref name="from"/> to <paramref name="to"/>: the forward
    /// path for a later forward state or <c>complete</c> (only from <c>collecting</c>), one step for the other
    /// terminals — except <c>created → incomplete</c>, which the guard does not declare, so it steps through
    /// <c>queued</c> first. Empty for the same state; throws for a move the machine does not allow.
    /// </summary>
    internal static IReadOnlyList<string> Path(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return [];
        }

        var fromRank = Array.IndexOf(Forward, from);
        if (fromRank < 0)
        {
            throw new InvalidOperationException($"run state '{from}' is terminal; seed a fresh run instead of leaving it");
        }

        var toRank = Array.IndexOf(Forward, to);
        if (toRank >= 0)
        {
            return toRank > fromRank
                ? Forward[(fromRank + 1)..(toRank + 1)]
                : throw new InvalidOperationException($"run state cannot move backwards: {from} -> {to}");
        }

        if (to == RunStates.Complete)
        {
            return [.. Forward[(fromRank + 1)..], RunStates.Complete];
        }

        // `created` declares no edge to `incomplete`; the guard's own path is created -> queued -> incomplete.
        if (to == RunStates.Incomplete && fromRank == 0)
        {
            return [RunStates.Queued, to];
        }

        return [to];
    }

    /// <summary>Walks the run's row from its current state to <paramref name="target"/> as the owner role.</summary>
    internal static async Task MoveAsync(PostgresTenancyFixture fixture, string database, Guid runId, string target)
    {
        var current = await fixture.ScalarAsync<string>(database, $"select state from public.run where id = '{runId:D}'");
        foreach (var next in Path(current, target))
        {
            await fixture.ExecuteAsync(database, $"update public.run set state = '{next}' where id = '{runId:D}'");
        }
    }
}
