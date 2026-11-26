using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Mediator;

namespace Evidgate.Modules.Healing.Application;

/// <summary>A cursor page of one project's incidents (<c>GET /v1/projects/{p}/incidents</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="State">The <c>state</c> filter, or null.</param>
/// <param name="ExecutionKey">The <c>execution_key</c> filter, or null.</param>
/// <param name="Cursor">The cursor of the previous page, or null for the first.</param>
/// <param name="Limit">The page size, 1 to 200.</param>
internal sealed record ListIncidentsQuery(Guid ProjectId, string? State, string? ExecutionKey, string? Cursor, int Limit)
    : IQuery<Result<IncidentPage>>;

/// <summary>
/// Handler of <see cref="ListIncidentsQuery"/>: <c>healing:triage</c> reads, a project outside the credential's or
/// the request's scope is 403, and the page is one statement over <c>incident</c>. An unknown project or
/// another tenant's reads as an empty page, because RLS leaves no row to find.
/// </summary>
[Permission(Permissions.Healing.Triage)]
internal sealed class ListIncidentsQueryHandler(
    IncidentReadStore store,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<ListIncidentsQuery, Result<IncidentPage>>
{
    /// <inheritdoc />
    public async ValueTask<Result<IncidentPage>> Handle(ListIncidentsQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Healing, nameof(ListIncidentsQuery));

        if (HealingScopeGuard.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<IncidentPage>(mismatch);
        }

        if (IncidentListRules.Validate(query.State, query.ExecutionKey) is { } invalid)
        {
            return Result.Failure<IncidentPage>(invalid);
        }

        (DateTimeOffset CreatedAt, Guid Id)? after = null;
        if (query.Cursor is not null)
        {
            if (!KeysetCursor.TryDecodeDescending(query.Cursor, out var createdAt, out var id))
            {
                return Result.Failure<IncidentPage>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("cursor", "malformed")]));
            }

            after = (createdAt, id);
        }

        var rows = await store
            .ListAsync(query.ProjectId, query.State, query.ExecutionKey, after, query.Limit + 1, cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        IReadOnlyList<Incident> page = hasMore ? [.. rows.Take(query.Limit)] : rows;
        return Result.Success(new IncidentPage(
            page,
            hasMore ? KeysetCursor.EncodeDescending(page[^1].CreatedAt, page[^1].Id) : null));
    }
}
