using Dapper;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Mediator;

namespace Evidgate.Modules.Orchestrator.Application.Queries;

/// <summary>
/// The optional, AND-combined, exact-match filters of the run list (05.2.2 §5, AC6; F43/S143 adds
/// <c>state</c>, <c>decision</c>, <c>rerun_scope</c> and <c>parent_run_id</c>). Every member is a value a
/// caller supplied and the endpoint already validated: <c>ref_name</c> an identity name (1..255 code points, no
/// control character), <c>pr_number</c> at least 1 and inside <c>int</c>, <c>head_sha</c> a
/// <c>^[0-9a-f]{40}([0-9a-f]{24})?$</c> commit, <c>trigger_event</c> one of the closed vocabulary,
/// <c>is_fork</c> a boolean, <c>state</c> one of the seven 00 §8.1 states, <c>decision</c> only
/// <c>pending</c> (no <c>evaluation</c> row exists before 07.2.2), <c>rerun_scope</c> <c>full</c> or
/// <c>failed_subset</c>, and <c>parent_run_id</c> a uuid. A null member means "no filter on that column",
/// never "match null" — a plain run (no parent, no rerun scope) matches neither of those two filters.
/// <para>
/// The four F43 members are declared with a default so a caller that only narrows the recorded identity
/// (the <c>EXPLAIN</c> harness of <c>ListRunsIndexPlanTests</c>) keeps stating exactly the filters it binds;
/// the produced statement and its parameters stay shape-driven either way.
/// </para>
/// </summary>
/// <param name="RefName">Exact <c>ref_name</c>; the one filter the <c>run_project_ref_idx</c> keyset serves.</param>
/// <param name="PrNumber">Exact <c>pr_number</c>.</param>
/// <param name="HeadSha">Exact <c>head_sha</c>.</param>
/// <param name="TriggerEvent">Exact <c>trigger_event</c>.</param>
/// <param name="IsFork">Exact <c>is_fork</c>.</param>
/// <param name="State">Exact <c>run.state</c>, one of the seven 00 §8.1 states; not index-served, so it scans.</param>
/// <param name="Decision">
/// The run's evaluation decision. Only <c>pending</c> is accepted (F43): no stored decision exists before
/// 07.2.2, so <c>pending</c> matches every run and contributes no predicate, and every other value is refused
/// 422 <c>request_invalid</c> by <see cref="RunListFilterRules"/> instead of narrowing to an empty page.
/// </param>
/// <param name="RerunScope">Exact <c>run.rerun_scope</c> (0031): <c>full</c> or <c>failed_subset</c>.</param>
/// <param name="ParentRunId">Exact <c>run.parent_run_id</c> (0031): the run a rerun was created from.</param>
internal sealed record RunListFilters(
    string? RefName,
    int? PrNumber,
    string? HeadSha,
    string? TriggerEvent,
    bool? IsFork,
    string? State = null,
    string? Decision = null,
    string? RerunScope = null,
    Guid? ParentRunId = null);

/// <summary>
/// The closed vocabularies of the run list's F43 filters and the one refusal they own (00 §9.2 <c>422
/// request_invalid</c> with <c>details[].path</c>). The endpoint validates a query-string value with the same
/// rules, so a malformed value never reaches the handler; the handler re-checks the typed filters because the
/// contract it serves is the handler's — a <c>decision</c> other than <c>pending</c> must be a 422 and never a
/// silently narrowed (or silently widened) page, whatever bound the query.
/// </summary>
internal static class RunListFilterRules
{
    /// <summary>The only <c>decision</c> value the contract accepts today (00 §9.3 F43).</summary>
    internal const string PendingDecision = "pending";

    /// <summary>The accepted <c>decision</c> vocabulary; 07.2.2/07.5.2 widen it with the stored verdicts.</summary>
    internal static readonly IReadOnlyList<string> Decisions = [PendingDecision];

    /// <summary>The <c>run.rerun_scope</c> values 0031's <c>run_rerun_scope_check</c> admits.</summary>
    internal static readonly IReadOnlyList<string> RerunScopes = ["full", "failed_subset"];

