using System.Xml.Linq;

namespace Architecture.Tests.Rules;

/// <summary>
/// 06.3.6 AC1 and §8: the parser child is one console project that reads stdin, writes stdout/stderr and nothing else,
/// in a process (and a container) with no network and no database. It runs the 06.3.1 profile by <em>linking</em> the
/// profile sources rather than copying them, so the profile's limits and the child's transport are one implementation.
/// <para>
/// Those are structural facts about one project, so every rule here reads the real <c>.csproj</c> text and the real
/// source files instead of resolved types: the reference set, the (empty) package allow-list, the framework
/// references, the transitive project-reference closure from <c>Evidgate.slnx</c>, and the source text of the child's
/// own files plus every file its <c>&lt;Compile Include&gt;</c> globs link. Each rule takes its inputs explicitly, so a
/// synthetic <c>.csproj</c> or source text proves the rule, and the same function then runs over the real project.
/// </para>
/// </summary>
public static class ParserChildIsolationRules
{
    /// <summary>The child project's assembly name; also the key it has in the <c>Evidgate.slnx</c> reference graph.</summary>
    public const string ProjectName = "Evidgate.Parser";

    /// <summary>The child project file, repo-relative with '/'.</summary>
    public const string ProjectFile = "src/Evidgate.Parser/Evidgate.Parser.csproj";

    /// <summary>The child project's directory, repo-relative with '/'.</summary>
    public const string ProjectDirectory = "src/Evidgate.Parser/";

    /// <summary>The profile directory the child links (06.3.1): the only other place a reader is constructed.</summary>
    public const string ProfileDirectory = ParserConstructionRules.ProfileDirectory;

    /// <summary>The framework the console child must not reference: it is not an ASP.NET Core application.</summary>
    public const string ForbiddenFrameworkReference = "Microsoft.AspNetCore.App";

    private const string ModulePrefix = "Evidgate.Modules.";
    private const string EvidenceContractsProject = "Evidgate.Modules.Evidence.Contracts";

    /// <summary>
    /// The child's complete <c>ProjectReference</c> allow-list: the artifact contracts (the failure reasons and the
    /// media types it reports) and the shared kernel (its <c>Result</c> arm). Nothing else — no module implementation,
    /// no persistence, no pipeline, no observability stack.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedProjectReferences =
    [
        "Evidgate.BuildingBlocks.SharedKernel",
        EvidenceContractsProject,
    ];

    /// <summary>
    /// The child's complete closure allow-list: the same two projects, because <c>Evidence.Contracts</c> references
    /// only the shared kernel. Nothing else may be reachable — a database, mediator, cloud-SDK or telemetry project
    /// must never appear in the closure of a process that has neither a network nor a connection string.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedClosureProjects = AllowedProjectReferences;

    /// <summary>
    /// The child's complete <c>PackageReference</c> allow-list. Empty by default: the base class library is enough
    /// for a stdin/stdout process, so the rule denies every package unless a later task adds one here with a reason.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedPackageReferences = [];

    private static readonly string[] DeniedPackagePrefixes =
    [
        "Npgsql",
        "Microsoft.EntityFrameworkCore",
        "Dapper",
        "Mediator",
        "FluentValidation",
        "AWSSDK",
        "Amazon",
        "OpenTelemetry",
    ];

    /// <summary>
    /// APIs the child must not mention, in its own files and in the linked profile files: network access
    /// (<c>System.Net</c>, <c>HttpClient</c>, sockets, DNS), persistence (<c>Npgsql</c>, <c>DbContext</c>, the
    /// persistence building block), and the ASP.NET Core stack.
    /// </summary>
    private static readonly string[] ForbiddenApis =
    [
        "System.Net",
        "HttpClient",
        "Socket",
        "TcpClient",
        "Dns.",
        "Npgsql",
        "Microsoft.EntityFrameworkCore",
        "DbContext",
        "Microsoft.AspNetCore",
        "Evidgate.BuildingBlocks.Persistence",
    ];

    /// <summary>
    /// The only module namespaces the child may name: the artifact contracts it reports through, and the profile's own
    /// namespace (the linked sources declare it, so the child's entry point uses it).
    /// </summary>
    private static readonly string[] AllowedModulePrefixes =
    [
        "Evidgate.Modules.Evidence.Contracts",
        "Evidgate.Modules.Evidence.Infrastructure.Parsing",
    ];

    /// <summary>
    /// Member accesses the child's <em>own</em> files must not use: it reads stdin and writes stdout/stderr, so it
    /// opens no file, starts no process and reads no environment variable (the launcher applies the limits and hands
    /// the caps in as arguments). The linked profile files are exempt: the archive arm materialises entries into the
    /// temp directory the caller owns.
    /// </summary>
    private static readonly string[] ForbiddenChildApis =
    [
        "File.",
        "Directory.",
        "FileStream",
        "Process.",
        "Environment.GetEnvironmentVariable",
    ];

