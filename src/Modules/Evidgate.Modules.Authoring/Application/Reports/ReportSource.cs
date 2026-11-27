using System.Text.Json;
using System.Text.Json.Nodes;

namespace Evidgate.Modules.Authoring.Application.Reports;

/// <summary>A feature node of the report's tree: the selected ones, what they hold and the path above them.</summary>
internal sealed record ReportNode(Guid Id, Guid? ParentId, string Kind, string Title, int Position);

/// <summary>A live checklist item with its display values.</summary>
internal sealed record ReportItem(Guid Id, Guid FeatureNodeId, string Title, string? Description, string Priority, string State);

/// <summary>A live manual case with its display values and the authoring version it was read at.</summary>
internal sealed record ReportCase(
    Guid Id,
    Guid ChecklistItemId,
    string Code,
    string Title,
    string? Description,
    string? Precondition,
    JsonElement Steps,
    string Priority,
    string State,
    int? VersionNo,
    string? ContentHash);

/// <summary>A live requirement read at its head version.</summary>
internal sealed record ReportRequirement(
    Guid Id,
    string Kind,
    string Title,
    string? ExternalRef,
    Guid? FeatureNodeId,
    Guid? ParentRequirementId,
    Guid VersionId,
    int VersionNo,
    string Body,
    string ContentHash);

/// <summary>A requirement link with the number of the version it pins, so a builder can tell a stale pin from a current one.</summary>
internal sealed record ReportLink(
    Guid Id,
    Guid RequirementId,
    Guid RequirementVersionId,
    int RequirementVersionNo,
    Guid? ChecklistItemId,
    Guid? ManualTestCaseId);

/// <summary>The latest recorded manual result of one case: <c>passed</c>, <c>failed</c>, <c>blocked</c> or <c>skipped</c>.</summary>
internal sealed record ReportManualResult(Guid CaseId, string Status);

/// <summary>The release decision a release-scoped report shows as recorded: <c>PASS</c>, <c>FAIL</c> or <c>INCONCLUSIVE</c> with its reason codes.</summary>
internal sealed record ReportReleaseDecision(string Decision, IReadOnlyList<string> Reasons);

/// <summary>
/// Everything a section builder may read, taken at one point of the database: a plain value with no connection, clock
/// or randomness, so the same source and request always build the same sections.
/// </summary>
/// <param name="Request">What the author asked for.</param>
/// <param name="Nodes">The tree the report covers, ascending by id.</param>
/// <param name="Items">Live checklist items, ascending by id.</param>
/// <param name="Cases">Live manual cases, ascending by id.</param>
/// <param name="Requirements">Live requirements at their head, ascending by id.</param>
/// <param name="Links">Links between them, ascending by id.</param>
internal sealed record ReportSource(
    ReportRequest Request,
    IReadOnlyList<ReportNode> Nodes,
    IReadOnlyList<ReportItem> Items,
    IReadOnlyList<ReportCase> Cases,
    IReadOnlyList<ReportRequirement> Requirements,
    IReadOnlyList<ReportLink> Links)
{
    /// <summary>Latest manual result per case, ascending by case id; empty until a manual run exists.</summary>
    internal IReadOnlyList<ReportManualResult> ManualResults { get; init; } = [];

    /// <summary>The bound release's recorded decision; null when none exists or aggregation is pending.</summary>
    internal ReportReleaseDecision? ReleaseDecision { get; init; }
}

/// <summary>
/// One section of a report. The job runs the builders the request selected, in the request's order, and the registry
/// holds them by id, so a new section is a new registration and no change to the job.
/// </summary>
internal interface IReportSectionBuilder
{
    /// <summary>The section id a request names; unique across the registered builders.</summary>
    string SectionId { get; }

    /// <summary>Builds the section's display data from the source alone; it must be deterministic and place no call.</summary>
    /// <param name="source">The source the job read.</param>
    JsonNode Build(ReportSource source);
}

/// <summary>The registered builders by section id; a section that is not here is refused at create and never published.</summary>
internal sealed class ReportSectionRegistry
{
    private readonly Dictionary<string, IReportSectionBuilder> _builders;

    /// <summary>Indexes the container's builders; two builders for one id is a composition error.</summary>
    /// <param name="builders">Every registered builder.</param>
    public ReportSectionRegistry(IEnumerable<IReportSectionBuilder> builders)
    {
        ArgumentNullException.ThrowIfNull(builders);

        _builders = builders.ToDictionary(builder => builder.SectionId, StringComparer.Ordinal);
    }

    /// <summary>The builder of <paramref name="sectionId"/>, or null.</summary>
    /// <param name="sectionId">A section id.</param>
    public IReportSectionBuilder? Find(string sectionId) => _builders.GetValueOrDefault(sectionId);

    /// <summary>The index of the first requested section no builder serves, or null when all are served.</summary>
    /// <param name="sections">The section ids of a request.</param>
    public int? FirstUnsupported(IReadOnlyList<string> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        for (var index = 0; index < sections.Count; index++)
        {
            if (!_builders.ContainsKey(sections[index]))
            {
                return index;
            }
        }

        return null;
    }
}

/// <summary>A build that cannot publish, with the stable code the job outcome reports as its reason.</summary>
internal sealed class ReportBuildException : Exception
{
    /// <summary>The source holds more than one report may.</summary>
    internal const string TooLarge = "report_too_large";

    /// <summary>Any other refusal of the build itself.</summary>
    internal const string Failed = "build_failed";

    /// <summary>Creates the failure; the message's first line is what the queue stores, so it carries the code first.</summary>
    /// <param name="code"><see cref="TooLarge"/> or <see cref="Failed"/>.</param>
    /// <param name="detail">A secret-free, customer-text-free summary.</param>
    internal ReportBuildException(string code, string detail)
        : base(code + ": " + detail)
    {
    }
}
