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
/// is still the statement's. An ordering assertion does not: a <c>Sort</c> inside a detached subplan belongs
/// to that subplan, not to the query whose ordering is being pinned, so <see cref="SortsScanOf"/> walks only
/// the child plans the query itself attaches as nested nodes.
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
    /// True when a <c>Sort</c> node of the plan's own query has a scan of <paramref name="relation"/> in its
    /// input — the planner ordering that relation's rows in memory instead of reading them in index order.
    /// <c>InitPlan</c>, <c>SubPlan</c> and <c>Subquery</c> children are separate subplans and are not followed:
    /// a sort inside them is that subplan's ordering, not the query's.
    /// </summary>
    internal static bool SortsScanOf(string json, string relation) => SortsInside(Root(json), relation);

    /// <summary>The plan of the document's one statement, detached from the parsed document.</summary>
    private static JsonElement Root(string json)
    {
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetArrayLength().ShouldBe(1, json);

        return document.RootElement[0].GetProperty("Plan").Clone();
    }

    private static bool SortsInside(JsonElement node, string relation) =>
        (Text(node, "Node Type") == "Sort" && Attached(node).Any(child => Reads(child, relation)))
        || Attached(node).Any(child => SortsInside(child, relation));

    private static bool Reads(JsonElement node, string relation) =>
        Text(node, "Relation Name") == relation || Attached(node).Any(child => Reads(child, relation));

    /// <summary>The children the node's own query attaches by nesting.</summary>
    private static IEnumerable<JsonElement> Attached(JsonElement node) =>
        node.TryGetProperty("Plans", out var children)
            ? children.EnumerateArray().Where(child => !Detached(child))
            : Enumerable.Empty<JsonElement>();

    /// <summary>A child plan the parent only refers to: a CTE producer, a scalar subplan or a subquery scan's input.</summary>
    private static bool Detached(JsonElement child) =>
        Text(child, "Parent Relationship") is "InitPlan" or "SubPlan" or "Subquery";

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
