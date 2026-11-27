using System.Text.Json.Nodes;

namespace Evidgate.Modules.Authoring.Application.Reports.Sections;

/// <summary>
/// The requirement and case matrix, read from both sides. Uncovered requirements, unlinked cases and stale links are
/// three separate lists: a link pinned to a superseded requirement version never counts as coverage, and a case that only
/// has stale links is not called unlinked.
/// </summary>
internal sealed class TraceabilitySectionBuilder : IReportSectionBuilder
{
    public string SectionId => ReportSectionIds.Traceability;

    public JsonNode Build(ReportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var labels = ReportLabels.For(source.Request.Language);
        var matrix = TraceabilityMatrix.Create(source);
        var uncovered = matrix.Requirements.Where(requirement => !matrix.IsCovered(requirement)).ToList();
        var unlinked = matrix.Cases.Where(matrix.IsUnlinked).ToList();
        var staleLinks = matrix.Links.Where(link => link.Stale).ToList();

        return new JsonObject
        {
            ["title"] = labels.TraceabilityTitle,
            ["counts"] = new JsonObject
            {
                ["requirements"] = matrix.Requirements.Count,
                ["covered_requirements"] = matrix.Requirements.Count - uncovered.Count,
                ["uncovered_requirements"] = uncovered.Count,
                ["cases"] = matrix.Cases.Count,
                ["unlinked_cases"] = unlinked.Count,
                ["stale_links"] = staleLinks.Count,
            },
            ["labels"] = new JsonObject
            {
                ["covered"] = labels.Covered,
                ["uncovered_requirements"] = labels.UncoveredRequirements,
                ["unlinked_cases"] = labels.UnlinkedCases,
                ["stale_links"] = labels.StaleLinks,
                ["stale_note"] = labels.StaleNote,
                ["pinned_version"] = labels.PinnedVersion,
                ["current_version"] = labels.CurrentVersion,
                ["requirements"] = labels.Requirements,
                ["cases"] = labels.CasesTitle,
            },
            ["requirement_rows"] = ReportJson.Array(matrix.Requirements.Select(requirement => (JsonNode?)RequirementRow(requirement, matrix))),
            ["case_rows"] = ReportJson.Array(matrix.Cases.Select(testCase => (JsonNode?)CaseRow(testCase, matrix))),
            ["uncovered_requirements"] = ReportJson.Array(uncovered.Select(requirement => (JsonNode?)UncoveredRow(requirement, matrix))),
            ["unlinked_cases"] = ReportJson.Array(unlinked.Select(testCase => (JsonNode?)ReportJson.CaseRef(testCase))),
            ["stale_links"] = ReportJson.Array(staleLinks.Select(link => (JsonNode?)StaleRow(link))),
        };
    }

    private static JsonObject RequirementRow(ReportRequirement requirement, TraceabilityMatrix matrix) => new()
    {
        ["requirement"] = ReportJson.RequirementRef(requirement),
        ["status"] = matrix.IsCovered(requirement) ? "covered" : "uncovered",
        ["cases"] = ReportJson.Array(matrix.CasesOf(requirement, stale: false).Select(testCase => (JsonNode?)ReportJson.CaseRef(testCase))),
        ["items"] = ReportJson.Array(ItemOnlyLinks(requirement, matrix)),
        ["stale_cases"] = ReportJson.Array(matrix.CasesOf(requirement, stale: true).Select(testCase => (JsonNode?)ReportJson.CaseRef(testCase))),
    };

    private static JsonObject CaseRow(ReportCase testCase, TraceabilityMatrix matrix)
    {
        var current = matrix.RequirementsOf(testCase, stale: false);
        var stale = matrix.RequirementsOf(testCase, stale: true);
        return new JsonObject
        {
            ["case"] = ReportJson.CaseRef(testCase),
            ["status"] = matrix.IsUnlinked(testCase) ? "unlinked" : current.Count > 0 ? "linked" : "stale_only",
            ["requirements"] = ReportJson.Array(current.Select(requirement => (JsonNode?)ReportJson.RequirementRef(requirement))),
            ["stale_requirements"] = ReportJson.Array(stale.Select(requirement => (JsonNode?)ReportJson.RequirementRef(requirement))),
        };
    }

    private static JsonObject UncoveredRow(ReportRequirement requirement, TraceabilityMatrix matrix) => new()
    {
        ["requirement"] = ReportJson.RequirementRef(requirement),
        ["has_stale_link"] = matrix.ByRequirement[requirement.Id].Any(link => link.Stale),
    };

    private static JsonObject StaleRow(TraceLink link) => new()
    {
        ["requirement"] = ReportJson.RequirementRef(link.Requirement),
        ["pinned_version_no"] = link.PinnedVersionNo,
        ["current_version_no"] = link.Requirement.VersionNo,
        ["case"] = link.Case is null ? null : ReportJson.CaseRef(link.Case),
        ["item"] = link.Item is null ? null : new JsonObject { ["id"] = link.Item.Id.ToString("D"), ["title"] = link.Item.Title },
    };

    // A current link to a checklist item that holds no case still covers the requirement; it is listed by item.
    private static IEnumerable<JsonNode?> ItemOnlyLinks(ReportRequirement requirement, TraceabilityMatrix matrix) =>
        matrix.ByRequirement[requirement.Id]
            .Where(link => !link.Stale && link.Case is null && link.Item is not null)
            .Select(link => link.Item!)
            .DistinctBy(item => item.Id)
            .OrderBy(item => item.Title, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .Select(item => (JsonNode?)new JsonObject { ["id"] = item.Id.ToString("D"), ["title"] = item.Title });
}