    /// <summary>The 00 §9.2 reason of a filter value the column's own vocabulary cannot hold.</summary>
    internal const string InvalidValue = "invalid_value";

    /// <summary>True when <paramref name="state"/> is one of the seven 00 §8.1 run states (00 §8.1, 0031's <c>run_state_check</c>).</summary>
    internal static bool IsKnownState(string state) => RunStateMachine.IsKnownState(state);

    /// <summary>
    /// The per-parameter failures of the F43 filter values, in parameter order; empty when every present value is
    /// one the column can hold. <c>parent_run_id</c> is a <see cref="Guid"/> already, so a non-UUID value is the
    /// endpoint's refusal and never reaches here.
    /// </summary>
    internal static List<ErrorDetail> Validate(RunListFilters filters)
    {
        var errors = new List<ErrorDetail>(3);

        if (filters.State is { } state && !IsKnownState(state))
        {
            errors.Add(new ErrorDetail("state", InvalidValue));
        }

        if (filters.Decision is { } decision && !Decisions.Contains(decision, StringComparer.Ordinal))
        {
            errors.Add(new ErrorDetail("decision", InvalidValue));
        }

        if (filters.RerunScope is { } rerunScope && !RerunScopes.Contains(rerunScope, StringComparer.Ordinal))
        {
            errors.Add(new ErrorDetail("rerun_scope", InvalidValue));
        }

        return errors;
    }
}

/// <summary>
/// Cursor page of a project's runs (<c>GET /v1/projects/{p}/runs</c>, 05.2.2 §5, AC6): the project's runs ordered by
/// <c>(created_at, id)</c> ascending, narrowed by the run's stored identity and — since F43/S143 — by its
/// <c>state</c>, <c>decision</c>, <c>rerun_scope</c> and <c>parent_run_id</c>.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Filters">The validated filters, AND-combined and applied before the page is cut.</param>
/// <param name="Cursor">Opaque cursor, or null for the first page.</param>
/// <param name="Limit">Page size, 1–200.</param>
internal sealed record ListRunsQuery(Guid ProjectId, RunListFilters Filters, string? Cursor, int Limit)
    : IQuery<Result<RunPage>>;

