using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Application.Queries;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Domain.Changes;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Authoring.Application.Changes;

/// <summary>
/// One page of a project's authoring changes, or one selection of them. A selection read (<c>ids</c>) carries the
/// batch ETag and a per-change <c>stale</c> flag; a filtered page carries neither, because it resolves no affected set.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="State">An optional state filter.</param>
/// <param name="ChangeType">An optional change-type filter.</param>
/// <param name="TargetId">An optional target filter.</param>
/// <param name="Ids">An optional selection of 1..200 change ids; no other filter may accompany it.</param>
/// <param name="Cursor">The opaque cursor of the next page, or null for the first.</param>
/// <param name="Limit">The page size the route bounded.</param>
internal sealed record ListAuthoringChangesQuery(
    Guid ProjectId,
    string? State,
    string? ChangeType,
    Guid? TargetId,
    IReadOnlyList<Guid>? Ids,
    string? Cursor,
    int Limit) : IQuery<Result<AuthoringChangePage>>;

/// <summary>
/// Handler of <see cref="ListAuthoringChangesQuery"/>: one keyset statement on the shared-database read scope. The
/// order is <c>(created_at, id)</c>, so the cursor is one searchable row comparison and no row is skipped or repeated
/// at a page boundary; the selection shape runs one extra statement over the union of the selected changes' affected
/// items to compute the batch ETag and each change's <c>stale</c> flag.
/// </summary>
/// <param name="db">The tenant-scoped data access.</param>
/// <param name="scopes">The request's tenant scope.</param>
/// <param name="principals">The request principal.</param>
/// <param name="tenant">The request tenant context.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class ListAuthoringChangesQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant) : IQueryHandler<ListAuthoringChangesQuery, Result<AuthoringChangePage>>
{
    /// <summary>The closed state set the table's CHECK mirrors.</summary>
    private static readonly string[] States = ["pending", "applied", "discarded"];

    /// <summary>The first page's sentinel pair: smaller than any stored <c>clock_timestamp()</c>, so the seek is one row comparison.</summary>
    private static readonly DateTimeOffset FirstPageCreatedAt = DateTimeOffset.UnixEpoch;

    /// <summary>The first page's sentinel id, the smallest uuid in the column's ordering.</summary>
    private static readonly Guid FirstPageId = Guid.Empty;

    /// <inheritdoc />
    public async ValueTask<Result<AuthoringChangePage>> Handle(
        ListAuthoringChangesQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ListAuthoringChangesQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AuthoringChangePage>(scopeError);
        }

        if (Filters(query) is { } filterError)
        {
            return Result.Failure<AuthoringChangePage>(filterError);
        }

        var cursorCreatedAt = FirstPageCreatedAt;
        var cursorId = FirstPageId;

        if (query.Cursor is { Length: > 0 } rawCursor
            && !AuthoringVersionCursor.TryDecode(rawCursor, out cursorCreatedAt, out cursorId))
        {
            return Result.Failure<AuthoringChangePage>(Error.Invalid(
                "the cursor is not a value this endpoint issued",
                [new ErrorDetail("/cursor", "malformed")]));
        }

        var orgId = scopes.Require().OrgId;

        if (await db.ScalarAsync<int?>(
                AuthoringVersionReadSql.ProjectExists,
                new { org_id = orgId, project_id = query.ProjectId },
                cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<AuthoringChangePage>(AuthoringRules.ProjectNotFound);
        }

        return query.Ids is { } ids
            ? await SelectionAsync(db, orgId, query, ids, cancellationToken).ConfigureAwait(false)
            : await PageAsync(db, orgId, query, cursorCreatedAt, cursorId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The query-shape rules: known filter values, a bounded page, a bounded selection and no filter beside <c>ids</c>.</summary>
    private static Error? Filters(ListAuthoringChangesQuery query)
    {
        if (query.State is { } state && !States.Contains(state, StringComparer.Ordinal))
        {
            return Error.Invalid("the state filter is not one of pending, applied or discarded", [new ErrorDetail("/state", "invalid")]);
        }

        if (query.ChangeType is { } changeType && !AuthoringChangeTypes.IsValid(changeType))
        {
            return Error.Invalid("the change_type filter is not a known change type", [new ErrorDetail("/change_type", "invalid")]);
        }

        if (query.Limit is < 1 or > AuthoringChangeLimits.PageMax)
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"limit must be 1..{AuthoringChangeLimits.PageMax}"),
                [new ErrorDetail("/limit", "invalid")]);
        }

        if (query.Ids is not { } ids)
        {
            return null;
        }

        if (query.State is not null || query.ChangeType is not null || query.TargetId is not null)
        {
            return AuthoringChangeErrors.IdsWithFilters();
        }

        return ids.Count is < 1 or > AuthoringChangeLimits.SelectionMax
            ? Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"ids must name 1..{AuthoringChangeLimits.SelectionMax} changes"),
                [new ErrorDetail("/ids", "invalid")])
            : null;
    }

    /// <summary>The filtered keyset page: one statement, one row of look-ahead to tell a full page from the last.</summary>
    private static async ValueTask<Result<AuthoringChangePage>> PageAsync(
        ITenantDb db,
        Guid orgId,
        ListAuthoringChangesQuery query,
        DateTimeOffset cursorCreatedAt,
        Guid cursorId,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<AuthoringChangeSql.ChangeRow>(
            query.TargetId is null ? AuthoringChangeSql.Page : AuthoringChangeSql.PageByTarget,
            new
            {
                org_id = orgId,
                project_id = query.ProjectId,
                target_id = query.TargetId,
                state = query.State,
                change_type = query.ChangeType,
                cursor_created_at = cursorCreatedAt,
                cursor_id = cursorId,
                take = query.Limit + 1,
            },
            cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        var size = hasMore ? query.Limit : rows.Count;
        var items = new List<AuthoringChangeListItem>(size);

        for (var index = 0; index < size; index++)
        {
            items.Add(AuthoringChangeJson.Item(rows[index], stale: null));
        }

        return Result.Success(new AuthoringChangePage(
            items,
            hasMore ? AuthoringVersionCursor.Encode(rows[size - 1].CreatedAt, rows[size - 1].ChangeId) : null,
            Etag: null));
    }

    /// <summary>
    /// The selection read: the selected changes in queue order, the current versions of every affected item they
    /// name, and the two figures only this shape can answer — the batch ETag over those versions and each change's
    /// <c>stale</c> flag.
    /// </summary>
    private static async ValueTask<Result<AuthoringChangePage>> SelectionAsync(
        ITenantDb db,
        Guid orgId,
        ListAuthoringChangesQuery query,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<AuthoringChangeSql.ChangeRow>(
            AuthoringChangeSql.Selection,
            new { org_id = orgId, project_id = query.ProjectId, ids = ids.ToArray() },
            cancellationToken).ConfigureAwait(false);

        var stamped = rows.ToDictionary(row => row.ChangeId, row => AuthoringChangeJson.ParseVersions(row.AffectedVersions));
        var affectedIds = stamped.Values.SelectMany(map => map.Keys).Distinct().ToArray();

        var currentRows = await db.QueryAsync<AuthoringChangeSql.ItemVersion>(
            AuthoringChangeSql.ItemVersions,
            new { org_id = orgId, project_id = query.ProjectId, item_ids = affectedIds },
            cancellationToken).ConfigureAwait(false);

        var current = currentRows.ToDictionary(row => row.Id, row => row.Version);
        var items = new List<AuthoringChangeListItem>(rows.Count);

        foreach (var row in rows)
        {
            var moved = stamped[row.ChangeId].Any(pair =>
                !current.TryGetValue(pair.Key, out var version) || version != pair.Value);

            items.Add(AuthoringChangeJson.Item(row, moved));
        }

        return Result.Success(new AuthoringChangePage(
            items,
            NextCursor: null,
            AuthoringHashes.OrderHash(current.Select(pair => (pair.Key, pair.Value)))));
    }
}

