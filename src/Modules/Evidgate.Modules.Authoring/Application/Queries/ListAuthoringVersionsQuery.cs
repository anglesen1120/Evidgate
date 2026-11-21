using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Authoring.Application.Queries;

/// <summary>
/// One keyset page of a project's version timeline (13.1.1 §5 route 22, AC9): newest first, every row carrying the
/// <c>batch_id</c> one command wrote, and never a <c>content</c>.
/// </summary>
/// <remarks>
/// The <c>actor</c> member of the §5 table is spelled <see cref="ActorMembershipId"/> here while AC9 calls the filter
/// <c>actor_membership_id</c>; the route binds §5's spelling and the record member is not on the wire, so only the query
/// parameter name is a contract. Recorded as a [DECISION] with the slice's hand-over.
/// </remarks>
/// <param name="ProjectId">The project from the route; another tenant's project is a 404 (AC10).</param>
/// <param name="EntityKind">Optional filter: <c>feature_node</c>, <c>checklist_item</c> or <c>manual_test_case</c>.</param>
/// <param name="EntityId">Optional filter: one entity's history. It requires <paramref name="EntityKind"/> — sent alone it
/// is a 422 on <c>/entity_kind</c>, because the entity key index starts with the kind and the id would otherwise be a
/// residual filter over the whole project timeline.</param>
/// <param name="Source">Optional filter: <c>manual</c>, <c>ai</c> or <c>import</c>.</param>
/// <param name="ActorMembershipId">Optional filter: the authoring membership that wrote the change.</param>
/// <param name="From">Optional inclusive lower bound, an RFC 3339 instant.</param>
/// <param name="To">Optional <b>exclusive</b> upper bound, an RFC 3339 instant.</param>
/// <param name="Cursor">The opaque <c>(created_at, id)</c> cursor, or null for the first page.</param>
/// <param name="Limit">The page size the route already bounded (1..200).</param>
/// <param name="RequestError">The first query-binding error, answered after authorization.</param>
internal sealed record ListAuthoringVersionsQuery(
    Guid ProjectId,
    string? EntityKind,
    Guid? EntityId,
    string? Source,
    Guid? ActorMembershipId,
    string? From,
    string? To,
    string? Cursor,
    int Limit,
    Error? RequestError = null) : IQuery<Result<AuthoringVersionListResponse>>;

