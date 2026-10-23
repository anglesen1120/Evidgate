using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;
using Mediator;

namespace Evidgate.Modules.Identity.Application.Queries;

/// <summary><c>GET /v1/projects/{p}/ci-bindings</c>: bindings are configuration, readable with <c>project:read</c>.</summary>
internal sealed record ListCiBindingsQuery(Guid ProjectId, string? Cursor, int Limit) : IQuery<Result<CiBindingListResponse>>;

[Permission(Permissions.Project.Read)]
internal sealed class ListCiBindingsQueryHandler(ITenantDb db)
    : IQueryHandler<ListCiBindingsQuery, Result<CiBindingListResponse>>
{
    private const string Sql =
        """
        select id as Id, project_id as ProjectId, provider as Provider, repository_id as RepositoryId,
               repository_full_name as RepositoryFullName, allowed_ref_patterns as AllowedRefPatterns,
               allowed_workflow_refs as AllowedWorkflowRefs, status as Status, activated_at as ActivatedAt,
               created_at as CreatedAt, version as Version
          from public.ci_binding
         where project_id = @project_id
           and (@cursor_created_at::timestamptz is null or (created_at, id) > (@cursor_created_at, @cursor_id::uuid))
         order by created_at, id
         limit @limit
        """;

    public async ValueTask<Result<CiBindingListResponse>> Handle(ListCiBindingsQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(ListCiBindingsQuery));

        if (!TryCursor(query.Cursor, out var createdAt, out var id, out var invalid))
        {
            return Result.Failure<CiBindingListResponse>(invalid!);
        }

        var rows = await db.QueryAsync<BindingRow>(
            Sql,
            new { project_id = query.ProjectId, cursor_created_at = createdAt, cursor_id = id, limit = query.Limit + 1 },
            cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        var page = rows.Take(query.Limit).Select(row => row.ToView()).ToList();

        return Result.Success(new CiBindingListResponse(
            page,
            hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null));
    }

    internal static bool TryCursor(string? cursor, out DateTimeOffset? createdAt, out Guid? id, out Error? error)
    {
        createdAt = null;
        id = null;
        error = null;

        if (cursor is null)
        {
            return true;
        }

        if (!KeysetCursor.TryDecode(cursor, out var decodedAt, out var decodedId))
        {
            error = Error.Invalid("the cursor is not a value this endpoint issued", [new ErrorDetail("cursor", "malformed")]);

            return false;
        }

        createdAt = decodedAt;
        id = decodedId;

        return true;
    }

    private sealed class BindingRow
    {
        public Guid Id { get; set; }

        public Guid ProjectId { get; set; }

        public string Provider { get; set; } = string.Empty;

        public long RepositoryId { get; set; }

        public string RepositoryFullName { get; set; } = string.Empty;

        public string[] AllowedRefPatterns { get; set; } = [];

        public string[] AllowedWorkflowRefs { get; set; } = [];

        public string Status { get; set; } = string.Empty;

        public DateTimeOffset? ActivatedAt { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public int Version { get; set; }

        public CiBindingView ToView() => new(
            Id, ProjectId, Provider, RepositoryId, RepositoryFullName, AllowedRefPatterns, AllowedWorkflowRefs, Status, ActivatedAt, CreatedAt, Version);
    }
}

/// <summary><c>GET /v1/projects/{p}/ingest-tokens</c> (<c>ingest_token:read</c>): metadata only, never the hash.</summary>
internal sealed record ListIngestTokensQuery(Guid ProjectId, string? Cursor, int Limit) : IQuery<Result<IngestTokenListResponse>>;

[Permission(Permissions.IngestToken.Read)]
internal sealed class ListIngestTokensQueryHandler(ITenantDb db, TimeProvider clock)
    : IQueryHandler<ListIngestTokensQuery, Result<IngestTokenListResponse>>
{
    private const string Sql =
        """
        select id as Id, project_id as ProjectId, name as Name, prefix as Prefix, scopes as Scopes,
               expires_at as ExpiresAt, overlap_ends_at as OverlapEndsAt, revoked_at as RevokedAt,
               rotated_to_id as RotatedToId, last_used_at as LastUsedAt, created_at as CreatedAt
          from public.ingest_token
         where project_id = @project_id
           and (@cursor_created_at::timestamptz is null or (created_at, id) > (@cursor_created_at, @cursor_id::uuid))
         order by created_at, id
         limit @limit
        """;

    public async ValueTask<Result<IngestTokenListResponse>> Handle(ListIngestTokensQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(ListIngestTokensQuery));

        if (!ListCiBindingsQueryHandler.TryCursor(query.Cursor, out var createdAt, out var id, out var invalid))
        {
            return Result.Failure<IngestTokenListResponse>(invalid!);
        }

        var rows = await db.QueryAsync<TokenRow>(
            Sql,
            new { project_id = query.ProjectId, cursor_created_at = createdAt, cursor_id = id, limit = query.Limit + 1 },
            cancellationToken).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        var hasMore = rows.Count > query.Limit;
        var page = rows.Take(query.Limit).Select(row => row.ToView(now)).ToList();

        return Result.Success(new IngestTokenListResponse(
            page,
            hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null));
    }

    private sealed class TokenRow
    {
        public Guid Id { get; set; }

        public Guid ProjectId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Prefix { get; set; } = string.Empty;

        public string[] Scopes { get; set; } = [];

        public DateTimeOffset? ExpiresAt { get; set; }

        public DateTimeOffset? OverlapEndsAt { get; set; }

        public DateTimeOffset? RevokedAt { get; set; }

        public Guid? RotatedToId { get; set; }

        public DateTimeOffset? LastUsedAt { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public IngestTokenView ToView(DateTimeOffset now) => new IngestToken
        {
            Id = Id,
            ProjectId = ProjectId,
            Name = Name,
            Prefix = Prefix,
            Scopes = Scopes,
            ExpiresAt = ExpiresAt,
            OverlapEndsAt = OverlapEndsAt,
            RevokedAt = RevokedAt,
            RotatedToId = RotatedToId,
            LastUsedAt = LastUsedAt,
            CreatedAt = CreatedAt,
        }.ToView(now);
    }
}
