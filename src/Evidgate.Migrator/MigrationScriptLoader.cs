using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Evidgate.Migrator;

/// <summary>
/// Loads and validates a migrations directory (00 §5.2): plain
/// <c>NNNN_&lt;module&gt;_&lt;change&gt;.sql</c> files, applied in lexical order,
/// forward-only. The whole set is validated before anything touches the
/// database, because a gap or a duplicate number means two branches produced
/// the same sequence and neither history can be trusted.
/// </summary>
public static class MigrationScriptLoader
{
    /// <summary>The one accepted file name shape: 4 digits, underscore, lowercase snake slug, <c>.sql</c>.</summary>
    public static readonly Regex FileNamePattern = new(
        @"^(?<number>[0-9]{4})_(?<slug>[a-z0-9]+(?:_[a-z0-9]+)*)\.sql$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string SqlExtension = ".sql";

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Reads every <c>*.sql</c> file from <paramref name="directory"/>, checks
    /// names, duplicate numbers/names and contiguity (0001..000N, no gaps), and
    /// returns them ordered by number.
    /// </summary>
    /// <exception cref="MigratorSetupException">The directory is missing/empty or the migration set is invalid.</exception>
    public static IReadOnlyList<MigrationScript> Load(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new MigratorSetupException($"migrations directory '{directory}' does not exist");
        }

        // Every file that *looks* like a migration is validated, whatever its
        // case: a case-sensitive `*.sql` glob would silently skip `0002_b.SQL` on
        // Linux and apply it nowhere, which is worse than refusing to start.
        var files = Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(file => Path.GetFileName(file).EndsWith(SqlExtension, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
        {
            throw new MigratorSetupException($"no *{SqlExtension} migration files in '{directory}'");
        }

        var errors = new List<string>();
        var scripts = new List<MigrationScript>();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var match = FileNamePattern.Match(name);
            if (!match.Success)
            {
                errors.Add($"invalid migration file name '{name}': expected NNNN_<module>_<change>.sql (lowercase digits, slug and .sql)");
                continue;
            }

            var number = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
            if (number == 0)
            {
                errors.Add($"invalid migration file name '{name}': numbering starts at 0001");
                continue;
            }

            var bytes = File.ReadAllBytes(file);
            scripts.Add(new MigrationScript(number, name, file, Sha256Hex(bytes), DecodeUtf8(bytes)));
        }

        foreach (var group in scripts.GroupBy(s => s.Number).Where(g => g.Count() > 1).OrderBy(g => g.Key))
        {
            errors.Add(
                $"duplicate migration number {group.Key.ToString("0000", CultureInfo.InvariantCulture)}: {string.Join(", ", group.Select(s => s.Name).Order(StringComparer.Ordinal))}");
        }

        foreach (var group in scripts.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"duplicate migration file name: {string.Join(", ", group.Select(s => s.Name).Order(StringComparer.Ordinal))}");
        }

        var ordered = scripts.OrderBy(s => s.Number).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var expected = index + 1;
            if (ordered[index].Number != expected)
            {
                errors.Add(
                    $"missing migration number {expected.ToString("0000", CultureInfo.InvariantCulture)}: " +
                    $"'{directory}' jumps from {DescribePrevious(expected)} to {ordered[index].Number.ToString("0000", CultureInfo.InvariantCulture)} ({ordered[index].Name}); " +
                    "numbers must be a contiguous run from 0001");
                break;
            }
        }

        if (errors.Count > 0)
        {
            throw new MigratorSetupException(errors);
        }

        return ordered;
    }

    internal static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string DecodeUtf8(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith(Utf8Bom))
        {
            span = span[Utf8Bom.Length..];
        }

        return Encoding.UTF8.GetString(span);
    }

    private static string DescribePrevious(int expected) =>
        expected == 1 ? "nothing" : (expected - 1).ToString("0000", CultureInfo.InvariantCulture);
}
