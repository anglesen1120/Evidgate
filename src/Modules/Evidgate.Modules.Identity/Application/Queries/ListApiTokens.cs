using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;
using Mediator;

namespace Evidgate.Modules.Identity.Application.Queries;

/// <summary>
/// <c>GET /v1/projects/{p}/api-tokens</c>: cursor page of a project's tokens. Metadata only — the plaintext
/// was shown once at creation and the HMAC is never selected, so neither can leak through this route.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Cursor">Opaque cursor of a previous page, or null for the first.</param>
/// <param name="Limit">Page size, already bounded by the route.</param>
internal sealed record ListApiTokensQuery(Guid ProjectId, string? Cursor, int Limit)
    : IQuery<Result<ApiTokenListResponse>>;

/// <summary>Handler of <see cref="ListApiTokensQuery"/>; keyset order is <c>(created_at, id)</c>, the same as the ingest list.</summary>
[Permission(Permissions.ApiToken.Read)]
internal sealed class ListApiTokensQueryHandler(ITenantDb db, TimeProvider clock)
    : IQueryHandler<ListApiTokensQuery, Result<ApiTokenListResponse>>
{
    private const string Sql =
        """
        select id as Id, name as Name, prefix as Prefix, permissions as Permissions,
               expires_at as ExpiresAt, revoked_at as RevokedAt, last_used_at as LastUsedAt,
               created_by as CreatedBy, created_at as CreatedAt
          from public.api_token
         where project_id = @project_id
           and (@cursor_created_at::timestamptz is null or (created_at, id) > (@cursor_created_at, @cursor_id::uuid))
         order by created_at, id
         limit @limit
        """;

    /// <summary>The cursor this route issues, and nothing else.</summary>
    private static readonly Error CursorMalformed =
        Error.Invalid("the cursor is not a value this endpoint issued", [new ErrorDetail("cursor", "malformed")]);

    /// <inheritdoc />
    public async ValueTask<Result<ApiTokenListResponse>> Handle(
        ListApiTokensQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(ListApiTokensQuery));

        DateTimeOffset? afterCreatedAt = null;
        Guid? afterId = null;

        if (query.Cursor is { } cursor)
        {
            if (!KeysetCursor.TryDecode(cursor, out var decodedAt, out var decodedId))
            {
                return Result.Failure<ApiTokenListResponse>(CursorMalformed);
            }

            afterCreatedAt = decodedAt;
            afterId = decodedId;
        }

        var rows = await db.QueryAsync<TokenRow>(
            Sql,
            new
            {
                project_id = query.ProjectId,
                cursor_created_at = afterCreatedAt,
                cursor_id = afterId,
                limit = query.Limit + 1,
            },
            cancellationToken).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        var hasMore = rows.Count > query.Limit;
        var pageRows = rows.Take(query.Limit).ToList();

        return Result.Success(new ApiTokenListResponse(
            pageRows.Select(row => row.ToView(now)).ToList(),
            hasMore ? KeysetCursor.Encode(pageRows[^1].CreatedAt, pageRows[^1].Id) : null));
    }

    /// <summary>One row of the read model; it carries <c>created_at</c> only to build the next cursor.</summary>
    private sealed class TokenRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Prefix { get; set; } = string.Empty;

        public string[] Permissions { get; set; } = [];

        public DateTimeOffset ExpiresAt { get; set; }

        public DateTimeOffset? RevokedAt { get; set; }

        public DateTimeOffset? LastUsedAt { get; set; }

        public Guid CreatedBy { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public ApiTokenView ToView(DateTimeOffset now) => new(
            Id,
            Name,
            Prefix,
            Permissions,
            new ApiToken { RevokedAt = RevokedAt, ExpiresAt = ExpiresAt }.StatusAt(now),
            ExpiresAt,
            LastUsedAt,
            CreatedBy);
    }
}
