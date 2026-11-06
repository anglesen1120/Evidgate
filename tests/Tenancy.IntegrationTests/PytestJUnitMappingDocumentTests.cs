using System.Globalization;
using System.Reflection;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Guards <c>contracts/adapter/junit-pytest-mapping.md</c> against the mapper it describes: every constant of the
/// mapper types has a Symbol index row carrying its own value, every row names a constant that exists, and a removed
/// row fails the check, so neither a new constant nor a stale row can reach the document unnoticed.
/// </summary>
public sealed class PytestJUnitMappingDocumentTests
{
    private const string DocumentPath = "contracts/adapter/junit-pytest-mapping.md";
    private const string IndexHeading = "## Symbol index";

    private static readonly string DocumentFile = Path.Combine(RepoRoot(), DocumentPath);
    private static readonly IReadOnlyList<SymbolRow> Symbols = DocumentedSymbols();

    [Fact]
    public void MapperSymbols_EachHasADocRow()
    {
        var comparison = Compare(File.ReadAllText(DocumentFile), Symbols);

        comparison.Missing.ShouldBeEmpty(
            $"these mapper constants have no Symbol index row: {string.Join(", ", comparison.Missing)}");
        comparison.Mismatched.ShouldBeEmpty(
            $"these Symbol index rows disagree with their constant: {string.Join("; ", comparison.Mismatched)}");
    }

    [Fact]
    public void DocRows_EachResolvesToAMapperSymbol()
    {
        var comparison = Compare(File.ReadAllText(DocumentFile), Symbols);

        comparison.Stale.ShouldBeEmpty(
            $"these Symbol index rows name no mapper constant: {string.Join(", ", comparison.Stale)}");
        comparison.Mismatched.ShouldBeEmpty(
            $"these Symbol index rows disagree with their constant: {string.Join("; ", comparison.Mismatched)}");
    }

    [Fact]
    public void DocDrift_FailsWhenAMapperRowHasNoDocRow()
    {
        const string Removed = "JUnitDiscrepancies.CountMismatch";
        var document = File.ReadAllText(DocumentFile);
        var withoutRow = WithoutRow(document, Removed);

        withoutRow.ShouldNotBe(document);
        Compare(withoutRow, Symbols).Missing.ShouldBe([Removed]);
    }

    /// <summary>Compares one document text with the mapper's constants, in both directions.</summary>
    /// <param name="document">The document text (the real file, or a copy with one row removed).</param>
    /// <param name="symbols">The constants the document must describe.</param>
    /// <returns>The missing, mismatched and stale symbol names.</returns>
    private static DocumentComparison Compare(string document, IReadOnlyList<SymbolRow> symbols)
    {
        var rows = IndexRows(document);
        var documented = new HashSet<string>(rows.Keys, StringComparer.Ordinal);
        var missing = new List<string>();
        var mismatched = new List<string>();

        foreach (var symbol in symbols)
        {
            if (!rows.TryGetValue(symbol.Symbol, out var value))
            {
                missing.Add(symbol.Symbol);
            }
            else if (!string.Equals(value, symbol.Value, StringComparison.Ordinal))
            {
                mismatched.Add($"{symbol.Symbol} (doc `{value}`, constant `{symbol.Value}`)");
            }
        }

        var stale = documented
            .Where(name => symbols.All(symbol => symbol.Symbol != name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new DocumentComparison(missing, mismatched, stale);
    }

    /// <summary>Reads the two leading backticked cells of every row under the Symbol index heading.</summary>
    /// <param name="document">The document text.</param>
    /// <returns>Symbol name → documented value, in document order.</returns>
    private static Dictionary<string, string> IndexRows(string document)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var inIndex = false;

        foreach (var raw in document.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inIndex = string.Equals(line, IndexHeading, StringComparison.Ordinal);
                continue;
            }

            if (!inIndex || !line.StartsWith('|'))
            {
                continue;
            }

            var cells = line.Split('|');
            if (cells.Length < 3)
            {
                continue;
            }

            var symbol = Backticked(cells[1]);
            var value = Backticked(cells[2]);
            if (symbol is null || value is null)
            {
                continue;
            }

            rows[symbol] = value;
        }

        return rows;
    }

