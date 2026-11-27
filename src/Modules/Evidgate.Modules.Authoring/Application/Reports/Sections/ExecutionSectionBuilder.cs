using System.Text.Json.Nodes;

namespace Evidgate.Modules.Authoring.Application.Reports.Sections;

/// <summary>
/// What was executed, from pinned inputs only. A release scope shows the decision the release recorded, or says plainly
/// that none is recorded; any other scope counts manual results and says there is no release decision. Before any manual
/// result exists the section says so instead of showing zero failures, because an empty run is not a passing one.
/// </summary>
internal sealed class ExecutionSectionBuilder : IReportSectionBuilder
{
    private static readonly string[] Decisions = ["PASS", "FAIL", "INCONCLUSIVE"];

    public string SectionId => ReportSectionIds.Execution;

    public JsonNode Build(ReportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var labels = ReportLabels.For(source.Request.Language);
        var section = new JsonObject { ["title"] = labels.ExecutionTitle };
        if (source.Request.ScopeKind == ReportScopeKinds.Release)
        {
            section["release_decision"] = ReleaseDecision(source.ReleaseDecision, labels);
        }
        else
        {
            // Only a release shows a decision; for any other scope a manual failure is a count, never a policy outcome.
            section["release_decision"] = new JsonObject { ["label"] = labels.ReleaseDecision, ["state"] = "not_applicable", ["note"] = labels.NoReleaseDecision };
        }

        section["manual"] = Manual(source, labels);
        return section;
    }

    private static JsonObject ReleaseDecision(ReportReleaseDecision? decision, ReportLabels labels)
    {
        if (decision is null)
        {
            return new JsonObject { ["label"] = labels.ReleaseDecision, ["state"] = "absent", ["note"] = labels.DecisionAbsent };
        }

        if (Array.IndexOf(Decisions, decision.Decision) < 0)
        {
            throw new ReportBuildException(ReportBuildException.Failed, "the release decision has an unknown value");
        }

        return new JsonObject
        {
            ["label"] = labels.ReleaseDecision,
            ["state"] = "recorded",
            ["decision"] = decision.Decision,
            ["reasons_label"] = labels.Reasons,
            ["reasons"] = ReportJson.Array(decision.Reasons.Select(reason => (JsonNode?)reason)),
        };
    }

    private static JsonObject Manual(ReportSource source, ReportLabels labels)
    {
        var caseIds = source.Cases.Select(testCase => testCase.Id).ToHashSet();
        var latest = new Dictionary<Guid, string>();
        foreach (var result in source.ManualResults.Where(result => caseIds.Contains(result.CaseId)))
        {
            latest[result.CaseId] = labels.Status(result.Status) is null || result.Status == ManualStatuses.NotExecuted
                ? throw new ReportBuildException(ReportBuildException.Failed, "a manual result has an unknown status")
                : result.Status;
        }

        // No result in scope: there are no counts at all, so no "0 failed" can be read from an empty run.
        if (latest.Count == 0)
        {
            return new JsonObject { ["label"] = labels.ManualResults, ["state"] = "none", ["note"] = labels.NoManualExecution };
        }

        var counts = latest.Values.GroupBy(status => status, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        counts[ManualStatuses.NotExecuted] = caseIds.Count - latest.Count;

        return new JsonObject
        {
            ["label"] = labels.ManualResults,
            ["state"] = "recorded",
            ["total_cases"] = caseIds.Count,
            ["counts"] = ReportJson.Array(ManualStatuses.Ordered.Select(status =>
                (JsonNode?)ReportJson.Counted(status, labels.Status(status)!, counts.GetValueOrDefault(status)))),
        };
    }
}
