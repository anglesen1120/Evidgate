using System.Globalization;
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
/// Cursor page of one run's attempts (<c>GET /v1/runs/{r}/attempts</c>, F52/S143; 09.3.1-T4's attempts table):
/// the run's <c>case_attempt</c> rows ordered <c>(execution_key, attempt_no)</c> ascending.
/// </summary>
/// <param name="RunId">The run from the route (<c>{r}</c>).</param>
/// <param name="Cursor">Opaque cursor of the next page, or null for the first page.</param>
/// <param name="Limit">Page size, 1–200 (00 §9.1).</param>
internal sealed record ListRunAttemptsQuery(Guid RunId, string? Cursor, int Limit)
    : IQuery<Result<RunAttemptPage>>;

/// <summary>
/// Handler of <see cref="ListRunAttemptsQuery"/>: the run lookup and the page read over the same ambient tenant
/// scope (00 §5.5 C1/C3/C5), one parameterised keyset statement plus one extra row to decide the next page.
/// <para>
/// The run is resolved by id alone, exactly like <see cref="GetRunQuery"/>: RLS (forced on <c>run</c>, 0031) scopes
/// the row, so an unknown id and another tenant's run take the one <c>404 not_found</c> of
/// <see cref="RunNotFound"/>, and a project-scoped credential asking for a sibling project's run of the same org is
/// that same 404 — <see cref="OrchestratorScope.Project"/>'s scope mismatch is mapped to it below, never to
/// <c>403 project_scope_mismatch</c> (00 §5.4: no existence oracle). The only 403 is the permission stage's:
/// <c>run:read</c> is outside the service-role ceiling (<c>PermissionEvaluator.Service</c>), so a
/// <c>ci_producer</c>/service credential can never hold it and is refused <c>403 forbidden</c>.
/// </para>
/// <para>
/// Order is <c>(execution_key, attempt_no)</c> ascending — the pair the unique constraint
/// <c>case_attempt_run_execution_attempt_key</c> keys on — so one execution's attempts are adjacent and in attempt
/// order, and <c>(execution_key, attempt_no) &gt; (cursor)</c> is stable under concurrent writes: a row written
/// between two pages sorts after the cursor and is neither repeated nor skipped. The cursor is not signed and carries
/// no tenant; it is bound to the caller's own run and tenant by the query itself (the <c>run_id</c> predicate and the
/// forced RLS policy), so a well-formed cursor can only move the page position inside the caller's own run. A cursor
/// this codec did not produce — including one that is well-formed but not in this endpoint's canonical encoding — is
/// <c>422 request_invalid</c> (<c>cursor</c>, <c>malformed</c>) rather than a guessed page position. A run with no
/// attempts answers <c>200</c> with an empty page.
/// </para>
/// <para>
/// The artifact count is computed in the same statement: one grouped pass over the run's <c>artifact</c> rows in state
/// <c>ready</c> (0028), counted per <c>(execution_key, attempt_no)</c> and left-joined to the page rows — the run's
/// artifacts are read once for the whole page, never once per page row (00 §5.5 C5). <c>healing_candidate</c> is
/// <c>null</c> on every row: no candidate row exists until the 08.3.1 candidate model (shadow rows 08.3.3), so it is
/// reported null rather than fabricated.
/// </para>
/// </summary>
[Permission(Permissions.Run.Read)]
internal sealed class ListRunAttemptsQueryHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<ListRunAttemptsQuery, Result<RunAttemptPage>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RunAttemptPage>> Handle(ListRunAttemptsQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(ListRunAttemptsQuery));

        var run = await db.QuerySingleOrDefaultAsync<RunScopeRow>(
                ListRunAttemptsSql.RunScope,
                new { run_id = query.RunId },
                cancellationToken)
            .ConfigureAwait(false);
        if (run is null || OrchestratorScope.Project(run.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<RunAttemptPage>(RunNotFound);
        }

        return await ReadPageAsync(db, query.RunId, query.Cursor, query.Limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One page of a run's attempts, shared by this route and the first page of the run detail. The cursor is decoded
    /// against <paramref name="runId"/>: one minted for another run is refused like a malformed one.
    /// </summary>
    /// <param name="db">The ambient tenant scope.</param>
    /// <param name="runId">The run the caller already resolved as visible.</param>
    /// <param name="cursor">The previous page's <c>next_cursor</c>, or null for the first page.</param>
    /// <param name="limit">Page size, 1–200.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal static async ValueTask<Result<RunAttemptPage>> ReadPageAsync(
        ITenantDb db,
        Guid runId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var firstPage = cursor is null;
        string cursorExecutionKey = string.Empty;
        var cursorAttemptNo = 0;
        if (!firstPage && !RunAttemptCursor.TryDecode(cursor, runId, out cursorExecutionKey, out cursorAttemptNo))
        {
            return Result.Failure<RunAttemptPage>(Error.Invalid(
                "the cursor is not a value this endpoint issued for this run",
                [new ErrorDetail("cursor", "malformed")]));
        }

        var rows = await db.QueryAsync<RunAttemptRow>(
                firstPage ? ListRunAttemptsSql.FirstPage : ListRunAttemptsSql.NextPage,
                firstPage
                    ? ListRunAttemptsSql.Parameters(runId, null, 0, limit + 1)
                    : ListRunAttemptsSql.Parameters(runId, cursorExecutionKey, cursorAttemptNo, limit + 1),
                cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > limit;
        IReadOnlyList<RunAttemptRow> page = hasMore ? [.. rows.Take(limit)] : rows;

        return Result.Success(new RunAttemptPage(
            [.. page.Select(row => new RunAttemptSummary(
                row.AttemptId,
                row.ExecutionKey,
                row.CaseId,
                row.AttemptNo,
                row.RawStatus,
                row.NormalizedStatus,
                row.StartedAt,
                row.FinishedAt,
                row.DurationMs,
                row.ArtifactCount,
                HealingCandidate: null))],
            hasMore ? RunAttemptCursor.Encode(runId, page[^1].ExecutionKey, page[^1].AttemptNo) : null));
    }

    /// <summary>
    /// 404 <c>not_found</c>: no run with that id is visible to this tenant — the same code, message and body for an
    /// unknown id, for another tenant's run (the RLS branch above) and for a sibling project's run of the same org
    /// (the <see cref="OrchestratorScope.Project"/> mismatch, mapped here), so the answer is no existence oracle
    /// (00 §5.4). The body is <see cref="GetRunQueryHandler"/>'s, verbatim.
    /// </summary>
    private static Error RunNotFound => Error.NotFound("no run with that id is visible to this tenant");

    /// <summary>The run's visibility row: the project the scope rule compares (null row = no visible run).</summary>
    private sealed class RunScopeRow
    {
        public Guid ProjectId { get; set; }
    }

    /// <summary>One attempt row as <see cref="ListRunAttemptsSql"/> returns it (snake_case columns mapped by Dapper).</summary>
    private sealed class RunAttemptRow
    {
        public Guid AttemptId { get; set; }

        public string ExecutionKey { get; set; } = string.Empty;

        public Guid CaseId { get; set; }

        public int AttemptNo { get; set; }

        public string RawStatus { get; set; } = string.Empty;

        public string NormalizedStatus { get; set; } = string.Empty;

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? FinishedAt { get; set; }

        public int? DurationMs { get; set; }

        public int ArtifactCount { get; set; }
    }
}

/// <summary>
/// The Dapper statements of the attempts page (00 §5.5 C5): one parameterised statement per page shape, keyset-paged
/// on <c>(execution_key, attempt_no)</c> — the order the unique constraint <c>case_attempt_run_execution_attempt_key</c>
/// keys on — with the attempt's ready-artifact count as one grouped aggregate over the run's artifacts in the same
/// statement, never a per-row round trip. <c>org_id</c> is left to RLS (forced on <c>case_attempt</c> and
/// <c>artifact</c>), so the run predicate is the only extra filter and an invisible run's rows are unreachable. The
/// members are <c>internal</c> as this module's statement surface: nothing outside this assembly binds them, and
/// <c>tests/Tenancy.IntegrationTests</c> pins their plans with <c>EXPLAIN (FORMAT JSON)</c> — the page's keyset
/// index, the ready-artifact slot index and the counts index — so a change that loses one fails there.
/// </summary>
internal static class ListRunAttemptsSql
{
    private const string Columns = """
        a.id as attempt_id,
               a.execution_key,
               a.case_id,
               a.attempt_no,
               a.raw_status,
               a.normalized_status,
               a.started_at,
               a.finished_at,
               a.duration_ms,
               coalesce(art.artifact_count, 0) as artifact_count
        """;

    /// <summary>
    /// The one aggregate of the page: the run's artifacts in state <c>ready</c> counted per
    /// <c>(execution_key, attempt_no)</c> — the slot the artifact's own key carries (0028/0032) — in a single grouped
    /// pass, restricted to the page's own slots (the <c>page</c> CTE of the statements below) so the grouping never
    /// covers the rest of the run, and left-joined to the page rows on that same pair. One pass serves the whole page
    /// instead of one aggregate per page row, the group by makes at most one row per slot so the join never
    /// multiplies a page row, and <see cref="Columns"/>'s <c>coalesce</c> keeps an attempt with no ready artifact at
    /// 0 rather than null. <c>org_id</c> is left to RLS exactly as in the page statements.
    /// </summary>
    private const string ReadyArtifacts = """
        left join (
            select ar.execution_key,
                   ar.attempt_no,
                   count(*)::int as artifact_count
              from public.artifact ar
             where ar.run_id = @run_id
               and ar.state = 'ready'
               and (ar.execution_key, ar.attempt_no) in (select p.execution_key, p.attempt_no from page p)
             group by ar.execution_key,
                      ar.attempt_no
        ) art
               on art.execution_key = a.execution_key
              and art.attempt_no = a.attempt_no
        """;

    /// <summary>The run's visibility row, for the existence check (unknown, other tenant → no row → 404).</summary>
    internal const string RunScope = "select r.project_id from public.run r where r.id = @run_id";

    /// <summary>First page of a run's attempts, in <c>(execution_key, attempt_no)</c> ascending order.</summary>
    internal const string FirstPage = $"""
        with page as (
            select a.*
              from public.case_attempt a
             where a.run_id = @run_id
             order by a.execution_key,
                      a.attempt_no
             limit @limit
        )
        select {Columns}
          from page a
          {ReadyArtifacts}
         order by a.execution_key,
                  a.attempt_no
        """;

    /// <summary>The next page: the same projection and order with the row-comparison cursor (00 §9.1).</summary>
    internal const string NextPage = $"""
        with page as (
            select a.*
              from public.case_attempt a
             where a.run_id = @run_id
               and (a.execution_key, a.attempt_no) > (@cursor_execution_key::text, @cursor_attempt_no::int)
             order by a.execution_key,
                      a.attempt_no
             limit @limit
        )
        select {Columns}
          from page a
          {ReadyArtifacts}
         order by a.execution_key,
                  a.attempt_no
        """;

    /// <summary>
    /// The parameters a page statement declares: the run, the limit and — for a next page — the decoded cursor pair,
    /// never a client string. <c>cursorExecutionKey</c> is null for the first page, whose statement has no cursor
    /// parameters at all (the extended query protocol refuses a bind that supplies a parameter the statement omits).
    /// </summary>
    internal static object Parameters(Guid runId, string? cursorExecutionKey, int cursorAttemptNo, int limit) =>
        cursorExecutionKey is null
            ? new { run_id = runId, limit }
            : new { run_id = runId, cursor_execution_key = cursorExecutionKey, cursor_attempt_no = cursorAttemptNo, limit };
}

/// <summary>
/// The opaque cursor of the attempts page (00 §9.1): the run it was minted for and the keyset pair
/// <c>(execution_key, attempt_no)</c> of the page's last row, encoded as
/// <c>&lt;run uuid&gt;-&lt;64 hex execution key&gt;-&lt;attempt_no&gt;</c>. Opaque to clients, and decoded to typed
/// values so the SQL never compares a client string against a key. It is not signed: binding it to the run it names
/// makes a cursor taken from another run refused, and the query's run predicate plus forced RLS keep any well-formed
/// cursor inside the caller's own tenant. A value that is well formed but not this codec's canonical spelling (an
/// uppercase key, a padded attempt number) is refused exactly like a malformed one.
/// </summary>
internal static class RunAttemptCursor
{
    /// <summary>The length of the canonical run uuid half (<c>D</c> format).</summary>
    private const int RunIdLength = 36;

    /// <summary>The length of the <c>execution_key</c> half: the 00 §7 sha256 hex.</summary>
    private const int KeyLength = 64;

    /// <summary>Encodes one row's keyset position for <paramref name="runId"/>.</summary>
    internal static string Encode(Guid runId, string executionKey, int attemptNo) =>
        string.Create(CultureInfo.InvariantCulture, $"{runId:D}-{executionKey}-{attemptNo}");

    /// <summary>
    /// Decodes a cursor minted for <paramref name="runId"/>. False for anything this codec did not produce, or that
    /// names another run — the caller answers 422 rather than guessing.
    /// </summary>
    internal static bool TryDecode(string? cursor, Guid runId, out string executionKey, out int attemptNo)
    {
        executionKey = string.Empty;
        attemptNo = 0;

        const int keyStart = RunIdLength + 1;
        const int numberStart = keyStart + KeyLength + 1;
        if (string.IsNullOrEmpty(cursor)
            || cursor.Length <= numberStart
            || cursor[RunIdLength] != '-'
            || cursor[numberStart - 1] != '-'
            || !Guid.TryParseExact(cursor.AsSpan(0, RunIdLength), "D", out var cursorRun)
            || cursorRun != runId)
        {
            return false;
        }

        var key = cursor.AsSpan(keyStart, KeyLength);
        foreach (var character in key)
        {
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }

        if (!int.TryParse(
                cursor.AsSpan(numberStart),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed)
            || parsed < 1)
        {
            return false;
        }

        var decoded = key.ToString();
        if (!string.Equals(Encode(runId, decoded, parsed), cursor, StringComparison.Ordinal))
        {
            return false;
        }

        executionKey = decoded;
        attemptNo = parsed;

        return true;
    }
}
