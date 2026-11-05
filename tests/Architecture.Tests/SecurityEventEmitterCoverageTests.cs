using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Every <c>security_event</c> type of the closed V1 set has an alert rule and either a production emitter or an
/// explicit known-gap entry. The set is the baseline page's table, the endpoint's closed array and the alert-rule
/// rows, which must agree. An emitter is recognised only when the INSERT text and quoted type literal share a
/// file; a writer taking the type as a parameter or from a constants file must extend this scanner's gap-list
/// handling in the same change. A type with neither an emitter nor a gap entry fails, as does a gap whose emitter lands.
/// </summary>
public sealed class SecurityEventEmitterCoverageTests
{
    private const string BaselinePath = "docs/security/security-event-baseline.md";

    private const string AlertRulesPath = "docs/security/alert-rules.md";

    private const string EndpointPath = "src/Modules/Evidgate.Modules.Identity/Endpoints/SecurityEventEndpoints.cs";

    /// <summary>The table's own migration creates it and must not count as a writer.</summary>
    private const string SchemaMigration = "0053_security_security_event.sql";

    /// <summary>
    /// Types with no production emitter yet. Remove an entry in the change that adds its emitter; the receivers
    /// are listed in <c>docs/security/alert-rules.md</c> §3.
    /// </summary>
    private static readonly string[] KnownGaps =
    [
        "auth_failure", "cross_tenant_probe", "secret_scanner_alert", "kill_switch_used", "support_access",
    ];

