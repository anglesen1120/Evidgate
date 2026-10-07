using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Contracts;
using Mediator;

namespace Evidgate.Modules.Policy.Application.Queries;

/// <summary>Cursor page of a project's policy revisions (<c>GET /v1/policies/{id}/revisions</c>, 07.1.1 §5).</summary>
/// <param name="RevisionId">Any revision of the project whose list is wanted (the <c>{id}</c> of the route).</param>
/// <param name="Cursor">Opaque cursor, or null for the first page.</param>
/// <param name="Limit">Page size, 1–200.</param>
/// <param name="IfMatch">The caller's cached head revision id, or null for no precondition (00 §9.1, AC5).</param>
internal sealed record ListPolicyRevisionsQuery(Guid RevisionId, string? Cursor, int Limit, string? IfMatch)
    : IQuery<Result<PolicyRevisionPage>>;

/// <summary>
/// One page of a project's policy revisions and the project's current head (the <c>ETag</c> the endpoint sets).
/// The head travels beside the wire response because it is a header, not a body member.
/// </summary>
/// <param name="Response">The page's wire body.</param>
/// <param name="HeadId">The project's head revision id (the highest <c>revision_no</c>).</param>
internal sealed record PolicyRevisionPage(PolicyRevisionListResponse Response, Guid? HeadId);

/// <summary>
/// Handler of <see cref="ListPolicyRevisionsQuery"/>: the anchor revision resolves its project (another tenant's,
/// another project's or an unknown id is the same 404), then one keyset statement over
/// <c>(revision_no, id)</c> ascending — the append-only order — with one extra row deciding the next page.
/// </summary>
[Permission(Permissions.Policy.Read)]
internal sealed class ListPolicyRevisionsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<ListPolicyRevisionsQuery, Result<PolicyRevisionPage>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PolicyRevisionPage>> Handle(ListPolicyRevisionsQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(ListPolicyRevisionsQuery));

        var anchor = await db.QuerySingleOrDefaultAsync<AnchorRow>(
            PolicyRevisionListSql.Anchor,
            new { revision_id = query.RevisionId },
            cancellationToken).ConfigureAwait(false);

        // A credential scoped to project A learns nothing about project B's revisions: the same 404 as an unknown id.
        if (anchor is null || PolicyScope.Project(anchor.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<PolicyRevisionPage>(PolicyErrors.RevisionNotFound);
        }

        if (!string.IsNullOrEmpty(query.IfMatch)
            && (!PolicyIfMatch.TryParse(query.IfMatch, out var expected) || expected != anchor.HeadId))
        {
            return Result.Failure<PolicyRevisionPage>(PolicyErrors.Stale(anchor.HeadId));
        }

        var orgId = scopes.Require().OrgId;
        IReadOnlyList<PolicyRevisionSummaryDto> rows;
        if (query.Cursor is null)
        {
            rows = await db.QueryAsync<PolicyRevisionSummaryDto>(
                PolicyRevisionListSql.FirstPage,
                new { org_id = orgId, project_id = anchor.ProjectId, limit = query.Limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!PolicyRevisionCursor.TryDecode(query.Cursor, out var revisionNo, out var id))
            {
                return Result.Failure<PolicyRevisionPage>(PolicyErrors.CursorInvalid);
            }

            rows = await db.QueryAsync<PolicyRevisionSummaryDto>(
                PolicyRevisionListSql.NextPage,
                new { org_id = orgId, project_id = anchor.ProjectId, cursor_revision_no = revisionNo, cursor_id = id, limit = query.Limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }

        var hasMore = rows.Count > query.Limit;
        IReadOnlyList<PolicyRevisionSummaryDto> page = hasMore ? [.. rows.Take(query.Limit)] : rows;

        return Result.Success(new PolicyRevisionPage(
            new PolicyRevisionListResponse(page, hasMore ? PolicyRevisionCursor.Encode(page[^1].RevisionNo, page[^1].Id) : null),
            anchor.HeadId));
    }

    private sealed class AnchorRow
    {
        public Guid ProjectId { get; set; }

        public Guid? HeadId { get; set; }
    }
}

/// <summary>The list statements of the policy-revision query; every one runs in the request's read-only tenant transaction.</summary>
internal static class PolicyRevisionListSql
{
    /// <summary>
    /// The anchor revision's project and that project's head, in one round trip. RLS scopes both to the request's
    /// tenant, so another tenant's id resolves no row; the project join (finding S3, the
    /// <c>OrchestratorSql.RevisionById</c> precedent) hides a revision whose project is soft-deleted, so it is the
    /// same 404 as an unknown id.
    /// </summary>
    internal const string Anchor = """
        select r.project_id,
               (select h.id
                  from public.policy_revision h
                 where h.org_id = r.org_id and h.project_id = r.project_id
                 order by h.revision_no desc
                 limit 1) as head_id
          from public.policy_revision r
          join public.project p on p.org_id = r.org_id and p.id = r.project_id and p.status <> 'deleted'
         where r.id = @revision_id
        """;

    /// <summary>The first page: the unique btree (org_id, project_id, revision_no) serves the order.</summary>
    internal const string FirstPage = """
        select id, revision_no, hash, state, created_by, created_at, version
          from public.policy_revision
         where org_id = @org_id and project_id = @project_id
         order by revision_no,
                  id
         limit @limit
        """;

    /// <summary>The next page: the same order, resumed after the cursor's keyset pair.</summary>
    internal const string NextPage = """
        select id, revision_no, hash, state, created_by, created_at, version
          from public.policy_revision
         where org_id = @org_id and project_id = @project_id
           and (revision_no, id) > (@cursor_revision_no::integer, @cursor_id::uuid)
         order by revision_no,
                  id
         limit @limit
        """;
}
