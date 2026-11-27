using System.Text.Json;
using System.Text.Json.Nodes;

namespace Evidgate.Modules.Authoring.Application.Reports.Sections;

/// <summary>The stable section ids a create request may name; each is served by one builder.</summary>
internal static class ReportSectionIds
{
    internal const string TestPlan = "test_plan";
    internal const string TestCases = "test_cases";
    internal const string Traceability = "traceability";
    internal const string Execution = "execution";
}

/// <summary>Small JSON constructors shared by the builders; every id is written in its canonical <c>D</c> form.</summary>
internal static class ReportJson
{
    internal static JsonArray Array(IEnumerable<JsonNode?> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(item);
        }

        return array;
    }

    internal static JsonObject RequirementRef(ReportRequirement requirement) => new()
    {
        ["id"] = requirement.Id.ToString("D"),
        ["ref"] = requirement.ExternalRef,
        ["title"] = requirement.Title,
        ["version_no"] = requirement.VersionNo,
    };

    internal static JsonObject CaseRef(ReportCase testCase) => new()
    {
        ["id"] = testCase.Id.ToString("D"),
        ["code"] = testCase.Code,
        ["title"] = testCase.Title,
    };

    internal static JsonObject Counted(string key, string label, int count) =>
        new() { ["key"] = key, ["label"] = label, ["count"] = count };

    /// <summary>The steps of a case's steps document as display rows; a document with no steps array gives none.</summary>
    /// <param name="document">The case's <c>steps</c> value.</param>
    internal static JsonArray Steps(JsonElement document)
    {
        var rows = new JsonArray();
        if (document.ValueKind != JsonValueKind.Object
            || !document.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var step in steps.EnumerateArray())
        {
            rows.Add(new JsonObject
            {
                ["no"] = StepNumber(step),
                ["action"] = Text(step, "action"),
                ["data"] = Text(step, "data"),
                ["expected"] = Text(step, "expected"),
            });
        }

        return rows;
    }

    private static JsonValue? StepNumber(JsonElement step) =>
        step.ValueKind == JsonValueKind.Object
        && step.TryGetProperty("no", out var number)
        && number.ValueKind == JsonValueKind.Number
        && number.TryGetInt32(out var value)
            ? JsonValue.Create(value)
            : null;

    private static string? Text(JsonElement step, string name) =>
        step.ValueKind == JsonValueKind.Object
        && step.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>The one display order of requirements and cases, so every section lists them the same way.</summary>
internal static class ReportOrder
{
    internal static IReadOnlyList<ReportRequirement> Requirements(IEnumerable<ReportRequirement> requirements) =>
    [
        .. requirements
            .OrderBy(requirement => requirement.ExternalRef ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(requirement => requirement.Title, StringComparer.Ordinal)
            .ThenBy(requirement => requirement.Id),
    ];

    internal static IReadOnlyList<ReportCase> Cases(IEnumerable<ReportCase> cases) =>
    [
        .. cases.OrderBy(testCase => testCase.Code, StringComparer.Ordinal).ThenBy(testCase => testCase.Id),
    ];
}
