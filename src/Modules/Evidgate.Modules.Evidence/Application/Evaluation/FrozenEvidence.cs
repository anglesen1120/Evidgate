using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;

namespace Evidgate.Modules.Evidence.Application.Evaluation;

/// <summary>
/// <see cref="IFrozenEvidence"/> over the caller's tenant transaction. It never opens a connection or a transaction and
/// never commits: the read sees the caller's uncommitted rows and its rollback undoes nothing here because nothing is
/// written. One statement serves the call, whatever the number of executions or attempts.
/// </summary>
/// <param name="db">The Dapper path over the ambient scope; it demands an active scope before it touches the database.</param>
internal sealed class FrozenEvidence(ITenantDb db) : IFrozenEvidence
{
    /// <inheritdoc />
    public async ValueTask<FrozenEvidenceInput?> GetFrozenInputAsync(
        Guid orgId,
        Guid runId,
        int revisionNo,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<InputRow>(
            FrozenEvidenceSql.LoadFrozenInput,
            new { org_id = orgId, run_id = runId, revision_no = revisionNo },
            cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return null;
        }

        var header = rows[0];
        var attempts = new List<FrozenAttemptStatus>(rows.Count);
        foreach (var row in rows)
        {
            if (row.ExecutionKey is not null && row.Status is not null)
            {
                attempts.Add(new FrozenAttemptStatus(row.ExecutionKey, row.Status));
            }
        }

        return new FrozenEvidenceInput(
            new FrozenEvidenceRevision(
                header.RevisionNo,
                header.Completeness,
                header.Missing,
                header.ManifestHash,
                header.CreatedAt),
            attempts);
    }

    /// <summary>
    /// One result row: the revision's stored columns (repeated on each row; <c>missing</c> arrives as a <c>text[]</c>,
    /// which the contract exposes read-only) and one admitted attempt, both null for a revision without attempts.
    /// </summary>
    private sealed record InputRow(
        int RevisionNo,
        string Completeness,
        string[] Missing,
        string ManifestHash,
        DateTimeOffset CreatedAt,
        string? ExecutionKey,
        string? Status);
}
