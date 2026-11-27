using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application.Reports;

/// <summary>Reads what a report is built from, inside the caller's open tenant scope.</summary>
/// <param name="db">The tenant-bound database of the open scope.</param>
internal sealed class TestReportSourceReader(ITenantDb db)
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>The stored-byte cap of one source; a property so a test can lower it instead of seeding megabytes.</summary>
    internal long MaxSourceBytes { get; init; } = TestReportLimits.MaxSourceBytes;

    /// <summary>How many of the ids are live items of the scope kind in the project.</summary>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="projectId">The project of the route.</param>
    /// <param name="request">The validated request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<int> CountLiveAsync(Guid orgId, Guid projectId, ReportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sql = request.ScopeKind == ReportScopeKinds.FeatureNodes
            ? TestReportSourceSql.CountFeatureNodes
            : TestReportSourceSql.CountRequirements;

        return await db.ScalarAsync<int>(
            sql,
            new { org_id = orgId, project_id = projectId, ids = request.SelectedIds.ToArray() },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the whole source in one statement; a source over a cap throws <see cref="ReportBuildException"/>.</summary>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="projectId">The project of the report.</param>
    /// <param name="request">The validated request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<ReportSource> ReadAsync(Guid orgId, Guid projectId, ReportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sql = request.ScopeKind switch
        {
            ReportScopeKinds.FeatureNodes => TestReportSourceSql.ReadFeatureNodes,
            ReportScopeKinds.Requirements => TestReportSourceSql.ReadRequirements,
            _ => throw new ReportBuildException(ReportBuildException.Failed, "the scope kind has no source"),
        };

        var row = await db.QuerySingleOrDefaultAsync<SourceRow>(
            sql,
            new
            {
                org_id = orgId,
                project_id = projectId,
                ids = request.SelectedIds.ToArray(),
                max_rows = TestReportLimits.MaxRows,
                max_links = TestReportLimits.MaxLinks,
                max_bytes = MaxSourceBytes,
            },
            cancellationToken).ConfigureAwait(false)
            ?? throw new ReportBuildException(ReportBuildException.Failed, "the source read returned no row");

        // The statement leaves its arrays empty over the byte cap, so the size outcome is explicit, never a truncation.
        if (row.Bytes > MaxSourceBytes)
        {
            throw new ReportBuildException(ReportBuildException.TooLarge, "the source is larger than one report may hold");
        }

        return new ReportSource(
            request,
            Rows<ReportNode>(row.Nodes, TestReportLimits.MaxRows),
            Rows<ReportItem>(row.Items, TestReportLimits.MaxRows),
            Rows<ReportCase>(row.Cases, TestReportLimits.MaxRows),
            Rows<ReportRequirement>(row.Requirements, TestReportLimits.MaxRows),
            Rows<ReportLink>(row.Links, TestReportLimits.MaxLinks));
    }

    private static List<T> Rows<T>(string json, int cap)
    {
        var rows = JsonSerializer.Deserialize<List<T>>(json, Wire) ?? [];

        return rows.Count > cap
            ? throw new ReportBuildException(ReportBuildException.TooLarge, "the source holds more rows than one report may")
            : rows;
    }

    private sealed class SourceRow
    {
        public long Bytes { get; set; }

        public string Nodes { get; set; } = "[]";

        public string Items { get; set; } = "[]";

        public string Cases { get; set; } = "[]";

        public string Requirements { get; set; } = "[]";

        public string Links { get; set; } = "[]";
    }
}
