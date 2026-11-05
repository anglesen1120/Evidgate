// The one rule that keeps a release decision honest: a run joins a release by digest match, never by recency, and
// the binding lives in `release_run` — the run row carries no release pointer to be rewritten later.
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// The rule as a pure function over SQL text, plus its run over every Orchestrator source file, so a "latest run
/// wins" join or a <c>run.release_id</c> column cannot be reintroduced without a failing test.
/// </summary>
public sealed class ReleaseArchitectureTests
{
    /// <summary>The "newest run" selector: an ordered read that takes one row, or a <c>max(created_at)</c> subquery.</summary>
    private static readonly Regex NewestRun = new(
        @"order\s+by[^;]*\bcreated_at\b[^;]*\blimit\s+1\b|select\s+max\s*\(\s*[^)]*created_at",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>The release side of a join: the release or one of its components.</summary>
    private static readonly Regex ReleaseSide = new(
        @"\bpublic\.release(?:_component)?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The run table as a joined table, wherever it is named.</summary>
    private static readonly Regex RunTable = new(
        @"\bpublic\.run\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A write that would store the release pointer on the run itself.</summary>
    private static readonly Regex RunWrite = new(
        @"\b(insert\s+into|update)\s+(public\.)?run\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Every C# source of the module that owns the release and run tables.</summary>
    private static IReadOnlyList<(string Path, string Text)> ModuleSources()
    {
        var root = Path.Combine(SolutionLayout.RepoRoot, "src", "Modules", "Evidgate.Modules.Orchestrator");

        var paths = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal);

        return [.. paths.Select(path => (Path.GetRelativePath(SolutionLayout.RepoRoot, path), File.ReadAllText(path)))];
    }

    /// <summary>The statements of a source file, split on the semicolon that ends them.</summary>
    private static IEnumerable<(string Path, string Text)> Statements(IEnumerable<(string Path, string Text)> sources) =>
        sources.SelectMany(source => source.Text
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(statement => (source.Path, Text: statement)));

    /// <summary>One violation per statement that picks a run for a release by recency or stores a release pointer on it.</summary>
    /// <remarks>
    /// The recency rule looks at any statement that spans a release and the run table - the binding itself
    /// (<c>release_run</c>), or a direct release-to-run join - because both are the shape "which run belongs to this
    /// release" takes. A worker sweep ordering runs by <c>created_at</c> on its own binds nothing and stays legal.
    /// </remarks>
    private static IReadOnlyList<string> FindViolations(IEnumerable<(string Path, string Text)> sources) =>
    [
        .. Statements(sources).SelectMany(statement =>
        {
            var violations = new List<string>();
            var binds = statement.Text.Contains("public.release_run", StringComparison.Ordinal)
                || (ReleaseSide.IsMatch(statement.Text) && RunTable.IsMatch(statement.Text));

            if (binds && NewestRun.IsMatch(statement.Text))
            {
                violations.Add($"{statement.Path} picks a run for a release by recency: {Squash(statement.Text)}");
            }

            if (RunWrite.IsMatch(statement.Text)
                && statement.Text.Contains("release_id", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{statement.Path} writes release_id on the run: {Squash(statement.Text)}");
            }

            return violations;
        }),
    ];

    private static string Squash(string text) =>
        Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    [Fact]
    public void Synthetic_NewestRunSelectorOfARelease_IsDetected()
    {
        var sources = new (string, string)[]
        {
            ("Persistence/Bypass.cs", """
                select rr.run_id from public.release_run rr
                  join public.run r on r.id = rr.run_id
                 order by r.created_at desc limit 1
                """),
            ("Persistence/Bypass.cs", """
                select component_key from public.release_run
                 where created_at = (select max(created_at) from public.release_run)
                """),
        };

        FindViolations(sources).Count.ShouldBe(2);
    }

    /// <summary>
    /// The same rule without <c>release_run</c>: a release joined straight to a run and ordered by creation is the
    /// "latest run of this release" query in its other spelling, so the guard must see it too.
    /// </summary>
    [Fact]
    public void Synthetic_ReleaseToRunJoin_IsDetectedWithoutReleaseRun()
    {
        var sources = new (string, string)[]
        {
            ("Persistence/Bypass.cs", """
                select r.id from public.release rel
                  join public.run r on r.project_id = rel.project_id
                 order by r.created_at desc limit 1
                """),
            ("Persistence/Bypass.cs", """
                select c.component_key from public.release_component c
                  join public.run r on r.project_id = c.org_id
                 where r.created_at = (select max(created_at) from public.run)
                """),
        };

        FindViolations(sources).Count.ShouldBe(2, "a release-to-run recency read is the rule's target even unnamed");
    }

    [Fact]
    public void Synthetic_ReleasePointerOnTheRun_IsDetected()
    {
        var sources = new (string, string)[]
        {
            ("Persistence/Bypass.cs", "update public.run set release_id = @release_id where id = @run_id"),
            ("Persistence/Bypass.cs", "insert into public.run (id, release_id) values (@id, @release_id)"),
        };

        FindViolations(sources).Count.ShouldBe(2);
    }

    [Fact]
    public void Synthetic_BoundRunOrdering_IsNot_A_Violation()
    {
        // Ordering the bindings of one release is the required read; only selecting a run by recency is forbidden.
        var sources = new (string, string)[]
        {
            ("Persistence/Bypass.cs", """
                select run_id, component_key, created_at from public.release_run
                 where org_id = @org_id and release_id = @release_id order by created_at, id
                """),
            ("Persistence/Bypass.cs", """
                select r.id from public.run r order by r.created_at desc limit 1
                """),
        };

        FindViolations(sources).ShouldBeEmpty("a sweep over runs alone does not bind a release");
    }

    [Fact]
    public void NoRecencyJoin()
    {
        var sources = ModuleSources();

        sources.ShouldNotBeEmpty("the scan must read the module's sources, or it would pass vacuously");
        Statements(sources)
            .ShouldContain(statement => statement.Text.Contains("public.release_run", StringComparison.Ordinal),
                "the bind path's SQL is what this rule exists to guard");
        Statements(sources)
            .ShouldContain(statement => statement.Text.Contains("public.release_component", StringComparison.Ordinal));

        FindViolations(sources).ShouldBeEmpty();
    }
}
