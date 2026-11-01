using System.Text.RegularExpressions;

namespace Architecture.Tests.Rules;

/// <summary>
/// Module data is private. A module's SQL reads and writes only its own tables, and another module's data is
/// reached through that module's <c>.Contracts</c>, never by naming its table here. The rule is pure — it takes the
/// files to scan and the tables the caller owns — so the test proves it on synthetic cross-module reads and then runs
/// the same function over every Policy persistence file, where the fix that split the evaluation reads landed.
/// </summary>
public static class PolicySqlTableRules
{
    /// <summary>A schema-qualified table name as the statements spell it (<c>public.evaluation</c>).</summary>
    /// <remarks>
    /// Schema-qualified only, because every statement must name its schema: a bare <c>from run</c> is a
    /// different defect, and matching it here would flag unrelated text.
    /// </remarks>
    private static readonly Regex QualifiedTable = new(
        @"\bpublic\.([a-z][a-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The table names <paramref name="text"/> spells, distinct and ordered ordinally.</summary>
    public static IReadOnlyList<string> TablesNamedIn(string text) =>
    [
        .. QualifiedTable.Matches(text)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>One violation per file and table the module does not own.</summary>
    /// <param name="files">The sources to scan, repo-relative with '/'.</param>
    /// <param name="ownedTables">The tables the module owns and may name.</param>
    public static IReadOnlyList<string> FindViolations(
        IEnumerable<(string Path, string Text)> files,
        IReadOnlySet<string> ownedTables) =>
    [
        .. files.SelectMany(file => TablesNamedIn(file.Text)
            .Where(table => !ownedTables.Contains(table))
            .Select(table => $"{file.Path} names public.{table}, a table this module does not own")),
    ];
}
