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
/// <b>The hint rules, most specific level first.</b> pytest's <c>classname</c> is a dotted module path (optionally
/// ending in a class), and the mapping document's identity rule is not written yet, so this index accepts the shapes
/// a report can honestly carry. Each level is matched as a whole, and the first level that matches anything decides:
/// </para>
/// <list type="number">
/// <item><description>
/// the <c>classname</c> as a planned file path, verbatim, plus <c>::name</c> — a producer that writes the path where
/// pytest writes the module path;
/// </description></item>
/// <item><description>
/// the full <c>classname</c>: <c>classname::name</c> as a planned case name, and the file path the whole classname
/// spells (<c>tests.test_payments</c> → <c>tests/test_payments.py</c>) plus <c>::name</c>;
/// </description></item>
/// <item><description>
/// each shorter module prefix, with the file path it spells plus <c>::name</c>, beside the class path that follows it
/// plus <c>::name</c> (<c>tests.test_payments.TestPayments</c> → <c>tests/test_payments.py</c> and
/// <c>TestPayments</c>).
/// </description></item>
/// <item><description>
/// the bare <c>name</c> — but only when the report carries no <c>classname</c> at all.
/// </description></item>
/// </list>
/// <para>
/// <b>Ambiguity is not resolved by preference.</b> Two planned executions matching within one level are ambiguous
/// evidence, and resolution stops there rather than falling through to a still coarser shape. An explicit
/// <c>classname</c> that matches no planned shape stays unresolved instead of borrowing an unrelated case that
/// happens to share the name: the module the report names is evidence the plan does not carry, so the report and the
/// plan disagree.
/// </para>
/// <para>
/// <b>The hint is hostile input, so it is bounded before any string is expanded.</b> A <c>classname</c> or
/// <c>name</c> longer than <see cref="MaxClassnameLength"/>/<see cref="MaxNameLength"/>, or a <c>classname</c> with
/// more than <see cref="MaxClassnameSegments"/> dotted segments, is unresolved — never an exception. Without the
/// bounds one crafted attribute would make the mapper join a segmented string once per level, which is quadratic in
/// the attribute's length; with them the classname is split and joined once, every level slices those buffers, and a
/// report of hostile attributes cannot grow the mapping's work beyond its own bytes.
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

    /// <summary>The most characters of a report's <c>classname</c> this index inspects; a longer one is unresolved.</summary>
    public const int MaxClassnameLength = 512;

    /// <summary>The most characters of a report's <c>name</c> this index inspects; a longer one is unresolved.</summary>
    public const int MaxNameLength = 512;

    /// <summary>The most dotted segments a <c>classname</c> may carry; a deeper one is unresolved.</summary>
    public const int MaxClassnameSegments = 32;

    private const string ClassFilePathSuffix = ".py::";
    private const string NodeSeparator = "::";

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
            Add(byFileAndName, string.Concat(row.FilePath, NodeSeparator, row.CaseName), row.ExecutionKey);
        }

        return new PlannedExecutionIndex(byCaseName, byFileAndName);
    }

    /// <summary>Resolves one testcase's identity hint to a planned execution key, or <see langword="null"/>.</summary>
    /// <param name="classname">The raw <c>classname</c> attribute; empty when the report omits it.</param>
    /// <param name="name">The raw <c>name</c> attribute; empty when the report omits it.</param>
    /// <returns>
    /// The unique planned key, or <see langword="null"/> when nothing matches, more than one row matches at the same
    /// specificity, or the hint exceeds the bounds. Never throws for a hint's content.
    /// </returns>
    public string? Resolve(string classname, string name)
    {
        classname ??= string.Empty;
        name ??= string.Empty;

        if (name.Length == 0 || name.Length > MaxNameLength)
        {
            return null;
        }

        if (classname.Length == 0)
        {
            return _byCaseName.TryGetValue(name, out var bare) ? Unique(bare) : null;
        }

        return classname.Length > MaxClassnameLength ? null : ResolveExplicit(classname, name);
    }

    /// <summary>
    /// Resolves an explicit <c>classname</c> level by level, most specific first. Each level is looked up whole, so a
    /// level that names two planned executions stops resolution instead of letting a coarser shape decide.
    /// </summary>
    /// <param name="classname">The raw <c>classname</c> attribute, at most <see cref="MaxClassnameLength"/> characters.</param>
    /// <param name="name">The raw <c>name</c> attribute, at most <see cref="MaxNameLength"/> characters.</param>
    /// <returns>The unique planned key, or <see langword="null"/>.</returns>
    private string? ResolveExplicit(string classname, string name)
    {
        // A producer may write the file path where pytest writes the module path. That verbatim path is the most
        // exact hint there is; splitting it would read its extension as a directory segment and could reach a
        // planned file one level down before the file the report actually names.
        if (_byFileAndName.TryGetValue(string.Concat(classname, NodeSeparator, name), out var literal))
        {
            return Unique(literal);
        }

        Span<int> starts = stackalloc int[MaxClassnameSegments];
        Span<int> ends = stackalloc int[MaxClassnameSegments];
        var count = SplitSegments(classname, starts, ends);
        if (count <= 0)
        {
            return null;
        }

        // Both spellings of the classname are built once, from the same split: the file path a prefix spells and the
        // class path that follows it. Each level then slices these buffers instead of re-joining its segments.
        Span<int> pathEnds = stackalloc int[MaxClassnameSegments];
        var path = Join(classname, starts, ends, count, '/', pathEnds);
        Span<int> classEnds = stackalloc int[MaxClassnameSegments];
        var dotted = Join(classname, starts, ends, count, '.', classEnds);

        for (var level = count; level >= 1; level--)
        {
            HashSet<string>? matched = null;

            if (level == count)
            {
                // The whole classname as a planned case name: a plan may store pytest node ids verbatim.
                matched = Collect(matched, _byCaseName, string.Concat(classname, NodeSeparator, name));
            }

            var file = path.AsSpan(0, pathEnds[level - 1]);
            matched = Collect(
                matched,
                _byFileAndName,
                string.Concat(file, ClassFilePathSuffix.AsSpan(), name.AsSpan()));

            if (level < count)
            {
                // The class path after this module prefix, e.g. "TestPayments", as a planned case name.
                var className = dotted.AsSpan(classEnds[level - 1] + 1);
                matched = Collect(
                    matched,
                    _byCaseName,
                    string.Concat(className, NodeSeparator.AsSpan(), name.AsSpan()));
            }

            if (matched is not null)
            {
                return Unique(matched);
            }
        }

        return null;
    }

    /// <summary>Splits a dotted classname into its non-empty segments.</summary>
    /// <param name="classname">The raw <c>classname</c> attribute.</param>
    /// <param name="starts">Receives each segment's start index.</param>
    /// <param name="ends">Receives each segment's end index.</param>
    /// <returns>The segment count, or <c>-1</c> when there are more segments than the buffers hold.</returns>
    private static int SplitSegments(string classname, Span<int> starts, Span<int> ends)
    {
        var count = 0;
        var index = 0;

        while (index < classname.Length)
        {
            var dot = classname.IndexOf('.', index);
            if (dot < 0)
            {
                dot = classname.Length;
            }

            if (dot > index)
            {
                if (count == starts.Length)
                {
                    // Deeper than any pytest module path: refuse the hint, never grow the work to fit it.
                    return -1;
                }

                starts[count] = index;
                ends[count] = dot;
                count++;
            }

            index = dot + 1;
        }

        return count;
    }

    /// <summary>Joins the split segments with one separator, recording where each prefix ends.</summary>
    /// <param name="classname">The raw <c>classname</c> attribute.</param>
    /// <param name="starts">Each segment's start index.</param>
    /// <param name="ends">Each segment's end index.</param>
    /// <param name="count">The segment count.</param>
    /// <param name="separator">The separator between segments.</param>
    /// <param name="prefixEnds">Receives the buffer offset just past the segment at that position.</param>
    /// <returns>The joined buffer, at most <see cref="MaxClassnameLength"/> characters plus separators.</returns>
    private static char[] Join(
        string classname,
        Span<int> starts,
        Span<int> ends,
        int count,
        char separator,
        Span<int> prefixEnds)
    {
        var length = count - 1;
        for (var i = 0; i < count; i++)
        {
            length += ends[i] - starts[i];
        }

        var buffer = new char[length];
        var written = 0;

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                buffer[written++] = separator;
            }

            var segment = classname.AsSpan(starts[i], ends[i] - starts[i]);
            segment.CopyTo(buffer.AsSpan(written));
            written += segment.Length;
            prefixEnds[i] = written;
        }

        return buffer;
    }

    /// <summary>Adds one hint's keys to this level's matches, if the hint names any planned execution.</summary>
    /// <param name="matched">The matches collected at this level so far, or <see langword="null"/>.</param>
    /// <param name="index">The lookup the hint is read from.</param>
    /// <param name="hint">The candidate identity hint.</param>
    /// <returns>The matches, unchanged when the hint names nothing.</returns>
    private static HashSet<string>? Collect(
        HashSet<string>? matched,
        Dictionary<string, HashSet<string>> index,
        string hint)
    {
        if (!index.TryGetValue(hint, out var keys))
        {
            return matched;
        }

        if (matched is null)
        {
            return new HashSet<string>(keys, StringComparer.Ordinal);
        }

        matched.UnionWith(keys);
        return matched;
    }

    /// <summary>The one key the matches hold, or <see langword="null"/> when they disagree.</summary>
    /// <param name="keys">The planned keys this level matched.</param>
    /// <returns>The unique key, or <see langword="null"/> for several: ambiguity is never resolved by preference.</returns>
    private static string? Unique(HashSet<string> keys) => keys.Count == 1 ? keys.First() : null;

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
