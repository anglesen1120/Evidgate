namespace Evidgate.Modules.Policy.Infrastructure.Persistence;

/// <summary>
/// The read behind <c>IPolicyContracts.GetLiveEvaluationsAsync</c>: one statement for a whole page of a run's evidence
/// revisions, served by <c>evaluation_live_key</c> (<c>org_id, run_id, evidence_revision</c> where <c>mode = 'live'</c>,
/// <c>0059</c>) so the page costs one index lookup per revision and no sort of the run's evaluation history.
/// </summary>
/// <remarks>
/// <c>mode = 'live'</c> is a predicate of its own, not a narrowing of a uniqueness the table already enforces: a
/// <c>shadow</c> row is legal beside the live one, so leaving it out here is what keeps a second opinion from being
/// served as the run's verdict. The explicit <c>org_id</c> match sits on top of the tenant policy (C3), and the read
/// takes no lock and writes nothing (C1).
/// </remarks>
internal static class EvaluationLookupSql
{
    /// <summary>
    /// The live evaluation of each named revision of one run. The revision numbers arrive as one delimited scalar
    /// (commas) because the tenant Dapper path refuses a client array, and are expanded to <c>integer[]</c> in the
    /// statement; the caller never sends an empty list, so the cast always sees at least one element.
    /// </summary>
    internal const string LiveEvaluations = """
        select e.evidence_revision,
               e.id as evaluation_id
          from public.evaluation e
         where e.run_id = @run_id
           and e.org_id = @org_id
           and e.mode = 'live'
           and e.evidence_revision = any (string_to_array(@revision_nos, ',')::integer[])
         order by e.evidence_revision
        """;
}
