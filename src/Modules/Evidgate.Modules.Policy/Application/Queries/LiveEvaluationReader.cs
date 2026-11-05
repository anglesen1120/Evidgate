using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Contracts;
using Evidgate.Modules.Policy.Infrastructure.Persistence;

namespace Evidgate.Modules.Policy.Application.Queries;

/// <summary>
/// <see cref="ILiveEvaluationReader"/> over the caller's tenant transaction: one lock-free read of whether a run already
/// holds a stored <c>live</c> verdict. It never opens a connection or a transaction and writes nothing, so a caller that
/// already holds a lock on the run row decides the ordering, not this read.
/// </summary>
/// <param name="db">The Dapper path over the ambient scope.</param>
/// <param name="scopes">The ambient tenant scope the org predicate comes from.</param>
internal sealed class LiveEvaluationReader(ITenantDb db, ITenantScopeAccessor scopes) : ILiveEvaluationReader
{
    /// <inheritdoc />
    public async ValueTask<bool> HasLiveAsync(Guid runId, CancellationToken cancellationToken) =>
        await db.QuerySingleOrDefaultAsync<bool>(
            LiveEvaluationSql.HasLive,
            new { org_id = scopes.Require().OrgId, run_id = runId },
            cancellationToken).ConfigureAwait(false);
}
