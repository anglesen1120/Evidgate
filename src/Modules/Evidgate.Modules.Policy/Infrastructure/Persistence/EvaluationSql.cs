namespace Evidgate.Modules.Policy.Infrastructure.Persistence;

/// <summary>
/// The statements of the run evaluation that read and write this module's own tables: the pinned policy revision the
/// rules come from, and the insert whose unique keys, not the handler, decide who wins a race.
/// </summary>
/// <remarks>
/// The run, its plan, the pinned suite, the frozen revision, its attempts and the case behind each planned revision are
/// read through the modules that own them (module data is private), never from here. Every statement is parameterised
/// and runs on the caller's tenant scope, so row level security and the
/// <c>evidgate.org_id</c> setting apply to it. The arrays travel as delimited scalars, not as
/// <c>string[]</c>: the tenant Dapper path refuses a client-side array write (see
/// <c>EvidenceRevisionSql.InsertRevision</c>), and the closed vocabularies of these two columns can never carry the
/// unit separator.
/// </remarks>
internal static class EvaluationSql
{
    /// <summary>The separator the two closed-vocabulary arrays travel with.</summary>
    internal const string ArraySeparator = "\u001f";

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
