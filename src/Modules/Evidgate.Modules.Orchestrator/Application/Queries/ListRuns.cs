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
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Evidgate.Modules.Policy.Contracts;
using Mediator;

namespace Evidgate.Modules.Orchestrator.Application.Queries;

/// <summary>
/// A cursor page of the project's matching runs. The default is oldest first; newest first reverses the
/// timestamp order but keeps <c>id</c> ascending for ties.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Filters">The validated filters, AND-combined and applied before the page is cut.</param>
/// <param name="Cursor">Opaque cursor issued under the same <paramref name="Order"/>, or null for the first page.</param>
/// <param name="Limit">Page size, 1–200.</param>
/// <param name="Order">The page order; the default keeps the oldest-first list and its cursors.</param>
internal sealed record ListRunsQuery(
    Guid ProjectId,
    RunListFilters Filters,
    string? Cursor,
    int Limit,
    RunListOrder Order = RunListOrder.Ascending)
    : IQuery<Result<RunPage>>;

/// <summary>
/// Handler of <see cref="ListRunsQuery"/>: the project check, then one keyset statement (one extra row decides the
/// next page). The list is project-scoped and read-only: <c>run:read</c> is outside the service-role ceiling
/// (<c>PermissionEvaluator.Service</c>), so a <c>ci_producer</c>/service credential can never hold it and is the
/// permission stage's <c>403 forbidden</c>. <c>state</c>, <c>rerun_scope</c> and <c>parent_run_id</c> are <c>where</c>
/// predicates of that one statement, so the page is cut from rows that already match. <c>decision</c> is Policy data
/// and cannot join here, so that filter is applied over the same keyset order in bounded chunks — one Policy batch
/// read per chunk — and the page carries the newest live verdict Policy names for each row, null while the run has
/// none; never a per-row read and never a join across module tables. A row's <c>completeness</c> is the joined
/// <c>evidence_revision.completeness</c> of the run's current revision and stays null while the run has none (never a
/// fabricated <c>incomplete</c>); <c>parent_run_id</c> and <c>rerun_scope</c> are the run's own columns.
/// <para>
/// Each page continues strictly past the cursor in the requested order. The cursor is unsigned: project and tenant
/// isolation come from the query predicates and RLS, not from the encoded position. A budget-limited decision scan
/// resumes after the last scanned row. An unparseable position or wrong order marker is <c>422 request_invalid</c>.
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
/// <para>
/// One member needs a second permission: a run's <c>decision</c> is <c>decision:read</c> data (00 §9.3), so a caller
/// holding <c>run:read</c> only gets the page with every item's <c>decision</c> null and Policy is never asked, and
/// the <c>decision</c> filter answers <c>403 forbidden</c> instead of a page — an empty page would still say which
/// runs carry a verdict. The gate is asked once per page, inside this handler, because the pipeline sees only the
/// route's <c>run:read</c>.
/// </para>
/// </summary>
[Permission(Permissions.Run.Read)]
internal sealed class ListRunsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IPermissionEvaluator permissions,
    IPolicyContracts policy)
    : IQueryHandler<ListRunsQuery, Result<RunPage>>
{
    /// <summary>
    /// The most rows one <c>decision</c>-filtered page may scan. Policy data cannot join the Orchestrator statement
    /// (module data is private), so the filter reads chunks of runs and asks Policy about each chunk; this cap keeps a
    /// page whose filter matches almost nothing from scanning the whole project. A page may therefore hold fewer than
    /// <c>limit</c> items while <c>next_cursor</c> is present.
    /// </summary>
    internal const int MaxScannedRows = 1000;

    /// <summary>The smallest chunk the decision scan reads; a page limit of 1 must not cost one round trip per row.</summary>
    private const int MinScanChunk = 50;

    /// <summary>The largest chunk, equal to the batch read's own bound (the page limit's maximum).</summary>
    private const int MaxScanChunk = 200;

    /// <inheritdoc />
    public async ValueTask<Result<RunPage>> Handle(ListRunsQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(ListRunsQuery));

        // Non-HTTP callers must receive the same validation errors rather than a page narrowed by an unknown value.
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

        // A verdict is decision:read data (00 §9.3) while this page answers under run:read only, so the two are
        // gated apart here: without decision:read no verdict is read at all (every item's decision stays null and
        // Policy is never asked) and a `decision` filter is refused — an empty page would still disclose which runs
        // match a verdict. The project boundary above runs first, so a foreign project keeps its 404.
        var context = tenant.Require();
        var verdictsVisible = principals.Current is { } principal
            && (await permissions
                .EvaluateAsync(Permissions.Decision.Read, principal, context, cancellationToken)
                .ConfigureAwait(false))
                .IsGranted;

        if (query.Filters.Decision is not null && !verdictsVisible)
        {
            return Result.Failure<RunPage>(Error.Forbidden(
                "reading a run's decision needs decision:read, so the decision filter is refused without it"));
        }

        var firstPage = query.Cursor is null;
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        if (!firstPage && !TryDecodeCursor(query.Order, query.Cursor, out cursorCreatedAt, out cursorId))
        {
            return Result.Failure<RunPage>(Error.Invalid(
                "the cursor is not a value this endpoint issued",
                [new ErrorDetail("cursor", "malformed")]));
        }

        var orgId = scopes.Require().OrgId;
        var page = query.Filters.Decision is { } decision
            ? await ScanByDecisionAsync(query, orgId, decision, firstPage, cursorCreatedAt, cursorId, cancellationToken)
                .ConfigureAwait(false)
            : await ReadPageAsync(
                    query, orgId, firstPage, cursorCreatedAt, cursorId, verdictsVisible, cancellationToken)
                .ConfigureAwait(false);

        return Result.Success(new RunPage([.. page.Rows.Select(ToSummary)], page.NextCursor));
    }

    /// <summary>The wire row of one statement row, with the verdict Policy named for it (null while unevaluated).</summary>
    private static RunSummary ToSummary(RunListRow row) => new(
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
        row.RerunScope,
        row.Decision);

    /// <summary>
    /// The page without a <c>decision</c> filter: one keyset statement (one extra row decides the next page) and,
    /// only for a caller that holds <c>decision:read</c>, one Policy batch read for the page's decisions — never one
    /// read per row.
    /// </summary>
    private async ValueTask<ScannedPage> ReadPageAsync(
        ListRunsQuery query,
        Guid orgId,
        bool firstPage,
        DateTimeOffset cursorCreatedAt,
        Guid cursorId,
        bool verdictsVisible,
        CancellationToken cancellationToken)
    {
        var rows = await ReadChunkAsync(
                query,
                orgId,
                firstPage ? null : cursorCreatedAt,
                cursorId,
                query.Limit + 1,
                cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        IReadOnlyList<RunListRow> page = hasMore ? [.. rows.Take(query.Limit)] : rows;
        if (verdictsVisible)
        {
            await AttachDecisionsAsync(page, cancellationToken).ConfigureAwait(false);
        }

        return new ScannedPage(
            page,
            hasMore ? EncodeCursor(query.Order, page[^1].CreatedAt, page[^1].RunId) : null);
    }

    /// <summary>
    /// The page under a <c>decision</c> filter: the same keyset order read in chunks, each chunk's verdicts read in one
    /// Policy call, until <c>limit + 1</c> matches settle <c>has_more</c> or the runs or the scan budget end. Rows are
    /// only ever returned forward of the cursor, so paging neither repeats nor skips a run: when the budget ends
    /// mid-scan the cursor is the last <em>scanned</em> row, which every returned row sorts before.
    /// </summary>
    private async ValueTask<ScannedPage> ScanByDecisionAsync(
        ListRunsQuery query,
        Guid orgId,
        string decision,
        bool firstPage,
        DateTimeOffset cursorCreatedAt,
        Guid cursorId,
        CancellationToken cancellationToken)
    {
        var matches = new List<RunListRow>(query.Limit + 1);
        var scanned = 0;
        var exhausted = false;
        var position = firstPage ? (DateTimeOffset?)null : cursorCreatedAt;
        var lastScannedAt = cursorCreatedAt;
        var lastScannedId = cursorId;

        while (matches.Count <= query.Limit && !exhausted && scanned < MaxScannedRows)
        {
            var take = Math.Min(Math.Clamp(query.Limit * 2, MinScanChunk, MaxScanChunk), MaxScannedRows - scanned);
            var rows = await ReadChunkAsync(query, orgId, position, lastScannedId, take, cancellationToken)
                .ConfigureAwait(false);

            if (rows.Count > 0)
            {
                await AttachDecisionsAsync(rows, cancellationToken).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    lastScannedAt = row.CreatedAt;
                    lastScannedId = row.RunId;
                    if (MatchesDecision(row.Decision, decision))
                    {
                        matches.Add(row);
                    }
                }
            }

            scanned += rows.Count;
            position = lastScannedAt;
            exhausted = rows.Count < take;
        }

        var page = matches.Count > query.Limit ? [.. matches.Take(query.Limit)] : matches;
        string? nextCursor;
        if (matches.Count > query.Limit)
        {
            nextCursor = EncodeCursor(query.Order, page[^1].CreatedAt, page[^1].RunId);
        }
        else if (exhausted)
        {
            nextCursor = null;
        }
        else
        {
            nextCursor = EncodeCursor(query.Order, lastScannedAt, lastScannedId);
        }

        return new ScannedPage(page, nextCursor);
    }

    /// <summary>
    /// One keyset chunk, past <paramref name="cursorCreatedAt"/> in the query's order; a null cursor is the first
    /// page. The <c>decision</c> filter declares no SQL predicate, because the <c>evaluation</c> table belongs to
    /// Policy.
    /// </summary>
    private Task<IReadOnlyList<RunListRow>> ReadChunkAsync(
        ListRunsQuery query,
        Guid orgId,
        DateTimeOffset? cursorCreatedAt,
        Guid cursorId,
        int limit,
        CancellationToken cancellationToken) =>
        db.QueryAsync<RunListRow>(
            cursorCreatedAt is null
                ? ListRunsSql.FirstPage(query.Filters, query.Order)
                : ListRunsSql.NextPage(query.Filters, query.Order),
            ListRunsSql.Parameters(orgId, query.ProjectId, query.Filters, cursorCreatedAt, cursorId, limit),
            cancellationToken);

    /// <summary>The ascending cursor is the codec's original shape; the descending one carries its own marker.</summary>
    private static string EncodeCursor(RunListOrder order, DateTimeOffset createdAt, Guid id) =>
        order == RunListOrder.Descending
            ? KeysetCursor.EncodeDescending(createdAt, id)
            : KeysetCursor.Encode(createdAt, id);

    private static bool TryDecodeCursor(RunListOrder order, string? cursor, out DateTimeOffset createdAt, out Guid id) =>
        order == RunListOrder.Descending
            ? KeysetCursor.TryDecodeDescending(cursor, out createdAt, out id)
            : KeysetCursor.TryDecode(cursor, out createdAt, out id);

    /// <summary>Sets each row's stored verdict from one Policy batch read; a run Policy did not name stays null.</summary>
    private async ValueTask AttachDecisionsAsync(IReadOnlyList<RunListRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var decisions = await policy
            .GetLatestLiveDecisionsAsync([.. rows.Select(row => row.RunId)], cancellationToken)
            .ConfigureAwait(false);

        if (decisions.Count == 0)
        {
            return;
        }

        var byRun = decisions.ToDictionary(item => item.RunId);
        foreach (var row in rows)
        {
            if (byRun.TryGetValue(row.RunId, out var item))
            {
                row.Decision = item.Decision;
            }
        }
    }

    /// <summary>
    /// Compares the row's stored (upper case) verdict with the filter's mapped verdict, case-exactly; the
    /// vocabulary is the contract's and already validated, so <c>pending</c> is the only token that maps to
    /// <c>null</c> and matches a run without a live evaluation.
    /// </summary>
    private static bool MatchesDecision(string? stored, string filter) =>
        string.Equals(stored, RunListFilterRules.StoredDecision(filter), StringComparison.Ordinal);

    /// <summary>One page of rows plus its next cursor; the rows carry their stored verdicts already.</summary>
    private sealed record ScannedPage(IReadOnlyList<RunListRow> Rows, string? NextCursor);

    /// <summary>One row as <see cref="ListRunsSql"/> returns it, plus the verdict Policy names for it.</summary>
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

        /// <summary>The newest stored live verdict, read from Policy; null while the run is not evaluated.</summary>
        public string? Decision { get; set; }
    }
}
