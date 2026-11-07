using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Usage.Application.Queries;

/// <summary>
/// <c>GET /v1/orgs/{o}/usage/summary</c>: the current period's counters against quota for the org and, when the request
/// names one, a project. The period and the wait beside a blocked counter are the reading transaction's own clock, the
/// month the gate writes its counters in, so a host clock that already names the next month cannot answer a month the
/// counters do not live in. The wait is a hint to a reader, never an input to scheduling.
/// </summary>
/// <param name="OrgId">The org the route names; it must be the request's tenant.</param>
/// <param name="ProjectId">The project the request named, or null for the org-wide answer.</param>
/// <param name="Cursor">The previous page's <c>next_cursor</c>, or null for the first page.</param>
/// <param name="Limit">Projects per page, already bound by the route.</param>
internal sealed record GetUsageSummaryQuery(Guid OrgId, Guid? ProjectId, string? Cursor, int Limit)
    : IQuery<Result<UsageSummaryDto>>;

/// <summary>
/// Handler of <see cref="GetUsageSummaryQuery"/>: the C2 project-scope rule, the cursor and page-size refusals, the
/// reading transaction's own month, then one statement that reads that month's counters, roll-up totals, the requested
/// project's visibility and one page of projects together. The organization resolves through the pipeline's tenant
/// context, so an org outside the caller's visibility never reaches this handler.
/// </summary>
/// <remarks>
/// The statement answers "is the named project in this org?" by itself, so a foreign id is the same 404 an unknown one
/// is and the read is never an existence oracle. Only the current period's counter can contribute a limit: a month
/// whose counter does not exist yet reports the roll-up total with a null limit, because carrying the previous month's
/// limit is the gate's write-time job and a read that invented the same carry could disagree with it.
/// </remarks>
[Permission(Permissions.Usage.Read)]
internal sealed class GetUsageSummaryQueryHandler(
    UsageReadStore store,
    QuotaCounterStore counters,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetUsageSummaryQuery, Result<UsageSummaryDto>>
{
    /// <summary>The 404 of a project this org does not hold; unknown and foreign ids share it.</summary>
    internal static readonly Error ProjectNotFound = Error.NotFound("no such project");

    /// <inheritdoc />
    public async ValueTask<Result<UsageSummaryDto>> Handle(
        GetUsageSummaryQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(
            ModuleName.Usage,
            nameof(GetUsageSummaryQuery));

        var scope = scopes.Require();
        if (query.OrgId != scope.OrgId)
        {
            return Result.Failure<UsageSummaryDto>(Error.NotFound("no such organization"));
        }

        if (query.ProjectId is { } projectId
            && UsageProjectScope.Project(projectId, principals.Current?.ProjectScope, tenant.Current?.ProjectId)
                is { } mismatch)
        {
            return Result.Failure<UsageSummaryDto>(mismatch);
        }

        if (UsageSummaryPaging.LimitError(query.Limit) is { } invalidLimit)
        {
            return Result.Failure<UsageSummaryDto>(invalidLimit);
        }

        // The cursor is decoded even on the filtered answer, so a caller cannot learn that paging is ignored there by
        // sending a cursor the statement would have refused.
        DateTimeOffset? cursorCreatedAt = null;
        Guid? cursorId = null;
        if (query.Cursor is not null)
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var createdAt, out var id))
            {
                return Result.Failure<UsageSummaryDto>(UsageSummaryPaging.CursorInvalid);
            }

            cursorCreatedAt = createdAt;
            cursorId = id;
        }

        // Both reads run in this request's tenant transaction, so the counters and the clock that names them are the
        // gate's own.
        var clock = await counters.CurrentPeriodClockAsync(cancellationToken).ConfigureAwait(false);
        var rows = await store
            .ReadSummaryAsync(
                scope.OrgId,
                clock.Period,
                query.ProjectId,
                cursorCreatedAt,
                cursorId,
                query.Limit + 1,
                cancellationToken)
            .ConfigureAwait(false);
        var summary = UsageSummaryComposition.Compose(
            clock.Period, query.ProjectId, clock.DatabaseNow, rows, query.Limit);

        return summary is null
            ? Result.Failure<UsageSummaryDto>(ProjectNotFound)
            : Result.Success(summary);
    }
}

/// <summary>
/// The module's project-scope rule for the summary: a credential bound to another project, or a request already
/// narrowed to another project, is outside the scope and is refused 403 <c>project_scope_mismatch</c> before any row is
/// read. A project the org does not hold is the route's 404 instead (the statement's marker row), so the refusal is
/// never an oracle for another org's ids.
/// </summary>
internal static class UsageProjectScope
{
    /// <summary>Null when the addressed project is inside every scope the request carries.</summary>
    /// <param name="projectId">The project the request named.</param>
    /// <param name="credentialScope">The project the credential is bound to, if any.</param>
    /// <param name="requestProject">The project the request's tenant context is narrowed to, if any.</param>
    internal static Error? Project(Guid projectId, Guid? credentialScope, Guid? requestProject)
    {
        if (credentialScope is { } tokenScope && tokenScope != projectId)
        {
            return Error.Denied(ApiErrorCode.ProjectScopeMismatch, "the project is outside the credential's scope");
        }

        return requestProject is { } narrowed && narrowed != projectId
            ? Error.Denied(ApiErrorCode.ProjectScopeMismatch, "the project is outside the request's scope")
            : null;
    }
}

/// <summary>
/// The summary page's bounds and its two refusals (00 §9.1): the route parses <c>?limit=</c> through
/// <see cref="Limit(string?, out int)"/> and the handler repeats the range check, so an in-process caller that never
/// went through the route is bounded by the same numbers.
/// </summary>
internal static class UsageSummaryPaging
{
    /// <summary>00 §9.1 default when <c>limit</c> is absent.</summary>
    internal const int DefaultLimit = 50;

    /// <summary>00 §9.1 maximum <c>limit</c>.</summary>
    internal const int MaxLimit = 200;

    /// <summary>Reads <c>?limit=</c>: absent → <see cref="DefaultLimit"/>; anything else outside 1..200 → 422.</summary>
    /// <param name="raw">The raw query value, or null when the caller sent none.</param>
    /// <param name="limit">The page size to use; <see cref="DefaultLimit"/> when the value is refused.</param>
    internal static Error? Limit(string? raw, out int limit)
    {
        limit = DefaultLimit;

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return LimitInvalid();
        }

        limit = parsed;
        return LimitError(parsed);
    }

    /// <summary>The range half of the same rule, for a page size the route already parsed.</summary>
    /// <param name="limit">The page size to check.</param>
    internal static Error? LimitError(int limit) => limit is >= 1 and <= MaxLimit ? null : LimitInvalid();

    /// <summary>422: <c>limit</c> is not an integer between 1 and 200 (00 §9.1).</summary>
    internal static Error LimitInvalid() => Error.Invalid(
        string.Create(CultureInfo.InvariantCulture, $"limit must be an integer between 1 and {MaxLimit} (00 §9.1)"),
        [new ErrorDetail("limit", "out_of_range")]);

    /// <summary>422: the cursor is not a value this endpoint issued (00 §9.1).</summary>
    internal static Error CursorInvalid => Error.Invalid(
        "the cursor is not a page continuation this endpoint issued",
        [new ErrorDetail("cursor", "malformed")]);
}
