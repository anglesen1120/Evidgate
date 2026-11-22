using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Authoring.Application.Queries;

/// <summary>
/// Keyset page of a project's checklist items (<c>GET /v1/projects/{p}/authoring/checklist-items</c>, 13.1.1 §5
/// route 7), ordered <c>(position, id)</c> (AC2).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="NodeId">Optional node filter: the items of one category or sub-category.</param>
/// <param name="State">Optional §6 state filter.</param>
/// <param name="Priority">Optional priority filter.</param>
/// <param name="Cursor">The opaque cursor of the previous page, or null.</param>
/// <param name="Limit">Page size, 1..200 (validated at the edge).</param>
/// <param name="RequestError">The first query-binding error, answered after authorization.</param>
internal sealed record ListChecklistItemsQuery(
    Guid ProjectId,
    Guid? NodeId,
    string? State,
    string? Priority,
    string? Cursor,
    int Limit,
    Error? RequestError = null)
    : IQuery<Result<ChecklistItemListResponse>>;

/// <summary>
/// Scope authorization precedes carried query errors. An unknown project is a 404 rather than an empty page.
/// Fetching one row beyond the requested page detects the next page without a count query.
/// </summary>
[Permission(Permissions.Authoring.Read)]
internal sealed class ListChecklistItemsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<ListChecklistItemsQuery, Result<ChecklistItemListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<ChecklistItemListResponse>> Handle(
        ListChecklistItemsQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ListChecklistItemsQuery));

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<ChecklistItemListResponse>(scopeError);
        }

        if (query.RequestError is { } requestError)
        {
            return Result.Failure<ChecklistItemListResponse>(requestError);
        }

        var orgId = scopes.Require().OrgId;

        // An empty page would reveal that an otherwise invisible project exists.
        if (await db.ScalarAsync<int?>(
                AuthoringNodeReadSql.ProjectExists,
                new { org_id = orgId, project_id = query.ProjectId },
                cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<ChecklistItemListResponse>(AuthoringRules.ProjectNotFound);
        }

        if (ValidateFiltersAndCursor(query, out var afterPosition, out var afterId) is { } filterError)
        {
            return Result.Failure<ChecklistItemListResponse>(filterError);
        }

        var rows = await db.QueryAsync<ChecklistItemDto>(
            AuthoringChecklistReadSql.List,
            new
            {
                org_id = orgId,
                project_id = query.ProjectId,
                node_id = query.NodeId,
                state = query.State,
                priority = query.Priority,
                after_position = afterPosition,
                after_id = afterId,
                take = query.Limit + 1,
            },
            cancellationToken).ConfigureAwait(false);

        var hasNext = rows.Count > query.Limit;
        IReadOnlyList<ChecklistItemDto> items = hasNext ? [.. rows.Take(query.Limit)] : rows;

        return Result.Success(new ChecklistItemListResponse(
            items,
            hasNext && items.Count > 0 ? EncodeCursor(items[^1]) : null));
    }

    /// <summary>Refuses a filter value or cursor outside the route's vocabulary, answering the first offender.</summary>
    /// <returns>The refusal, or <c>null</c> with the decoded cursor halves set.</returns>
    private static Error? ValidateFiltersAndCursor(
        ListChecklistItemsQuery query,
        out int? afterPosition,
        out Guid? afterId)
    {
        afterPosition = null;
        afterId = null;

        if (query.State is { } state && !ChecklistItemStateMachine.States.Contains(state, StringComparer.Ordinal))
        {
            return AuthoringErrors.Invalid("/state", "value_invalid", "state must be one of the checklist item states");
        }

        if (query.Priority is { } priority && !AuthoringPriorities.IsValid(priority))
        {
            return AuthoringErrors.Invalid("/priority", "value_invalid", "priority must be one of the four priorities");
        }

        if (query.Cursor is { } cursor)
        {
            if (!TryDecodeCursor(cursor, out var position, out var itemId))
            {
                return AuthoringErrors.Invalid("/cursor", "invalid", "the cursor was not issued by this route");
            }

            afterPosition = position;
            afterId = itemId;
        }

        return null;
    }

    /// <summary>The cursor of one page position: the row's <c>(position, id)</c> pair, the order's own key (AC2).</summary>
    /// <param name="last">The page's last row.</param>
    /// <returns>The opaque cursor string.</returns>
    private static string EncodeCursor(ChecklistItemDto last) => string.Create(
        CultureInfo.InvariantCulture,
        $"{last.Position}:{last.ItemId:N}");

    /// <summary>Reads a cursor this route issued.</summary>
    /// <param name="cursor">The raw query value.</param>
    /// <param name="position">The row's position.</param>
    /// <param name="itemId">The row's id.</param>
    /// <returns><c>true</c> when both halves parse.</returns>
    private static bool TryDecodeCursor(string cursor, out int position, out Guid itemId)
    {
        position = 0;
        itemId = Guid.Empty;

        var separator = cursor.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        return int.TryParse(cursor[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out position)
            && Guid.TryParseExact(cursor[(separator + 1)..], "N", out itemId);
    }
}
