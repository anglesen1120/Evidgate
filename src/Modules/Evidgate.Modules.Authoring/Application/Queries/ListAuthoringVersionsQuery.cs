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
/// The project-scope gate precedes carried binding errors so malformed filters cannot mask a scope refusal.
/// Successful reads use two statements in a read-only transaction: a project-liveness probe and one keyset page.
/// Unsupported filters are refused before either statement runs.
/// </summary>
/// <remarks>
/// The statement is one of three complete constants chosen by the filters (<see cref="AuthoringVersionReadSql"/>),
/// never text assembled from the request.
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
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ListAuthoringVersionsQuery));
        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AuthoringVersionListResponse>(scopeError);
        }

        if (query.RequestError is { } requestError)
        {
            return Result.Failure<AuthoringVersionListResponse>(requestError);
        }

        var orgId = scopes.Require().OrgId;

        if (ReadEntityFilters(query, out var entityKind, out var source) is { } filterError)
        {
            return Result.Failure<AuthoringVersionListResponse>(filterError);
        }

        if (ReadTimeRange(query, out var from, out var to) is { } rangeError)
        {
            return Result.Failure<AuthoringVersionListResponse>(rangeError);
        }

        return await ReadPageAsync(db, orgId, query, entityKind, source, from, to, cancellationToken).ConfigureAwait(false);
    }

    private static Error? ReadEntityFilters(
        ListAuthoringVersionsQuery query,
        out string? entityKind,
        out string? source)
    {
        source = null;
        entityKind = AuthoringEndpointText.Blank(query.EntityKind);

        if (entityKind is not null && !AuthoringVersionFilters.EntityKinds.Contains(entityKind, StringComparer.Ordinal))
        {
            return Error.Invalid(
                "entity_kind is feature_node, checklist_item or manual_test_case (13.1.1 §5 route 22)",
                [new ErrorDetail("/entity_kind", "value_invalid")]);
        }

        // Without entity_kind, the entity-key index cannot serve entity_id and the query would scan the project timeline.
        // The pair is also the only shape the statements serve.
        if (query.EntityId is not null && entityKind is null)
        {
            return Error.Invalid(
                "entity_id requires entity_kind (13.1.1 §5 route 22)",
                [new ErrorDetail("/entity_kind", "required_with_entity_id")]);
        }

        source = AuthoringEndpointText.Blank(query.Source);

        if (source is not null && !AuthoringVersionFilters.Sources.Contains(source, StringComparer.Ordinal))
        {
            return Error.Invalid(
                "source is manual, ai or import (13.1.1 §5 route 22)",
                [new ErrorDetail("/source", "value_invalid")]);
        }

        return null;
    }

    private static Error? ReadTimeRange(
        ListAuthoringVersionsQuery query,
        out DateTimeOffset? from,
        out DateTimeOffset? to)
    {
        to = null;

        if (AuthoringVersionInstant.Read(AuthoringEndpointText.Blank(query.From), "/from", out from) is { } fromError)
        {
            return fromError;
        }

        if (AuthoringVersionInstant.Read(AuthoringEndpointText.Blank(query.To), "/to", out to) is { } toError)
        {
            return toError;
        }

        if (from is { } lower && to is { } upper && lower > upper)
        {
            // Half-open on purpose (from inclusive, to exclusive), so an empty window is a caller error rather than a
            // silently empty page a client would read as "no history".
            return Error.Invalid(
                "from must not be later than to (13.1.1 §5 route 22)",
                [new ErrorDetail("/from", "invalid_range")]);
        }

        return null;
    }

    private static async ValueTask<Result<AuthoringVersionListResponse>> ReadPageAsync(
        ITenantDb db,
        Guid orgId,
        ListAuthoringVersionsQuery query,
        string? entityKind,
        string? source,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
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

        return Result.Success(CreateResponse(rows, query.Limit));
    }

    private static AuthoringVersionListResponse CreateResponse(IReadOnlyList<AuthoringVersionRow> rows, int limit)
    {
        // `limit + 1` rows tell a last page from a full one: the extra row is read and dropped, never returned.
        var hasMore = rows.Count > limit;
        var size = hasMore ? limit : rows.Count;
        var items = new List<AuthoringVersionListItemDto>(size);

        for (var index = 0; index < size; index++)
        {
            items.Add(rows[index].ToListItem());
        }

        return new AuthoringVersionListResponse(
            items,
            hasMore ? AuthoringVersionCursor.Encode(rows[size - 1].CreatedAt, rows[size - 1].VersionId) : null);
    }
}
