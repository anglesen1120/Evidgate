using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

namespace Evidgate.Modules.Orchestrator.Application.Releases;

/// <summary>
/// The release read's binding section: every stored <c>release_run</c> row of one release, in <c>created_at, id</c>
/// order over the <c>(org_id, release_id)</c> prefix of the binding key. Insertion order is the answer; no "latest run"
/// selection exists, so the read can never stand in for a recency join.
/// </summary>
/// <param name="db">The Dapper path over the ambient scope.</param>
internal sealed class BoundRunsQuery(ITenantDb db)
{
    /// <summary>The bindings of <paramref name="releaseId"/>, ordered by when each row was written.</summary>
    /// <param name="orgId">The scope's org, the tenant key every row carries.</param>
    /// <param name="releaseId">The release whose bindings are read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<IReadOnlyList<ReleaseBoundRun>> ReadAsync(
        Guid orgId,
        Guid releaseId,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<BoundRunRow>(
            ReleaseSql.BoundRuns,
            new { org_id = orgId, release_id = releaseId },
            cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(row => new ReleaseBoundRun(row.RunId, row.ComponentKey, row.CreatedAt))];
    }

    /// <summary>One stored binding.</summary>
    private sealed class BoundRunRow
    {
        public Guid RunId { get; set; }

        public string ComponentKey { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }
    }
}
