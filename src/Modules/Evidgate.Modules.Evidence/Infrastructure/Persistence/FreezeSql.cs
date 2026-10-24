namespace Evidgate.Modules.Evidence.Infrastructure.Persistence;

/// <summary>
/// The SQL of <c>IEvidenceFreeze</c> (06.5.1-T1.2, T2.1): one read statement and at most one conditional write statement,
/// whatever the number of artifacts or requirements (00 §5.7 P2/P3). Every statement runs inside the caller's tenant
/// transaction (06.5.1 §7, 00 §5.5 C3) and passes the org and project ids explicitly, exactly like the Orchestrator
/// module's <c>CaseAttemptSql</c>; row level security also applies. Both statements reach the run through
/// <c>public.run</c> as well, so an artifact row whose own <c>project_id</c> disagrees with its run's project is still
/// unreachable from a project-scoped scope (defence in depth, F9). The resolution batch travels as ONE <c>jsonb</c>
/// text parameter expanded with <c>jsonb_to_recordset</c>, because the tenant Dapper path refuses array parameters
/// (the <c>StringArrayTypeHandler</c> precedent).
/// </summary>
internal static class FreezeSql
{
    /// <summary>
    /// Statement 1 of <c>FreezeAsync</c> (06.5.1-T1.2, plan §4 T1): the <c>ready</c> artifacts of the run
    /// left-joined to <c>case_attempt</c> by the identity tuple <c>(execution_key, attempt_no)</c>, so one read
    /// answers both the grouping and the attempt resolution (AC1, AC2). Only <c>ready</c> rows count (00 §8.4); the
    /// run and project filters keep the read inside the ambient scope's visibility, so an unknown or foreign run
    /// answers no row, and the <c>(org_id, run_id)</c> partial index <c>artifact_run_idx</c> covers the scan while
    /// the <c>case_attempt</c> unique key covers the join.
    /// <para>
    /// There is deliberately no <c>order by</c> (F5): the service re-orders in memory (the text columns ordinally,
    /// <c>attempt_no</c> numerically) and never depends on DB collation or physical row order, so a database sort would
    /// cost the scan for nothing. <c>@max_rows</c> is bound to one more than <c>FreezeRequest.MaxReadyArtifacts</c>
    /// (F4, D-S57-Q10): the statement stops there, and a run that exceeds the bound is refused before any write, so an
    /// unbounded run can never fill the process.
    /// <c>@project_id</c> is also applied to <c>public.run</c> (F9): the artifact's own <c>project_id</c> filter stays,
    /// and the run join makes an artifact that carries a foreign project still unreachable.
    /// </para>
    /// </summary>
    internal const string SelectReadyArtifacts = """
        select a.id as artifact_id,
               a.execution_key,
               a.attempt_no,
               a.kind,
               a.object_key,
               a.checksum,
               a.sensitivity,
               a.attempt_id as current_attempt_id,
               ca.id as resolved_attempt_id
          from public.artifact a
          join public.run ru
            on ru.org_id = a.org_id and ru.id = a.run_id
           and (@project_id::uuid is null or ru.project_id = @project_id::uuid)
          left join public.case_attempt ca
            on ca.org_id = a.org_id and ca.run_id = a.run_id
           and ca.execution_key = a.execution_key and ca.attempt_no = a.attempt_no
         where a.org_id = @org_id
           and a.run_id = @run_id
           and (@project_id::uuid is null or a.project_id = @project_id::uuid)
           and a.state = 'ready'
         limit @max_rows
        """;

    /// <summary>
    /// Statement 2 of <c>FreezeAsync</c> (06.5.1-T2.1, plan §4 T2), issued only when at least one row resolved for
    /// the first time: one idempotent <c>UPDATE</c> for the whole batch, however many rows it holds. The batch is
    /// keyed by primary key and the guard is repeated here (<c>state = 'ready' and attempt_id is null</c>) so a row
    /// whose <c>attempt_id</c> was set by a concurrent or earlier call is never rewritten (AC4, 06.5.1 §6) — the
    /// application guard and this clause must agree, and the 06.5.1-T3 trigger enforces the same invariant at the
    /// database. The run and project filters make a row of another run or project unreachable even with a forged id
    /// in the batch, and the <c>public.run</c> join repeats the run's project filter (F9).
    /// <para>
    /// <b>The lock order is a property of this statement (F7).</b> <c>batch</c> is
    /// <c>as materialized</c> on purpose: the rows are locked by <c>for update of a</c> in <c>order by a.id</c>
    /// order before the outer <c>UPDATE</c> runs, so two concurrent freezes of one run walk the same rows in the
    /// same order and cannot deadlock; the materialization keeps a planner from folding the locking scan (and its
    /// ordering) into the outer <c>UPDATE … FROM</c>. The statement returns the number of rows it actually updated,
    /// which the service compares with the number of rows it sent (F3, D-S57-Q11): a row that left <c>ready</c>, or
    /// was resolved by a concurrent freeze, after the read is simply absent from <c>batch</c> and shortens the count
    /// instead of being silently reported as resolved.
    /// </para>
    /// </summary>
    internal const string ResolveAttemptIds = """
        with batch as materialized (
            select a.id, r.resolved_attempt_id
              from public.artifact a
              join public.run ru
                on ru.org_id = a.org_id and ru.id = a.run_id
               and (@project_id::uuid is null or ru.project_id = @project_id::uuid)
              join jsonb_to_recordset(@rows::jsonb) as r(id uuid, resolved_attempt_id uuid)
                on r.id = a.id
             where a.org_id = @org_id
               and a.run_id = @run_id
               and (@project_id::uuid is null or a.project_id = @project_id::uuid)
               and a.state = 'ready'
               and a.attempt_id is null
             order by a.id
               for update of a
        )
        update public.artifact as a
           set attempt_id = b.resolved_attempt_id
          from batch b
         where a.org_id = @org_id
           and a.id = b.id
        """;
}
