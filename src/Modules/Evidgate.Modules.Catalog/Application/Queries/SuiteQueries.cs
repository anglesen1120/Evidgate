using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Catalog.Application.Queries;

/// <summary>Cursor page of a project's suites (<c>GET /v1/projects/{p}/suites</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Cursor">Opaque cursor, or null.</param>
/// <param name="Limit">Page size, 1–200.</param>
/// <param name="Platform">Only suites of this platform, or null (03.1.4 AC3).</param>
/// <param name="Level">Only suites of this level, or null.</param>
/// <param name="Purpose">Only suites of this purpose, or null.</param>
internal sealed record ListSuitesQuery(Guid ProjectId, string? Cursor, int Limit, string? Platform = null, string? Level = null, string? Purpose = null)
    : IQuery<Result<SuiteListResponse>>;

/// <summary>Handler of <see cref="ListSuitesQuery"/>: project check, then one keyset statement (one extra row decides the next page).</summary>
[Permission(Permissions.Suite.Read)]
internal sealed class ListSuitesQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<ListSuitesQuery, Result<SuiteListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<SuiteListResponse>> Handle(ListSuitesQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(ListSuitesQuery));

        if (CatalogScope.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<SuiteListResponse>(mismatch);
        }

        var exists = await db.ScalarAsync<int?>(CatalogReadSql.ProjectExists, new { project_id = query.ProjectId }, cancellationToken).ConfigureAwait(false);
        if (exists is null)
        {
            return Result.Failure<SuiteListResponse>(CatalogErrors.ProjectNotFound);
        }

        var orgId = scopes.Require().OrgId;

        // A filter is part of the closed taxonomy, so an unknown value is a 422 naming the member, never an empty page.
        var platform = query.Platform is null ? null : Taxonomy.Canonical(query.Platform, Taxonomy.Platforms);
        if (query.Platform is not null && platform is null)
        {
            return Result.Failure<SuiteListResponse>(SuiteErrors.Invalid("platform must be one of web, api, unit or mobile", [new ErrorDetail("platform", "invalid_value")]));
        }

        var level = query.Level is null ? null : Taxonomy.Canonical(query.Level, Taxonomy.Levels);
        if (query.Level is not null && level is null)
        {
            return Result.Failure<SuiteListResponse>(SuiteErrors.Invalid("level must be one of unit, integration, e2e or smoke", [new ErrorDetail("level", "invalid_value")]));
        }

        var purpose = query.Purpose is null ? null : Taxonomy.Canonical(query.Purpose, Taxonomy.Purposes);
        if (query.Purpose is not null && purpose is null)
        {
            return Result.Failure<SuiteListResponse>(SuiteErrors.Invalid("purpose must be one of regression, smoke, release_gate or exploratory", [new ErrorDetail("purpose", "invalid_value")]));
        }

        IReadOnlyList<SuiteDto> rows;
        if (query.Cursor is null)
        {
            rows = await db.QueryAsync<SuiteDto>(
                SuiteReadSql.SuitesFirstPage,
                new { org_id = orgId, project_id = query.ProjectId, platform, level, purpose, limit = query.Limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var createdAt, out var id))
            {
                return Result.Failure<SuiteListResponse>(Error.Invalid("the cursor is not a value this endpoint issued", [new ErrorDetail("cursor", "malformed")]));
            }

            rows = await db.QueryAsync<SuiteDto>(
                SuiteReadSql.SuitesNextPage,
                new { org_id = orgId, project_id = query.ProjectId, platform, level, purpose, cursor_created_at = createdAt, cursor_id = id, limit = query.Limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }

        var hasMore = rows.Count > query.Limit;
        IReadOnlyList<SuiteDto> page = hasMore ? [.. rows.Take(query.Limit)] : rows;

        return Result.Success(new SuiteListResponse(page, hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null));
    }
}

/// <summary>One suite (<c>GET /v1/suites/{s}</c>); the endpoint turns the head revision number into the ETag.</summary>
/// <param name="SuiteId">The suite from the route.</param>
internal sealed record GetSuiteQuery(Guid SuiteId) : IQuery<Result<SuiteDto>>;

