namespace Evidgate.Modules.Policy.Infrastructure.Persistence;

/// <summary>
/// The statement set of the run evaluation: the four reads that build the evaluator's inputs and the insert
/// whose unique keys, not the handler, decide who wins a race.
/// </summary>
/// <remarks>
/// Every statement is parameterised and runs on the caller's tenant scope, so row level security and the
/// <c>evidgate.org_id</c> setting apply to it. The arrays travel as delimited scalars, not as
/// <c>string[]</c>: the tenant Dapper path refuses a client-side array write (see
/// <c>EvidenceRevisionSql.InsertRevision</c>), and the closed vocabularies of these two columns can never carry the
/// unit separator.
/// </remarks>
internal static class EvaluationSql
{
    /// <summary>The separator the two closed-vocabulary arrays travel with.</summary>
    internal const string ArraySeparator = "\u001f";

    /// <summary>
    /// The run's pinned policy revision and the plan the evidence is judged against: how many executions it planned,
    /// the pinned suite's key, and whether that suite revision declares no case at all. The pin is nullable — a run may
    /// carry none — and the reader refuses a null one instead of judging the evidence against nothing.
    /// </summary>
    internal const string LoadRun = """
        select r.policy_revision_id as "PolicyRevisionId",
               r.project_id as "ProjectId",
               r.purpose as "Purpose",
               m.planned_count as "PlannedCount",
               s.slug as "SuiteKey",
               not exists (
                 select 1
                   from public.suite_member sm
                  where sm.org_id = r.org_id
                    and sm.suite_revision_id = r.suite_revision_id) as "SuiteDeclaredEmpty"
          from public.run r
          join public.manifest m on m.org_id = r.org_id and m.id = r.manifest_id
          join public.suite_revision sr on sr.org_id = r.org_id and sr.id = r.suite_revision_id
          join public.suite s on s.org_id = sr.org_id and s.id = sr.suite_id
         where r.org_id = @org_id
           and r.id = @run_id
        """;

    /// <summary>
    /// The frozen revision the evaluation reads: its number, its own completeness and verdict inputs and its freeze
    /// stamp. The stamp bounds which attempts the results read may see, and <c>completeness</c> is the reconciler's own
    /// answer — a reader that inferred it from <c>missing</c> alone would read an unexplained incomplete row as complete.
    /// </summary>
    internal const string LoadRevision = """
        select er.revision_no as "RevisionNo",
               er.completeness as "Completeness",
               er.missing as "Missing",
               er.manifest_hash as "ManifestHash",
               er.created_at as "FrozenAt"
          from public.evidence_revision er
         where er.org_id = @org_id
           and er.run_id = @run_id
           and er.revision_no = @evidence_revision
        """;

    /// <summary>The pinned policy revision: its id, its content address and the body the rules are read from.</summary>
    internal const string LoadPolicy = """
        select p.id as "Id",
               p.hash as "Hash",
               p.body::text as "Body"
          from public.policy_revision p
         where p.org_id = @org_id
           and p.id = @policy_revision_id
        """;

    /// <summary>
    /// One result per planned execution that had an attempt when this revision froze: the normalized status of the
    /// latest attempt the revision's own <c>created_at</c> admits, and the manifest's own required flag. An execution
    /// whose every attempt is missing is left out on purpose — the evaluator derives <c>missing_result</c> from the
    /// planned count it was given, so a synthesised row here would report a decision the evidence never made. Without
    /// the bound a retry of an older revision would read the attempts of a revision that came after it and decide from
    /// evidence the frozen row never saw.
    /// <para>
    /// <c>created_at</c> is the transaction start time, not commit order, so the bound can differ from the attempts the
    /// freeze counted at the edges; a deterministic cutoff needs an insert-order watermark stored with the revision.
    /// </para>
    /// </summary>
    internal const string LoadResults = """
        select tr.case_id as "CaseId",
               tc.name as "CaseKey",
               latest.normalized_status as "Status",
               pe.required as "Required"
          from public.planned_execution pe
          join public.test_revision tr on tr.org_id = pe.org_id and tr.id = pe.case_revision_id
          join public.test_case tc on tc.org_id = tr.org_id and tc.id = tr.case_id
          join public.evidence_revision er on er.org_id = pe.org_id
                                          and er.run_id = pe.run_id
                                          and er.revision_no = @evidence_revision
          join lateral (
            select a.normalized_status
              from public.case_attempt a
             where a.org_id = pe.org_id
               and a.run_id = pe.run_id
               and a.execution_key = pe.execution_key
               and a.created_at <= er.created_at
             order by a.attempt_no desc
             limit 1) latest on true
         where pe.org_id = @org_id
           and pe.run_id = @run_id
         order by pe.execution_key
        """;

    /// <summary>
    /// The one write of this story: it lands only when neither unique key holds an answer to this evidence, so the
    /// second of two racers inserts nothing. No <c>UPDATE</c> exists on the table at all (the trigger and the grants
    /// refuse one), which is why the conflict clause is <c>do nothing</c> rather than an upsert. The clause carries no
    /// conflict target on purpose: the six-column idempotency key and the partial live key (<c>where mode = 'live'</c>)
    /// both mean "this verdict is already stored", so a target naming one of them answers the other with a 23505 that an
    /// idempotent write has no use for. The loser is still read back by the six columns below, so a live key it lost to
    /// under another policy revision or evaluator version raises there instead of being returned as this attempt's row.
    /// </summary>
    internal const string Insert = """
        insert into public.evaluation
          (id, org_id, run_id, evidence_revision, policy_revision_id, policy_hash, manifest_hash,
           completeness, missing, outcome, decision, reasons, evaluator_version, mode,
           exceptions_hash, exceptions_as_of)
        values
          (@id, @org_id, @run_id, @evidence_revision, @policy_revision_id, @policy_hash, @manifest_hash,
           @completeness, string_to_array(@missing, @array_separator), @outcome, @decision,
           string_to_array(@reasons, @array_separator), @evaluator_version, @mode,
           @exceptions_hash, @exceptions_as_of)
        on conflict do nothing
        returning id as "Id", created_at as "CreatedAt"
        """;

    /// <summary>
    /// The winner's row, read by the same key the losing insert used. It runs inside the caller's transaction, so it
    /// sees the winner's row as soon as that transaction commits; a loser whose read still returns nothing raises in
    /// the handler instead of inventing a result.
    /// </summary>
    internal const string SelectExisting = """
        select e.id as "Id",
               e.run_id as "RunId",
               e.evidence_revision as "EvidenceRevision",
               e.policy_revision_id as "PolicyRevisionId",
               e.policy_hash as "PolicyHash",
               e.manifest_hash as "ManifestHash",
               e.completeness as "Completeness",
               e.missing as "Missing",
               e.outcome as "Outcome",
               e.decision as "Decision",
               e.reasons as "Reasons",
               e.evaluator_version as "EvaluatorVersion",
               e.mode as "Mode",
               e.exceptions_hash as "ExceptionsHash",
               e.exceptions_as_of as "ExceptionsAsOf",
               e.created_at as "CreatedAt"
          from public.evaluation e
         where e.org_id = @org_id
           and e.run_id = @run_id
           and e.evidence_revision = @evidence_revision
           and e.policy_revision_id = @policy_revision_id
           and e.evaluator_version = @evaluator_version
           and e.mode = @mode
        """;
}
