using Dapper;
using Evidgate.Modules.Orchestrator.Application.Queries;

namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The run-list statements: one parameterised statement per page shape, keyset-paged on <c>(created_at, id)</c>
/// from fixed fragments chosen by filter shape and order; a filter value is always a parameter, never text.
/// No shape needs a full Sort. Descending project-wide reads <c>run_project_created_idx</c> forward in page order
/// and uses no sort. Ascending project-wide reads it backward, and <c>ref_name</c> with descending reads
/// <c>run_project_ref_idx</c> backward; both can settle <c>created_at</c> ties with an Incremental Sort.
/// The page shapes are <c>internal</c> so the plan tests can <c>EXPLAIN</c> the exact statements the handler runs.
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

    /// <summary>First page of a project's runs in the chosen order (oldest first by default).</summary>
    internal static string FirstPage(RunListFilters filters, RunListOrder order = RunListOrder.Ascending) => $"""
        select {Columns}
          from public.run r{EvidenceJoin}
         where r.org_id = @org_id
           and r.project_id = @project_id{Where(filters)}
         order by {OrderBy(order)}
         limit @limit
        """;

    /// <summary>The next page: the same projection and order with the row-comparison cursor (00 §9.1).</summary>
    internal static string NextPage(RunListFilters filters, RunListOrder order = RunListOrder.Ascending) => $"""
        select {Columns}
          from public.run r{EvidenceJoin}
         where r.org_id = @org_id
           and r.project_id = @project_id{Where(filters)}
           {CursorPredicate(order)}
         order by {OrderBy(order)}
         limit @limit
        """;

    private static string OrderBy(RunListOrder order) => order == RunListOrder.Descending
        ? "r.created_at desc,\n                  r.id"
        : "r.created_at,\n                  r.id";

    /// <summary>
    /// The position past the cursor in the page's order. The descending form states the leading <c>created_at &lt;=</c>
    /// bound separately because a row comparison cannot mix directions, and a bare <c>or</c> would not be an index
    /// condition; the second line then only decides the rows tied on <c>created_at</c>.
    /// </summary>
    private static string CursorPredicate(RunListOrder order) => order == RunListOrder.Descending
        ? "and r.created_at <= @cursor_created_at::timestamptz"
          + "\n           and (r.created_at < @cursor_created_at::timestamptz or r.id > @cursor_id::uuid)"
        : "and (r.created_at, r.id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)";

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
        AddFilterParameters(parameters, filters);

        if (cursorCreatedAt is not null)
        {
            parameters.Add("cursor_created_at", cursorCreatedAt.Value);
            parameters.Add("cursor_id", cursorId);
        }

        parameters.Add("limit", limit);

        return parameters;
    }

    private static void AddFilterParameters(DynamicParameters parameters, RunListFilters filters)
    {
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

        if (filters.Completeness is not null)
        {
            parameters.Add("completeness", filters.Completeness);
        }
    }

    /// <summary>
    /// The fixed <c>and …</c> fragments of the present filters, in a stable order; empty when none is set. Every
    /// column is qualified with the <c>r</c> alias because <see cref="EvidenceJoin"/> brings a second relation into
    /// scope. <c>decision</c> is deliberately absent — it is Policy data, filtered by the handler's chunked scan.
    /// The fragments are predicates of the statement that carries them, so they narrow the rows the page is cut
    /// from, never a page already read. <c>completeness</c> is the one predicate on the joined revision: a run
    /// with no current revision has a null there and matches neither value.
    /// </summary>
    private static string Where(RunListFilters filters)
    {
        var fragments = new List<string>(9);
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

        if (filters.Completeness is not null)
        {
            fragments.Add("and er.completeness = @completeness");
        }

        return fragments.Count == 0 ? string.Empty : "\n   " + string.Join("\n   ", fragments);
    }
}
