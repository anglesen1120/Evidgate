using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Application.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Modules.Authoring.Application.Reports;

/// <summary>
/// The task a create enqueues: everything the build needs, by id and by the author's own words. It stays under the
/// queue's 64 KiB payload cap at the request limits (1000 ids, two 2000 character texts).
/// </summary>
/// <param name="OrgId">The organization the job runs under.</param>
/// <param name="ProjectId">The project of the report.</param>
/// <param name="JobId">The job reference the create answered with.</param>
/// <param name="CreatedBy">The membership that asked, or null.</param>
/// <param name="ScopeKind">The validated scope kind.</param>
/// <param name="SelectedIds">The validated ids.</param>
/// <param name="Sections">The validated section ids.</param>
/// <param name="Language"><c>vi</c> or <c>en</c>.</param>
/// <param name="Strategy">Free text, or null.</param>
/// <param name="Environment">Free text, or null.</param>
internal sealed record ReportJobPayload(
    Guid OrgId,
    Guid ProjectId,
    Guid JobId,
    Guid? CreatedBy,
    string ScopeKind,
    IReadOnlyList<Guid> SelectedIds,
    IReadOnlyList<string> Sections,
    string Language,
    string? Strategy,
    string? Environment);

/// <summary>The names and the enqueue of the report build task.</summary>
internal static class TestReportTasks
{
    /// <summary>The handler key of the build task.</summary>
    internal const string TaskType = "authoring_test_report_build";

    /// <summary>The aggregate the outbox row belongs to; its id is the job id.</summary>
    internal const string AggregateType = "authoring_test_report";

    // Raw UTF-8 keeps a non-ASCII text at 2-4 bytes a character instead of six, so the payload cap holds at the limits.
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The outbox dedupe key. The project is inside it, so the job route finds only this project's job and a job id
    /// from another project matches nothing.
    /// </summary>
    /// <param name="projectId">The project of the report.</param>
    /// <param name="jobId">The job reference.</param>
    internal static string OutboxKey(Guid projectId, Guid jobId) =>
        string.Create(CultureInfo.InvariantCulture, $"authoring:report:{projectId:D}:{jobId:D}");

    /// <summary>The wire form of the payload.</summary>
    /// <param name="payload">The task payload.</param>
    internal static string ToJson(ReportJobPayload payload) => JsonSerializer.Serialize(payload, Wire);

    /// <summary>Writes the outbox row in the caller's scope transaction, so a rolled back create queues nothing.</summary>
    /// <param name="outbox">The outbox writer.</param>
    /// <param name="payload">The task payload.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static Task<Guid> EnqueueAsync(IOutboxWriter outbox, ReportJobPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(payload);

        return outbox.EnqueueAsync(
            TaskType,
            ToJson(payload),
            AggregateType,
            payload.JobId,
            OutboxKey(payload.ProjectId, payload.JobId),
            cancellationToken);
    }
}

/// <summary>
/// Builds and publishes one snapshot inside the task's tenant scope: it reads the source, builds the sections and
/// inserts the report, all in the scope's one transaction, so an interrupted run leaves no row. No call leaves the
/// database, and a failure is thrown, never returned, so the queue retries or dead-letters it.
/// </summary>
/// <param name="db">The tenant-bound database of the open scope.</param>
/// <param name="reader">The source read.</param>
/// <param name="store">The report table.</param>
/// <param name="registry">The registered section builders.</param>
internal sealed class TestReportBuildJob(
    ITenantDb db,
    TestReportSourceReader reader,
    TestReportStore store,
    ReportSectionRegistry registry)
{
    /// <summary>Runs one attempt.</summary>
    /// <param name="payload">The task payload.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task RunAsync(ReportJobPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (await store.ReportOfJobAsync(payload.OrgId, payload.ProjectId, payload.JobId, cancellationToken).ConfigureAwait(false) is not null)
        {
            // A redelivery after the commit: the report is already published.
            return;
        }

        if (await AuthoringProjectProbe.MissingAsync(db, payload.OrgId, payload.ProjectId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new ReportBuildException(ReportBuildException.Failed, "the project is not live");
        }

        var request = new ReportRequest(
            payload.ScopeKind,
            payload.SelectedIds,
            payload.Sections,
            payload.Language,
            payload.Strategy,
            payload.Environment);
        var source = await reader.ReadAsync(payload.OrgId, payload.ProjectId, request, cancellationToken).ConfigureAwait(false);
        var snapshot = ReportSnapshotBuilder.Build(source, registry, cancellationToken);

        // false means a concurrent delivery published first; both built from one source kind, so either row is the report.
        await store.InsertAsync(payload, snapshot, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The queue handler of <see cref="TestReportTasks.TaskType"/>; the consumer opened the tenant scope and commits it on return.</summary>
/// <param name="serviceScopes">Creates the scope the scoped build job lives in.</param>
internal sealed class TestReportTaskHandler(IServiceScopeFactory serviceScopes) : ITaskHandler<ReportJobPayload>
{
    /// <inheritdoc />
    public string TaskType => TestReportTasks.TaskType;

    /// <inheritdoc />
    public async ValueTask HandleAsync(ReportJobPayload payload, ITenantScope? scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (scope is null || payload.OrgId != scope.OrgId)
        {
            // The scope's org is the only identity the work may run under; a payload naming another is a misrouted row.
            throw new InvalidOperationException("the report task has no tenant scope or names another organization");
        }

        await using var services = serviceScopes.CreateAsyncScope();
        await services.ServiceProvider.GetRequiredService<TestReportBuildJob>()
            .RunAsync(payload, cancellationToken).ConfigureAwait(false);
    }
}
