using System.Text.Json;
using Shouldly;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Reads an <c>EXPLAIN (FORMAT JSON)</c> document as its flat node list, so a test asserts on node types,
/// index names and relation names without depending on the plan's shape or nesting. Every assertion in
/// <see cref="ListRunAttemptsTests"/>'s plan cases goes through this type, so the page, the aggregate and
/// the status counts are all read the same way.
/// </summary>
internal static class PlanJson
{
    /// <summary>Every plan node of the first statement in the document, parents before children.</summary>
    internal static IReadOnlyList<JsonElement> Nodes(string json)
    {
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetArrayLength().ShouldBe(1, json);

        return Flatten(document.RootElement[0].GetProperty("Plan")).ToList();
    }

    /// <summary>True when an index scan of <paramref name="index"/> (plain, only or bitmap) is in the plan.</summary>
    internal static bool UsesIndex(IReadOnlyList<JsonElement> nodes, string index) =>
        nodes.Any(node => Text(node, "Index Name") == index
            && Text(node, "Node Type") is "Index Scan" or "Index Only Scan" or "Bitmap Index Scan");

    /// <summary>True when <paramref name="relation"/> is read by a sequential scan anywhere in the plan.</summary>
    internal static bool ScansSequentially(IReadOnlyList<JsonElement> nodes, string relation) =>
        nodes.Any(node => Text(node, "Node Type") is "Seq Scan" or "Parallel Seq Scan"
            && Text(node, "Relation Name") == relation);

    /// <summary>
    /// True when a <c>Sort</c> node has a scan of <paramref name="relation"/> below it — the planner ordering
    /// that relation's rows in memory instead of reading them in index order.
    /// </summary>
    internal static bool SortsScanOf(IReadOnlyList<JsonElement> nodes, string relation) =>
        nodes.Any(node => Text(node, "Node Type") == "Sort"
            && Descendants(node).Any(descendant => Text(descendant, "Relation Name") == relation));

    private static IEnumerable<JsonElement> Flatten(JsonElement node)
    {
        yield return node.Clone();

        foreach (var descendant in Descendants(node))
        {
            yield return descendant;
        }
    }

    private static IEnumerable<JsonElement> Descendants(JsonElement node)
    {
        if (!node.TryGetProperty("Plans", out var children))
        {
            yield break;
        }

        foreach (var child in children.EnumerateArray())
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private static string? Text(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) ? value.GetString() : null;
}
