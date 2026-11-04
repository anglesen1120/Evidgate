namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The SQL of the durable cancel request. Every statement runs inside the caller's tenant transaction, which is what
/// makes the request row and the run's <c>canceled</c> entry one unit: a rollback leaves neither behind.
/// </summary>
internal static class RunCancelSql
{
    /// <summary>
    /// Records the request, one row per run: the run's own <c>unique (org_id, run_id)</c> is the arbiter of a repeat, so
    /// a second insert writes nothing instead of raising a duplicate key. The affected-row count is what tells a request
    /// that recorded the row (1) from one that arrived after it (0); the row's windows and reason are never rewritten.
    /// </summary>
    internal const string InsertRequest = """
        insert into public.run_cancel_request
               (id, org_id, run_id, requested_by, reason, requested_at, soft_stop_deadline_at, kill_at)
        values (@id, @org_id, @run_id, @requested_by, @reason, @requested_at, @soft_stop_deadline_at, @kill_at)
        on conflict (org_id, run_id) do nothing
        """;
}
