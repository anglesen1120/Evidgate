namespace Evidgate.Modules.Catalog.Domain;

/// <summary>The outcome of ordering a suite's members.</summary>
/// <param name="Cycle">The refs along one cycle (first == last), or empty when the graph is a DAG.</param>
/// <param name="Order">A topological order of member indexes when there is no cycle.</param>
internal sealed record GraphResult(IReadOnlyList<int> Cycle, IReadOnlyList<int> Order);

/// <summary>
/// Validates a suite's dependency edges (03.2.1-T3.2, FR-03-6): a Kahn topological sort over member indexes, and when
/// nodes remain, one concrete cycle is extracted so the author sees the path. A self-edge is a cycle of length one.
/// Linear in members plus edges; no recursion, so a 10 000-member chain cannot overflow the stack.
/// </summary>
internal static class DependencyGraph
{
    /// <summary>Orders <paramref name="count"/> members under the parent → child <paramref name="edges"/>.</summary>
    /// <param name="count">Number of members.</param>
    /// <param name="edges">Distinct (parent, child) pairs of member indexes.</param>
    public static GraphResult Analyse(int count, IReadOnlyList<(int Parent, int Child)> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        foreach (var (parent, child) in edges)
        {
            if (parent == child)
            {
                return new GraphResult([parent, parent], []);
            }
        }

        var indegree = new int[count];
        var children = new List<int>[count];
        foreach (var (parent, child) in edges)
        {
            (children[parent] ??= []).Add(child);
            indegree[child]++;
        }

        var queue = new Queue<int>();
        for (var i = 0; i < count; i++)
        {
            if (indegree[i] == 0)
            {
                queue.Enqueue(i);
            }
        }

        var order = new List<int>(count);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            order.Add(node);
            if (children[node] is not { } next)
            {
                continue;
            }

            foreach (var child in next)
            {
                if (--indegree[child] == 0)
                {
                    queue.Enqueue(child);
                }
            }
        }

        return order.Count == count ? new GraphResult([], order) : new GraphResult(ExtractCycle(count, children, indegree), []);
    }

    /// <summary>
    /// The unresolved nodes sit on a cycle or below one. Peel the ones with no unresolved successor (they are below), and
    /// every node that is left has a successor in the remainder, so walking successors must come back to a visited node.
    /// </summary>
    private static List<int> ExtractCycle(int count, List<int>[] children, int[] indegree)
    {
        var alive = new bool[count];
        var outdegree = new int[count];
        var parents = new List<int>[count];
        for (var i = 0; i < count; i++)
        {
            alive[i] = indegree[i] > 0;
        }

        for (var i = 0; i < count; i++)
        {
            if (!alive[i] || children[i] is not { } list)
            {
                continue;
            }

            foreach (var child in list)
            {
                if (alive[child])
                {
                    outdegree[i]++;
                    (parents[child] ??= []).Add(i);
                }
            }
        }

        var peel = new Queue<int>();
        for (var i = 0; i < count; i++)
        {
            if (alive[i] && outdegree[i] == 0)
            {
                peel.Enqueue(i);
            }
        }

        while (peel.Count > 0)
        {
            var node = peel.Dequeue();
            alive[node] = false;
            if (parents[node] is not { } above)
            {
                continue;
            }

            foreach (var parent in above)
            {
                if (alive[parent] && --outdegree[parent] == 0)
                {
                    peel.Enqueue(parent);
                }
            }
        }

        var current = Array.FindIndex(alive, flag => flag);
        var seenAt = new Dictionary<int, int>();
        var path = new List<int>();
        while (!seenAt.ContainsKey(current))
        {
            seenAt[current] = path.Count;
            path.Add(current);
            current = children[current]!.First(child => alive[child]);
        }

        var cycle = path.GetRange(seenAt[current], path.Count - seenAt[current]);
        cycle.Add(current);

        return cycle;
    }
}
