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
using Mediator;

namespace Evidgate.Modules.Orchestrator.Application.Queries;

/// <summary>
/// The optional, AND-combined, exact-match filters of the run list (05.2.2 §5, AC6). Every member is a value a
/// caller supplied and the endpoint already validated: <c>ref_name</c> an identity name (1..255 code points, no
/// control character), <c>pr_number</c> at least 1 and inside <c>int</c>, <c>head_sha</c> a
/// <c>^[0-9a-f]{40}([0-9a-f]{24})?$</c> commit, <c>trigger_event</c> one of the closed vocabulary and
/// <c>is_fork</c> a boolean. A null member means "no filter on that column", never "match null".
/// </summary>
/// <param name="RefName">Exact <c>ref_name</c>; the one filter the <c>run_project_ref_idx</c> keyset serves.</param>
/// <param name="PrNumber">Exact <c>pr_number</c>.</param>
/// <param name="HeadSha">Exact <c>head_sha</c>.</param>
/// <param name="TriggerEvent">Exact <c>trigger_event</c>.</param>
/// <param name="IsFork">Exact <c>is_fork</c>.</param>
internal sealed record RunListFilters(
    string? RefName,
    int? PrNumber,
    string? HeadSha,
    string? TriggerEvent,
    bool? IsFork);

/// <summary>
/// Cursor page of a project's runs (<c>GET /v1/projects/{p}/runs</c>, 05.2.2 §5, AC6): the project's runs ordered by
/// <c>(created_at, id)</c> ascending, narrowed by the run's stored identity.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Filters">The validated identity filters, AND-combined.</param>
/// <param name="Cursor">Opaque cursor, or null for the first page.</param>
/// <param name="Limit">Page size, 1–200.</param>
internal sealed record ListRunsQuery(Guid ProjectId, RunListFilters Filters, string? Cursor, int Limit)
    : IQuery<Result<RunPage>>;

/// <summary>
/// Handler of <see cref="ListRunsQuery"/>: the project check, then one keyset statement (one extra row decides the
/// next page). The list is project-scoped and read-only: <c>run:read</c> is outside the service-role ceiling
/// (<c>PermissionEvaluator.Service</c>), so a <c>ci_producer</c>/service credential can never hold it and is the
/// permission stage's <c>403 forbidden</c>.
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
/// The project must exist and be live, exactly like the module's other project-scoped reads: an unknown id, another
/// tenant's project (hidden by RLS) and a credential scoped to a different project all take the one
/// <c>404 not_found</c> of <see cref="OrchestratorErrors.ProjectNotFound"/>, so the answer is no existence oracle
/// (00 §5.4) and the same code, message and body for every one of them.
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
                row.UnverifiedRef))],
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
               r.unverified_ref
        """;

    /// <summary>The project's liveness, for the project-existence check (unknown, other tenant, soft-deleted → 404).</summary>
    internal const string ProjectExists = "select 1 from public.project where id = @project_id and status <> 'deleted'";

    /// <summary>First page of a project's runs, oldest first.</summary>
    internal static string FirstPage(RunListFilters filters) => $"""
        select {Columns}
          from public.run r
         where org_id = @org_id
           and project_id = @project_id{Where(filters)}
         order by created_at,
                  id
         limit @limit
        """;

    /// <summary>The next page: the same projection and order with the row-comparison cursor (00 §9.1).</summary>
    internal static string NextPage(RunListFilters filters) => $"""
        select {Columns}
          from public.run r
         where org_id = @org_id
           and project_id = @project_id{Where(filters)}
           and (created_at, id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
         order by created_at,
                  id
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

        if (cursorCreatedAt is not null)
        {
            parameters.Add("cursor_created_at", cursorCreatedAt.Value);
            parameters.Add("cursor_id", cursorId);
        }

        parameters.Add("limit", limit);

        return parameters;
    }

    /// <summary>The fixed <c>and …</c> fragments of the present filters, in a stable order; empty when none is set.</summary>
    private static string Where(RunListFilters filters)
    {
        var fragments = new List<string>(5);
        if (filters.RefName is not null)
        {
            fragments.Add("and ref_name = @ref_name");
        }

        if (filters.PrNumber is not null)
        {
            fragments.Add("and pr_number = @pr_number");
        }

        if (filters.HeadSha is not null)
        {
            fragments.Add("and head_sha = @head_sha");
        }

        if (filters.TriggerEvent is not null)
        {
            fragments.Add("and trigger_event = @trigger_event");
        }

        if (filters.IsFork is not null)
        {
            fragments.Add("and is_fork = @is_fork");
        }

        return fragments.Count == 0 ? string.Empty : "\n   " + string.Join("\n   ", fragments);
    }
}
