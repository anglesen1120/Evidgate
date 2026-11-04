using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Ingestion.Application;

namespace Evidgate.Modules.Ingestion.Infrastructure.Persistence;

/// <summary>
/// Reads the metering source of an accepted batch: the row's own accepted count and its commit instant, never a value
/// carried by the task payload. The read runs on the ambient tenant scope, so another org's batch is not a row at all.
/// </summary>
internal sealed class MeterEventBatchStore(ITenantDb db) : IMeterEventBatchReader
{
    private const string ReadSql = """
        select run_id as "RunId", accepted as "Accepted", created_at as "CreatedAt"
        from public.event_batch
        where org_id = @org_id and id = @id
        """;

    /// <inheritdoc />
    public Task<MeterEventBatch?> ReadAsync(
        Guid orgId,
        Guid batchId,
        CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<MeterEventBatch>(
            ReadSql,
            new { org_id = orgId, id = batchId },
            cancellationToken);
}
