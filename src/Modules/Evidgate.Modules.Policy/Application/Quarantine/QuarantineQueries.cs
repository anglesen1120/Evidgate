using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Mediator;

namespace Evidgate.Modules.Policy.Application.Quarantine;

/// <summary>Cursor page of a project's quarantine entries (<c>GET /v1/projects/{p}/quarantine</c>, 07.3.1 §5).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Cursor">Opaque cursor, or null.</param>
/// <param name="Limit">Page size, 1–200.</param>
internal sealed record ListQuarantineEntriesQuery(Guid ProjectId, string? Cursor, int Limit) : IQuery<Result<QuarantineEntryPage>>;

/// <summary>
/// Handler of <see cref="ListQuarantineEntriesQuery"/>: project check, then one keyset statement (one extra row
/// decides the next page). <c>policy:read</c> is open to every member role including <c>viewer</c> (T58), and the
/// stored state is returned as is — an exception is never hidden from someone who can already see the run.
/// </summary>
[Permission(Permissions.Policy.Read)]
internal sealed class ListQuarantineEntriesQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<ListQuarantineEntriesQuery, Result<QuarantineEntryPage>>
{
    /// <inheritdoc />
    public async ValueTask<Result<QuarantineEntryPage>> Handle(ListQuarantineEntriesQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(ListQuarantineEntriesQuery));

        if (QuarantineScopeGuard.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<QuarantineEntryPage>(mismatch);
        }

        var live = await db.ScalarAsync<int?>(QuarantineSql.ProjectExists, new { project_id = query.ProjectId }, cancellationToken).ConfigureAwait(false);
        if (live is null)
        {
            return Result.Failure<QuarantineEntryPage>(QuarantineErrors.ProjectNotFound);
        }

        var orgId = scopes.Require().OrgId;
        IReadOnlyList<QuarantineEntry> rows;
        if (query.Cursor is null)
        {
            rows = await db.QueryAsync<QuarantineEntry>(
                QuarantineSql.FirstPage,
                new { org_id = orgId, project_id = query.ProjectId, limit = query.Limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var createdAt, out var id))
            {
                return Result.Failure<QuarantineEntryPage>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("cursor", "malformed")]));
            }

            rows = await db.QueryAsync<QuarantineEntry>(
                QuarantineSql.NextPage,
                new { org_id = orgId, project_id = query.ProjectId, cursor_created_at = createdAt, cursor_id = id, limit = query.Limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }

        var hasMore = rows.Count > query.Limit;
        IReadOnlyList<QuarantineEntry> page = hasMore ? [.. rows.Take(query.Limit)] : rows;

        return Result.Success(new QuarantineEntryPage(page, hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null));
    }
}
