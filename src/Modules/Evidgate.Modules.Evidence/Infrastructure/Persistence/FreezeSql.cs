namespace Evidgate.Modules.Evidence.Infrastructure.Persistence;

/// <summary>
/// The SQL of <c>IEvidenceFreeze</c>: one read statement and at most one conditional write statement, whatever the
/// number of artifacts or requirements. Both run inside the caller's tenant transaction and pass the org and project
/// ids explicitly; row level security also applies. The resolution batch travels as one <c>jsonb</c> text parameter
/// expanded with <c>jsonb_to_recordset</c>, because the tenant Dapper path refuses array parameters.
/// </summary>
internal static class FreezeSql
{
    /// <summary>
    /// Statement 1 of <c>FreezeAsync</c>: the <c>ready</c> artifacts of the run left-joined to <c>case_attempt</c> by
    /// the identity tuple <c>(execution_key, attempt_no)</c>, so one read answers both the grouping and the attempt
    /// resolution. Only <c>ready</c> rows count; the run and project filters keep the read inside the ambient scope's
    /// visibility, so an unknown or foreign run answers no row.
    /// <para>
    /// There is deliberately no <c>order by</c>: the service re-orders in memory (text columns ordinally,
    /// <c>attempt_no</c> numerically) and never depends on collation or physical row order. <c>@max_rows</c> is bound
    /// to one more than <c>FreezeRequest.MaxReadyArtifacts</c>, so an unbounded run is refused before any write.
    /// <c>@project_id</c> is applied to <c>public.run</c> too, so an artifact whose own <c>project_id</c> disagrees
    /// with its run's stays unreachable from a project-scoped scope.
    /// </para>
    /// <para>
    /// <b>A report counts only once its parse has decided.</b> Completing an upload leaves a raw report <c>ready</c>
    /// before the out-of-process parse runs, and that parse can still reject it; a revision is immutable, so a report
    /// frozen in between would be evidence the parser later refused. Two rows carry the verdict and either one still
    /// owed keeps the report out of the set, so it reaches the caller as the missing required artifact instead:
    /// an <c>artifact_parse</c> task row that is not <c>done</c>, and the completion's undispatched <c>outbox</c> row
    /// for that artifact — the window between the completion commit and the dispatcher's task insert, where no task
    /// row exists yet to read.
    /// </para>
    /// <para>
    /// The window that closes is as long as the verdict's row is kept: <c>task.sweep</c> deletes terminal rows
    /// (<c>done</c>, <c>dead_letter</c>) past the retention setting, so a parse that dead-lettered more than that
    /// span before a freeze leaves a <c>ready</c> report with no row left to read, and only the run's own retention
    /// bounds that case; a dispatcher-lag freeze counts the report as the missing requirement, never as rejected
    /// evidence.
    /// </para>
    /// <para>
    /// <b>The queue tables are read through their scoped views.</b> <c>public.task</c> and
    /// <c>public.outbox</c> are mechanism tables without a tenant policy, so the app role
    /// holds <c>SELECT</c> on <c>public.task_in_scope</c> and <c>public.outbox_in_scope</c>
    /// instead: owner-owned, <c>security_barrier</c>, filtered by
    /// <c>evidgate.current_org_id()</c> and exposing only the columns this read uses.
    /// <c>artifact_id</c> is the views' own projection of <c>payload ->> 'artifact_id'</c>, the
    /// expression the parse-state partial indexes are built on, so the equality below stays a
    /// plain text comparison the planner may push through the barrier into an index probe. The
    /// explicit <c>org_id</c> equality on each anti-join stays as the second lock: an
    /// <c>artifact_id</c> alone would match another tenant's row.
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
           and not exists (
                 select 1
                   from public.task_in_scope t
                  where t.org_id = a.org_id
                    and t.task_type = 'artifact_parse'
                    and t.state <> 'done'
                    and t.artifact_id = a.id::text)
           and not exists (
                 select 1
                   from public.outbox_in_scope o
                  where o.org_id = a.org_id
                    and o.task_type = 'artifact_parse'
                    and o.dispatched_at is null
                    and o.artifact_id = a.id::text)
         limit @max_rows
        """;

    /// <summary>
    /// Statement 2 of <c>FreezeAsync</c>, issued only when at least one row resolved for the first time: one idempotent
    /// <c>UPDATE</c> for the whole batch, however many rows it holds. The batch is keyed by primary key and the guard
    /// is repeated here (<c>state = 'ready' and attempt_id is null</c>) so a row whose <c>attempt_id</c> was set by a
    /// concurrent or earlier call is never rewritten; the application guard, this clause and the immutability trigger
    /// must agree. The run and project filters make a row of another run or project unreachable even with a forged id
    /// in the batch.
    /// <para>
    /// <b>The lock order is a property of this statement.</b> <c>batch</c> is <c>as materialized</c> on purpose: the
    /// rows are locked by <c>for no key update of a</c> in <c>order by a.id</c> order before the outer <c>UPDATE</c>
    /// runs, so two concurrent freezes of one run walk the same rows in the same order and cannot deadlock. The
    /// statement returns the number of rows it actually updated, which the service compares with the number of rows it
    /// sent: a row that left <c>ready</c>, or was resolved by a concurrent freeze, after the read is simply absent from
    /// <c>batch</c> and shortens the count. It repeats no parse gate: <c>batch</c> holds only ids the read already
    /// admitted, and re-testing them would turn a parse that started in between into a spurious short count.
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
               for no key update of a
        )
        update public.artifact as a
           set attempt_id = b.resolved_attempt_id
          from batch b
         where a.org_id = @org_id
           and a.id = b.id
        """;
}
