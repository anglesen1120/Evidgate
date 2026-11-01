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
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Evidgate.Modules.Policy.Contracts;
using Mediator;

namespace Evidgate.Modules.Orchestrator.Application.Queries;

/// <summary>
/// Cursor page of one run's frozen evidence revisions (<c>GET /v1/runs/{run_id}/evidence-revisions</c>): the run's
/// <c>evidence_revision</c> rows ordered <c>revision_no</c> descending, newest first.
/// </summary>
/// <param name="RunId">The run from the route.</param>
/// <param name="Cursor">Opaque cursor of the next page, or null for the first page.</param>
/// <param name="Limit">Page size, 1–200 (00 §9.1).</param>
internal sealed record ListEvidenceRevisionsQuery(Guid RunId, string? Cursor, int Limit)
    : IQuery<Result<EvidenceRevisionPage>>;

/// <summary>
/// Handler of <see cref="ListEvidenceRevisionsQuery"/>: the run lookup and the page read over the same ambient tenant
/// scope (00 §5.5 C1/C3/C5), one parameterised keyset statement plus one extra row to decide the next page.
/// <para>
/// The run is resolved by id alone, exactly like <see cref="GetRunQuery"/> and the attempts page: RLS (forced on
/// <c>run</c>) scopes the row, so an unknown id, another tenant's run and — for a project-scoped credential — a sibling
/// project's run take the one <c>404 not_found</c> of <see cref="RunNotFound"/> (00 §5.4: no existence oracle). The
/// only 403 is the permission stage's: <c>run:read</c> is outside the service-role ceiling, so a service credential is
/// refused before this handler runs.
/// </para>
/// <para>
/// The rows are the Orchestrator's own table, so the page statement reads nothing else. Each row's
/// <c>evaluation_id</c> is a Policy fact: it comes from one batched <see cref="IPolicyContracts"/> call for the whole
/// page, never a statement per row, and only a <c>live</c> verdict is named — a <c>shadow</c> evaluation is a second
/// opinion and is never served.
/// </para>
/// <para>
/// A cursor this codec did not produce is <c>422 request_invalid</c> (<c>cursor</c>, <c>malformed</c>) rather than a
/// guessed page position; the cursor is not signed and carries no tenant, so the query's own run predicate and RLS are
/// what bind it to the caller's run. A run with no revision answers <c>200</c> with an empty page.
/// </para>
/// </summary>
[Permission(Permissions.Run.Read)]
internal sealed class ListEvidenceRevisionsQueryHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IPolicyContracts policies)
    : IQueryHandler<ListEvidenceRevisionsQuery, Result<EvidenceRevisionPage>>
{
    /// <inheritdoc />
    public async ValueTask<Result<EvidenceRevisionPage>> Handle(
        ListEvidenceRevisionsQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(ListEvidenceRevisionsQuery));

        var run = await db.QuerySingleOrDefaultAsync<RunScopeRow>(
                EvidenceRevisionListSql.RunScope,
                new { run_id = query.RunId },
                cancellationToken)
            .ConfigureAwait(false);
        if (run is null || OrchestratorScope.Project(run.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<EvidenceRevisionPage>(RunNotFound);
        }

        var firstPage = query.Cursor is null;
        var cursorRevisionNo = 0;
        if (!firstPage && !EvidenceRevisionCursor.TryDecode(query.Cursor, out cursorRevisionNo))
        {
            return Result.Failure<EvidenceRevisionPage>(Error.Invalid(
                "the cursor is not a value this endpoint issued",
                [new ErrorDetail("cursor", "malformed")]));
        }

        var rows = await db.QueryAsync<EvidenceRevisionRow>(
                firstPage ? EvidenceRevisionListSql.FirstPage : EvidenceRevisionListSql.NextPage,
                EvidenceRevisionListSql.Parameters(query.RunId, firstPage ? null : cursorRevisionNo, query.Limit + 1),
                cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        IReadOnlyList<EvidenceRevisionRow> page = hasMore ? [.. rows.Take(query.Limit)] : rows;

        var evaluations = await LiveEvaluationsAsync(query.RunId, page, cancellationToken).ConfigureAwait(false);

        return Result.Success(new EvidenceRevisionPage(
            [.. page.Select(row => new EvidenceRevision(
                row.RevisionNo,
                row.CreatedAt,
                row.Completeness,
                [.. row.Missing],
                evaluations.TryGetValue(row.RevisionNo, out var evaluationId) ? evaluationId : null))],
            hasMore ? EvidenceRevisionCursor.Encode(page[^1].RevisionNo) : null));
    }

    /// <summary>
    /// The page's live evaluation ids by revision number: ONE contract call for the whole page (00 §5.5 C5), and none
    /// for an empty page, whose answer is empty by construction.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, Guid>> LiveEvaluationsAsync(
        Guid runId,
        IReadOnlyList<EvidenceRevisionRow> page,
        CancellationToken cancellationToken)
    {
        if (page.Count == 0)
        {
            return new Dictionary<int, Guid>();
        }

        var live = await policies.GetLiveEvaluationsAsync(
                runId,
                [.. page.Select(row => row.RevisionNo)],
                cancellationToken)
            .ConfigureAwait(false);

        return live.ToDictionary(evaluation => evaluation.EvidenceRevision, evaluation => evaluation.EvaluationId);
    }

    /// <summary>
    /// 404 <c>not_found</c>: no run with that id is visible to this tenant — one body for an unknown id, another
    /// tenant's run and a sibling project's run, so the answer is no existence oracle. The body is
    /// <see cref="GetRunQueryHandler"/>'s, verbatim.
    /// </summary>
    private static Error RunNotFound => Error.NotFound("no run with that id is visible to this tenant");

    /// <summary>The run's visibility row: the project the scope rule compares (null row = no visible run).</summary>
    private sealed class RunScopeRow
    {
        public Guid ProjectId { get; set; }
    }

    /// <summary>One revision row as <see cref="EvidenceRevisionListSql"/> returns it (snake_case columns mapped by Dapper).</summary>
    private sealed class EvidenceRevisionRow
    {
        public int RevisionNo { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public string Completeness { get; set; } = string.Empty;

        public string[] Missing { get; set; } = [];
    }
}

/// <summary>
/// The opaque cursor of the evidence-revision page (00 §9.1): the <c>revision_no</c> of the page's last row, written in
/// plain decimal. That single number is the whole key because <c>revision_no</c> is unique per run. Opaque to clients —
/// nothing is promised beyond "hand it back verbatim" — and decoded to an integer so the SQL never compares client text.
/// A value that is well formed but not this codec's canonical spelling (a padded number, a sign) is refused exactly like
/// a malformed one, so a cursor can only ever be one this endpoint issued.
/// </summary>
internal static class EvidenceRevisionCursor
{
    /// <summary>Encodes one row's keyset position.</summary>
    internal static string Encode(int revisionNo) => revisionNo.ToString(CultureInfo.InvariantCulture);

    /// <summary>Decodes a cursor. False for anything this codec did not produce — the caller answers 422 rather than guessing.</summary>
    internal static bool TryDecode(string? cursor, out int revisionNo)
    {
        revisionNo = 0;

        if (string.IsNullOrEmpty(cursor)
            || !int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1
            || !string.Equals(Encode(parsed), cursor, StringComparison.Ordinal))
        {
            return false;
        }

        revisionNo = parsed;

        return true;
    }
}
