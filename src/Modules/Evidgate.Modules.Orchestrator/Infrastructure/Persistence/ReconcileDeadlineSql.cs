namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The two statements of the collection-deadline sweep: the platform scan that reads which runs are
/// overdue, and the per-tenant probe that keeps a sweep from queueing a reconcile the previous sweep
/// already queued.
/// </summary>
/// <remarks>
/// <para>
/// The scan is not run over the ambient tenant scope, because no tenant is known before it runs: it
/// calls <c>evidgate.reconcile_overdue_runs</c> on the <c>evidgate_worker_sched</c> connection, the
/// owner <c>SECURITY DEFINER</c> projection that reads <c>public.run</c> across tenants and returns only
/// <c>(org_id, run_id, collection_deadline_at)</c>. The role holds <c>EXECUTE</c> on that one function
/// and no privilege on the table, so the boundary is a grant and not a convention. The deadline itself
/// is compared by the database (<c>now()</c> inside the function), so application time cannot widen the
/// window.
/// </para>
/// <para>
/// The probe runs inside the run's own tenant scope and names <c>org_id</c> explicitly, and that filter
/// is the whole of its boundary: <c>public.outbox</c> is a mechanism table and deliberately carries no
/// tenant policy (0006 — the dispatcher scans every org by design), so nothing else narrows the read. It
/// reads the key rather than a count so the sweep can enqueue the runs a page has not queued yet in one
/// statement instead of one round trip per run.
/// </para>
/// </remarks>
internal static class ReconcileDeadlineSql
{
    /// <summary>
    /// One page of overdue runs, keyset-paged by <c>(org_id, id)</c>: the cursor moves past the last row
    /// of the previous page, so a page boundary can neither repeat a run nor skip one — regardless of
    /// what other transactions commit meanwhile.
    /// </summary>
    internal const string ReadPage = """
        select org_id, run_id, collection_deadline_at
          from evidgate.reconcile_overdue_runs(@after_org, @after_run, @limit)
        """;

    /// <summary>
    /// The deadline keys of one org already in its outbox, among the keys a page carries: one statement for
    /// the whole page, so the probe costs one round trip instead of one per run.
    /// <para>
    /// The keys travel as JSON text and are unpacked in SQL. A <c>string[]</c> parameter cannot be bound here:
    /// the tenant Dapper path registers a read-only <c>string[]</c> type handler (M1W3-02 — writes are EF
    /// Core's), so <c>Dapper</c> refuses to write an array, and a module cannot bind the shared
    /// <c>TextArrayParameter</c>, which is internal to its own assembly.
    /// </para>
    /// </summary>
    internal const string ExistingKeys = """
        select idempotency_key
          from public.outbox
         where org_id = @org_id
           and idempotency_key in (select t.value from jsonb_array_elements_text(@keys::jsonb) as t(value))
        """;
}