    /// <summary>
    /// AC1(a): every <c>ProjectReference</c> of the child resolves to a project in
    /// <paramref name="allowed"/>; one violation per other reference.
    /// </summary>
    /// <param name="projectXml">The child project's XML text.</param>
    /// <param name="projectName">The project's name, used in the message.</param>
    /// <param name="allowed">The allowed project names (csproj stems).</param>
    /// <returns>The violations, ordered by reference name.</returns>
    public static IReadOnlyList<string> ProjectReferenceViolations(
        string projectXml,
        string projectName,
        IReadOnlyList<string> allowed)
    {
        var violations = new List<string>();

        foreach (var reference in ProjectReferenceNames(projectXml))
        {
            if (allowed.Contains(reference, StringComparer.Ordinal))
            {
                continue;
            }

            violations.Add(
                $"{projectName} references {reference}: the parser child may reference only {Names(allowed)} "
                + "(06.3.6 AC1, §8)");
        }

        return violations;
    }

    /// <summary>
    /// AC1(b): every <c>PackageReference</c> of the child is on <paramref name="allowed"/> — which is empty, so the
    /// rule denies any package and names the family when the package is one of the known database, mediator,
    /// cloud-SDK or telemetry packages.
    /// </summary>
    /// <param name="projectXml">The child project's XML text.</param>
    /// <param name="projectName">The project's name, used in the message.</param>
    /// <param name="allowed">The allowed package ids.</param>
    /// <returns>The violations, in document order.</returns>
    public static IReadOnlyList<string> PackageReferenceViolations(
        string projectXml,
        string projectName,
        IReadOnlyList<string> allowed)
    {
        var violations = new List<string>();

        foreach (var package in ElementAttributes(projectXml, "PackageReference", "Include"))
        {
            if (allowed.Contains(package, StringComparer.Ordinal))
            {
                continue;
            }

            var denied = DeniedPackagePrefixes.FirstOrDefault(
                prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            violations.Add(denied is null
                ? $"{projectName} takes a package reference to {package}: the parser child carries "
                    + $"{Allowance(allowed)} (06.3.6 AC1, §8)"
                : $"{projectName} takes a package reference to {package}: {denied}* is a database, mediator, cloud-SDK "
                    + "or telemetry package, and the parser child runs without a network or a connection string "
                    + "(06.3.6 AC1, §8 AC4)");
        }

        return violations;
    }

    /// <summary>
    /// AC1(c): the child references no ASP.NET Core framework, so it cannot host HTTP, Kestrel or the hosting model.
    /// </summary>
    /// <param name="projectXml">The child project's XML text.</param>
    /// <param name="projectName">The project's name, used in the message.</param>
    /// <returns>The violations, in document order.</returns>
    public static IReadOnlyList<string> FrameworkReferenceViolations(string projectXml, string projectName) =>
        ElementAttributes(projectXml, "FrameworkReference", "Include")
            .Where(name => string.Equals(name, ForbiddenFrameworkReference, StringComparison.OrdinalIgnoreCase))
            .Select(name =>
                $"{projectName} references the {ForbiddenFrameworkReference} framework (declared as \"{name}\"): the "
                + "parser child is a console process that reads stdin and writes stdout, so the ASP.NET Core stack "
                + "cannot be part of it (06.3.6 AC1, §8)")
            .ToList();

    /// <summary>AC1(a): the <c>ProjectReference</c> names of a project file, in sorted order.</summary>
    /// <param name="projectXml">The project file XML text.</param>
    /// <returns>The referenced project names (csproj stems).</returns>
    public static IReadOnlyList<string> ProjectReferenceNames(string projectXml) =>
        ElementAttributes(projectXml, "ProjectReference", "Include")
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .Where(name => name.Length > 0)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// AC1(d): the raw <c>&lt;Compile Include&gt;</c> values of a project file, in document order — how the child
    /// links the profile sources instead of copying them.
    /// </summary>
    /// <param name="projectXml">The project file XML text.</param>
    /// <returns>The include values, still relative to the project directory.</returns>
    public static IReadOnlyList<string> CompileIncludes(string projectXml) =>
        ElementAttributes(projectXml, "Compile", "Include");

    /// <summary>
    /// AC1(d): the files a set of <c>&lt;Compile Include&gt;</c> values resolves to, with the project directory as the
    /// base. A value without a wildcard is the file itself; a value with one is a directory pattern (a single segment,
    /// or a recursive <c>**</c> one) enumerated under the directory it names. Build outputs are never linked.
    /// </summary>
    /// <param name="projectDirectory">The project's directory (absolute, or repo-relative from the working directory).</param>
    /// <param name="includes">The include values.</param>
    /// <returns>Absolute paths, '/'-separated and sorted.</returns>
    public static IReadOnlyList<string> ResolveLinkedSources(
        string projectDirectory,
        IEnumerable<string> includes)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var include in includes)
        {
            if (include.Trim().Length == 0)
            {
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(projectDirectory, include.Replace('\\', '/')))
                .Replace('\\', '/');

            if (!full.Contains('*', StringComparison.Ordinal))
            {
                if (File.Exists(full))
                {
                    files.Add(full);
                }

                continue;
            }

            var slash = full.LastIndexOf('/');
            if (slash <= 0)
            {
                continue;
            }

            var directory = full[..slash];
            var pattern = full[(slash + 1)..];
            var recursive = directory.Contains("**", StringComparison.Ordinal)
                || pattern.Contains("**", StringComparison.Ordinal);

            directory = directory.Replace("/**", string.Empty, StringComparison.Ordinal)
                .Replace("**", string.Empty, StringComparison.Ordinal)
                .TrimEnd('/');

            if (pattern.Length == 0 || !Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(
                directory,
                pattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                if (IsBuildOutput(directory, file))
                {
                    continue;
                }

                files.Add(file.Replace('\\', '/'));
            }
        }

        return files.ToList();
    }