/// <summary>
/// Handler of <see cref="ListAuthoringVersionsQuery"/>: a refusal the edge carried on the query, else the project gate and
/// <b>two</b> statements in a <b>read-only</b> transaction (the project-liveness probe and one keyset page) whatever the
/// page size, and a 422 per unsupported filter value — including the combination the statements cannot serve,
/// <c>entity_id</c> without <c>entity_kind</c>.
/// </summary>
/// <remarks>
/// The filters are answered here, inside the handler, and the page size and the two uuids are bound at the route edge —
/// the split D-S55-Q3 records for the authoring surface (the delegate detects every <em>shape</em> refusal and carries
/// it here, and the handler answers it along with every <em>value</em> refusal). The statement is one of three complete
/// constants chosen by which filters are present (<see cref="AuthoringVersionReadSql"/>), never text assembled from the
/// request.
/// </remarks>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class ListAuthoringVersionsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant) : IQueryHandler<ListAuthoringVersionsQuery, Result<AuthoringVersionListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AuthoringVersionListResponse>> Handle(
        ListAuthoringVersionsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.RequestError is { } requestError)
        {
            return Result.Failure<AuthoringVersionListResponse>(requestError);
        }

        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ListAuthoringVersionsQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AuthoringVersionListResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        var entityKind = AuthoringEndpointText.Blank(query.EntityKind);

        if (entityKind is not null && !AuthoringVersionFilters.EntityKinds.Contains(entityKind, StringComparer.Ordinal))
        {
            return Result.Failure<AuthoringVersionListResponse>(Error.Invalid(
                "entity_kind is feature_node, checklist_item or manual_test_case (13.1.1 §5 route 22)",
                [new ErrorDetail("/entity_kind", "value_invalid")]));
        }

        // `entity_id` alone is refused: without `entity_kind` the entity key index of AC9
        // (`org_id, project_id, entity_kind, entity_id, version_no`) cannot serve the filter — `entity_id` would be a
        // residual predicate — so an unknown or another project's id would scan the whole project timeline. The pair is
        // also the only shape the statements serve (<see cref="AuthoringVersionStatement.Pick"/>).
        if (query.EntityId is not null && entityKind is null)
        {
            return Result.Failure<AuthoringVersionListResponse>(Error.Invalid(
                "entity_id requires entity_kind (13.1.1 §5 route 22)",
                [new ErrorDetail("/entity_kind", "required_with_entity_id")]));
        }

        var source = AuthoringEndpointText.Blank(query.Source);

        if (source is not null && !AuthoringVersionFilters.Sources.Contains(source, StringComparer.Ordinal))
        {
            return Result.Failure<AuthoringVersionListResponse>(Error.Invalid(
                "source is manual, ai or import (13.1.1 §5 route 22)",
                [new ErrorDetail("/source", "value_invalid")]));
        }

        if (AuthoringVersionInstant.Read(AuthoringEndpointText.Blank(query.From), "/from", out var from) is { } fromError)
        {
            return Result.Failure<AuthoringVersionListResponse>(fromError);
        }

        if (AuthoringVersionInstant.Read(AuthoringEndpointText.Blank(query.To), "/to", out var to) is { } toError)
        {
            return Result.Failure<AuthoringVersionListResponse>(toError);
        }

        if (from is { } lower && to is { } upper && lower > upper)
        {
            // Half-open on purpose (from inclusive, to exclusive), so an empty window is a caller error rather than a
            // silently empty page a client would read as "no history".
            return Result.Failure<AuthoringVersionListResponse>(Error.Invalid(
                "from must not be later than to (13.1.1 §5 route 22)",
                [new ErrorDetail("/from", "invalid_range")]));
        }

        var cursorCreatedAt = AuthoringVersionCursor.FirstPageTimestamp;
        var cursorId = AuthoringVersionCursor.FirstPageId;

        if (AuthoringEndpointText.Blank(query.Cursor) is { } rawCursor)
        {
            if (!AuthoringVersionCursor.TryDecode(rawCursor, out cursorCreatedAt, out cursorId))
            {
                return Result.Failure<AuthoringVersionListResponse>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("/cursor", "malformed")]));
            }
        }

        if (await db.ScalarAsync<int?>(AuthoringVersionReadSql.ProjectExists, new { org_id = orgId, project_id = query.ProjectId }, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            return Result.Failure<AuthoringVersionListResponse>(AuthoringRules.ProjectNotFound);
        }

        var sql = AuthoringVersionStatement.Pick(entityKind, query.EntityId);

        var rows = await db.QueryAsync<AuthoringVersionRow>(
            sql,
            new AuthoringVersionPageParameters(
                org_id: orgId,
                project_id: query.ProjectId,
                cursor_created_at: cursorCreatedAt,
                cursor_id: cursorId,
                entity_kind: entityKind,
                entity_id: query.EntityId,
                source: source,
                actor: query.ActorMembershipId,
                from_at: from,
                to_at: to,
                take: query.Limit + 1),
            cancellationToken).ConfigureAwait(false);

        // `limit + 1` rows tell a last page from a full one: the extra row is read and dropped, never returned.
        var hasMore = rows.Count > query.Limit;
        var size = hasMore ? query.Limit : rows.Count;
        var items = new List<AuthoringVersionListItemDto>(size);

        for (var index = 0; index < size; index++)
        {
            items.Add(rows[index].ToListItem());
        }

        return Result.Success(new AuthoringVersionListResponse(
            items,
            hasMore ? AuthoringVersionCursor.Encode(rows[size - 1].CreatedAt, rows[size - 1].VersionId) : null));
    }
}
