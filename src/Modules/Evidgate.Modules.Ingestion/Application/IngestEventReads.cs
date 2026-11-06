using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// The batch's own read of the events of one run that are already on file, in one statement. The quota
/// admission asks it before it reserves, so a resend of a committed batch reserves nothing, and the insert's
/// read-back asks it after, so a row this batch did not insert is called a duplicate only when the stored
/// canonical payload matches. Both callers ask the same <c>(org_id, run_id, event_id)</c> question, so they
/// share one statement instead of two copies that could drift.
/// </summary>
internal static class IngestEventReads
{
    /// <summary>One already-stored event of the batch, keyed by the client-assigned id.</summary>
    /// <param name="EventId">The stored <c>event_id</c>.</param>
    /// <param name="PayloadHash">The stored canonical <c>payload_hash</c>, the duplicate test's other half.</param>
    internal sealed record StoredEvent(Guid EventId, string PayloadHash);

    /// <summary>
    /// Reads the rows of <paramref name="eventIds"/> that <paramref name="runId"/> already holds, as the
    /// caller's tenant scope admits them; an id of another tenant or run is simply not a row.
    /// </summary>
    /// <param name="db">The tenant connection the caller's transaction runs on.</param>
    /// <param name="orgId">The tenant of the run (an index column, and the RLS scope).</param>
    /// <param name="runId">The run the batch was posted to.</param>
    /// <param name="eventIds">The batch's distinct ids; empty reads nothing.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async Task<Dictionary<Guid, string>> ReadStoredAsync(
        ITenantDb db,
        Guid orgId,
        Guid runId,
        IEnumerable<Guid> eventIds,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<StoredEvent>(
            StoredEventsSql,
            new
            {
                org_id = orgId,
                run_id = runId,
                // jsonb_to_recordset takes an array of OBJECTS, not an array of strings: a bare uuid
                // array answers 22023 'argument of jsonb_to_recordset must be an array of objects'.
                ids = JsonSerializer.Serialize(eventIds.Select(id => new { event_id = id })),
            },
            cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(row => row.EventId, row => row.PayloadHash);
    }

    /// <summary>The bounded read of the batch's rows already on file, matched through the tenant's own run.</summary>
    private const string StoredEventsSql = """
        select e.event_id as "EventId", e.payload_hash as "PayloadHash"
        from public.event e
        join jsonb_to_recordset(@ids::jsonb) as i (event_id uuid) on i.event_id = e.event_id
        where e.org_id = @org_id and e.run_id = @run_id
        """;
}
