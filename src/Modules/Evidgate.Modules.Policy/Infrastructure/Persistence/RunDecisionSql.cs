namespace Evidgate.Modules.Policy.Infrastructure.Persistence;

/// <summary>
/// The read behind <c>GET /v1/runs/{r}/decision</c>: this module's own <c>evaluation</c> row, so the run and its
/// project are reached through the Orchestrator contract instead of a join here (module data is private).
/// </summary>
/// <remarks>
/// <c>mode = 'live'</c> is a predicate of its own rather than a narrowing of the table's unique keys: a <c>shadow</c>
/// row is legal beside the live one, and leaving it out is what keeps a second opinion from being served as the run's
/// verdict. The explicit <c>org_id</c> match sits on top of the tenant policy (C3), and the read takes no lock and
/// writes nothing (C1).
/// </remarks>
internal static class RunDecisionSql
{
    /// <summary>
    /// The run's newest <c>live</c> evaluation: every member of the response is copied from this one row, whatever the
    /// size of its <c>missing</c>/<c>reasons</c> arrays, so the route costs one statement and never N+1. The ordering is
    /// structural — <c>(org_id, run_id, evidence_revision)</c> is unique for <c>live</c> rows, so no tie-break is needed —
    /// and a run with no live row reads nothing, which the handler answers as <c>pending</c>.
    /// </summary>
    internal const string LatestLive = """
        select e.id as "EvaluationId",
               e.evidence_revision as "EvidenceRevision",
               e.completeness as "Completeness",
               e.outcome as "Outcome",
               e.decision as "DecisionValue",
               e.missing as "Missing",
               e.reasons as "Reasons",
               e.policy_hash as "PolicyHash",
               e.manifest_hash as "ManifestHash",
               e.evaluator_version as "EvaluatorVersion",
               e.mode as "Mode"
          from public.evaluation e
         where e.org_id = @org_id
           and e.run_id = @run_id
           and e.mode = 'live'
         order by e.evidence_revision desc
         limit 1
        """;
}

/// <summary>One row as <see cref="RunDecisionSql.LatestLive"/> returns it (quoted columns mapped by Dapper).</summary>
internal sealed class RunDecisionRow
{
    public Guid EvaluationId { get; set; }

    public int EvidenceRevision { get; set; }

    public string Completeness { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string DecisionValue { get; set; } = string.Empty;

    public string[] Missing { get; set; } = [];

    public string[] Reasons { get; set; } = [];

    public string PolicyHash { get; set; } = string.Empty;

    public string ManifestHash { get; set; } = string.Empty;

    public string EvaluatorVersion { get; set; } = string.Empty;

    public string Mode { get; set; } = string.Empty;
}
