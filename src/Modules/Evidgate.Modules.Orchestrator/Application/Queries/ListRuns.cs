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
/// The optional, AND-combined, exact-match filters of the run list (05.2.2 §5, AC6; F43/S143 adds
/// <c>state</c>, <c>decision</c>, <c>rerun_scope</c> and <c>parent_run_id</c>). Every member is a value a
/// caller supplied and the endpoint already validated: <c>ref_name</c> an identity name (1..255 code points, no
/// control character), <c>pr_number</c> at least 1 and inside <c>int</c>, <c>head_sha</c> a
/// <c>^[0-9a-f]{40}([0-9a-f]{24})?$</c> commit, <c>trigger_event</c> one of the closed vocabulary,
/// <c>is_fork</c> a boolean, <c>state</c> one of the seven 00 §8.1 states, <c>decision</c> <c>pending</c> or one
/// verdict in lower snake (<c>pass</c>, <c>fail</c>, <c>inconclusive</c>, mapped to the stored 00 §7 verdict by
/// <see cref="RunListFilterRules.StoredDecision"/>), <c>rerun_scope</c> <c>full</c> or <c>failed_subset</c>, and
/// <c>parent_run_id</c> a uuid. A null
/// member means "no filter on that column",
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
/// The run's newest live evaluation verdict: <c>PASS</c>, <c>FAIL</c>, <c>INCONCLUSIVE</c>, or <c>pending</c> for a
/// run with no live evaluation. The verdicts live in Policy, so this filter declares no SQL predicate and is applied
/// by a bounded chunked scan of the keyset order; any other value is refused 422 <c>request_invalid</c>.
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
/// contract it serves is the handler's — a <c>decision</c> outside the vocabulary must be a 422 and never a silently
/// narrowed (or silently widened) page, whatever bound the query.
/// </summary>
internal static class RunListFilterRules
{
    /// <summary>The <c>decision</c> value for a run whose newest live evaluation does not exist yet.</summary>
    internal const string PendingDecision = "pending";

    /// <summary>
    /// The accepted <c>decision</c> filter vocabulary, lower snake because a filter is not a stored value: the
    /// three verdicts plus <c>pending</c> for a run whose newest live evaluation does not exist. Mapped to the
    /// stored (00 §7 upper case) verdict by <see cref="StoredDecision"/>; upper case is not accepted, so a filter
    /// can never be confused with the value it selects.
    /// </summary>
    internal static readonly IReadOnlyList<string> Decisions = ["pending", "pass", "fail", "inconclusive"];

    /// <summary>
    /// The single filter-to-stored mapping: <c>pending</c> selects the absence of a live verdict (<c>null</c>),
    /// every other token its 00 §7 verdict. Callers validate first, so an unknown token throws.
    /// </summary>
    internal static string? StoredDecision(string filter) => filter switch
    {
        PendingDecision => null,
        "pass" => "PASS",
        "fail" => "FAIL",
        "inconclusive" => "INCONCLUSIVE",
        _ => throw new ArgumentOutOfRangeException(
            nameof(filter), filter, "not a run-list decision filter"),
    };

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
/// permission stage's <c>403 forbidden</c>. <c>state</c>, <c>rerun_scope</c> and <c>parent_run_id</c> are <c>where</c>
/// predicates of that one statement, so the page is cut from rows that already match. <c>decision</c> is Policy data
/// and cannot join here, so that filter is applied over the same keyset order in bounded chunks — one Policy batch
/// read per chunk — and the page carries the newest live verdict Policy names for each row, null while the run has
/// none; never a per-row read and never a join across module tables. A row's <c>completeness</c> is the joined
/// <c>evidence_revision.completeness</c> of the run's current revision and stays null while the run has none (never a
/// fabricated <c>incomplete</c>); <c>parent_run_id</c> and <c>rerun_scope</c> are the run's own columns.
/// <para>
/// Order is <c>(created_at, id)</c> ascending and the cursor is the opaque <c>KeysetCursor</c> over the last row of
/// the page, so <c>(created_at, id) &gt; (cursor)</c> is stable under concurrent inserts: a row created between two
/// pages sorts after the cursor (or after the rows already read) and is neither repeated nor skipped. The cursor is
/// not signed and carries no tenant: it is bound to the caller's own rows by the query itself, because the
/// <c>org_id</c> predicate, the project scope and the <c>FORCE</c>d RLS policy are always applied — a well-formed
/// cursor can only move the page position inside the caller's own project, never read another project's or tenant's
/// rows. A <c>decision</c>-filtered scan that ends on its row budget answers the last <em>scanned</em> row as the
/// cursor rather than the last returned one, so a short page still advances past everything it read; every returned
/// row sorts before that position. A cursor this codec did not produce is <c>422 request_invalid</c>
/// (<c>cursor</c>, <c>malformed</c>) rather than a guessed page position.
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

        // The F43 vocabularies are re-checked here, before the project check, because the handler owns the contract
        // it serves: a `decision` outside `pending` plus the three verdict tokens must be a 422, never a page. The
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
        if (!firstPage && !KeysetCursor.TryDecode(query.Cursor, out cursorCreatedAt, out cursorId))
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
            hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].RunId) : null);
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
            nextCursor = KeysetCursor.Encode(page[^1].CreatedAt, page[^1].RunId);
        }
        else if (exhausted)
        {
            nextCursor = null;
        }
        else
        {
            nextCursor = KeysetCursor.Encode(lastScannedAt, lastScannedId);
        }

        return new ScannedPage(page, nextCursor);
    }

    /// <summary>
    /// One keyset chunk, forward of <paramref name="cursorCreatedAt"/>; a null cursor is the first page. The
    /// <c>decision</c> filter declares no SQL predicate, because the <c>evaluation</c> table belongs to Policy.
    /// </summary>
    private Task<IReadOnlyList<RunListRow>> ReadChunkAsync(
        ListRunsQuery query,
        Guid orgId,
        DateTimeOffset? cursorCreatedAt,
        Guid cursorId,
        int limit,
        CancellationToken cancellationToken) =>
        db.QueryAsync<RunListRow>(
            cursorCreatedAt is null ? ListRunsSql.FirstPage(query.Filters) : ListRunsSql.NextPage(query.Filters),
            ListRunsSql.Parameters(orgId, query.ProjectId, query.Filters, cursorCreatedAt, cursorId, limit),
            cancellationToken);

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