/// <summary>One change by id (<c>GET /v1/projects/{p}/authoring/changes/{c}</c>); a foreign id is the same 404 as a random one.</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="ChangeId">The change from the route.</param>
internal sealed record GetAuthoringChangeQuery(Guid ProjectId, Guid ChangeId) : IQuery<Result<AuthoringChangeView>>;

/// <summary>Handler of <see cref="GetAuthoringChangeQuery"/>: one row of this project, with the payload echoed and the row's own version as the <c>ETag</c>.</summary>
/// <param name="db">The tenant-scoped data access.</param>
/// <param name="scopes">The request's tenant scope.</param>
/// <param name="principals">The request principal.</param>
/// <param name="tenant">The request tenant context.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class GetAuthoringChangeQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant) : IQueryHandler<GetAuthoringChangeQuery, Result<AuthoringChangeView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AuthoringChangeView>> Handle(
        GetAuthoringChangeQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(GetAuthoringChangeQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AuthoringChangeView>(scopeError);
        }

        var row = await db.QuerySingleOrDefaultAsync<AuthoringChangeSql.ChangeRow>(
            AuthoringChangeSql.Get,
            new { org_id = scopes.Require().OrgId, project_id = query.ProjectId, change_id = query.ChangeId },
            cancellationToken).ConfigureAwait(false);

        return row is null
            ? Result.Failure<AuthoringChangeView>(AuthoringErrors.NotFound("no pending change with that id is visible in this project"))
            : Result.Success(AuthoringChangeJson.View(row));
    }
}
