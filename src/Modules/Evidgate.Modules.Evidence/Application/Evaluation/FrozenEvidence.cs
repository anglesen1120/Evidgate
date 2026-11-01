using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;

namespace Evidgate.Modules.Evidence.Application.Evaluation;

/// <summary>
/// <see cref="IFrozenEvidence"/> over the caller's tenant transaction. It never opens a connection or a transaction and
/// never commits: both reads see the caller's uncommitted rows and its rollback undoes nothing here because nothing is
/// written. One statement serves each call, whatever the number of executions or attempts.
/// </summary>
/// <param name="db">The Dapper path over the ambient scope; it demands an active scope before it touches the database.</param>
internal sealed class FrozenEvidence(ITenantDb db) : IFrozenEvidence
{
    /// <inheritdoc />
    public async ValueTask<FrozenEvidenceRevision?> GetRevisionAsync(
        Guid orgId,
        Guid runId,
        int revisionNo,
        CancellationToken cancellationToken)
    {
        var row = await db.QuerySingleOrDefaultAsync<RevisionRow>(
            FrozenEvidenceSql.LoadRevision,
            new { org_id = orgId, run_id = runId, revision_no = revisionNo },
            cancellationToken).ConfigureAwait(false);

        return row is null
            ? null
            : new FrozenEvidenceRevision(row.RevisionNo, row.Completeness, row.Missing, row.ManifestHash, row.CreatedAt);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<FrozenAttemptStatus>> GetLatestAttemptStatusesAsync(
        Guid orgId,
        Guid runId,
        int revisionNo,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<FrozenAttemptStatus>(
            FrozenEvidenceSql.LoadLatestAttemptStatuses,
            new { org_id = orgId, run_id = runId, revision_no = revisionNo },
            cancellationToken).ConfigureAwait(false);

        return [.. rows];
    }

    /// <summary>
    /// The stored columns, before they become the contract record: <c>missing</c> arrives as a <c>text[]</c>, which the
    /// contract exposes read-only.
    /// </summary>
    private sealed record RevisionRow(
        int RevisionNo,
        string Completeness,
        string[] Missing,
        string ManifestHash,
        DateTimeOffset CreatedAt);
}
