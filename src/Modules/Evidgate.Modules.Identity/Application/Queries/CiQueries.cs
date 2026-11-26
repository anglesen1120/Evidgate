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

        if (!TryCursor(query.Cursor, out var createdAt, out var id, out var invalid))
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

    private static bool TryCursor(string? cursor, out DateTimeOffset? createdAt, out Guid? id, out Error? error)
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
