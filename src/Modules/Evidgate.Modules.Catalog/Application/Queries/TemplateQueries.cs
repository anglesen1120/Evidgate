using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Catalog.Application.Queries;

/// <summary>Keyset page of the active templates (<c>GET /v1/templates</c>).</summary>
/// <param name="Kind">Optional kind filter: <c>web</c>, <c>api</c>, <c>unit</c> or <c>mobile</c>.</param>
/// <param name="Platform">Optional platform filter, canonicalised against the taxonomy catalogue.</param>
/// <param name="Level">Optional level filter, canonicalised against the taxonomy catalogue.</param>
/// <param name="Purpose">Optional purpose filter, canonicalised against the taxonomy catalogue.</param>
/// <param name="Cursor">The opaque cursor of the previous page, or null.</param>
/// <param name="Limit">The raw <c>limit</c> query value, parsed here so an unauthorized caller never learns its bound.</param>
internal sealed record ListTemplatesQuery(
    string? Kind,
    string? Platform,
    string? Level,
    string? Purpose,
    string? Cursor,
    string? Limit)
    : IQuery<Result<TemplateListResponse>>;

/// <summary>
/// Handler of <see cref="ListTemplatesQuery"/>: one statement per page over the template registry; one extra row
/// decides whether a next page exists, so no count runs. RLS supplies the built-ins plus the caller's own org rows.
/// </summary>
[Permission(Permissions.Case.Read)]
internal sealed class ListTemplatesQueryHandler(ITenantDb db)
    : IQueryHandler<ListTemplatesQuery, Result<TemplateListResponse>>
{
    /// <summary>The closed kind set of the table's CHECK and the contract's enum.</summary>
    private static readonly string[] Kinds = ["web", "api", "unit", "mobile"];

    /// <inheritdoc />
    public async ValueTask<Result<TemplateListResponse>> Handle(ListTemplatesQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(ListTemplatesQuery));

        if (InvalidFilter(query, out var limit, out var platform, out var level, out var purpose) is { } filterError)
        {
            return Result.Failure<TemplateListResponse>(filterError);
        }

        IReadOnlyList<TemplateRow> rows;
        if (query.Cursor is null)
        {
            rows = await db.QueryAsync<TemplateRow>(
                TemplateReadSql.TemplatesFirstPage,
                new { kind = query.Kind, platform, level, purpose, limit = limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var cursorCreatedAt, out var cursorId))
            {
                return Result.Failure<TemplateListResponse>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("cursor", "malformed")]));
            }

            rows = await db.QueryAsync<TemplateRow>(
                TemplateReadSql.TemplatesNextPage,
                new
                {
                    kind = query.Kind,
                    platform,
                    level,
                    purpose,
                    cursor_created_at = cursorCreatedAt,
                    cursor_id = cursorId,
                    limit = limit + 1,
                },
                cancellationToken).ConfigureAwait(false);
        }

        var hasMore = rows.Count > limit;
        var page = (hasMore ? rows.Take(limit) : rows).Select(row => row.ToDto()).ToList();

        return Result.Success(new TemplateListResponse(
            page,
            hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null));
    }

    /// <summary>
    /// The query members a caller can fix, validated inside the handler so the pipeline's authorization stage answers
    /// first: an unknown kind, an unknown taxonomy value and an out-of-range page size are all the 422 the contract documents.
    /// </summary>
    /// <param name="query">The list query.</param>
    /// <param name="limit">The page size, the default when absent.</param>
    /// <param name="platform">The canonical platform filter, or null.</param>
    /// <param name="level">The canonical level filter, or null.</param>
    /// <param name="purpose">The canonical purpose filter, or null.</param>
    private static Error? InvalidFilter(
        ListTemplatesQuery query,
        out int limit,
        out string? platform,
        out string? level,
        out string? purpose)
    {
        if (CatalogPreconditions.Limit(query.Limit, CatalogPreconditions.DefaultListLimit, CatalogPreconditions.MaxListLimit, out limit) is { } limitError)
        {
            platform = null;
            level = null;
            purpose = null;
            return limitError;
        }

        if (query.Kind is not null && !Kinds.Contains(query.Kind, StringComparer.Ordinal))
        {
            platform = null;
            level = null;
            purpose = null;
            return Error.Invalid(
                "kind must be one of web, api, unit, mobile",
                [new ErrorDetail("kind", "invalid")]);
        }

        return CatalogPreconditions.TaxonomyFilters(query.Platform, query.Level, query.Purpose, out platform, out level, out purpose);
    }
}

/// <summary>One template (<c>GET /v1/templates/{t}</c>).</summary>
/// <param name="TemplateId">The template from the route.</param>
internal sealed record GetTemplateQuery(Guid TemplateId) : IQuery<Result<TemplateDetailDto>>;

/// <summary>
/// Handler of <see cref="GetTemplateQuery"/>: one statement; an inactive row and another org's row are both the same
/// 404, so the id alone never tells a caller whether a row exists.
/// </summary>
[Permission(Permissions.Case.Read)]
internal sealed class GetTemplateQueryHandler(ITenantDb db)
    : IQueryHandler<GetTemplateQuery, Result<TemplateDetailDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<TemplateDetailDto>> Handle(GetTemplateQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(GetTemplateQuery));

        var row = await db.QuerySingleOrDefaultAsync<TemplateDetailRow>(
            TemplateReadSql.TemplateById,
            new { template_id = query.TemplateId },
            cancellationToken).ConfigureAwait(false);

        return row is null
            ? Result.Failure<TemplateDetailDto>(CatalogErrors.TemplateNotFound)
            : Result.Success(row.ToDto());
    }
}
