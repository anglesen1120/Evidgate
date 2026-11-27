using System.Text.Json.Nodes;

namespace Evidgate.Modules.Authoring.Application.Reports.Sections;

/// <summary>
/// The test cases with their steps, data and expected results, grouped by the feature their checklist item belongs to.
/// Each case also names the requirements it is linked to, current and stale apart, so a reader sees coverage per case.
/// </summary>
internal sealed class TestCasesSectionBuilder : IReportSectionBuilder
{
    public string SectionId => ReportSectionIds.TestCases;

    public JsonNode Build(ReportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var labels = ReportLabels.For(source.Request.Language);
        var matrix = TraceabilityMatrix.For(source);
        var features = new FeatureTree(source.Nodes);
        var featureOfItem = source.Items.ToDictionary(item => item.Id, item => item.FeatureNodeId);

        var groups = matrix.Cases
            .GroupBy(testCase => featureOfItem.TryGetValue(testCase.ChecklistItemId, out var feature) && features.Has(feature) ? feature : Guid.Empty)
            .OrderBy(group => group.Key == Guid.Empty)
            .ThenBy(group => features.Path(group.Key), StringComparer.Ordinal)
            .ThenBy(group => group.Key)
            .Select(group => (JsonNode?)Group(group.Key, group, features, matrix, labels));

        return new JsonObject
        {
            ["title"] = labels.CasesTitle,
            ["labels"] = new JsonObject
            {
                ["precondition"] = labels.Precondition,
                ["step"] = labels.Step,
                ["action"] = labels.Action,
                ["data"] = labels.Data,
                ["expected"] = labels.Expected,
                ["linked_requirements"] = labels.LinkedRequirements,
                ["stale_requirements"] = labels.StaleRequirements,
            },
            ["groups"] = ReportJson.Array(groups),
        };
    }

    private static JsonObject Group(Guid feature, IEnumerable<ReportCase> cases, FeatureTree features, TraceabilityMatrix matrix, ReportLabels labels) => new()
    {
        ["feature_id"] = feature == Guid.Empty ? null : feature.ToString("D"),
        ["feature_title"] = feature == Guid.Empty ? labels.NoFeature : features.Title(feature),
        ["feature_path"] = feature == Guid.Empty ? null : features.Path(feature),
        ["cases"] = ReportJson.Array(cases.Select(testCase => (JsonNode?)Case(testCase, matrix))),
    };

    private static JsonObject Case(ReportCase testCase, TraceabilityMatrix matrix)
    {
        var row = ReportJson.CaseRef(testCase);
        row["description"] = testCase.Description;
        row["precondition"] = testCase.Precondition;
        row["priority"] = testCase.Priority;
        row["state"] = testCase.State;
        row["version_no"] = testCase.VersionNo;
        row["steps"] = ReportJson.Steps(testCase.Steps);
        row["requirements"] = ReportJson.Array(matrix.RequirementsOf(testCase, stale: false).Select(requirement => (JsonNode?)ReportJson.RequirementRef(requirement)));
        row["stale_requirements"] = ReportJson.Array(matrix.RequirementsOf(testCase, stale: true).Select(requirement => (JsonNode?)ReportJson.RequirementRef(requirement)));
        return row;
    }

    /// <summary>The feature nodes by id with their title and the path of titles above them; a parent loop stops at the node it returns to.</summary>
    private sealed class FeatureTree
    {
        private readonly Dictionary<Guid, ReportNode> _nodes;
        private readonly Dictionary<Guid, string> _paths = [];

        internal FeatureTree(IEnumerable<ReportNode> nodes) => _nodes = nodes.ToDictionary(node => node.Id);

        internal bool Has(Guid id) => _nodes.ContainsKey(id);

        internal string Title(Guid id) => _nodes[id].Title;

        internal string Path(Guid id)
        {
            if (id == Guid.Empty || !_nodes.ContainsKey(id))
            {
                return string.Empty;
            }

            if (_paths.TryGetValue(id, out var known))
            {
                return known;
            }

            var titles = new List<string>();
            var seen = new HashSet<Guid>();
            for (Guid? at = id; at is { } current && _nodes.TryGetValue(current, out var node) && seen.Add(current); at = node.ParentId)
            {
                titles.Add(node.Title);
            }

            titles.Reverse();
            return _paths[id] = string.Join(" / ", titles);
        }
    }
}
