using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Mediator;

namespace Evidgate.Modules.Authoring.Application.Queries;

/// <summary>One keyset page of a project's live requirements (<c>GET …/authoring/requirements</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Cursor">The opaque cursor of the previous page, or null for the first page.</param>
/// <param name="Limit">The page size the route already bounded.</param>
/// <param name="RequestError">The first edge validation error, answered after authorization.</param>
internal sealed record ListRequirementsQuery(Guid ProjectId, string? Cursor, int Limit, Error? RequestError = null)
    : IQuery<Result<RequirementListResponse>>;

/// <summary>Handler of <see cref="ListRequirementsQuery"/>: one statement, one row more than the page to know if there is a next one.</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, whose credential may be narrowed to one project.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="writer">The module's requirement reads.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class ListRequirementsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    RequirementWriter writer) : IQueryHandler<ListRequirementsQuery, Result<RequirementListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RequirementListResponse>> Handle(ListRequirementsQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ListRequirementsQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<RequirementListResponse>(scopeError);
        }

        if (query.RequestError is { } requestError)
        {
            return Result.Failure<RequirementListResponse>(requestError);
        }

        DateTimeOffset? afterCreatedAt = null;
        Guid? afterId = null;
        if (query.Cursor is not null)
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var createdAt, out var id))
            {
                return Result.Failure<RequirementListResponse>(AuthoringErrors.Invalid("/cursor", "invalid", "the cursor is not one this list issued"));
            }

            afterCreatedAt = createdAt;
            afterId = id;
        }

        if (await RequirementGate.RefusalAsync(db, scopes, principals, tenant, query.ProjectId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Result.Failure<RequirementListResponse>(refusal);
        }

        var rows = await writer.ListLiveAsync(scopes.Require().OrgId, query.ProjectId, afterCreatedAt, afterId, query.Limit + 1, cancellationToken).ConfigureAwait(false);
        if (!rows.IsSuccess)
        {
            return Result.Failure<RequirementListResponse>(rows.Error!);
        }

        var page = rows.Value.Take(query.Limit).Select(row => new RequirementListItemDto(
            row.Id,
            row.Kind,
            row.Title,
            row.ExternalRef,
            row.FeatureNodeId,
            row.ParentRequirementId,
            row.HeadVersionNo,
            row.Version,
            RequirementTime.Utc(row.CreatedAt),
            RequirementTime.Utc(row.UpdatedAt))).ToList();
        var next = rows.Value.Count > query.Limit
            ? KeysetCursor.Encode(RequirementTime.Utc(rows.Value[query.Limit - 1].CreatedAt), rows.Value[query.Limit - 1].Id)
            : null;

        return Result.Success(new RequirementListResponse(page, next));
    }
}

/// <summary>One requirement with its head body (<c>GET …/authoring/requirements/{r}</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="RequirementId">The requirement from the route.</param>
internal sealed record GetRequirementQuery(Guid ProjectId, Guid RequirementId) : IQuery<Result<RequirementDetailDto>>;

/// <summary>Handler of <see cref="GetRequirementQuery"/>. A deleted requirement is still answered, with <c>deleted_at</c> set.</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, whose credential may be narrowed to one project.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="writer">The module's requirement reads.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class GetRequirementQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    RequirementWriter writer) : IQueryHandler<GetRequirementQuery, Result<RequirementDetailDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RequirementDetailDto>> Handle(GetRequirementQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(GetRequirementQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (await RequirementGate.RefusalAsync(db, scopes, principals, tenant, query.ProjectId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Result.Failure<RequirementDetailDto>(refusal);
        }

        var found = await writer.GetAsync(scopes.Require().OrgId, query.ProjectId, query.RequirementId, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return Result.Failure<RequirementDetailDto>(found.Error!);
        }

        var row = found.Value;

        return Result.Success(new RequirementDetailDto(
            row.Id,
            row.Kind,
            row.Title,
            row.ExternalRef,
            row.FeatureNodeId,
            row.ParentRequirementId,
            row.Body,
            row.ContentHash,
            row.HeadVersionId,
            row.HeadVersionNo,
            row.Version,
            row.DeletedAt is { } deleted ? RequirementTime.Utc(deleted) : null,
            RequirementTime.Utc(row.CreatedAt),
            RequirementTime.Utc(row.UpdatedAt)));
    }
}

/// <summary>One page of a requirement's history (<c>GET …/authoring/requirements/{r}/versions</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="RequirementId">The requirement from the route.</param>
/// <param name="Cursor">The version number the previous page ended at, or null for the newest page.</param>
/// <param name="Limit">The page size the route already bounded.</param>
/// <param name="RequestError">The first edge validation error, answered after authorization.</param>
internal sealed record ListRequirementVersionsQuery(Guid ProjectId, Guid RequirementId, string? Cursor, int Limit, Error? RequestError = null)
    : IQuery<Result<RequirementVersionListResponse>>;

/// <summary>
/// Handler of <see cref="ListRequirementVersionsQuery"/>. The page is cut by row count and by body bytes, so a
/// requirement with large bodies cannot make one response arbitrarily big; the cursor is the last version number
/// returned, and exists exactly when an older version does.
/// </summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, whose credential may be narrowed to one project.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="writer">The module's requirement reads.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class ListRequirementVersionsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    RequirementWriter writer) : IQueryHandler<ListRequirementVersionsQuery, Result<RequirementVersionListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RequirementVersionListResponse>> Handle(
        ListRequirementVersionsQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ListRequirementVersionsQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<RequirementVersionListResponse>(scopeError);
        }

        if (query.RequestError is { } requestError)
        {
            return Result.Failure<RequirementVersionListResponse>(requestError);
        }

        int? before = null;
        if (query.Cursor is not null)
        {
            if (!int.TryParse(query.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 2)
            {
                return Result.Failure<RequirementVersionListResponse>(AuthoringErrors.Invalid("/cursor", "invalid", "the cursor is not one this list issued"));
            }

            before = parsed;
        }

        if (await RequirementGate.RefusalAsync(db, scopes, principals, tenant, query.ProjectId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Result.Failure<RequirementVersionListResponse>(refusal);
        }

        var orgId = scopes.Require().OrgId;
        var found = await writer.GetAsync(orgId, query.ProjectId, query.RequirementId, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return Result.Failure<RequirementVersionListResponse>(found.Error!);
        }

        var versions = await writer.ListVersionsAsync(orgId, query.ProjectId, query.RequirementId, before, query.Limit, cancellationToken).ConfigureAwait(false);
        if (!versions.IsSuccess)
        {
            return Result.Failure<RequirementVersionListResponse>(versions.Error!);
        }

        var items = versions.Value.Select(row => new RequirementVersionDto(
            row.Id,
            row.VersionNo,
            row.Body,
            row.ContentHash,
            row.Source,
            row.AuthorMembershipId,
            row.Reason,
            RequirementTime.Utc(row.CreatedAt))).ToList();
        var next = items.Count > 0 && items[^1].VersionNo > 1
            ? items[^1].VersionNo.ToString(CultureInfo.InvariantCulture)
            : null;

        return Result.Success(new RequirementVersionListResponse(items, next));
    }
}

/// <summary>Reads a <c>timestamptz</c> the way the rows carry it: UTC.</summary>
internal static class RequirementTime
{
    /// <summary>The instant of <paramref name="value"/>, read as UTC (Npgsql already normalized it).</summary>
    /// <param name="value">A <c>timestamptz</c> value.</param>
    internal static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
