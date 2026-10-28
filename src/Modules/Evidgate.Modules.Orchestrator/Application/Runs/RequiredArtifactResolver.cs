using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Evidgate.Modules.Policy.Contracts;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The requirement set of one run, as <see cref="IRequiredArtifactResolver"/> resolved it (05.3.2-T2.4, D-S123-Q3).
/// </summary>
/// <param name="Specs">
/// The requirements the freeze is asked for: one <see cref="RequiredArtifactSpec"/> per (planned execution, required
/// kind) pair, all with the <c>artifact_missing</c> reason code. Empty when the run pinned no policy, the policy requires
/// no artifact kind, the run has no planned execution, or the set is over the freeze bound
/// (<see cref="Unsatisfiable"/>).
/// </param>
/// <param name="Unsatisfiable">
/// True when the requirement set cannot be satisfied however the evidence arrives. Two ways it does: the plan's
/// executions times the policy's kinds exceed <see cref="FreezeRequest.MaxRequired"/>, the bound a single
/// <see cref="FreezeRequest"/> accepts (<see cref="RequiredArtifactResolver.Compose"/>), or the freeze refused the set
/// by shape — a kind outside the closed artifact-kind list the policy body may still state — in which case the finisher
/// sets the flag itself. Either way the finisher still freezes — with <see cref="Specs"/>, which is empty here — and
/// records one extra <c>artifact_missing</c>, so the revision is <c>incomplete</c> instead of the attempt failing and
/// being retried until the queue dead-letters it (PerfRev P1, SecRev F1/F2).
/// </param>
internal sealed record RequiredSet(IReadOnlyList<RequiredArtifactSpec> Specs, bool Unsatisfiable)
{
    /// <summary>The set a run with nothing to require resolves to: no requirement, nothing unsatisfiable.</summary>
    public static RequiredSet None { get; } = new([], Unsatisfiable: false);
}

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
/// <para>
/// The kinds are forwarded exactly as the policy body states them: the closed artifact-kind list lives in the Evidence
/// module (<c>ArtifactKind</c>, internal, not exported by <c>Evidgate.Modules.Evidence.Contracts</c>), so this type
/// neither copies nor re-declares that vocabulary — and a policy body may state any <c>lower_snake</c> token
/// (<c>contracts/schemas/policy-revision.v1.json</c>). A kind outside the list therefore reaches the freeze, whose
/// <see cref="IEvidenceFreeze"/> validation refuses the request by shape before its first statement; the finisher turns
/// exactly that refusal into <see cref="RequiredSet.Unsatisfiable"/> with an empty set, so the revision records
/// <c>artifact_missing</c> and the run leaves <c>collecting</c> instead of the attempt dead-lettering. That drops the
/// set <b>whole</b>: a policy mixing known and unknown kinds has none of its kinds checked in that revision (the freeze
/// has no per-spec refusal to keep the rest), which is why the durable fix belongs at the kind vocabulary. Should the
/// closed list ever be exported through <c>Evidence.Contracts</c> (or the policy write validate kinds), this reader can
/// filter to the satisfiable pairs itself and keep the known-kind checks; copying the eight kinds here is deliberately
/// not the answer.
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
    /// The requirement set: the pairs, plus whether the set is over the freeze bound and so unsatisfiable by
    /// construction (see <see cref="RequiredSet"/>).
    /// </returns>
    ValueTask<RequiredSet> ResolveAsync(Guid runId, CancellationToken cancellationToken);
}

/// <summary>
/// The Dapper reader behind <see cref="IRequiredArtifactResolver"/>: three statements over the ambient tenant scope
/// (00 §5.5 C3) — the pinned policy revision, the contract call that turns the revision into kinds, and the planned
/// execution keys, bounded to one row past <see cref="FreezeRequest.MaxRequired"/> so an over-large plan is detected
/// without reading it whole.
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
    public async ValueTask<RequiredSet> ResolveAsync(
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
            return RequiredSet.None;
        }

        // The contract call, not a Policy table read (00 §5.5 C5). It runs in this same transaction and opens none.
        var kinds = await policies.GetRequiredArtifactKindsAsync(policyRevisionId, cancellationToken).ConfigureAwait(false);
        if (kinds.Count == 0)
        {
            return RequiredSet.None;
        }

        // Statement 2: the plan's execution keys, one bounded read (never one per execution). The limit is one past the
        // freeze bound, so a plan that alone exceeds it is answered as unsatisfiable below instead of being silently cut.
        var executions = await db
            .QueryAsync<string>(
                EvidenceRevisionSql.ListPlannedExecutionKeys,
                new { org_id = orgId, run_id = runId, limit = FreezeRequest.MaxRequired + 1 },
                cancellationToken)
            .ConfigureAwait(false);

        return Compose(executions, kinds);
    }

    /// <summary>
    /// The requirement set of <paramref name="executions"/> × <paramref name="kinds"/>, or an unsatisfiable marker when
    /// that product exceeds <see cref="FreezeRequest.MaxRequired"/>. Pure, so the bound is asserted without a database.
    /// <para>
    /// The product is computed in <c>long</c> from two counts that are each at most <see cref="FreezeRequest.MaxRequired"/>
    /// + 1 (the read's limit), so it cannot overflow; over the bound the pairs are <b>not</b> truncated — the set is
    /// reported unsatisfiable and the freeze is asked for nothing, because a truncated set would let evidence look
    /// complete against the part of the policy it happened to keep.
    /// </para>
    /// </summary>
    /// <param name="executions">The planned execution keys, in the read's order.</param>
    /// <param name="kinds">The required artifact kinds, de-duplicated by the contract read.</param>
    internal static RequiredSet Compose(IReadOnlyList<string> executions, IReadOnlyList<string> kinds)
    {
        ArgumentNullException.ThrowIfNull(executions);
        ArgumentNullException.ThrowIfNull(kinds);

        var pairs = (long)executions.Count * kinds.Count;
        if (executions.Count > FreezeRequest.MaxRequired || pairs > FreezeRequest.MaxRequired)
        {
            return new RequiredSet([], Unsatisfiable: true);
        }

        var required = new List<RequiredArtifactSpec>((int)pairs);
        for (var execution = 0; execution < executions.Count; execution++)
        {
            for (var kind = 0; kind < kinds.Count; kind++)
            {
                required.Add(new RequiredArtifactSpec(
                    executions[execution],
                    kinds[kind],
                    ReconcileMissingCodes.ArtifactMissing));
            }
        }

        return new RequiredSet(required, Unsatisfiable: false);
    }

    /// <summary>The one column statement 1 reads (Dapper's underscore map); null <c>PolicyRevisionId</c> is "no policy pinned".</summary>
    private sealed class RunPolicyRow
    {
        public Guid? PolicyRevisionId { get; set; }
    }
}
