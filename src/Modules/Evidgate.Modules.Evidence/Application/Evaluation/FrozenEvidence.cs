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
        var history = new List<FrozenAttemptRecord>(rows.Count);
        var latest = new List<FrozenAttemptStatus>();
        foreach (var row in rows)
        {
            if (row.ExecutionKey is null || row.AttemptNo is null || row.Status is null)
            {
                continue;
            }

            history.Add(new FrozenAttemptRecord(row.ExecutionKey, row.AttemptNo.Value, row.Status));

            // Rows arrive by key then ascending attempt number, so the last row of a key is its highest attempt.
            var status = new FrozenAttemptStatus(row.ExecutionKey, row.Status);
            if (latest.Count > 0 && string.Equals(latest[^1].ExecutionKey, row.ExecutionKey, StringComparison.Ordinal))
            {
                latest[^1] = status;
            }
            else
            {
                latest.Add(status);
            }
        }

        return new FrozenEvidenceInput(
            new FrozenEvidenceRevision(
                header.RevisionNo,
                header.Completeness,
                header.Missing,
                header.ManifestHash,
                header.CreatedAt),
            latest)
        {
            AttemptHistory = history,
        };
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
        int? AttemptNo,
        string? Status);
}
