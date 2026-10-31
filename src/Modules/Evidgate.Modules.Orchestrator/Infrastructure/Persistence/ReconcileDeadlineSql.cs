namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The two statements of the collection-deadline sweep: the platform scan that reads which runs are
/// overdue, and the per-tenant probe that says which attempt of each one the sweep may still queue.
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
/// is the whole of its boundary: <c>public.outbox</c> and <c>public.task</c> are mechanism tables and
/// deliberately carry no tenant policy (0006 — the dispatcher scans every org by design), so nothing else
/// narrows the read. It reads the keys with the state of the task each became rather than a count, so the
/// sweep can decide every run of a page in one statement instead of one round trip per run.
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
    /// The deadline attempts of one org already written to its outbox, among the keys a page carries, with the
    /// state of the task each one became (null while the row is still undispatched): one statement for the whole
    /// page, so the probe costs one round trip instead of one per run.
    /// <para>
    /// The join is the only link from a trigger to the work it produced: a task row carries no outbox id, it
    /// carries <c>'outbox:' || outbox.id</c> as its own idempotency key, so that expression — not a column — is
    /// what pairs the two. <c>org_id</c> is named on both sides: neither table has a tenant policy, so the
    /// predicates are the whole boundary, and a task row of another tenant can never answer for this one.
    /// </para>
    /// <para>
    /// The keys travel as JSON text and are unpacked in SQL. A <c>string[]</c> parameter cannot be bound here:
    /// the tenant Dapper path registers a read-only <c>string[]</c> type handler (writes are EF Core's), so
    /// <c>Dapper</c> refuses to write an array, and a module cannot bind the shared
    /// <c>TextArrayParameter</c>, which is internal to its own assembly.
    /// </para>
    /// </summary>
    internal const string AttemptStates = """
        select o.idempotency_key as "IdempotencyKey",
               t.state as "TaskState"
          from public.outbox o
          left join public.task t
            on t.idempotency_key = 'outbox:' || o.id::text
           and t.org_id = @org_id
         where o.org_id = @org_id
           and o.idempotency_key in (select k.value from jsonb_array_elements_text(@keys::jsonb) as k(value))
        """;
}

/// <summary>One deadline attempt as the probe projects it: the outbox key and the state of the task it became.</summary>
/// <param name="IdempotencyKey">The outbox idempotency key of the attempt.</param>
/// <param name="TaskState">The task's state, or null while the outbox row is still undispatched.</param>
internal sealed record DeadlineAttemptRow(string IdempotencyKey, string? TaskState);
