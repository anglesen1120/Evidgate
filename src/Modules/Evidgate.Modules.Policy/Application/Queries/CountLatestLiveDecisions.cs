using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.Modules.Policy.Contracts;
using Evidgate.Modules.Policy.Infrastructure.Persistence;

namespace Evidgate.Modules.Policy.Application.Queries;

/// <summary>
/// The in-module reader behind <see cref="IPolicyContracts.CountLatestLiveDecisionsAsync"/>: one grouped, lock-free
/// statement per chunk of run ids inside the caller's tenant transaction. It reads the same latest-live source as
/// <see cref="GetLatestLiveDecisionsQuery"/>, so a count and a page of verdicts always agree.
/// </summary>
internal sealed class CountLatestLiveDecisionsQuery(ITenantDb db, ITenantScopeAccessor scopes)
{
    private const string Pass = "PASS";
    private const string Fail = "FAIL";
    private const string Inconclusive = "INCONCLUSIVE";

    /// <summary>The per-decision counts of the named runs' newest live verdicts.</summary>
    /// <param name="runIds">At most 200 run ids; empty input performs no query.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<LiveDecisionCounts> CountAsync(
        IReadOnlyList<Guid> runIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(runIds.Count, 200);

        if (runIds.Count == 0)
        {
            return new LiveDecisionCounts(0, 0, 0);
        }

        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(CountLatestLiveDecisionsQuery));

        var rows = await db.QueryAsync<DecisionCountRow>(
            RunDecisionSql.LatestLiveCounts,
            new { org_id = scopes.Require().OrgId, ids = runIds.Distinct().ToArray() },
            cancellationToken).ConfigureAwait(false);

        return new LiveDecisionCounts(
            CountOf(rows, Pass),
            CountOf(rows, Fail),
            CountOf(rows, Inconclusive));
    }

    private static int CountOf(IReadOnlyList<DecisionCountRow> rows, string decision) =>
        rows.Where(row => string.Equals(row.DecisionValue, decision, StringComparison.Ordinal)).Sum(row => row.Runs);

    /// <summary>One row as <see cref="RunDecisionSql.LatestLiveCounts"/> returns it.</summary>
    private sealed class DecisionCountRow
    {
        public string DecisionValue { get; set; } = string.Empty;

        public int Runs { get; set; }
    }
}
