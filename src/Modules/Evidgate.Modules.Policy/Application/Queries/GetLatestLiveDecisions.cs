using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.Modules.Policy.Contracts;
using Evidgate.Modules.Policy.Infrastructure.Persistence;

namespace Evidgate.Modules.Policy.Application.Queries;

/// <summary>
/// The in-module reader behind <see cref="IPolicyContracts.GetLatestLiveDecisionsAsync"/>: the newest live verdict of
/// each named run, read for a whole page of runs in one lock-free statement inside the caller's tenant transaction —
/// never one statement per run. Only <c>mode = 'live'</c> rows are read, because a <c>shadow</c> row is a second
/// opinion about the same evidence and never the verdict the run reports. A run without a live row is absent from the
/// answer: "not evaluated yet" is the caller's null, not a fabricated verdict.
/// </summary>
internal sealed class GetLatestLiveDecisionsQuery(ITenantDb db, ITenantScopeAccessor scopes)
{
    /// <summary>The newest live verdict of each named run, in the statement's order.</summary>
    /// <param name="runIds">At most 200 run ids; empty input performs no query.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<IReadOnlyList<RunLiveDecision>> GetAsync(
        IReadOnlyList<Guid> runIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(runIds.Count, 200);

        if (runIds.Count == 0)
        {
            return [];
        }

        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(GetLatestLiveDecisionsQuery));

        var orgId = scopes.Require().OrgId;
        var rows = await db.QueryAsync<RunLiveDecisionRow>(
            RunDecisionSql.LatestLiveBatch,
            // A Guid[] binds as uuid[] through Npgsql's own inference; the tenant path only refuses a custom string
            // array handler.
            new { org_id = orgId, ids = runIds.Distinct().ToArray() },
            cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(row => new RunLiveDecision(
            row.RunId,
            row.DecisionValue,
            row.EvidenceRevision,
            row.EvaluationId))];
    }

    /// <summary>One row as <see cref="RunDecisionSql.LatestLiveBatch"/> returns it (quoted aliases via the type map).</summary>
    private sealed class RunLiveDecisionRow
    {
        public Guid RunId { get; set; }

        public string DecisionValue { get; set; } = string.Empty;

        public int EvidenceRevision { get; set; }

        public Guid EvaluationId { get; set; }
    }
}
