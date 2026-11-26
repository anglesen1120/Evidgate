namespace Evidgate.Modules.Policy.Infrastructure.Persistence;

/// <summary>
/// The release aggregation statements over this module's own tables. The release and run facts come through the
/// Orchestrator contract, so nothing here joins another module's table.
/// </summary>
internal static class ReleaseDecisionSql
{
    /// <summary>
    /// The newest live verdict of each named run, with the policy revision it was reached under: driven from the id
    /// array and probing the partial <c>evaluation_live_key</c> once per run, so the read costs one statement however
    /// many runs a release binds. A run with no live row yields no row.
    /// </summary>
    internal const string LiveVerdicts = """
        select v.run_id as "RunId",
               v.id as "EvaluationId",
               v.evidence_revision as "EvidenceRevision",
               v.policy_revision_id as "PolicyRevisionId",
               v.decision as "Decision"
          from unnest(@ids::uuid[]) as u(run_id)
         cross join lateral (
              select e.id, e.run_id, e.evidence_revision, e.policy_revision_id, e.decision
                from public.evaluation e
               where e.org_id = @org_id
                 and e.run_id = u.run_id
                 and e.mode = 'live'
               order by e.evidence_revision desc
               limit 1
          ) v
        """;

    /// <summary>
    /// The one write: lands only when the same inputs under the same policy revision are not stored yet. No target
    /// names a column list other than the dedupe key, and no update exists on the table.
    /// </summary>
    internal const string Insert = """
        insert into public.release_decision
          (id, org_id, release_id, policy_revision_id, component_evaluations, completeness, decision, reasons, inputs_hash)
        values
          (@id, @org_id, @release_id, @policy_revision_id, @component_evaluations::jsonb, @completeness, @decision,
           string_to_array(@reasons, @array_separator), @inputs_hash)
        on conflict (org_id, release_id, policy_revision_id, inputs_hash) do nothing
        returning id as "Id"
        """;

    /// <summary>The row that already holds this key, read when the insert lost the race.</summary>
    internal const string Winner = """
        select d.id
          from public.release_decision d
         where d.org_id = @org_id
           and d.release_id = @release_id
           and d.policy_revision_id = @policy_revision_id
           and d.inputs_hash = @inputs_hash
        """;

    /// <summary>
    /// The release's newest aggregate: one row from <c>release_decision_release_created_idx</c>, no sort. The read
    /// takes no lock and writes nothing.
    /// </summary>
    internal const string Latest = """
        select d.id as "Id",
               d.policy_revision_id as "PolicyRevisionId",
               d.component_evaluations::text as "ComponentEvaluations",
               d.completeness as "Completeness",
               d.decision as "DecisionValue",
               d.reasons as "Reasons",
               d.inputs_hash as "InputsHash",
               d.created_at as "CreatedAt"
          from public.release_decision d
         where d.org_id = @org_id
           and d.release_id = @release_id
         order by d.created_at desc, d.id desc
         limit 1
        """;
}