    /// <summary>The content of one cell when the whole cell is one backticked span, otherwise null.</summary>
    /// <param name="cell">The raw cell text between two table pipes.</param>
    /// <returns>The span's content, or null for a header, separator or free-form cell.</returns>
    private static string? Backticked(string cell)
    {
        var trimmed = cell.Trim();
        return trimmed.Length > 2 && trimmed[0] == '`' && trimmed[^1] == '`'
            ? trimmed[1..^1]
            : null;
    }

    /// <summary>Drops the one markdown line that documents <paramref name="symbol"/>.</summary>
    /// <param name="document">The document text.</param>
    /// <param name="symbol">The symbol whose row is removed.</param>
    /// <returns>The document text with that row gone.</returns>
    private static string WithoutRow(string document, string symbol) =>
        string.Join(
            '\n',
            document.Split('\n').Where(line => !line.Contains($"`{symbol}`", StringComparison.Ordinal)));

    /// <summary>The constants the document must index: every mapper constant, plus the named bounds elsewhere.</summary>
    /// <returns>The expected Symbol index rows, ordered by symbol name.</returns>
    private static SymbolRow[] DocumentedSymbols()
    {
        var rows = new List<SymbolRow>();
        AddConstants(rows, typeof(PytestJUnitMapper));
        AddConstants(rows, typeof(PytestStatus));
        AddConstants(rows, typeof(JUnitDiscrepancies));
        AddConstants(rows, typeof(PlannedExecutionIndex), "MaxClassnameLength", "MaxNameLength", "MaxClassnameSegments");
        AddConstants(rows, typeof(ParsingLimits), "MaxDepth", "MaxReportBytes");

        // The parse timeout is a TimeSpan, not a literal, so the document states its seconds.
        rows.Add(new SymbolRow(
            $"{nameof(ParsingLimits)}.{nameof(ParsingLimits.ParseTimeout)}",
            ParsingLimits.ParseTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)));

        return rows.OrderBy(row => row.Symbol, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Adds the literal constants of one type, or only the named ones.</summary>
    /// <param name="rows">The rows being built.</param>
    /// <param name="type">The type whose constants the document must describe.</param>
    /// <param name="only">The constant names to include; empty means every literal constant.</param>
    private static void AddConstants(List<SymbolRow> rows, Type type, params string[] only)
    {
        var wanted = only.Length == 0 ? null : new HashSet<string>(only, StringComparer.Ordinal);

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (!field.IsLiteral || field.IsInitOnly || (wanted is not null && !wanted.Contains(field.Name)))
            {
                continue;
            }

            rows.Add(new SymbolRow($"{type.Name}.{field.Name}", Literal(field.GetRawConstantValue())));
        }
    }

    /// <summary>Formats a constant's literal value the way the document spells it.</summary>
    /// <param name="value">The raw constant value.</param>
    /// <returns>The invariant text; strings carry no quotes.</returns>
    private static string Literal(object? value) => value switch
    {
        string text => text,
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? string.Empty,
    };

    /// <summary>The directory holding <c>Evidgate.slnx</c>, found by walking up from the test output.</summary>
    /// <returns>The repository root.</returns>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Evidgate.slnx not found above '{AppContext.BaseDirectory}'");
    }

    /// <summary>One Symbol index row: the symbol's dotted name and its documented value.</summary>
    private sealed record SymbolRow(string Symbol, string Value);

    /// <summary>How one document text compares with the mapper's constants.</summary>
    private sealed record DocumentComparison(
        IReadOnlyList<string> Missing,
        IReadOnlyList<string> Mismatched,
        IReadOnlyList<string> Stale);
}