/// <summary>Handler of <see cref="GetSuiteQuery"/>: 404 for another org's suite and for one outside the credential's project scope.</summary>
[Permission(Permissions.Suite.Read)]
internal sealed class GetSuiteQueryHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetSuiteQuery, Result<SuiteDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<SuiteDto>> Handle(GetSuiteQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(GetSuiteQuery));

        var suite = await db.QuerySingleOrDefaultAsync<SuiteDto>(SuiteReadSql.SuiteById, new { suite_id = query.SuiteId }, cancellationToken).ConfigureAwait(false);

        return suite is null || CatalogScope.Project(suite.ProjectId, principals, tenant) is not null
            ? Result.Failure<SuiteDto>(SuiteErrors.SuiteNotFound)
            : Result.Success(suite);
    }
}

/// <summary>The full pinned content of a suite revision (<c>GET /v1/suite-revisions/{r}</c>).</summary>
/// <param name="RevisionId">The revision from the route.</param>
internal sealed record GetSuiteRevisionQuery(Guid RevisionId) : IQuery<Result<SuiteRevisionDto>>;

/// <summary>Handler of <see cref="GetSuiteRevisionQuery"/>: one statement with the members and edges as JSON aggregates.</summary>
[Permission(Permissions.Suite.Read)]
internal sealed class GetSuiteRevisionQueryHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetSuiteRevisionQuery, Result<SuiteRevisionDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<SuiteRevisionDto>> Handle(GetSuiteRevisionQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(GetSuiteRevisionQuery));

        var loaded = await SuiteRevisions.LoadAsync(db, query.RevisionId, cancellationToken).ConfigureAwait(false);

        return loaded is null || CatalogScope.Project(loaded.ProjectId, principals, tenant) is not null
            ? Result.Failure<SuiteRevisionDto>(SuiteErrors.RevisionNotFound)
            : Result.Success(loaded.Revision);
    }
}

/// <summary>What changed from <paramref name="AgainstId"/> to <paramref name="RevisionId"/> (<c>GET /v1/suite-revisions/{r}/diff?against=</c>).</summary>
/// <param name="RevisionId">The revision from the route.</param>
/// <param name="AgainstId">The revision it is compared with.</param>
internal sealed record GetSuiteRevisionDiffQuery(Guid RevisionId, Guid AgainstId) : IQuery<Result<SuiteRevisionDiff>>;

/// <summary>Handler of <see cref="GetSuiteRevisionDiffQuery"/>: two statements; both revisions must be visible and belong to one suite (404 otherwise).</summary>
[Permission(Permissions.Suite.Read)]
internal sealed class GetSuiteRevisionDiffQueryHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetSuiteRevisionDiffQuery, Result<SuiteRevisionDiff>>
{
    /// <inheritdoc />
    public async ValueTask<Result<SuiteRevisionDiff>> Handle(GetSuiteRevisionDiffQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(GetSuiteRevisionDiffQuery));

        var current = await SuiteRevisions.LoadAsync(db, query.RevisionId, cancellationToken).ConfigureAwait(false);
        var against = await SuiteRevisions.LoadAsync(db, query.AgainstId, cancellationToken).ConfigureAwait(false);

        if (current is null || against is null
            || current.Revision.SuiteId != against.Revision.SuiteId
            || CatalogScope.Project(current.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<SuiteRevisionDiff>(SuiteErrors.RevisionNotFound);
        }

        return Result.Success(SuiteRevisions.Diff(current.Revision, against.Revision));
    }
}

/// <summary>Reading and comparing stored revisions.</summary>
internal static class SuiteRevisions
{
    private static readonly JsonSerializerOptions Snake = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>A revision with the project of its suite (for the scope rule).</summary>
    internal sealed record Loaded(Guid ProjectId, SuiteRevisionDto Revision);