    private static readonly Regex InsertIntoTable =
        new(@"\binsert\s+into\s+(?:""[^""]+""\s*\.\s*|[a-z_][a-z0-9_$]*\s*\.\s*)?(?:""security_event""|security_event)(?![a-z0-9_$""])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Types_BaselineEndpointAndAlertRules_Agree()
    {
        var baseline = BaselineTypes(ReadRepoFile(BaselinePath));
        baseline.ShouldNotBeEmpty();

        EndpointTypes(ReadRepoFile(EndpointPath)).ShouldBe(baseline, ignoreOrder: true);
        AlertRuleTypes(ReadRepoFile(AlertRulesPath)).ShouldBe(baseline, ignoreOrder: true);
    }

    [Fact]
    public void EveryType_HasEmitter_OrIsAKnownGap_AndNoGapHasAnEmitter()
    {
        var types = BaselineTypes(ReadRepoFile(BaselinePath));
        var sources = ProductionSources();
        sources.ShouldNotBeEmpty("the emitter scan must inspect production sources");
        sources.Keys.ShouldContain(EndpointPath);

        KnownGaps.Except(types).ShouldBeEmpty("a known gap names a type that is not in the baseline");
        foreach (var type in types)
        {
            var emitters = Emitters(sources, type);
            var isGap = KnownGaps.Contains(type, StringComparer.Ordinal);
            if (emitters.Count == 0)
            {
                isGap.ShouldBeTrue($"security_event type '{type}' has no production emitter and is not a known gap");
            }
            else
            {
                isGap.ShouldBeFalse(
                    $"security_event type '{type}' has an emitter ({emitters[0]}); remove it from KnownGaps");
            }
        }
    }

    [Fact]
    public void Scanner_FindsAnEmitterOnlyWhenTheFileInsertsAndNamesTheType()
    {
        var sources = new Dictionary<string, string>
        {
            ["a.cs"] = "const string Sql = \"INSERT INTO security_event (type) VALUES ('auth_failure')\";",
            ["b.cs"] = "var t = \"cross_tenant_probe\";",
            ["c.cs"] = "INSERT INTO security_event_other (type) VALUES ('kill_switch_used');",
            ["d.cs"] = "// insert into security_event (type) values ('kill_switch_used');",
            ["e.sql"] = "-- insert into security_event (type) values ('kill_switch_used');",
            ["f.cs"] = "/* insert into security_event (type) values ('kill_switch_used'); */",
            ["g.sql"] = """INSERT INTO "public"."security_event" (type) VALUES ('support_access');""",
            ["h.sql"] = "INSERT INTO evidgate.security_event (type) VALUES ('support_access');",
        };

        Emitters(sources, "auth_failure").ShouldBe(["a.cs"]);
        Emitters(sources, "cross_tenant_probe").ShouldBeEmpty();
        Emitters(sources, "kill_switch_used").ShouldBeEmpty();
        Emitters(sources, "support_access").ShouldBe(["g.sql", "h.sql"]);

        foreach (var directory in new[] { "bin", "obj" })
        {
            var root = Path.Combine(SolutionLayout.RepoRoot, directory, "checkout");
            IsBuildOutput(root, Path.Combine(root, EndpointPath)).ShouldBeFalse();
            IsBuildOutput(root, Path.Combine(root, "src", directory, "Generated.cs")).ShouldBeTrue();
        }
    }

    internal static IReadOnlyList<string> BaselineTypes(string markdown) =>
        TableRows(markdown, "## 2. Baseline table").Select(cells => Unquote(cells[0])).ToList();

    internal static IReadOnlyList<string> AlertRuleTypes(string markdown) =>
        TableRows(markdown, "## 2. Rules")
            .Select(cells => Regex.Match(cells[1], @"\]\([^)]*\)\s*`([a-z_]+)`").Groups[1].Value)
            .ToList();

    private static List<string> EndpointTypes(string source)
    {
        var block = Regex.Match(source, @"string\[\]\s+Types\s*=\s*\[(?<body>[^\]]*)\]");
        block.Success.ShouldBeTrue($"{EndpointPath} no longer declares the closed Types array");
        return Regex.Matches(block.Groups["body"].Value, "\"([a-z_]+)\"").Select(m => m.Groups[1].Value).ToList();
    }

    /// <summary>Repo-relative path of every production file that inserts into the table and names the type.</summary>
    private static List<string> Emitters(IReadOnlyDictionary<string, string> sources, string type) =>
        sources
            .Select(s => KeyValuePair.Create(s.Key, StripComments(s.Value)))
            .Where(s => InsertIntoTable.IsMatch(s.Value)
                && (s.Value.Contains($"'{type}'", StringComparison.Ordinal)
                    || s.Value.Contains($"\"{type}\"", StringComparison.Ordinal)))
            .Select(s => s.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

    // This heuristic avoids comment-only emitters; it is not a C# or SQL string-literal parser.
    private static string StripComments(string source) =>
        Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*|--[^\r\n]*", " ");

    private static Dictionary<string, string> ProductionSources()
    {
        var root = SolutionLayout.RepoRoot;
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(root, f))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "migrations"), "*.sql")
                .Where(f => !f.EndsWith(SchemaMigration, StringComparison.Ordinal)));
        return files.ToDictionary(
            f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);
    }

    private static bool IsBuildOutput(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    /// <summary>Data rows of the first markdown table after <paramref name="heading"/>, split into trimmed cells.</summary>
    private static List<string[]> TableRows(string markdown, string heading)
    {
        var lines = markdown.Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimEnd() == heading);
        start.ShouldBeGreaterThanOrEqualTo(0, $"heading '{heading}' is missing");

        var rows = new List<string[]>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith('|') && !line.StartsWith("|---", StringComparison.Ordinal))
            {
                rows.Add(SplitCells(line));
            }
        }

        // The first collected row is the header line of the table.
        return rows.Skip(1).Where(r => r[0].StartsWith('`')).ToList();
    }

    private static string[] SplitCells(string row) =>
        Regex.Split(row.Trim().Trim('|'), @"(?<!\\)\|").Select(c => c.Trim()).ToArray();

    private static string Unquote(string cell) => cell.Trim('`');

    private static string ReadRepoFile(string relative)
    {
        var path = Path.Combine(SolutionLayout.RepoRoot, relative);
        File.Exists(path).ShouldBeTrue($"{relative} does not exist");
        return File.ReadAllText(path);
    }
}
