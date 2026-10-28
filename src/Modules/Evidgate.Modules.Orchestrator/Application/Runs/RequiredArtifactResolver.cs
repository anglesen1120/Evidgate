using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Evidgate.Modules.Policy.Contracts;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The one seam where 05.3.2-T2.3 resolves the artifact set a run's evidence must contain (06.5.1 §1: 06 compares against
/// a caller-resolved set and never decides which artifacts a case needs). The requirement source is the policy revision
/// the run pinned at create (<c>run.policy_revision_id</c>, FR-07-2) read through <see cref="IPolicyContracts"/>
/// [DECISION D-S123-Q3]: every required kind for every planned execution of the run. Nothing here reads another
/// module's tables: the kinds come through the contract, the executions and the pin through this module's own rows.
/// <para>
/// A run that pinned no policy (the project had no active revision, 07.1.1-T4.2) requires nothing: an empty set is a
/// legitimate answer, never an error, and completeness is then decided by the event comparison and the orphan rule alone.
/// </para>
/// </summary>
internal interface IRequiredArtifactResolver
{
    /// <summary>
    /// Resolves the requirements of <paramref name="runId"/> inside the caller's tenant transaction.
    /// </summary>
    /// <param name="runId">The run whose evidence is being frozen; it must be visible to the caller's tenant scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// One <see cref="RequiredArtifactSpec"/> per (planned execution, required kind) pair, ordered by execution key
    /// (ordinal) and then kind (ordinal), all with the <c>artifact_missing</c> reason code; empty when the run pinned no
    /// policy, the policy requires no artifact kind, or the run has no planned execution.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The requirement set would exceed <see cref="FreezeRequest.MaxRequired"/>; it is refused as a whole rather than
    /// truncated, so a bounded freeze can never silently accept a run whose policy outgrew the bound.
    /// </exception>
    ValueTask<IReadOnlyList<RequiredArtifactSpec>> ResolveAsync(Guid runId, CancellationToken cancellationToken);
}

/// <summary>
/// The Dapper reader behind <see cref="IRequiredArtifactResolver"/>: three statements over the ambient tenant scope
/// (00 §5.5 C3) — the pinned policy revision, the contract call that turns the revision into kinds, and the planned
/// execution keys, bounded to one row past <see cref="FreezeRequest.MaxRequired"/> so the product can be refused without
/// reading an unbounded plan.
/// </summary>
/// <param name="db">The Dapper path over the ambient scope; it demands an active scope before it touches the database.</param>
/// <param name="scopes">The tenant-scope accessor; <c>Require()</c> is the guard that throws before any statement.</param>
/// <param name="policies">The Policy module's in-process read of one revision's required artifact kinds.</param>
internal sealed class RequiredArtifactResolver(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IPolicyContracts policies) : IRequiredArtifactResolver
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<RequiredArtifactSpec>> ResolveAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var orgId = scopes.Require().OrgId;

        // Statement 1: the pin the run's create wrote once and nothing updates afterwards (FR-07-2). An invisible run
        // answers no row, which the reconciler already reported as RunNotFound before this finisher is reached.
        var pinned = await db
            .QuerySingleOrDefaultAsync<RunPolicyRow>(
                EvidenceRevisionSql.SelectRunPolicyRevision,
                new { org_id = orgId, run_id = runId },
                cancellationToken)
            .ConfigureAwait(false);

        if (pinned?.PolicyRevisionId is not { } policyRevisionId)
        {
            return [];
        }

        // The contract call, not a Policy table read (00 §5.5 C5). It runs in this same transaction and opens none.
        var kinds = await policies.GetRequiredArtifactKindsAsync(policyRevisionId, cancellationToken).ConfigureAwait(false);
        if (kinds.Count == 0)
        {
            return [];
        }

        // Statement 2: the plan's execution keys, one bounded read (never one per execution). The limit is one past the
        // freeze bound, so a plan that alone exceeds it is refused below instead of being silently cut.
        var executions = await db
            .QueryAsync<string>(
                EvidenceRevisionSql.ListPlannedExecutionKeys,
                new { org_id = orgId, run_id = runId, limit = FreezeRequest.MaxRequired + 1 },
                cancellationToken)
            .ConfigureAwait(false);

        var pairs = (long)executions.Count * kinds.Count;
        if (executions.Count > FreezeRequest.MaxRequired || pairs > FreezeRequest.MaxRequired)
        {
            throw new InvalidOperationException(
                $"run {runId:D} needs more than {FreezeRequest.MaxRequired} required artifacts "
                + $"({executions.Count} planned execution(s) x {kinds.Count} kind(s)); a freeze waits for a smaller policy");
        }

        var required = new List<RequiredArtifactSpec>((int)pairs);
        foreach (var executionKey in executions)
        {
            for (var index = 0; index < kinds.Count; index++)
            {
                required.Add(new RequiredArtifactSpec(executionKey, kinds[index], ReconcileMissingCodes.ArtifactMissing));
            }
        }

        return required;
    }

    /// <summary>The one column statement 1 reads (Dapper's underscore map); null <c>PolicyRevisionId</c> is "no policy pinned".</summary>
    private sealed class RunPolicyRow
    {
        public Guid? PolicyRevisionId { get; set; }
    }
}
