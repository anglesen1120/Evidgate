using System.Reflection;
using System.Xml.Linq;

namespace Architecture.Tests;

/// <summary>
/// Loads the real inputs the architecture rules run against: the project
/// reference graph parsed from <c>Evidgate.slnx</c> + every <c>*.csproj</c>, and
/// the <c>Evidgate.*</c> assemblies copied into this project's output directory
/// by its ProjectReferences.
/// </summary>
public static class SolutionLayout
{
    public static string RepoRoot { get; } = LocateRepoRoot();

    /// <summary>Project paths listed in Evidgate.slnx, repo-relative with '/'.</summary>
    public static IReadOnlyList<string> SlnxProjectPaths()
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot, "Evidgate.slnx"));
        return doc.Descendants("Project")
            .Select(e => Normalize((string?)e.Attribute("Path") ?? string.Empty))
            .Where(p => p.Length > 0)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Project name (file stem) → names of its direct ProjectReferences.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> ProjectReferenceGraph()
    {
        var graph = new SortedDictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

        foreach (var projectPath in SlnxProjectPaths())
        {
            var fullPath = Path.Combine(RepoRoot, projectPath);
            var doc = XDocument.Load(fullPath);
            var references = doc.Descendants("ProjectReference")
                .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
                .Where(i => i.Length > 0)
                .Select(i => Path.GetFileNameWithoutExtension(Normalize(i)))
                .ToList();

            graph[Path.GetFileNameWithoutExtension(projectPath)] = references;
        }

        return graph;
    }

    /// <summary>Names of the test project's own ProjectReferences (csproj basename).</summary>
    public static IReadOnlyCollection<string> TestProjectReferences()
    {
        var csproj = Path.Combine(RepoRoot, "tests", "Architecture.Tests", "Architecture.Tests.csproj");
        return XDocument.Load(csproj)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(Normalize((string?)e.Attribute("Include") ?? string.Empty)))
            .Where(n => n.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Every <c>Evidgate.*.dll</c> in the test output directory, loaded for
    /// reflection. ProjectReferences in the csproj guarantee the production
    /// assemblies are copied here.
    /// </summary>
    public static IReadOnlyList<Assembly> EvidgateAssemblies() =>
        Directory.GetFiles(AppContext.BaseDirectory, "Evidgate.*.dll")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(Assembly.LoadFrom)
            .ToList();

    /// <summary>All declared types across the Evidgate.* assemblies, tolerating unloadable types.</summary>
    public static IReadOnlyList<Type> EvidgateTypes() =>
        EvidgateAssemblies()
            .SelectMany(Rules.ArchitectureRules.SafeGetTypes)
            .ToList();

    /// <summary>
    /// Every hand-written <c>src/**/*.cs</c> file as a repo-relative path with '/', sorted ordinally. A file is
    /// build output when a directory below <c>src</c> is named <c>bin</c> or <c>obj</c>.
    /// </summary>
    public static IReadOnlyList<string> ProductionSourceFiles() => SourceFiles("src");

    /// <summary>
    /// Every <c>*.cs</c> file below <paramref name="repoRelativeRoot"/> as a repo-relative path with '/', sorted
    /// ordinally, minus build output (see <see cref="IsBuildOutput"/>). The one place that filters; a missing root
    /// throws so a renamed directory cannot turn a rule green on an empty set.
    /// </summary>
    public static IReadOnlyList<string> SourceFiles(string repoRelativeRoot) => SourceFiles(RepoRoot, repoRelativeRoot);

    /// <summary><see cref="SourceFiles(string)"/> against an explicit checkout root, for tests on a temporary tree.</summary>
    public static IReadOnlyList<string> SourceFiles(string repoRoot, string repoRelativeRoot)
    {
        var root = Path.Combine(repoRoot, repoRelativeRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Source root not found: {root}");
        }

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(root, path))
            .Select(path => Normalize(Path.GetRelativePath(repoRoot, path)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary><see cref="SourceFiles(string)"/> with each file's text.</summary>
    public static List<(string Path, string Text)> SourcesOf(string repoRelativeRoot) =>
        SourceFiles(repoRelativeRoot)
            .Select(path => (path, File.ReadAllText(Path.Combine(RepoRoot, path))))
            .ToList();

    /// <summary><see cref="ProductionSourceFiles"/> with each file's text.</summary>
    public static List<(string Path, string Text)> ProductionSources() => SourcesOf("src");

    /// <summary>
    /// True when a directory below <paramref name="srcRoot"/> is named <c>bin</c> or <c>obj</c>. Only the part of
    /// the path below the source root is inspected, so the directories the checkout lives in never count.
    /// </summary>
    public static bool IsBuildOutput(string srcRoot, string path)
    {
        var segments = Path.GetRelativePath(srcRoot, path).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments[..^1].Any(segment =>
            string.Equals(segment, "bin", StringComparison.Ordinal)
            || string.Equals(segment, "obj", StringComparison.Ordinal));
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Evidgate.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Could not locate Evidgate.slnx above {AppContext.BaseDirectory}");
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
