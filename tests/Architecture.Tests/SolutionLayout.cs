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
