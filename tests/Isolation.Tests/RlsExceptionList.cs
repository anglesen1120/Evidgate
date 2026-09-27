using System.Text.RegularExpressions;

namespace Isolation.Tests;

/// <summary>
/// One line of <c>contracts/rls-exceptions.txt</c> (02.6.1-T1.4): a table outside
/// the tenant-scoped rules, the kind of exception and the reason the file records.
/// </summary>
internal sealed record RlsException(string Table, string Kind, string Reason);

/// <summary>
/// The reader of <c>contracts/rls-exceptions.txt</c> — the single source of the
/// tables that are not org-scoped (02.6.1-T1.4, 02 §4.5). 02.9.1-T2 subtracts its
/// entries from the catalogue-discovered <c>org_id</c> tables to get the tenant
/// set, so a table is a tenant table exactly when it carries <c>org_id</c> and the
/// file does not name it. The file is read from the repository (never copied into
/// the build output), and a missing or malformed file is a failure, not an empty
/// list: the suite must not silently turn into a no-op.
/// <para>
/// Entry format: <c>public.&lt;table&gt; &lt;kind&gt;</c>, then <c>#</c> and the
/// reason. Blank lines and everything after <c>#</c> are ignored. Kinds are
/// <c>platform</c> (no <c>org_id</c>), <c>catalogue</c> (nullable <c>org_id</c>)
/// and <c>mechanism</c> (01's tables without an org policy). The kinds document
/// <em>why</em> a table is out of scope; the suite's rule set is "not on the list"
/// regardless of kind, because the file is the one place the exception lives.
/// </para>
/// </summary>
internal static partial class RlsExceptionList
{
    /// <summary>The path of the list, repository-relative.</summary>
    internal const string PathInRepo = "contracts/rls-exceptions.txt";

    /// <summary>The three kinds the format defines; anything else is a malformed line.</summary>
    private static readonly string[] KnownKinds = ["platform", "catalogue", "mechanism"];

    /// <summary>The repository root (walks up for <c>Evidgate.slnx</c>, as the sibling suites do).</summary>
    internal static string RepoRoot { get; } = LocateRepoRoot();

    /// <summary>The absolute path of the list.</summary>
    internal static string Path => System.IO.Path.Combine(RepoRoot, PathInRepo);

    /// <summary>Reads and validates the whole list.</summary>
    /// <exception cref="FileNotFoundException">The list does not exist.</exception>
    /// <exception cref="InvalidDataException">A line is malformed, a kind is unknown, a table is listed twice, or the list is empty.</exception>
    internal static IReadOnlyList<RlsException> Read()
    {
        if (!File.Exists(Path))
        {
            throw new FileNotFoundException(
                $"{PathInRepo} not found above '{AppContext.BaseDirectory}': it is the single source of the tenant-table "
                + "exception list (02.6.1-T1.4), and the composite-FK suite (02.9.1-T2) cannot discover the tenant set without it.",
                Path);
        }

        var entries = new List<RlsException>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lineNumber = 0;

        foreach (var raw in File.ReadLines(Path))
        {
            lineNumber++;
            var comment = raw.IndexOf('#', StringComparison.Ordinal);
            var line = (comment < 0 ? raw : raw[..comment]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length != 2)
            {
                throw new InvalidDataException(
                    $"{PathInRepo}:{lineNumber}: expected '<schema>.<table> <kind>' before the comment, got '{line}'");
            }

            var (table, kind) = (tokens[0], tokens[1]);

            if (!TableToken().IsMatch(table))
            {
                throw new InvalidDataException(
                    $"{PathInRepo}:{lineNumber}: '{table}' is not a schema-qualified table name (the format is 'public.<table>')");
            }

            if (!KnownKinds.Contains(kind, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"{PathInRepo}:{lineNumber}: unknown kind '{kind}' — expected one of {string.Join(", ", KnownKinds)}");
            }

            if (!seen.Add(table))
            {
                throw new InvalidDataException($"{PathInRepo}:{lineNumber}: '{table}' is listed more than once");
            }

            entries.Add(new RlsException(table, kind, comment < 0 ? string.Empty : raw[(comment + 1)..].Trim()));
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException(
                $"{PathInRepo}: the exception list is empty — it must name the platform, catalogue and mechanism tables "
                + "(02 §4.5), otherwise the suite would treat them as tenant tables");
        }

        return entries;
    }

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "Evidgate.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Evidgate.slnx not found above '{AppContext.BaseDirectory}'");
    }

    [GeneratedRegex(@"^[a-z_][a-z0-9_]*\.[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TableToken();
}
