using System.Text.Json.Nodes;

namespace Evidgate.Modules.Authoring.Application.Reports.Sections;

/// <summary>
/// The test plan: what the report covers, which requirements are in it, how many items and cases each priority holds,
/// and the strategy and environment text exactly as the author wrote it (plain text; a renderer must not interpret it).
/// </summary>
internal sealed class TestPlanSectionBuilder : IReportSectionBuilder
{
    public string SectionId => ReportSectionIds.TestPlan;

    public JsonNode Build(ReportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var labels = ReportLabels.For(source.Request.Language);
        return new JsonObject
        {
            ["title"] = labels.PlanTitle,
            ["scope"] = Scope(source, labels),
            ["requirements"] = new JsonObject
            {
                ["label"] = labels.Requirements,
                ["items"] = ReportJson.Array(ReportOrder.Requirements(source.Requirements).Select(Requirement)),
            },
            ["priorities"] = new JsonObject { ["label"] = labels.Priorities, ["items"] = Priorities(source) },
            ["strategy"] = FreeText(labels.Strategy, source.Request.Strategy, labels),
            ["environment"] = FreeText(labels.Environment, source.Request.Environment, labels),
        };
    }

    private static JsonNode Requirement(ReportRequirement requirement)
    {
        var row = ReportJson.RequirementRef(requirement);
        row["kind"] = requirement.Kind;
        return row;
    }

    private static JsonObject Scope(ReportSource source, ReportLabels labels)
    {
        var titles = source.Nodes.ToDictionary(node => node.Id, node => node.Title);
        foreach (var requirement in source.Requirements)
        {
            titles.TryAdd(requirement.Id, requirement.Title);
        }

        var selected = source.Request.SelectedIds.Select(id => (JsonNode?)new JsonObject
        {
            ["id"] = id.ToString("D"),
            ["title"] = titles.GetValueOrDefault(id),
        });

        return new JsonObject
        {
            ["label"] = labels.Scope,
            ["kind"] = source.Request.ScopeKind,
            ["kind_label"] = labels.ScopeKind(source.Request.ScopeKind),
            ["selected"] = ReportJson.Array(selected),
        };
    }

    private static JsonArray Priorities(ReportSource source)
    {
        var items = source.Items.GroupBy(item => item.Priority, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var cases = source.Cases.GroupBy(testCase => testCase.Priority, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return ReportJson.Array(items.Keys.Union(cases.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(priority => (JsonNode?)new JsonObject
        {
            ["priority"] = priority,
            ["checklist_items"] = items.GetValueOrDefault(priority),
            ["cases"] = cases.GetValueOrDefault(priority),
        }));
    }

    // An empty text is shown as "not provided" by a label, so a renderer never has to invent the wording.
    private static JsonObject FreeText(string label, string? text, ReportLabels labels) => new()
    {
        ["label"] = label,
        ["text"] = text,
        ["empty_note"] = text is null ? labels.NotProvided : null,
    };
}
