namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// One planned execution of a run, reduced to what a JUnit report's identity hint can be matched against
/// (<c>classname</c>/<c>name</c> → <c>execution_key</c>).
/// </summary>
/// <param name="ExecutionKey">The planned execution's key (<c>^[0-9a-f]{64}$</c>).</param>
/// <param name="CaseName">The case's declared name, e.g. <c>test_capture_declined</c>.</param>
/// <param name="FilePath">The revision's file path, e.g. <c>tests/test_payments.py</c>.</param>
internal sealed record PlannedExecutionHint(string ExecutionKey, string CaseName, string FilePath);

/// <summary>
/// The run's planned executions as an in-memory lookup, so mapping never runs a statement per testcase (P2/P3).
/// <para>
/// <b>Only a unique match resolves.</b> An identity hint can name several planned executions (one per variant, or two
/// cases with the same name in different files), and picking one of them would be a guess: a wrong
/// <c>execution_key</c> attributes a result to another case, which is worse than reporting the testcase as unmapped.
/// So a hint that matches more than one planned execution resolves to <see langword="null"/> and the testcase is
/// reported as <c>unmapped_case</c>.
/// </para>
/// <para>
/// <b>The hint rules.</b> pytest's <c>classname</c> is a dotted module path (optionally ending in a class), and the
/// mapping document's identity rule is not written yet (04.4.1-T5), so this index accepts the shapes a report can
/// honestly carry, most specific first: <c>classname::name</c> as a whole case name, the file path a classname prefix
/// spells (<c>tests.test_payments</c> → <c>tests/test_payments.py</c>) plus <c>::name</c>, a trailing class segment plus
/// <c>::name</c>, and finally the bare <c>name</c>. The first shape that matches anything decides.
/// </para>
/// </summary>
internal sealed class PlannedExecutionIndex
{
    /// <summary>The one read of the run's plan: key, case name and revision path, one row per planned execution.</summary>
    internal const string SelectSql = """
        select pe.execution_key as "ExecutionKey", tc.name as "CaseName", tr.file_path as "FilePath"
          from public.planned_execution pe
          join public.test_revision tr on tr.org_id = pe.org_id and tr.id = pe.case_revision_id
          join public.test_case tc on tc.org_id = tr.org_id and tc.id = tr.case_id
         where pe.org_id = @org_id
           and pe.run_id = @run_id
        """;

    private readonly Dictionary<string, HashSet<string>> _byCaseName;
    private readonly Dictionary<string, HashSet<string>> _byFileAndName;

    private PlannedExecutionIndex(
        Dictionary<string, HashSet<string>> byCaseName,
        Dictionary<string, HashSet<string>> byFileAndName)
    {
        _byCaseName = byCaseName;
        _byFileAndName = byFileAndName;
    }

    /// <summary>Builds the lookup from the rows the plan read returned.</summary>
    /// <param name="rows">One row per planned execution; an empty plan builds an empty index.</param>
    public static PlannedExecutionIndex Build(IEnumerable<PlannedExecutionHint> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var byCaseName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var byFileAndName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            Add(byCaseName, row.CaseName, row.ExecutionKey);
            Add(byFileAndName, row.FilePath + "::" + row.CaseName, row.ExecutionKey);
        }

        return new PlannedExecutionIndex(byCaseName, byFileAndName);
    }

    /// <summary>Resolves one testcase's identity hint to a planned execution key, or <see langword="null"/>.</summary>
    /// <param name="classname">The raw <c>classname</c> attribute; empty when the report omits it.</param>
    /// <param name="name">The raw <c>name</c> attribute; empty when the report omits it.</param>
    /// <returns>The unique planned key, or <see langword="null"/> when nothing or more than one row matches.</returns>
    public string? Resolve(string classname, string name)
    {
        classname ??= string.Empty;
        name ??= string.Empty;

        if (name.Length == 0)
        {
            return null;
        }

        foreach (var hint in Hints(classname, name))
        {
            if (!TryFind(hint, out var keys))
            {
                continue;
            }

            // The first shape that matches anything decides: one key is the answer, several are ambiguous evidence,
            // never a pick.
            return keys.Count == 1 ? keys.First() : null;
        }

        return null;
    }

    /// <summary>The identity hints of one testcase, most specific first; the order is the resolution rule.</summary>
    /// <param name="classname">The raw <c>classname</c> attribute.</param>
    /// <param name="name">The raw <c>name</c> attribute.</param>
    /// <returns>The hint strings to look up, in order.</returns>
    private static List<string> Hints(string classname, string name)
    {
        var hints = new List<string>(6);

        if (classname.Length > 0)
        {
            hints.Add(classname + "::" + name);

            var segments = classname.Split('.', StringSplitOptions.RemoveEmptyEntries);
            for (var count = 1; count <= segments.Length; count++)
            {
                var file = string.Join('/', segments[..count]) + ".py";
                hints.Add(file + "::" + name);

                if (count < segments.Length)
                {
                    hints.Add(string.Join('.', segments[count..]) + "::" + name);
                }
            }
        }

        hints.Add(name);

        return hints;
    }

    /// <summary>True when any planned execution carries the hint.</summary>
    /// <param name="hint">The candidate identity hint.</param>
    /// <param name="keys">The planned keys the hint names; empty when none does.</param>
    private bool TryFind(string hint, out HashSet<string> keys) =>
        _byCaseName.TryGetValue(hint, out keys!) || _byFileAndName.TryGetValue(hint, out keys!);

    private static void Add(Dictionary<string, HashSet<string>> index, string hint, string executionKey)
    {
        if (!index.TryGetValue(hint, out var keys))
        {
            keys = new HashSet<string>(StringComparer.Ordinal);
            index.Add(hint, keys);
        }

        keys.Add(executionKey);
    }
}
