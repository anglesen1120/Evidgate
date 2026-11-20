using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Identity.Application.Queries;

/// <summary><c>GET /v1/orgs/{o}/support-grants</c> (02.8.1 §5): one cursor page of the org's grants, newest first.</summary>
/// <param name="OrgId">The org from the route.</param>
/// <param name="Cursor">Opaque cursor of a previous page, or null for the first.</param>
/// <param name="Limit">Page size, already bounded by the route.</param>
internal sealed record ListSupportGrantsQuery(Guid OrgId, string? Cursor, int Limit)
    : IQuery<Result<SupportGrantList>>;

/// <summary>
/// Handler of <see cref="ListSupportGrantsQuery"/>: keyset on <c>(created_at, id)</c> descending, one statement per
/// page, the state derived in the statement.
/// </summary>
/// <remarks>
/// No operator principal can reach this route today: authentication never produces one, and the evaluator denies
/// the kind outright (<c>PermissionEvaluator.OperatorWithoutSupportGrant</c>). The route is therefore strictly the
/// tenant admin's view of its own grants; the operator's own-request read of the story is still owed.
/// </remarks>
[Permission(Permissions.Support.Read)]
internal sealed class ListSupportGrantsQueryHandler(ITenantDb db, ITenantScopeAccessor scopes)
    : IQueryHandler<ListSupportGrantsQuery, Result<SupportGrantList>>
{
    /// <summary>The cursor this route issues, and nothing else.</summary>
    private static readonly Error CursorMalformed =
        Error.Invalid("the cursor is not a value this endpoint issued", [new ErrorDetail("cursor", "malformed")]);

    /// <inheritdoc />
    public async ValueTask<Result<SupportGrantList>> Handle(
        ListSupportGrantsQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(ListSupportGrantsQuery));

        var orgId = scopes.Require().OrgId;

        if (query.OrgId != orgId)
        {
            // A member without membership was already refused at tenant resolution; the only caller that can get
            // here with another org is a service credential, and 404 is the answer the contract gives it.
            return Result.Failure<SupportGrantList>(Error.NotFound(OrgMismatch.Message));
        }

        DateTimeOffset? afterCreatedAt = null;
        Guid? afterId = null;

        if (query.Cursor is { } cursor)
        {
            if (!KeysetCursor.TryDecode(cursor, out var decodedAt, out var decodedId))
            {
                return Result.Failure<SupportGrantList>(CursorMalformed);
            }

            afterCreatedAt = decodedAt;
            afterId = decodedId;
        }

        var rows = await db.QueryAsync<SupportGrantPageRow>(
            SupportGrantReadSql.Page,
            new
            {
                org_id = orgId,
                cursor_created_at = afterCreatedAt,
                cursor_id = afterId,
                limit = query.Limit + 1,
            },
            cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        var page = rows.Take(query.Limit).ToList();

        return Result.Success(new SupportGrantList(
            page.Select(ToView).ToList(),
            hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null));
    }

    private static SupportGrant ToView(SupportGrantPageRow row) => new(
        row.Id,
        row.OrgId,
        row.OperatorUserId,
        row.Scopes,
        row.ProjectIds,
        row.Reason,
        row.State,
        row.ApprovedBy,
        row.ApprovedAt,
        row.ExpiresAt,
        row.RevokedAt,
        row.CreatedAt);
}