    /// <summary>
    /// AC1(d): every <c>*.cs</c> file of a project directory (its own files, not the ones it links), build outputs
    /// excluded.
    /// </summary>
    /// <param name="projectDirectory">The project's directory.</param>
    /// <returns>Absolute paths, '/'-separated and sorted.</returns>
    public static IReadOnlyList<string> ProjectSources(string projectDirectory) =>
        Directory.EnumerateFiles(Path.GetFullPath(projectDirectory), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(projectDirectory, path))
            .Select(path => Path.GetFullPath(path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Reads the given source files as (path, text) pairs, ready for the source rules.</summary>
    /// <param name="paths">Absolute or repo-relative source paths.</param>
    /// <returns>The pairs, ordered by path.</returns>
    public static IReadOnlyList<(string Path, string Text)> ReadSources(IEnumerable<string> paths) =>
        paths.Order(StringComparer.Ordinal)
            .Select(path => (path.Replace('\\', '/'), File.ReadAllText(path)))
            .ToList();

    /// <summary>
    /// AC1(d): the child's own files and the files it links mention none of the forbidden APIs, and no module
    /// namespace but the artifact contracts and the profile's own. Comments and literals are blanked first, so a
    /// message that names <c>System.Net</c> is not a use of it.
    /// </summary>
    /// <param name="files">The child's own and linked files (path + text).</param>
    /// <returns>The violations, ordered by path.</returns>
    public static IReadOnlyList<string> ForbiddenApiViolations(IEnumerable<(string Path, string Text)> files)
    {
        var violations = new List<string>();

        foreach (var (path, text) in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var code = ParserConstructionRules.StripTrivia(text);

            foreach (var api in ForbiddenApis)
            {
                if (Mentions(code, api))
                {
                    violations.Add(
                        $"{path} mentions {api}: the parser child runs with no network, no database and no ASP.NET "
                        + "Core, so neither its own files nor the profile it links may use that API (06.3.6 §8 AC4)");
                }
            }

            foreach (var name in ModuleNamespaces(code))
            {
                if (AllowedModulePrefixes.Any(prefix => IsWithin(name, prefix)))
                {
                    continue;
                }

                violations.Add(
                    $"{path} refers to {name}: the parser child sees only {Names(AllowedModulePrefixes)} "
                    + "(06.3.6 §8)");
            }
        }

        return violations;
    }

    /// <summary>
    /// AC1(d), the transport half: the child's own files open no file, start no process and read no environment
    /// variable — the launcher applies the limits and the caps arrive as arguments, so the child reads stdin and
    /// writes stdout/stderr only. The linked profile files are deliberately not passed here.
    /// </summary>
    /// <param name="ownFiles">The child's own files (path + text).</param>
    /// <returns>The violations, ordered by path.</returns>
    public static IReadOnlyList<string> ChildIoViolations(IEnumerable<(string Path, string Text)> ownFiles)
    {
        var violations = new List<string>();

        foreach (var (path, text) in ownFiles.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var code = ParserConstructionRules.StripTrivia(text);

            foreach (var api in ForbiddenChildApis)
            {
                if (Mentions(code, api))
                {
                    violations.Add(
                        $"{path} uses {api}: the parser child reads stdin and writes stdout/stderr only — no file, "
                        + "no process, no environment variable (06.3.6 §7, AC1)");
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// AC1(e): walking the <c>Evidgate.slnx</c> project-reference graph from <paramref name="root"/>, every reachable
    /// project is on <paramref name="allowed"/>. A project the graph does not know (or a referenced project missing
    /// from the solution) is a violation too, so the rule cannot pass vacuously on a graph that omits the child.
    /// </summary>
    /// <param name="projectReferences">Project name → direct reference names.</param>
    /// <param name="root">The project to walk from.</param>
    /// <param name="allowed">The allowed closure.</param>
    /// <returns>The violations, in walk order.</returns>
    public static IReadOnlyList<string> ClosureViolations(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> projectReferences,
        string root,
        IReadOnlyList<string> allowed)
    {
        if (!projectReferences.ContainsKey(root))
        {
            return
            [
                $"{root} is not a project in Evidgate.slnx, so its reference closure cannot be checked (06.3.6 AC1)",
            ];
        }

        var violations = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { root };
        var pending = new Queue<string>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            var project = pending.Dequeue();

            if (!projectReferences.TryGetValue(project, out var references))
            {
                violations.Add(
                    $"{project} is reached from {root} but is not a project in Evidgate.slnx, so what it references "
                    + "cannot be checked (06.3.6 AC1)");

                continue;
            }

            foreach (var reference in references.Order(StringComparer.Ordinal))
            {
                if (!seen.Add(reference))
                {
                    continue;
                }

                pending.Enqueue(reference);

                if (!allowed.Contains(reference, StringComparer.Ordinal))
                {
                    violations.Add(
                        $"{root} reaches {reference} through {project}: {Why(reference)} (06.3.6 AC1, §8)");
                }
            }
        }

        return violations;
    }

    private static string Why(string project)
    {
        if (project.StartsWith("Evidgate.BuildingBlocks.Persistence", StringComparison.Ordinal)
            || project.Contains(".Persistence", StringComparison.Ordinal))
        {
            return "persistence (EF Core, Npgsql) cannot be reached from the no-network parser child";
        }

        if (project.StartsWith(ModulePrefix, StringComparison.Ordinal))
        {
            return "a module's implementation cannot be reached from the parser child; only "
                + $"{EvidenceContractsProject} and the shared kernel are";
        }

        return $"only {Names(AllowedClosureProjects)} may be part of the child's closure";
    }

    private static List<string> ElementAttributes(string projectXml, string element, string attribute) =>
        XDocument.Parse(projectXml, LoadOptions.None)
            .Descendants(element)
            .Select(e => ((string?)e.Attribute(attribute) ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .ToList();

    /// <summary>
    /// True when <paramref name="token"/> occurs in <paramref name="code"/> at a boundary — the character before it is
    /// not part of an identifier, so <c>Directory.</c> matches <c>System.IO.Directory.</c> but the <c>File.</c> inside
    /// <c>ZipFile.</c> does not.
    /// </summary>
    /// <param name="code">The source with comments and literals blanked.</param>
    /// <param name="token">The literal to find.</param>
    private static bool Mentions(string code, string token)
    {
        var from = 0;

        while (true)
        {
            var at = code.IndexOf(token, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            if (at == 0 || !IsIdentifier(code[at - 1]))
            {
                return true;
            }

            from = at + 1;
        }
    }

    /// <summary>Every qualified name starting with <c>Evidgate.Modules.</c> in the code, deduplicated.</summary>
    /// <param name="code">The source with comments and literals blanked.</param>
    private static List<string> ModuleNamespaces(string code)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var from = 0;

        while (true)
        {
            var at = code.IndexOf(ModulePrefix, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return names.ToList();
            }

            if (at > 0 && IsIdentifier(code[at - 1]))
            {
                from = at + 1;

                continue;
            }

            var end = at + ModulePrefix.Length;
            while (end < code.Length && (IsIdentifier(code[end]) || code[end] == '.'))
            {
                end++;
            }

            names.Add(code[at..end].TrimEnd('.'));
            from = end;
        }
    }

    /// <summary>True when <paramref name="name"/> is <paramref name="prefix"/> or lives inside it.</summary>
    /// <param name="name">The qualified name found in the code.</param>
    /// <param name="prefix">The allowed namespace.</param>
    private static bool IsWithin(string name, string prefix) =>
        string.Equals(name, prefix, StringComparison.Ordinal)
        || name.StartsWith(prefix + ".", StringComparison.Ordinal);

    private static bool IsIdentifier(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static bool IsBuildOutput(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');

        return relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment =>
                string.Equals(segment, "bin", StringComparison.Ordinal)
                || string.Equals(segment, "obj", StringComparison.Ordinal));
    }

    private static string Names(IReadOnlyList<string> values) =>
        values.Count == 0 ? "nothing" : string.Join(", ", values);

    private static string Allowance(IReadOnlyList<string> packages) =>
        packages.Count == 0 ? "no package" : $"only {string.Join(", ", packages)}";
}
