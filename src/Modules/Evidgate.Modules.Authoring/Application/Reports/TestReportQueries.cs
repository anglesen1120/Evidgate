using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.Modules.Authoring.Application.Queries;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Mediator;

namespace Evidgate.Modules.Authoring.Application.Reports;

/// <summary>One newest-first page of a project's reports (<c>GET …/authoring/reports</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Cursor">The opaque cursor of the previous page, or null for the first page.</param>
/// <param name="Limit">The page size the route already bounded.</param>
/// <param name="RequestError">The first edge validation error, answered after authorization.</param>
internal sealed record ListTestReportsQuery(Guid ProjectId, string? Cursor, int Limit, Error? RequestError = null)
    : IQuery<Result<TestReportListResponse>>;

/// <summary>Handler of <see cref="ListTestReportsQuery"/>: summaries only, never a payload.</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, whose credential may be narrowed to one project.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="store">The report table.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class ListTestReportsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    TestReportStore store) : IQueryHandler<ListTestReportsQuery, Result<TestReportListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<TestReportListResponse>> Handle(ListTestReportsQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ListTestReportsQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<TestReportListResponse>(scopeError);
        }

        if (query.RequestError is { } requestError)
        {
            return Result.Failure<TestReportListResponse>(requestError);
        }

        DateTimeOffset? afterCreatedAt = null;
        Guid? afterId = null;
        if (query.Cursor is not null)
        {
            if (!KeysetCursor.TryDecodeDescending(query.Cursor, out var createdAt, out var id))
            {
                return Result.Failure<TestReportListResponse>(AuthoringErrors.Invalid("/cursor", "invalid", "the cursor is not one this list issued"));
            }

            afterCreatedAt = createdAt;
            afterId = id;
        }

        if (await RequirementGate.RefusalAsync(db, scopes, principals, tenant, query.ProjectId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Result.Failure<TestReportListResponse>(refusal);
        }

        var rows = await store.ListAsync(scopes.Require().OrgId, query.ProjectId, afterCreatedAt, afterId, query.Limit + 1, cancellationToken).ConfigureAwait(false);
        var page = rows.Take(query.Limit)
            .Select(row => new TestReportSummaryDto(row.Id, row.JobId, row.ScopeKind, row.Language, row.ContentHash, RequirementTime.Utc(row.CreatedAt)))
            .ToList();
        var next = rows.Count > query.Limit
            ? KeysetCursor.EncodeDescending(RequirementTime.Utc(rows[query.Limit - 1].CreatedAt), rows[query.Limit - 1].Id)
            : null;

        return Result.Success(new TestReportListResponse(page, next));
    }
}

/// <summary>One report with its snapshot (<c>GET …/authoring/reports/{r}</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="ReportId">The report from the route.</param>
internal sealed record GetTestReportQuery(Guid ProjectId, Guid ReportId) : IQuery<Result<TestReportDetailDto>>;

/// <summary>Handler of <see cref="GetTestReportQuery"/>; a report of another project of the tenant answers 404 like a missing one.</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, whose credential may be narrowed to one project.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="store">The report table.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class GetTestReportQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    TestReportStore store) : IQueryHandler<GetTestReportQuery, Result<TestReportDetailDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<TestReportDetailDto>> Handle(GetTestReportQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(GetTestReportQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (await RequirementGate.RefusalAsync(db, scopes, principals, tenant, query.ProjectId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Result.Failure<TestReportDetailDto>(refusal);
        }

        var row = await store.GetAsync(scopes.Require().OrgId, query.ProjectId, query.ReportId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure<TestReportDetailDto>(AuthoringErrors.NotFound("the report does not exist in this project"));
        }

        return Result.Success(new TestReportDetailDto(
            row.Id,
            row.JobId,
            row.ScopeKind,
            row.Language,
            row.ContentHash,
            RequirementTime.Utc(row.CreatedAt),
            row.SchemaVersion,
            JsonSerializer.Deserialize<List<Guid>>(row.SelectedIds) ?? [],
            JsonSerializer.Deserialize<List<string>>(row.Sections) ?? [],
            JsonSerializer.Deserialize<JsonElement>(row.Payload)));
    }
}

/// <summary>Where a report job stands (<c>GET …/authoring/reports/jobs/{j}</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="JobId">The job reference the create answered with.</param>
internal sealed record GetTestReportJobQuery(Guid ProjectId, Guid JobId) : IQuery<Result<TestReportJobDto>>;

/// <summary>Handler of <see cref="GetTestReportJobQuery"/>: the published report wins, else the queue row tells the state.</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, whose credential may be narrowed to one project.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="store">The report table and the queue view.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class GetTestReportJobQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    TestReportStore store) : IQueryHandler<GetTestReportJobQuery, Result<TestReportJobDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<TestReportJobDto>> Handle(GetTestReportJobQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(GetTestReportJobQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (await RequirementGate.RefusalAsync(db, scopes, principals, tenant, query.ProjectId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Result.Failure<TestReportJobDto>(refusal);
        }

        var orgId = scopes.Require().OrgId;
        if (await store.ReportOfJobAsync(orgId, query.ProjectId, query.JobId, cancellationToken).ConfigureAwait(false) is { } reportId)
        {
            return Result.Success(new TestReportJobDto(query.JobId, TestReportJobStates.Complete, reportId, null));
        }

        var task = await store.JobTaskAsync(orgId, query.ProjectId, query.JobId, cancellationToken).ConfigureAwait(false);

        return task is null
            ? Result.Failure<TestReportJobDto>(AuthoringErrors.NotFound("the report job does not exist in this project"))
            : Result.Success(TestReportJobStates.Map(query.JobId, task));
    }
}

/// <summary>The job states of the contract and how a queue row maps onto them.</summary>
internal static class TestReportJobStates
{
    internal const string Pending = "pending";
    internal const string Running = "running";
    internal const string Retry = "retry";
    internal const string Failed = "failed";
    internal const string Complete = "complete";

    /// <summary>The state of a job that published no report, from its queue row; the raw error text never leaves the queue.</summary>
    /// <param name="jobId">The job reference.</param>
    /// <param name="task">The queue row.</param>
    internal static TestReportJobDto Map(Guid jobId, TestReportJobTaskRow task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!task.Dispatched)
        {
            return new TestReportJobDto(jobId, Pending, null, null);
        }

        return task.TaskState switch
        {
            null or "queued" => new TestReportJobDto(jobId, Pending, null, null),
            "active" => new TestReportJobDto(jobId, Running, null, null),
            "retry" => new TestReportJobDto(jobId, Retry, null, null),
            _ => new TestReportJobDto(jobId, Failed, null, Reason(task.LastError)),
        };
    }

    // The queue stores "<ExceptionType>: <message head>"; the build's own failures lead their message with a code.
    private static string Reason(string? lastError) =>
        lastError is not null && lastError.Contains(": " + ReportBuildException.TooLarge, StringComparison.Ordinal)
            ? ReportBuildException.TooLarge
            : ReportBuildException.Failed;
}
