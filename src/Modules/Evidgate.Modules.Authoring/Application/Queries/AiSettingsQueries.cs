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

/// <summary>The effective AI settings of a project: its head, or the platform default as version 0.</summary>
/// <param name="ProjectId">The project from the route; another tenant's project is a 404.</param>
internal sealed record GetAiSettingsQuery(Guid ProjectId) : IQuery<Result<AiSettingsView>>;

/// <summary>
/// Handler of <see cref="GetAiSettingsQuery"/>: one statement, so the project's liveness and the head (or the
/// default template) are decided by the same read and an unknown project cannot be distinguished from a
/// project with no settings by anything but the 404 it answers.
/// </summary>
/// <param name="db">The tenant-scoped data access.</param>
/// <param name="scopes">The request's tenant scope.</param>
/// <param name="principals">The request principal.</param>
/// <param name="tenant">The request tenant context.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class GetAiSettingsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant) : IQueryHandler<GetAiSettingsQuery, Result<AiSettingsView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AiSettingsView>> Handle(
        GetAiSettingsQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(GetAiSettingsQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AiSettingsView>(scopeError);
        }

        var row = await db.QuerySingleOrDefaultAsync<AiSettingsSql.EffectiveRow>(
            AiSettingsSql.Effective,
            new { org_id = scopes.Require().OrgId, project_id = query.ProjectId },
            cancellationToken).ConfigureAwait(false);

        return row is null
            ? Result.Failure<AiSettingsView>(AuthoringRules.ProjectNotFound)
            : Result.Success(AiSettingsModels.Effective(row));
    }
}

/// <summary>A keyset page of a project's settings versions, newest first.</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Cursor">The opaque cursor of the next page, or null for the first page.</param>
/// <param name="Limit">The page size the route already bounded.</param>
internal sealed record ListAiSettingsVersionsQuery(Guid ProjectId, string? Cursor, int Limit)
    : IQuery<Result<AiSettingsVersionListResponse>>;

/// <summary>
/// Handler of <see cref="ListAiSettingsVersionsQuery"/>: the project probe and one keyset statement, whatever
/// the page size. The order is the list index's <c>(created_at desc, id desc)</c>, so the cursor is a searchable
/// row comparison and no row is skipped or repeated at a page boundary.
/// </summary>
/// <param name="db">The tenant-scoped data access.</param>
/// <param name="scopes">The request's tenant scope.</param>
/// <param name="principals">The request principal.</param>
/// <param name="tenant">The request tenant context.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class ListAiSettingsVersionsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant) : IQueryHandler<ListAiSettingsVersionsQuery, Result<AiSettingsVersionListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AiSettingsVersionListResponse>> Handle(
        ListAiSettingsVersionsQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ListAiSettingsVersionsQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AiSettingsVersionListResponse>(scopeError);
        }

        DateTimeOffset? cursorCreatedAt = null;
        var cursorId = Guid.Empty;

        if (!string.IsNullOrEmpty(query.Cursor))
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var decodedAt, out var decodedId))
            {
                return Result.Failure<AiSettingsVersionListResponse>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("/cursor", "malformed")]));
            }

            cursorCreatedAt = decodedAt;
            cursorId = decodedId;
        }

        var orgId = scopes.Require().OrgId;

        if (await db.ScalarAsync<int?>(
                AuthoringVersionReadSql.ProjectExists,
                new { org_id = orgId, project_id = query.ProjectId },
                cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<AiSettingsVersionListResponse>(AuthoringRules.ProjectNotFound);
        }

        var rows = await db.QueryAsync<AiSettingsSql.VersionRow>(
            cursorCreatedAt is null ? AiSettingsSql.FirstPage : AiSettingsSql.NextPage,
            new
            {
                org_id = orgId,
                project_id = query.ProjectId,
                cursor_created_at = cursorCreatedAt,
                cursor_id = cursorId,
                take = query.Limit + 1,
            },
            cancellationToken).ConfigureAwait(false);

        // `limit + 1` rows tell a last page from a full one: the extra row is read and dropped, never returned.
        var hasMore = rows.Count > query.Limit;
        var size = hasMore ? query.Limit : rows.Count;
        var items = new List<AiSettingsVersionSummary>(size);

        for (var index = 0; index < size; index++)
        {
            items.Add(AiSettingsModels.Summary(rows[index]));
        }

        return Result.Success(new AiSettingsVersionListResponse(
            items,
            hasMore ? KeysetCursor.Encode(rows[size - 1].CreatedAt, rows[size - 1].SettingsId) : null));
    }
}

/// <summary>One stored settings version by its number.</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="VersionNo">The version number the route names.</param>
internal sealed record GetAiSettingsVersionQuery(Guid ProjectId, int VersionNo)
    : IQuery<Result<AiSettingsVersion>>;

/// <summary>
/// Handler of <see cref="GetAiSettingsVersionQuery"/>: one statement carrying the project's liveness, so a
/// version of another project, another tenant or a number that never existed is one 404.
/// </summary>
/// <param name="db">The tenant-scoped data access.</param>
/// <param name="scopes">The request's tenant scope.</param>
/// <param name="principals">The request principal.</param>
/// <param name="tenant">The request tenant context.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class GetAiSettingsVersionQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant) : IQueryHandler<GetAiSettingsVersionQuery, Result<AiSettingsVersion>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AiSettingsVersion>> Handle(
        GetAiSettingsVersionQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(GetAiSettingsVersionQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AiSettingsVersion>(scopeError);
        }

        var row = await db.QuerySingleOrDefaultAsync<AiSettingsSql.VersionRow>(
            AiSettingsSql.VersionDetail,
            new { org_id = scopes.Require().OrgId, project_id = query.ProjectId, version_no = query.VersionNo },
            cancellationToken).ConfigureAwait(false);

        return row is null
            ? Result.Failure<AiSettingsVersion>(AuthoringErrors.NotFound(
                "no AI settings version with that number is visible in this project"))
            : Result.Success(AiSettingsModels.Version(row));
    }
}

/// <summary>The platform's closed, read-only template catalogue.</summary>
/// <param name="ProjectId">The project from the route; the catalogue itself is platform-wide.</param>
internal sealed record ListAiSettingsTemplatesQuery(Guid ProjectId)
    : IQuery<Result<AiSettingsTemplateListResponse>>;

/// <summary>
/// Handler of <see cref="ListAiSettingsTemplatesQuery"/>: the project probe (so a foreign project is the same
/// 404 as its settings routes) and one ordered select over the small platform catalogue.
/// </summary>
/// <param name="db">The tenant-scoped data access.</param>
/// <param name="scopes">The request's tenant scope.</param>
/// <param name="principals">The request principal.</param>
/// <param name="tenant">The request tenant context.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class ListAiSettingsTemplatesQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant) : IQueryHandler<ListAiSettingsTemplatesQuery, Result<AiSettingsTemplateListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AiSettingsTemplateListResponse>> Handle(
        ListAiSettingsTemplatesQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ListAiSettingsTemplatesQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AiSettingsTemplateListResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (await db.ScalarAsync<int?>(
                AuthoringVersionReadSql.ProjectExists,
                new { org_id = orgId, project_id = query.ProjectId },
                cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<AiSettingsTemplateListResponse>(AuthoringRules.ProjectNotFound);
        }

        var rows = await db.QueryAsync<AiSettingsSql.TemplateRow>(
            AiSettingsSql.Templates,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result.Success(new AiSettingsTemplateListResponse(
            [.. rows.Select(AiSettingsModels.Template)],
            null));
    }
}
