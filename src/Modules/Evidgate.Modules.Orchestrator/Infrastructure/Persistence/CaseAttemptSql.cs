namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The SQL of the case-attempt writer and the blocked-dependant resolver (05.3.3-T2). Every statement runs inside the
/// caller's tenant transaction (05.3.3-T2.1) and passes the org and project ids explicitly, exactly like
/// <see cref="OrchestratorSql"/>; row level security also applies. Batches travel as ONE <c>jsonb</c> text parameter
/// expanded with <c>jsonb_to_recordset</c>, because the tenant Dapper path refuses array parameters (the
/// <c>StringArrayTypeHandler</c> precedent).
/// </summary>
internal static class CaseAttemptSql
{
    /// <summary>
    /// Statement 1 of <c>WriteAttemptsAsync</c> (05.3.3-T2.1 step 4): one batch insert of the distinct candidates.
    /// The planned-execution join makes an unplanned key insert nothing, the run join enforces the scope's project
    /// visibility, and <c>case_id</c> comes from the planned row's <c>test_revision</c> — never from the caller.
    /// <c>ON CONFLICT … DO NOTHING RETURNING id</c> is the duplicate detection: a slot that already holds a row is
    /// simply absent from the answer, never a caught unique violation. The explicit
    /// <c>order by r.execution_key, r.attempt_no</c> makes concurrent batches take the unique-key locks in one global
    /// <c>(execution_key, attempt_no)</c> order, so two transactions with overlapping slots cannot deadlock on the
    /// conflict check.
    /// <para>
    /// <c>after_cancel</c> is derived here, not passed in: the caller has taken the run row <c>FOR SHARE</c> in this
    /// transaction, so the run join reads the row under that same lock and a cancel that commits first can never be
    /// missed on an attempt it overtook.
    /// </para>
    /// </summary>
    internal const string InsertAttempts = """
        insert into public.case_attempt
               (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status,
                started_at, finished_at, duration_ms, job_id, after_cancel)
        select r.id, @org_id, @run_id, r.execution_key, tr.case_id, r.attempt_no, r.raw_status, r.normalized_status,
               r.started_at, r.finished_at, r.duration_ms, r.job_id, ru.state = 'canceled'
          from jsonb_to_recordset(@rows::jsonb)
               as r(id uuid, execution_key text, attempt_no integer, raw_status text, normalized_status text,
                    started_at timestamptz, finished_at timestamptz, duration_ms integer, job_id uuid)
          join public.planned_execution pe
            on pe.org_id = @org_id and pe.run_id = @run_id and pe.execution_key = r.execution_key
          join public.run ru
            on ru.org_id = pe.org_id and ru.id = pe.run_id
           and (@project_id::uuid is null or ru.project_id = @project_id::uuid)
          join public.test_revision tr
            on tr.org_id = pe.org_id and tr.id = pe.case_revision_id
        order by r.execution_key, r.attempt_no
        on conflict (org_id, run_id, execution_key, attempt_no) do nothing
        returning id
        """;

    /// <summary>
    /// Statement 2 of <c>WriteAttemptsAsync</c> (05.3.3-T2.1 step 5): only the slots statement 1 did not insert, one
    /// bounded statement however many. <c>planned</c> is true only when the slot is a planned execution of a run
    /// visible to the scope's project; the existing row's payload is what tells a same-payload replay
    /// (<c>Duplicate</c>) from a different payload (<c>DuplicateDifferentPayload</c>). The existing row is never read
    /// for writing and is never rewritten.
    /// </summary>
    internal const string ClassifyUnwrittenSlots = """
        select s.execution_key, s.attempt_no,
               (pe.execution_key is not null and ru.id is not null) as planned,
               (a.id is not null) as has_attempt,
               a.raw_status, a.normalized_status, a.started_at, a.finished_at, a.duration_ms, a.job_id
          from jsonb_to_recordset(@slots::jsonb) as s(execution_key text, attempt_no integer)
          left join public.planned_execution pe
            on pe.org_id = @org_id and pe.run_id = @run_id and pe.execution_key = s.execution_key
          left join public.run ru
            on ru.org_id = pe.org_id and ru.id = pe.run_id
           and (@project_id::uuid is null or ru.project_id = @project_id::uuid)
          left join public.case_attempt a
            on a.org_id = @org_id and a.run_id = @run_id
           and a.execution_key = s.execution_key and a.attempt_no = s.attempt_no
        """;

    /// <summary>
    /// Statement A of <c>ResolveDependantsAsync</c> (05.3.3-T2.2 step a): the run's suite revision and the failed
    /// execution's member (<c>case_revision_id</c>, <c>variant_hash</c>, <c>target_env</c>), with the run/project
    /// visibility filter of the writer, plus whether that execution has a <c>failed</c>, <c>timed_out</c> or
    /// <c>error</c> attempt in this run. An unknown or invisible run/key answers no row.
    /// </summary>
    internal const string FailedExecutionHead = """
        select ru.suite_revision_id, pe.case_revision_id, pe.variant_hash, pe.target_env,
               exists (select 1 from public.case_attempt a
                        where a.org_id = @org_id and a.run_id = @run_id and a.execution_key = @execution_key
                          and a.normalized_status in ('failed', 'timed_out', 'error')) as failing
          from public.planned_execution pe
          join public.run ru
            on ru.org_id = pe.org_id and ru.id = pe.run_id
           and (@project_id::uuid is null or ru.project_id = @project_id::uuid)
         where pe.org_id = @org_id and pe.run_id = @run_id and pe.execution_key = @execution_key
        """;

    /// <summary>
    /// Statement B of <c>ResolveDependantsAsync</c> (05.3.3-T2.2 step c): one insert for every dependant this run
    /// plans in the SAME <c>target_env</c> as the failed setup and that has no attempt yet. A dependant outside the
    /// plan, in another environment, or already attempted gets no row; the failed setup itself is excluded; the
    /// <c>NOT EXISTS</c> guard and the conflict clause make a repeated call write nothing (idempotent). The explicit
    /// <c>order by pe.execution_key</c> takes the unique-key locks in the same global order as
    /// <see cref="InsertAttempts"/>, so the two statements cannot deadlock against each other. The insert names no
    /// <c>after_cancel</c>: a synthetic <c>blocked</c> row is not post-cancel evidence, and the resolver has no caller
    /// yet (<see cref="ReconcileSql"/> records the same), so a flag here would be a claim no test can check.
    /// </summary>
    internal const string InsertBlockedAttempts = """
        insert into public.case_attempt
               (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status)
        select uuidv7(), @org_id, @run_id, pe.execution_key, tr.case_id, 1, 'blocked', 'blocked'
          from jsonb_to_recordset(@pairs::jsonb) as d(case_revision_id uuid, variant_hash text)
          join public.planned_execution pe
            on pe.org_id = @org_id and pe.run_id = @run_id
           and pe.case_revision_id = d.case_revision_id and pe.variant_hash = d.variant_hash
           and pe.target_env = @target_env and pe.execution_key <> @failed_key
          join public.test_revision tr
            on tr.org_id = pe.org_id and tr.id = pe.case_revision_id
         where not exists (select 1 from public.case_attempt a
                            where a.org_id = @org_id and a.run_id = @run_id and a.execution_key = pe.execution_key)
        order by pe.execution_key
        on conflict (org_id, run_id, execution_key, attempt_no) do nothing
        returning execution_key
        """;
}