    internal static async Task<Loaded?> LoadAsync(ITenantDb db, Guid revisionId, CancellationToken cancellationToken)
    {
        var row = await db.QuerySingleOrDefaultAsync<DetailRow>(SuiteReadSql.RevisionDetail, new { revision_id = revisionId }, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var revision = new SuiteRevisionDto(
            row.Id,
            row.SuiteId,
            row.RevisionNo,
            row.RevisionHash,
            row.Status,
            row.Platform,
            row.Level,
            row.Purpose,
            JsonSerializer.Deserialize<JsonElement>(row.ShardStrategy),
            JsonSerializer.Deserialize<JsonElement>(row.RetryPolicy),
            JsonSerializer.Deserialize<JsonElement>(row.EnvironmentRequirements),
            row.CreatedAt,
            JsonSerializer.Deserialize<List<SuiteMemberDto>>(row.MembersJson, Snake) ?? [],
            JsonSerializer.Deserialize<List<SuiteEdgeDto>>(row.EdgesJson, Snake) ?? []);

        return new Loaded(row.ProjectId, revision);
    }

    /// <summary>The set difference of two revisions, keyed by what a member means (case revision and variant), never by row id.</summary>
    internal static SuiteRevisionDiff Diff(SuiteRevisionDto current, SuiteRevisionDto against)
    {
        static SuiteMemberKey Key(SuiteMemberDto member) => new(member.CaseRevisionId, member.VariantHash);

        var now = current.Members.ToDictionary(Key);
        var before = against.Members.ToDictionary(Key);

        static IEnumerable<SuiteEdgeKey> EdgeKeys(SuiteRevisionDto revision)
        {
            var byId = revision.Members.ToDictionary(member => member.Id, Key);

            return revision.Edges.Select(edge => new SuiteEdgeKey(byId[edge.ParentMemberId], byId[edge.ChildMemberId]));
        }

        var nowEdges = EdgeKeys(current).ToHashSet();
        var beforeEdges = EdgeKeys(against).ToHashSet();

        static SuiteDelta? Delta(JsonElement to, JsonElement from) =>
            JsonSerializer.Serialize(to) == JsonSerializer.Serialize(from) ? null : new SuiteDelta(from, to);

        // The taxonomy a revision was authored under is content (it is in the hash), so a taxonomy-only change is a
        // difference like any other and must be visible here; the shape is the three members, not a free-form object.
        static SuiteDelta? TaxonomyDelta(SuiteRevisionDto to, SuiteRevisionDto from) =>
            to.Platform == from.Platform && to.Level == from.Level && to.Purpose == from.Purpose
                ? null
                : new SuiteDelta(
                    JsonSerializer.SerializeToElement(new TaxonomyBody(from.Platform, from.Level, from.Purpose), Snake),
                    JsonSerializer.SerializeToElement(new TaxonomyBody(to.Platform, to.Level, to.Purpose), Snake));

        return new SuiteRevisionDiff(
            current.Id,
            against.Id,
            [.. now.Keys.Where(key => !before.ContainsKey(key)).OrderBy(key => key.CaseRevisionId).ThenBy(key => key.VariantHash, StringComparer.Ordinal)],
            [.. before.Keys.Where(key => !now.ContainsKey(key)).OrderBy(key => key.CaseRevisionId).ThenBy(key => key.VariantHash, StringComparer.Ordinal)],
            [.. now.Where(pair => before.TryGetValue(pair.Key, out var old) && old.Required != pair.Value.Required).Select(pair => pair.Key)
                .OrderBy(key => key.CaseRevisionId).ThenBy(key => key.VariantHash, StringComparer.Ordinal)],
            [.. nowEdges.Where(edge => !beforeEdges.Contains(edge)).OrderBy(edge => edge.Parent.CaseRevisionId).ThenBy(edge => edge.Child.CaseRevisionId)
                .ThenBy(edge => edge.Parent.VariantHash, StringComparer.Ordinal).ThenBy(edge => edge.Child.VariantHash, StringComparer.Ordinal)],
            [.. beforeEdges.Where(edge => !nowEdges.Contains(edge)).OrderBy(edge => edge.Parent.CaseRevisionId).ThenBy(edge => edge.Child.CaseRevisionId)
                .ThenBy(edge => edge.Parent.VariantHash, StringComparer.Ordinal).ThenBy(edge => edge.Child.VariantHash, StringComparer.Ordinal)],
            Delta(current.ShardStrategy, against.ShardStrategy),
            Delta(current.RetryPolicy, against.RetryPolicy),
            Delta(current.EnvironmentRequirements, against.EnvironmentRequirements),
            TaxonomyDelta(current, against));
    }

    private sealed record TaxonomyBody(string Platform, string Level, string Purpose);

    private sealed class DetailRow
    {
        public Guid Id { get; set; }

        public Guid SuiteId { get; set; }

        public Guid ProjectId { get; set; }

        public int RevisionNo { get; set; }

        public string RevisionHash { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string Platform { get; set; } = "web";

        public string Level { get; set; } = "e2e";

        public string Purpose { get; set; } = "regression";

        public string ShardStrategy { get; set; } = "{}";

        public string RetryPolicy { get; set; } = "{}";

        public string EnvironmentRequirements { get; set; } = "{}";

        public DateTimeOffset CreatedAt { get; set; }

        public string MembersJson { get; set; } = "[]";

        public string EdgesJson { get; set; } = "[]";
    }
}

/// <summary>Preview of a suite revision (<c>POST /v1/suites/{s}/revisions?dry_run=true</c>, 03.2.1-T5.2): the hash and the number it would get, nothing written.</summary>
/// <param name="SuiteId">The suite from the route.</param>
/// <param name="Body">The authored revision.</param>
internal sealed record PreviewSuiteRevisionQuery(Guid SuiteId, CreateSuiteRevisionRequest Body) : IQuery<Result<SuiteRevisionHead>>;

/// <summary>
/// Handler of <see cref="PreviewSuiteRevisionQuery"/>: the same planner as the create command in a read-only
/// transaction, so the preview cannot disagree with the real create and cannot write. Needs <c>suite:write</c>: it answers
/// what a write would do.
/// </summary>
[Permission(Permissions.Suite.Write)]
internal sealed class PreviewSuiteRevisionQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    SuiteRevisionPlanner planner)
    : IQueryHandler<PreviewSuiteRevisionQuery, Result<SuiteRevisionHead>>
{
    /// <inheritdoc />
    public async ValueTask<Result<SuiteRevisionHead>> Handle(PreviewSuiteRevisionQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(PreviewSuiteRevisionQuery));

        var orgId = scopes.Require().OrgId;
        var suite = await db.QuerySingleOrDefaultAsync<SuiteStub>(SuiteReadSql.SuiteForRead, new { suite_id = query.SuiteId }, cancellationToken).ConfigureAwait(false);
        if (suite is null || CatalogScope.Project(suite.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<SuiteRevisionHead>(SuiteErrors.SuiteNotFound);
        }

        if (suite.Status != "active")
        {
            return Result.Failure<SuiteRevisionHead>(SuiteErrors.Archived);
        }

        var planned = await planner.PlanAsync(db, suite.ProjectId, query.Body, suite.AsTaxonomy(), cancellationToken).ConfigureAwait(false);
        if (!planned.IsSuccess)
        {
            return Result.Failure<SuiteRevisionHead>(planned.Error!);
        }

        var plan = planned.Value;
        var existing = await db.QuerySingleOrDefaultAsync<ExistingStub>(
            SuiteReadSql.ExistingByHash,
            new { org_id = orgId, suite_id = query.SuiteId, revision_hash = plan.RevisionHash },
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Result.Success(new SuiteRevisionHead(
                existing.Id, existing.RevisionNo, plan.RevisionHash, existing.Status, false, plan.Warnings, false,
                plan.Taxonomy.Platform, plan.Taxonomy.Level, plan.Taxonomy.Purpose));
        }

        var next = await db.ScalarAsync<int>(SuiteReadSql.NextRevisionNo, new { org_id = orgId, suite_id = query.SuiteId }, cancellationToken).ConfigureAwait(false);

        return Result.Success(new SuiteRevisionHead(
            null, next, plan.RevisionHash, "preview", false, plan.Warnings, false,
            plan.Taxonomy.Platform, plan.Taxonomy.Level, plan.Taxonomy.Purpose));
    }

    private sealed class SuiteStub
    {
        public Guid Id { get; set; }

        public Guid ProjectId { get; set; }

        public string Status { get; set; } = string.Empty;

        public string Platform { get; set; } = "web";

        public string Level { get; set; } = "e2e";

        public string Purpose { get; set; } = "regression";

        public SuiteTaxonomy AsTaxonomy() => new(Platform, Level, Purpose);
    }

    private sealed class ExistingStub
    {
        public Guid Id { get; set; }

        public int RevisionNo { get; set; }

        public string Status { get; set; } = string.Empty;
    }
}