/// <summary>
/// Handler of <see cref="ListRunsQuery"/>: the project check, then one keyset statement (one extra row decides the
/// next page). The list is project-scoped and read-only: <c>run:read</c> is outside the service-role ceiling
/// (<c>PermissionEvaluator.Service</c>), so a <c>ci_producer</c>/service credential can never hold it and is the
/// permission stage's <c>403 forbidden</c>. Every F43 filter is a <c>where</c> predicate of that one statement, so
/// the page is cut from rows that already match — never filtered client-side and never paged before the filter. A
/// row's <c>completeness</c> is the joined <c>evidence_revision.completeness</c> of the run's current revision and
/// stays null while the run has none (never a fabricated <c>incomplete</c>); <c>parent_run_id</c> and
/// <c>rerun_scope</c> are the run's own columns.
/// <para>
/// Order is <c>(created_at, id)</c> ascending and the cursor is the opaque <c>KeysetCursor</c> over the last row of
/// the page, so <c>(created_at, id) &gt; (cursor)</c> is stable under concurrent inserts: a row created between two
/// pages sorts after the cursor (or after the rows already read) and is neither repeated nor skipped. The cursor is
/// not signed and carries no tenant: it is bound to the caller's own rows by the query itself, because the
/// <c>org_id</c> predicate, the project scope and the <c>FORCE</c>d RLS policy are always applied — a well-formed
/// cursor can only move the page position inside the caller's own project, never read another project's or tenant's
/// rows. A cursor this codec did not produce is <c>422 request_invalid</c> (<c>cursor</c>, <c>malformed</c>) rather
/// than a guessed page position.
/// </para>
/// <para>
/// The project must exist and be live, exactly like the module's other project-scoped reads: an unknown id and
/// another tenant's project (hidden by RLS) both take the one <c>404 not_found</c> of
/// <see cref="OrchestratorErrors.ProjectNotFound"/>, so the answer is no existence oracle (00 §5.4) and the same
/// code, message and body for both. The scope-mismatch branch (a credential bound to a different project) follows
/// <see cref="GetRunQuery"/> and maps the same way, but it is defensive and not reachable over HTTP today: only a
/// service credential carries a project scope, and <c>run:read</c> is outside the service-role ceiling
/// (<c>PermissionEvaluator.Service</c>), so such a caller is refused at the permission stage with
/// <c>403 forbidden</c> first.
/// </para>
/// </summary>
[Permission(Permissions.Run.Read)]
internal sealed class ListRunsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<ListRunsQuery, Result<RunPage>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RunPage>> Handle(ListRunsQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(ListRunsQuery));

        // The F43 vocabularies are re-checked here, before the project check, because the handler owns the contract
        // it serves: a `decision` the column cannot hold (anything but `pending`) must be a 422, never a page. The
        // endpoint refuses the same values first, so this only fires for a caller that did not come through it.
        if (RunListFilterRules.Validate(query.Filters) is { Count: > 0 } filterErrors)
        {
            return Result.Failure<RunPage>(Error.Invalid(
                "a run-list filter value is outside the values this page accepts",
                filterErrors));
        }

        if (OrchestratorScope.Project(query.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<RunPage>(OrchestratorErrors.ProjectNotFound);
        }

        var live = await db.ScalarAsync<int?>(
                ListRunsSql.ProjectExists,
                new { project_id = query.ProjectId },
                cancellationToken)
            .ConfigureAwait(false);
        if (live is null)
        {
            return Result.Failure<RunPage>(OrchestratorErrors.ProjectNotFound);
        }

        var firstPage = query.Cursor is null;
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        if (!firstPage && !KeysetCursor.TryDecode(query.Cursor, out cursorCreatedAt, out cursorId))
        {
            return Result.Failure<RunPage>(Error.Invalid(
                "the cursor is not a value this endpoint issued",
                [new ErrorDetail("cursor", "malformed")]));
        }

        var orgId = scopes.Require().OrgId;
        var rows = await db.QueryAsync<RunListRow>(
                firstPage ? ListRunsSql.FirstPage(query.Filters) : ListRunsSql.NextPage(query.Filters),
                ListRunsSql.Parameters(orgId, query.ProjectId, query.Filters, firstPage ? null : cursorCreatedAt, cursorId, query.Limit + 1),
                cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        IReadOnlyList<RunListRow> page = hasMore ? [.. rows.Take(query.Limit)] : rows;

        return Result.Success(new RunPage(
            [.. page.Select(row => new RunSummary(
                row.RunId,
                row.State,
                row.CreatedAt,
                new RunRef(
                    row.RefKind,
                    row.RefName,
                    row.PrNumber,
                    row.HeadSha,
                    row.BaseSha,
                    row.TriggerEvent,
                    row.IsFork),
                row.UnverifiedRef,
                row.Completeness,
                row.ParentRunId,
                row.RerunScope))],
            hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].RunId) : null));
    }

    /// <summary>One row as <see cref="ListRunsSql"/> returns it (snake_case columns mapped by Dapper).</summary>
    private sealed class RunListRow
    {
        public Guid RunId { get; set; }

        public string State { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }

        public string? RefKind { get; set; }

        public string? RefName { get; set; }

        public int? PrNumber { get; set; }

        public string? HeadSha { get; set; }

        public string? BaseSha { get; set; }

        public string? TriggerEvent { get; set; }

        public bool IsFork { get; set; }

        public bool UnverifiedRef { get; set; }

        /// <summary><c>evidence_revision.completeness</c> of the run's current revision; null until it has one.</summary>
        public string? Completeness { get; set; }

        public Guid? ParentRunId { get; set; }

        public string? RerunScope { get; set; }
    }
}

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

        // `decision` declares no parameter: the only value the contract accepts today (`pending`) matches every
        // run, because no `evaluation` row exists before 07.2.2, so it contributes no predicate.
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
    /// scope. <c>decision</c> is deliberately absent: see <see cref="Parameters"/>. The fragments are predicates of
    /// the statement that carries them, so they narrow the rows the page is cut from, never a page already read.
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
