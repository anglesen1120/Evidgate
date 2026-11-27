using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application.Reports;

/// <summary>One summary row of the history.</summary>
internal sealed class TestReportRow
{
    public Guid Id { get; set; }

    public Guid JobId { get; set; }

    public string ScopeKind { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

/// <summary>One report with its snapshot; the JSON columns arrive as text.</summary>
internal sealed class TestReportDetailRow
{
    public Guid Id { get; set; }

    public Guid JobId { get; set; }

    public string ScopeKind { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public int SchemaVersion { get; set; }

    public string SelectedIds { get; set; } = "[]";

    public string Sections { get; set; } = "[]";

    public string Payload { get; set; } = "{}";
}

/// <summary>The queue state of a report job as the tenant view of outbox and task shows it.</summary>
internal sealed class TestReportJobTaskRow
{
    public bool Dispatched { get; set; }

    public string? TaskState { get; set; }

    public string? LastError { get; set; }
}

/// <summary>The scoped reads and the one write of <c>test_report</c>, inside the caller's open tenant scope.</summary>
/// <param name="db">The tenant-bound database of the open scope.</param>
internal sealed class TestReportStore(ITenantDb db)
{
    /// <summary>Publishes a snapshot; false when the job already published one (a redelivery).</summary>
    /// <param name="payload">The job that asked for the report.</param>
    /// <param name="snapshot">The built snapshot.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<bool> InsertAsync(ReportJobPayload payload, ReportSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(snapshot);

        var rows = await db.ExecuteAsync(
            TestReportSql.Insert,
            new
            {
                id = Guid.CreateVersion7(),
                org_id = payload.OrgId,
                project_id = payload.ProjectId,
                job_id = payload.JobId,
                scope_kind = payload.ScopeKind,
                selected_ids = JsonSerializer.Serialize(payload.SelectedIds),
                sections = JsonSerializer.Serialize(payload.Sections),
                language = payload.Language,
                schema_version = TestReportLimits.SchemaVersion,
                pinned_versions = snapshot.PinnedVersions,
                payload = snapshot.CanonicalPayload,
                content_hash = snapshot.ContentHash,
                created_by = payload.CreatedBy,
            },
            cancellationToken).ConfigureAwait(false);

        return rows == 1;
    }

    /// <summary>The report a job published, or null.</summary>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="projectId">The project of the route.</param>
    /// <param name="jobId">The job reference.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<Guid?> ReportOfJobAsync(Guid orgId, Guid projectId, Guid jobId, CancellationToken cancellationToken) =>
        db.ScalarAsync<Guid?>(TestReportSql.ByJob, new { org_id = orgId, project_id = projectId, job_id = jobId }, cancellationToken);

    /// <summary>One newest-first page; ask for one row more than the page to learn whether a next one exists.</summary>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="projectId">The project of the route.</param>
    /// <param name="afterCreatedAt">The keyset position of the previous page, or null.</param>
    /// <param name="afterId">The keyset position of the previous page, or null.</param>
    /// <param name="limit">The row count to read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<IReadOnlyList<TestReportRow>> ListAsync(
        Guid orgId,
        Guid projectId,
        DateTimeOffset? afterCreatedAt,
        Guid? afterId,
        int limit,
        CancellationToken cancellationToken) =>
        db.QueryAsync<TestReportRow>(
            TestReportSql.List,
            new { org_id = orgId, project_id = projectId, after_created_at = afterCreatedAt, after_id = afterId, limit },
            cancellationToken);

    /// <summary>One report with its snapshot, or null.</summary>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="projectId">The project of the route.</param>
    /// <param name="reportId">The report from the route.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<TestReportDetailRow?> GetAsync(Guid orgId, Guid projectId, Guid reportId, CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<TestReportDetailRow>(
            TestReportSql.Get,
            new { org_id = orgId, project_id = projectId, id = reportId },
            cancellationToken);

    /// <summary>The queue row of a job, or null when this organization queued no such job.</summary>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="projectId">The project of the route.</param>
    /// <param name="jobId">The job reference.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<TestReportJobTaskRow?> JobTaskAsync(Guid orgId, Guid projectId, Guid jobId, CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<TestReportJobTaskRow>(
            TestReportSql.JobTask,
            new { org_id = orgId, idempotency_key = TestReportTasks.OutboxKey(projectId, jobId) },
            cancellationToken);
}
