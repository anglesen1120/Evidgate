using System.Text.Json;
using Shouldly;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Reads an <c>EXPLAIN (FORMAT JSON)</c> document as its flat node list, so a test asserts on node types,
/// index names and relation names without depending on the plan's shape or nesting. Every assertion in
/// <see cref="ListRunAttemptsTests"/>'s plan cases goes through this type, so the page, the aggregate and
/// the status counts are all read the same way.
/// <para>
/// Index and sequential-scan assertions read the <em>whole</em> plan, because the statement under test may
/// reach a relation through a detached subplan (the page CTE's producer is an <c>InitPlan</c>) and that read
/// is still the statement's. An ordering assertion starts from every <c>Sort</c> of the plan, a producer's
/// included, and follows only that sort's own row input: an <c>InitPlan</c> or <c>SubPlan</c> child is a
/// subplan the sort does not order, while a <c>Subquery Scan</c>'s child is exactly the rows it does.
/// </para>
/// </summary>
internal static class PlanJson
{
    /// <summary>Every plan node of the first statement in the document, parents before children.</summary>
    internal static IReadOnlyList<JsonElement> Nodes(string json) => Flatten(Root(json)).ToList();

    /// <summary>True when an index scan of <paramref name="index"/> (plain, only or bitmap) is in the plan.</summary>
    internal static bool UsesIndex(IReadOnlyList<JsonElement> nodes, string index) =>
        nodes.Any(node => Text(node, "Index Name") == index
            && Text(node, "Node Type") is "Index Scan" or "Index Only Scan" or "Bitmap Index Scan");

    /// <summary>True when <paramref name="relation"/> is read by a sequential scan anywhere in the plan.</summary>
    internal static bool ScansSequentially(IReadOnlyList<JsonElement> nodes, string relation) =>
        nodes.Any(node => Text(node, "Node Type") is "Seq Scan" or "Parallel Seq Scan"
            && Text(node, "Relation Name") == relation);

    /// <summary>
    /// True when a <c>Sort</c> anywhere in the plan orders rows of <paramref name="relation"/> — the planner
    /// ordering that relation's rows in memory instead of reading them in index order. Each sort is checked
    /// against its own row input, so sorting a bounded CTE's output is not a sort of the producer's table.
    /// </summary>
    internal static bool SortsScanOf(string json, string relation) =>
        Nodes(json).Where(node => Text(node, "Node Type") == "Sort").Any(sort => Reads(sort, relation));

    /// <summary>The plan of the document's one statement, detached from the parsed document.</summary>
    private static JsonElement Root(string json)
    {
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetArrayLength().ShouldBe(1, json);

        return document.RootElement[0].GetProperty("Plan").Clone();
    }

    /// <summary>
    /// Whether <paramref name="node"/>'s own row input reaches <paramref name="relation"/>. Only an
    /// <c>InitPlan</c> or <c>SubPlan</c> child is skipped: its rows are a scalar value here, not this node's
    /// input. A <c>Subquery Scan</c>'s child supplies its rows and is followed.
    /// </summary>
    private static bool Reads(JsonElement node, string relation) =>
        Text(node, "Relation Name") == relation || Inputs(node).Any(child => Reads(child, relation));

    private static IEnumerable<JsonElement> Inputs(JsonElement node) =>
        node.TryGetProperty("Plans", out var children)
            ? children.EnumerateArray().Where(child => Text(child, "Parent Relationship") is not ("InitPlan" or "SubPlan"))
            : Enumerable.Empty<JsonElement>();

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
