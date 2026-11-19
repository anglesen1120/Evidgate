namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The read of <c>IOrchestratorEvaluationPlan</c>. It runs on the caller's tenant scope and passes the trusted
/// <c>org_id</c> explicitly as well as relying on row level security, so another tenant's run yields no row whatever
/// the caller asks for.
/// </summary>
internal static class EvaluationPlanSql
{
    /// <summary>
    /// The run's plan in one statement: the run and its manifest as the header, left-joined to every planned execution
    /// so a run that planned nothing still answers its header (one row, the execution columns null). <c>required</c> is
    /// read here rather than through the catalogue because it describes the plan, not the case; the key order is the
    /// order the evaluator assembles its result rows in.
    /// </summary>
    internal const string LoadPlan = """
        select r.policy_revision_id as "PolicyRevisionId",
               r.project_id as "ProjectId",
               r.purpose as "Purpose",
               r.is_fork as "IsFork",
               m.planned_count as "PlannedCount",
               r.suite_revision_id as "SuiteRevisionId",
               pe.execution_key as "ExecutionKey",
               pe.case_revision_id as "CaseRevisionId",
               pe.required as "Required"
          from public.run r
          join public.manifest m on m.org_id = r.org_id and m.id = r.manifest_id
          left join public.planned_execution pe on pe.org_id = r.org_id and pe.run_id = r.id
         where r.org_id = @org_id
           and r.id = @run_id
         order by pe.execution_key
        """;

    /// <summary>
    /// The run's scope columns alone: one primary-key row of <c>run</c>, no manifest and no planned execution, so a
    /// poll costs the same for a run of any size. The trusted <c>org_id</c> is a predicate as well as a scope.
    /// </summary>
    internal const string LoadHeader = """
        select r.project_id as "ProjectId",
               r.policy_revision_id as "PolicyRevisionId",
               r.purpose as "Purpose",
               r.is_fork as "IsFork",
               r.suite_revision_id as "SuiteRevisionId"
          from public.run r
         where r.org_id = @org_id
           and r.id = @run_id
        """;
}
