using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
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
internal sealed record ListChecklistItemsQuery(
    Guid ProjectId,
    Guid? NodeId,
    string? State,
    string? Priority,
    string? Cursor,
    int Limit)
    : IQuery<Result<ChecklistItemListResponse>>;

/// <summary>
/// Handler of <see cref="ListChecklistItemsQuery"/>: one statement per page (P3) over
/// <c>checklist_item_list_idx</c> (P4), one row more than asked to decide whether a next page exists, so no count
/// runs. A filter outside the stored vocabulary is a 422 naming the query member instead of an empty page.
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
        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<ChecklistItemListResponse>(scopeError);
        }

        if (query.State is { } state && !ChecklistItemStateMachine.States.Contains(state, StringComparer.Ordinal))
        {
            return Result.Failure<ChecklistItemListResponse>(
                AuthoringErrors.Invalid("/state", "value_invalid", "state must be one of the checklist item states"));
        }

        if (query.Priority is { } priority && !AuthoringPriorities.IsValid(priority))
        {
            return Result.Failure<ChecklistItemListResponse>(
                AuthoringErrors.Invalid("/priority", "value_invalid", "priority must be one of the four priorities"));
        }

        int? afterPosition = null;
        Guid? afterId = null;

        if (query.Cursor is { } cursor)
        {
            if (!TryDecodeCursor(cursor, out var position, out var itemId))
            {
                return Result.Failure<ChecklistItemListResponse>(
                    AuthoringErrors.Invalid("/cursor", "invalid", "the cursor was not issued by this route"));
            }

            afterPosition = position;
            afterId = itemId;
        }

        var rows = await db.QueryAsync<ChecklistItemDto>(
            AuthoringChecklistReadSql.List,
            new
            {
                org_id = scopes.Require().OrgId,
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
