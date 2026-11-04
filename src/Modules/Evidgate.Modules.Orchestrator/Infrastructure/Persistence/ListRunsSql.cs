using Dapper;
using Evidgate.Modules.Orchestrator.Application.Queries;

namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The Dapper statements of the run list (00 §5.5 C5): one parameterised statement per page shape, keyset-paged on
/// <c>(created_at, id)</c> over <c>run_project_ref_idx</c> when <c>ref_name</c> is filtered, and built from fixed
/// fragments chosen by the filter shape — a filter value is always a parameter, never concatenated into the text.
/// The two page shapes and the fragments are <c>internal</c> so <c>ListRunsIndexPlanTests</c> can run
/// <c>EXPLAIN</c> over the exact statement the handler executes (AC6, §8 P2).
/// </summary>
internal static class ListRunsSql
{
    private const string Columns = """
        r.id as run_id,
               r.state,
               r.created_at,
               r.ref_kind,
               r.ref_name,
               r.pr_number,
               r.head_sha,
               r.base_sha,
               r.trigger_event,
               r.is_fork,
               r.unverified_ref,
               r.parent_run_id,
               r.rerun_scope,
               er.completeness as completeness
        """;

    /// <summary>
    /// The one join of the statement: the run's <em>current</em> evidence revision, for its <c>completeness</c>
    /// only. It is a <c>left join</c> because the pointer is null until the reconciler freezes a revision (0049), so
    /// a run without one yields one row with a null <c>completeness</c> — no duplicate and no fabricated verdict.
    /// The join key carries <c>org_id</c> as well as <c>id</c>: the composite FK's shape, and the tenant predicate
    /// the <c>FORCE</c>d policy of <c>evidence_revision</c> needs on its own side of the join. The fragment starts
    /// with a line break so it continues the <c>from</c> clause on the next line of both page shapes.
    /// </summary>
    private const string EvidenceJoin =
        "\n  left join public.evidence_revision er"
        + "\n    on er.org_id = r.org_id"
        + "\n   and er.id = r.current_evidence_revision_id";

    /// <summary>The project's liveness, for the project-existence check (unknown, other tenant, soft-deleted → 404).</summary>
    internal const string ProjectExists = "select 1 from public.project where id = @project_id and status <> 'deleted'";

    /// <summary>First page of a project's runs, oldest first.</summary>
    internal static string FirstPage(RunListFilters filters) => $"""
        select {Columns}
          from public.run r{EvidenceJoin}
         where r.org_id = @org_id
           and r.project_id = @project_id{Where(filters)}
         order by r.created_at,
                  r.id
         limit @limit
        """;

    /// <summary>The next page: the same projection and order with the row-comparison cursor (00 §9.1).</summary>
    internal static string NextPage(RunListFilters filters) => $"""
        select {Columns}
          from public.run r{EvidenceJoin}
         where r.org_id = @org_id
           and r.project_id = @project_id{Where(filters)}
           and (r.created_at, r.id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
         order by r.created_at,
                  r.id
         limit @limit
        """;

    /// <summary>
    /// The parameters the chosen statement declares, one per filter that is present — never an extra one, because
    /// the extended query protocol refuses a bind that supplies a parameter the statement does not use.
    /// </summary>
    internal static DynamicParameters Parameters(
        Guid orgId,
        Guid projectId,
        RunListFilters filters,
        DateTimeOffset? cursorCreatedAt,
        Guid cursorId,
        int limit)
    {
        var parameters = new DynamicParameters();
        parameters.Add("org_id", orgId);
        parameters.Add("project_id", projectId);

        if (filters.RefName is not null)
        {
            parameters.Add("ref_name", filters.RefName);
        }

        if (filters.PrNumber is not null)
        {
            parameters.Add("pr_number", filters.PrNumber);
        }

        if (filters.HeadSha is not null)
        {
            parameters.Add("head_sha", filters.HeadSha);
        }

        if (filters.TriggerEvent is not null)
        {
            parameters.Add("trigger_event", filters.TriggerEvent);
        }

        if (filters.IsFork is not null)
        {
            parameters.Add("is_fork", filters.IsFork.Value);
        }

        if (filters.State is not null)
        {
            parameters.Add("state", filters.State);
        }

        // `decision` declares no parameter: the verdict lives in Policy's `evaluation` table, which this module may
        // not read, so the handler applies that filter over the keyset order instead of in this statement.
        if (filters.RerunScope is not null)
        {
            parameters.Add("rerun_scope", filters.RerunScope);
        }

        if (filters.ParentRunId is not null)
        {
            parameters.Add("parent_run_id", filters.ParentRunId.Value);
        }

        if (cursorCreatedAt is not null)
        {
            parameters.Add("cursor_created_at", cursorCreatedAt.Value);
            parameters.Add("cursor_id", cursorId);
        }

        parameters.Add("limit", limit);

        return parameters;
    }

    /// <summary>
    /// The fixed <c>and …</c> fragments of the present filters, in a stable order; empty when none is set. Every
    /// column is qualified with the <c>r</c> alias because <see cref="EvidenceJoin"/> brings a second relation into
    /// scope. <c>decision</c> is deliberately absent — it is Policy data, filtered by the handler's chunked scan.
    /// The fragments are predicates of the statement that carries them, so they narrow the rows the page is cut
    /// from, never a page already read.
    /// </summary>
    private static string Where(RunListFilters filters)
    {
        var fragments = new List<string>(8);
        if (filters.RefName is not null)
        {
            fragments.Add("and r.ref_name = @ref_name");
        }

        if (filters.PrNumber is not null)
        {
            fragments.Add("and r.pr_number = @pr_number");
        }

        if (filters.HeadSha is not null)
        {
            fragments.Add("and r.head_sha = @head_sha");
        }

        if (filters.TriggerEvent is not null)
        {
            fragments.Add("and r.trigger_event = @trigger_event");
        }

        if (filters.IsFork is not null)
        {
            fragments.Add("and r.is_fork = @is_fork");
        }

        if (filters.State is not null)
        {
            fragments.Add("and r.state = @state");
        }

        if (filters.RerunScope is not null)
        {
            fragments.Add("and r.rerun_scope = @rerun_scope");
        }

        if (filters.ParentRunId is not null)
        {
            fragments.Add("and r.parent_run_id = @parent_run_id");
        }

        return fragments.Count == 0 ? string.Empty : "\n   " + string.Join("\n   ", fragments);
    }
}
