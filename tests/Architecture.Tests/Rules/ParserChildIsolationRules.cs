using System.Text.Json;
using System.Xml.Linq;

namespace Architecture.Tests.Rules;

/// <summary>
/// 06.3.6 AC1 and §8: the parser child is one console project that reads stdin, writes stdout/stderr and nothing else,
/// in a process (and a container) with no network and no database. It runs the 06.3.1 profile by <em>linking</em> the
/// profile sources rather than copying them, so the profile's limits and the child's transport are one implementation.
/// <para>
/// Those are structural facts about one project, so every rule here reads the real <c>.csproj</c> text and the real
/// source files instead of resolved types: the reference set, the (empty) package allow-list plus the resolved
/// <c>packages.lock.json</c>, the framework references, the contracts project's friend-assembly set, the transitive
/// project-reference closure from <c>Evidgate.slnx</c>, and the source text of the child's own files plus every file
/// its <c>&lt;Compile Include&gt;</c> globs link. Each rule takes its inputs explicitly, so a synthetic <c>.csproj</c>
/// or source text proves the rule, and the same function then runs over the real project.
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

    /// <summary>The child's restore graph, repo-relative with '/': what the build actually resolves, not what the
    /// project file asks for.</summary>
    public const string LockFile = "src/Evidgate.Parser/packages.lock.json";

    /// <summary>The contracts project whose internals the profile and the child construct the report handle with.</summary>
    public const string ContractsProjectFile =
        "src/Modules/Evidgate.Modules.Evidence.Contracts/Evidgate.Modules.Evidence.Contracts.csproj";

    /// <summary>
    /// The child's complete restore allow-list: the two projects its project file references (the artifact contracts
    /// and the shared kernel), plus the analyzer package the build adds to every project. Anything else in
    /// <c>packages.lock.json</c> — a transitive dependency included — is code the no-network child would run.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedLockedPackages =
    [
        "Evidgate.BuildingBlocks.SharedKernel",
        EvidenceContractsProject,
        "Microsoft.VisualStudio.Threading.Analyzers",
    ];

    /// <summary>
    /// <c>Evidgate.Modules.Evidence.Contracts</c>' complete friend-assembly set: the module that owns the profile and
    /// the parser child that links it, because both construct <c>ParsedXmlReport</c> through the profile's factory
    /// (06.3.1-T1: its constructor is internal for exactly that reason, 06.3.6 AC1). A third friend would let another
    /// assembly build the report handle outside the profile.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedFriendAssemblies =
    [
        "Evidgate.Modules.Evidence",
        ProjectName,
    ];

    /// <summary>
    /// Package families a lock file must never resolve for the child: the web stack, <c>System.Net</c>, the database
    /// drivers and the ORM/micro-ORM, and the cloud SDKs — reachable code in a process that has no network and no
    /// connection string.
    /// </summary>
    private static readonly string[] DeniedLockedPackagePrefixes =
    [
        "Microsoft.AspNetCore",
        "System.Net",
        "Npgsql",
        "Microsoft.EntityFrameworkCore",
        "EntityFrameworkCore",
        "Dapper",
        "AWSSDK",
    ];

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
    /// The <c>System.Net.Http</c> types a bare implicit using puts in scope, denied by name. <c>ImplicitUsings</c> is
    /// enabled repo-wide (<c>Directory.Build.props</c>), so every file of a <c>Microsoft.NET.Sdk</c> project compiles
    /// with <c>global using System.Net.Http;</c>: <c>var client = new HttpClient();</c> needs no
    /// <c>using System.Net;</c> line for the namespace match above to catch. The alternative — requiring
    /// <c>&lt;ImplicitUsings&gt;disable&lt;/ImplicitUsings&gt;</c> in the child's project — would mean an explicit
    /// <c>using</c> in each of its four files and would still be undone by a <c>global using</c> in a props file, so
    /// the rule takes the other route: the implicit usings stay, and the type names they expose are denied here, so
    /// an unqualified use is a violation with no namespace text to match. <c>SocketsHttpHandler</c> and
    /// <c>HttpClientHandler</c> need no entry of their own: the <c>Socket</c> and <c>HttpClient</c> tokens in
    /// <see cref="ForbiddenApis"/> match them at a boundary.
    /// </summary>
    private static readonly string[] ImplicitNetworkApis =
    [
        "HttpRequestMessage",
        "HttpResponseMessage",
        "HttpMethod",
        "HttpContent",
        "StringContent",
        "FormUrlEncodedContent",
        "ByteArrayContent",
        "MultipartFormDataContent",
        "HttpMessageHandler",
        "HttpRequestException",
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
    /// Member accesses and types the child's <em>own</em> files must not use: it reads stdin and writes stdout/stderr,
    /// so it opens no file, starts no process, reads no environment variable, resolves no path and loads no native
    /// library. The linked profile
    /// files are deliberately not scanned here — the profile owns the archive's file creation for the callers that
    /// extract (see <see cref="ChildExtractionCodeViolations"/> for the rule that keeps that arm out of the child).
    /// <para>
    /// <c>FileStream</c> itself is not banned, and <c>FileMode.</c>/<c>FileStreamOptions</c> are: the child wraps its
    /// standard output in <c>new FileStream(new SafeFileHandle((nint)1, ownsHandle: false), FileAccess.Write,
    /// bufferSize: 0)</c> because the .NET console stream swallows a broken pipe on Unix and the child has to report it
    /// (06.3.6 §7 AC3) — no path and no file is opened by that constructor. Every <em>path</em> constructor of
    /// <c>FileStream</c> names <c>FileMode</c> or <c>FileStreamOptions</c> (there is no <c>FileStream(string, …)</c>
    /// overload without one), so banning those two tokens keeps the "opens no file" guarantee exact without forbidding
    /// the descriptor stream.
    /// </para>
    /// <para>
    /// <c>ImplicitUsings</c> is enabled repo-wide (<c>Directory.Build.props</c>), so these files also compile with
    /// <c>global using System.IO;</c>: an unqualified <c>File.ReadAllBytes</c> or <c>new StreamReader(path)</c> needs
    /// no using line, which is why the list holds these type and member names rather than a namespace, and why
    /// <c>System.IO</c> is denied as text too — that one token covers a qualified use, a <c>using static System.IO</c>,
    /// an <c>using F = System.IO.File</c> alias and <c>System.IO.Pipes</c> at once.
    /// </para>
    /// <para>
    /// Native code and the launcher's own environment are denied the same way: <c>NativeLibrary</c> and
    /// <c>Marshal.</c> keep the child inside the runtime the launcher started — a process that loads a library by
    /// name or marshals its own pointers is doing something the profile does not — and
    /// <c>Environment.SetEnvironmentVariable</c>/<c>Environment.GetFolderPath</c> complete the environment rule:
    /// the child neither reads nor writes one. <c>System.Diagnostics.Process</c> is listed for its message; the name
    /// itself is denied as an identifier by <see cref="ForbiddenChildIdentifiers"/>, because a parameter type
    /// (<c>Process p</c>) or a target-typed <c>new()</c> carries no <c>.</c> or constructor text for a token to
    /// match.
    /// </para>
    /// <para>
    /// <c>DllImport</c>/<c>LibraryImport</c> are not on this list because the child has exactly <em>one</em> legal
    /// native import — libc's <c>setrlimit</c>, which the launcher cannot apply for it (the chiselled image has no
    /// launcher) and which 06.3.6 §7 requires the child to set on itself. That import is not a token here but a
    /// declaration rule of its own (<see cref="NativeImportViolations"/>): the exact attribute, library and entry
    /// point, declared once, in the child's own project — while every other import and every second one, in the
    /// child's files or in the profile it links, keeps failing the rule.
    /// </para>
    /// </summary>
    private static readonly string[] ForbiddenChildApis =
    [
        "File.",
        "Directory.",
        "FileMode.",
        "FileStreamOptions",
        "FileInfo",
        "DirectoryInfo",
        "StreamReader(",
        "StreamWriter(",
        "Path.GetTemp",
        "System.IO",
        "ZipFile.",
        "System.Diagnostics.Process",
        "ProcessStartInfo",
        "NativeLibrary",
        "Marshal.",
        "Environment.GetEnvironmentVariable",
        "Environment.SetEnvironmentVariable",
        "Environment.GetFolderPath",
        "Environment.ExpandEnvironmentVariables",
        "Environment.CurrentDirectory",
    ];

    /// <summary>
    /// The one native import the child may declare (06.3.6 §7, hand-over Notes <c>Self-applied limits</c>): the
    /// C library it comes from, and the entry point it must name. <c>RLIMIT_CPU</c>, <c>RLIMIT_FSIZE</c> and
    /// <c>RLIMIT_CORE</c> are all applied by this one call, so one import covers the whole limit contract.
    /// </summary>
    public const string AllowedNativeImportLibrary = "libc";

    /// <summary>The single entry point the one allowed import may name.</summary>
    public const string AllowedNativeImportEntryPoint = "setrlimit";

    /// <summary>The two attribute spellings that declare a native import; both are scanned, one is allowed.</summary>
    private static readonly string[] NativeImportAttributes = ["DllImport", "LibraryImport"];

    /// <summary>
    /// Identifiers the child's own files must not use as a name at all, whatever qualifies them. The token list above
    /// matches spellings that carry a member access or a constructor; a type used as a name carries neither, so
    /// <c>Process p = …;</c> and <c>Process p = new();</c> — a target-typed construction, which no token can see —
    /// need the identifier itself. The child starts no process (the launcher does, once, and hands it stdin), so the
    /// name has no legitimate use; a use is reported once per file, by the more specific entry when one matched it.
    /// </summary>
    private static readonly string[] ForbiddenChildIdentifiers =
    [
        "Process",
    ];

    /// <summary>
    /// The profile's <em>file-creating</em> archive arm, which the child's own files must never name:
    /// <c>SafeZipArchive.Extract</c> (the entry point), <c>ArchiveExtraction</c> (the handle that owns the temp root),
    /// and the <c>extract:</c> argument that turns the walk into the writing one. The code is linked into the child
    /// (one implementation of the caps, 06.3.6 D-06.3.6-1), so "the child cannot reach it" is a fact this rule asserts
    /// rather than one the linker provides: the child reports archive entries through the non-extracting
    /// <c>SafeZipArchive.Enumerate</c>, and a later edit that reached for the writing arm would fail the build.
    /// <c>Extract</c> matches every longer spelling at a boundary too (<c>ExtractToDirectory</c>, <c>ExtractAsync</c>),
    /// so one token covers the family; <c>ArchiveExtraction</c> needs its own because <c>Extract</c> sits inside that
    /// identifier and cannot match there.
    /// </summary>
    private static readonly string[] ForbiddenChildExtractionApis =
    [
        "Extract",
        "ArchiveExtraction",
        "extract:",
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

            // The implicit `global using System.Net.Http;` (Directory.Build.props enables ImplicitUsings for every
            // project) makes these types reachable without a using line, so each one is denied by name.
            foreach (var api in ImplicitNetworkApis)
            {
                if (Mentions(code, api))
                {
                    violations.Add(
                        $"{path} mentions {api}: `ImplicitUsings` puts System.Net.Http in scope for every file of the "
                        + "child, so the network types are denied by name — the parser child runs with no network "
                        + "(06.3.6 §8 AC4)");
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
    /// AC1(d), the transport half: the child's own files open no file, start no process, read no environment variable,
    /// resolve no temp path and load no native library beyond the one declaration
    /// <see cref="NativeImportViolations"/> licenses, so the child reads stdin, writes stdout/stderr and sets its own
    /// rlimits only. The linked profile files are deliberately not passed here: the profile's extraction arm
    /// legitimately creates files for the callers that own a temp root (and
    /// <see cref="ChildExtractionCodeViolations"/> is what keeps that arm out of the child).
    /// </summary>
    /// <param name="ownFiles">The child's own files (path + text).</param>
    /// <returns>The violations, ordered by path.</returns>
    public static IReadOnlyList<string> ChildIoViolations(IEnumerable<(string Path, string Text)> ownFiles)
    {
        var violations = new List<string>();

        foreach (var (path, text) in ownFiles.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var code = ParserConstructionRules.StripTrivia(text);
            var matched = ForbiddenChildApis.Where(api => Mentions(code, api)).ToList();

            foreach (var api in matched)
            {
                violations.Add(ChildIoViolation(path, api));
            }

            // Native imports have their own declaration rule (the one legal libc setrlimit), so the token list no
            // longer carries their names: this is where a file's own import is judged, including the second copy
            // of the allowed one.
            violations.AddRange(NativeImportViolations([(path, text)]));

            // A bare identifier is the last net, for the spellings no token matches (a parameter type, a target-typed
            // `new()`). One use is reported once: when an entry above already named `Process`
            // (`System.Diagnostics.Process`), that entry's message is the one.
            foreach (var identifier in ForbiddenChildIdentifiers)
            {
                if (matched.Any(api => NamesSameIdentifier(api, identifier))
                    || !MentionsIdentifier(code, identifier))
                {
                    continue;
                }

                violations.Add(ChildIoViolation(path, identifier));
            }
        }

        return violations;
    }

    private static string ChildIoViolation(string path, string api) =>
        $"{path} uses {api}: the parser child reads stdin and writes stdout/stderr only — no file, no process, "
        + "no environment variable, no temp path, no native library (06.3.6 §7, AC1)";

    /// <summary>
    /// 06.3.6 §7, hand-over Notes <c>Self-applied limits</c>: the child may declare <em>one</em> native import —
    /// libc's <c>setrlimit</c>, which applies <c>RLIMIT_CPU</c>, <c>RLIMIT_FSIZE</c> and <c>RLIMIT_CORE</c> to the
    /// process the child is — and nothing else. The chiselled image carries no launcher, so this is the only way
    /// the limits can be applied at all; every other import (a different library, a different entry point, the
    /// source-generated <c>LibraryImport</c> spelling, or a second copy of the allowed one) is code the profile
    /// does not have and the child must not run.
    /// <para>
    /// The shape is checked rather than the intent: the declaration must be the <c>DllImport</c> attribute with
    /// <c>"libc"</c>, <c>EntryPoint = "setrlimit"</c> and <c>SetLastError = true</c>, on an <c>extern</c> method
    /// taking a <c>ref</c> value, and it must sit in the child's own project — the linked profile sources may
    /// declare none, because the profile is also compiled in-process where no limit may be set. Comments and
    /// literals are blanked before the scan, so a comment naming <c>setrlimit</c> is not a declaration, and every
    /// structural part of the declaration (<c>SetLastError = true</c>, <c>extern</c>, <c>ref</c>) is checked on that
    /// blanked copy: trivia inside the declaration cannot stand in for a missing named argument or keyword. Only the
    /// two string literals — the library and the entry point — are read from the original text, each at its own
    /// position, so a comment cannot supply them and a non-literal argument is not the allowed declaration.
    /// </para>
    /// </summary>
    /// <param name="files">The child's own files and the profile files it links (path + text).</param>
    /// <returns>The violations, ordered by path and position.</returns>
    public static IReadOnlyList<string> NativeImportViolations(IEnumerable<(string Path, string Text)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var violations = new List<string>();
        var allowed = 0;

        foreach (var (path, text) in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var code = ParserConstructionRules.StripTrivia(text);

            foreach (var at in NativeImportMentions(code))
            {
                if (IsAllowedNativeImport(path, code, at, text))
                {
                    allowed++;

                    if (allowed > 1)
                    {
                        violations.Add(
                            $"{path} declares a second native import: the parser child declares libc's "
                            + $"{AllowedNativeImportEntryPoint} exactly once and imports no other native symbol "
                            + "(06.3.6 §7, AC1)");
                    }

                    continue;
                }

                violations.Add(
                    $"{path} declares {NativeImportSpelling(code, at)}: the parser child declares libc's "
                    + $"{AllowedNativeImportEntryPoint} (RLIMIT_CPU, RLIMIT_FSIZE, RLIMIT_CORE) once, in its own "
                    + "project, and no other native import — the profile declares none (06.3.6 §7, AC1)");
            }
        }

        return violations;
    }

    /// <summary>
    /// How many declarations of the allowed native import <paramref name="files"/> carry. The real-solution test
    /// asserts this is exactly one, so a clean rule cannot pass because the import silently disappeared.
    /// </summary>
    /// <param name="files">The child's own files and the profile files it links (path + text).</param>
    /// <returns>The number of allowed declarations found.</returns>
    public static int AllowedNativeImportCount(IEnumerable<(string Path, string Text)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        return files.Sum(file =>
        {
            var code = ParserConstructionRules.StripTrivia(file.Text);

            return NativeImportMentions(code).Count(at => IsAllowedNativeImport(file.Path, code, at, file.Text));
        });
    }

    /// <summary>Every position in <paramref name="code"/> where a native-import attribute is named.</summary>
    /// <param name="code">The source with comments and literals blanked.</param>
    /// <returns>The positions, in order.</returns>
    private static List<int> NativeImportMentions(string code)
    {
        var positions = new SortedSet<int>();

        foreach (var attribute in NativeImportAttributes)
        {
            var from = 0;

            while (true)
            {
                var at = code.IndexOf(attribute, from, StringComparison.Ordinal);
                if (at < 0)
                {
                    break;
                }

                // A mention is a use when nothing identifier-like precedes it (`[DllImport`, `…Services.DllImport`),
                // which is the same boundary the token rules use: a longer identifier that merely contains the
                // name is someone else's type.
                if (at == 0 || !IsIdentifier(code[at - 1]))
                {
                    positions.Add(at);
                }

                from = at + 1;
            }
        }

        return positions.ToList();
    }

    /// <summary>The attribute spelling that matched at <paramref name="at"/>, for the message.</summary>
    /// <param name="code">The source with comments and literals blanked.</param>
    /// <param name="at">The match position.</param>
    private static string NativeImportSpelling(string code, int at) =>
        code.AsSpan(at).StartsWith("LibraryImport", StringComparison.Ordinal) ? "LibraryImport" : "DllImport";

    /// <summary>
    /// True when the declaration at <paramref name="at"/> is the one legal native import: in the child's own
    /// project, the <c>DllImport</c> attribute (not <c>LibraryImport</c>, not <c>DllImportAttribute</c>), the
    /// <c>"libc"</c> library, the <c>setrlimit</c> entry point, <c>SetLastError = true</c>, on an <c>extern</c>
    /// method taking a <c>ref</c> value.
    /// </summary>
    /// <param name="path">The file the declaration is in.</param>
    /// <param name="code">The file's source with comments and literals blanked.</param>
    /// <param name="at">The attribute's position.</param>
    /// <param name="text">The file's original source, read for the attribute's string literals.</param>
    private static bool IsAllowedNativeImport(string path, string code, int at, string text)
    {
        // The allowed declaration lives in the child's own project files; a path is repo-relative in a synthetic
        // case and absolute in the real-solution scan, so the project directory is matched in both shapes.
        var normalizedPath = path.Replace('\\', '/');
        if (!normalizedPath.StartsWith(ProjectDirectory, StringComparison.Ordinal)
            && !normalizedPath.Contains("/" + ProjectDirectory, StringComparison.Ordinal))
        {
            return false;
        }

        // `LibraryImport` and `DllImportAttribute` are different spellings of a different declaration: the rule
        // allows one exact form, so the name must end at a boundary and be followed by its argument list.
        var after = at + "DllImport".Length;
        if (!code.AsSpan(at).StartsWith("DllImport", StringComparison.Ordinal)
            || after >= code.Length
            || IsIdentifier(code[after]))
        {
            return false;
        }

        // The attribute must open an attribute section and be followed by its argument list, so a bare mention or a
        // member access cannot pass for a declaration.
        var section = at - 1;
        while (section >= 0 && char.IsWhiteSpace(code[section]))
        {
            section--;
        }

        var open = after;
        while (open < code.Length && char.IsWhiteSpace(code[open]))
        {
            open++;
        }

        if (section < 0 || code[section] != '[' || open >= code.Length || code[open] != '(')
        {
            return false;
        }

        var end = code.IndexOf(';', open);
        if (end < 0)
        {
            return false;
        }

        // The two string literals are located in the blanked copy (whose indices are the original ones) and read from
        // the original text at exactly those positions: a comment can neither supply them nor hide a different value.
        // The step over whitespace reads the original text, because the blanked copy turns the literal itself into
        // spaces and would step straight past it.
        var libraryAt = open + 1;
        while (libraryAt < end && char.IsWhiteSpace(text[libraryAt]))
        {
            libraryAt++;
        }

        var entryPointAt = code.IndexOf("EntryPoint", open, end - open, StringComparison.Ordinal);
        var entryPointValueAt = -1;
        if (entryPointAt > 0)
        {
            var equals = code.IndexOf('=', entryPointAt + "EntryPoint".Length);
            if (equals > 0 && equals < end)
            {
                entryPointValueAt = equals + 1;
                while (entryPointValueAt < end && char.IsWhiteSpace(text[entryPointValueAt]))
                {
                    entryPointValueAt++;
                }
            }
        }

        if (entryPointValueAt < 0)
        {
            return false;
        }

        // Everything else is structural and is checked on the blanked copy: nothing the compiler treats as a comment
        // or a literal can stand in for a named argument or a keyword.
        var shape = WithoutWhitespace(code[open..end]);

        return string.Equals(ReadStringLiteral(text, libraryAt), "\"" + AllowedNativeImportLibrary + "\"", StringComparison.Ordinal)
            && string.Equals(ReadStringLiteral(text, entryPointValueAt), "\"" + AllowedNativeImportEntryPoint + "\"", StringComparison.Ordinal)
            && shape.Contains("SetLastError=true", StringComparison.Ordinal)
            && shape.Contains(")]", StringComparison.Ordinal)
            && shape.Contains("extern", StringComparison.Ordinal)
            && shape.Contains("ref", StringComparison.Ordinal);
    }

    /// <summary>The source with every whitespace character removed, for shape checks that must not care about layout.</summary>
    /// <param name="code">A source fragment (already trivia-blanked by the caller).</param>
    /// <returns>The fragment without whitespace.</returns>
    private static string WithoutWhitespace(string code) =>
        new(code.Where(character => !char.IsWhiteSpace(character)).ToArray());

    /// <summary>
    /// The ordinary string literal starting at <paramref name="at"/>, quotes included, or the empty string when the
    /// argument at that position is not a plain string literal (<c>nameof(…)</c>, a constant, a verbatim literal).
    /// </summary>
    /// <param name="text">The original, untriviaed source.</param>
    /// <param name="at">The position the blanked copy marks as the start of the literal.</param>
    /// <returns>The literal token, or the empty string.</returns>
    private static string ReadStringLiteral(string text, int at)
    {
        if (at < 0 || at >= text.Length || text[at] != '"')
        {
            return string.Empty;
        }

        var close = text.IndexOf('"', at + 1);

        return close < 0 ? string.Empty : text[at..(close + 1)];
    }

    /// <summary>
    /// True when <paramref name="api"/> is a spelling of <paramref name="identifier"/> itself — the name, or a
    /// namespace-qualified form of it — so a use matched by a token entry is not reported a second time by the
    /// identifier entry.
    /// </summary>
    /// <param name="api">A token that matched.</param>
    /// <param name="identifier">The bare identifier.</param>
    private static bool NamesSameIdentifier(string api, string identifier) =>
        api == identifier
        || api.EndsWith("." + identifier, StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="identifier"/> occurs in <paramref name="code"/> as a whole identifier: the character
    /// before it is not part of one (so the <c>Process</c> inside <c>MyProcess</c> is not a use) and the character
    /// after it is not either (so <c>ProcessStartInfo</c> stays with its own entry).
    /// </summary>
    /// <param name="code">The source with comments and literals blanked.</param>
    /// <param name="identifier">The identifier to find.</param>
    private static bool MentionsIdentifier(string code, string identifier)
    {
        var from = 0;

        while (true)
        {
            var at = code.IndexOf(identifier, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            var before = at == 0 || !IsIdentifier(code[at - 1]);
            var end = at + identifier.Length;
            var after = end >= code.Length || !IsIdentifier(code[end]);

            if (before && after)
            {
                return true;
            }

            from = at + 1;
        }
    }

    /// <summary>
    /// AC1(d), the archive half: the child's own files never name the profile's <em>file-creating</em> archive arm. The
    /// profile links <c>SafeZipArchive.Extract</c>, which writes every accepted entry under a temporary root with
    /// <c>Path.GetTempPath</c>/<c>Directory.CreateDirectory</c>/<c>new FileStream(…, FileMode.CreateNew)</c>; that code
    /// is linked into the child because the profile's caps must be one implementation, but the child's transport is
    /// stdin/stdout, so the archive it reports comes from the non-extracting <c>Enumerate</c> walk. Nothing else stops
    /// a later edit from calling the writing arm — the linker cannot, since the code is in the assembly — so the rule
    /// does: no child file may name the entry point, the extraction handle or the <c>extract:</c> flag.
    /// </summary>
    /// <param name="ownFiles">The child's own files (path + text).</param>
    /// <returns>The violations, ordered by path.</returns>
    public static IReadOnlyList<string> ChildExtractionCodeViolations(
        IEnumerable<(string Path, string Text)> ownFiles)
    {
        var violations = new List<string>();

        foreach (var (path, text) in ownFiles.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var code = ParserConstructionRules.StripTrivia(text);

            foreach (var api in ForbiddenChildExtractionApis)
            {
                if (Mentions(code, api))
                {
                    violations.Add(
                        $"{path} names {api}: the parser child reports archive entries through the profile's "
                        + "non-extracting walk, so the linked file-creating arm (SafeZipArchive.Extract, "
                        + "ArchiveExtraction) is unreachable from it (06.3.6 §8, AC1)");
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

    /// <summary>
    /// AC1(b), the restore half: every dependency in the child's <c>packages.lock.json</c> — which is the graph the
    /// build resolves, transitive packages included, not the short list the project file asks for — is on
    /// <paramref name="allowed"/>. One violation per other package; a package from a denied family is named as such.
    /// </summary>
    /// <param name="lockJson">The lock file's JSON text.</param>
    /// <param name="projectName">The project's name, used in the message.</param>
    /// <param name="allowed">The allowed dependency names, compared without case (the lock file lowercases ids).</param>
    /// <returns>The violations, ordered by package name.</returns>
    public static IReadOnlyList<string> LockedPackageViolations(
        string lockJson,
        string projectName,
        IReadOnlyList<string> allowed)
    {
        var violations = new List<string>();

        foreach (var package in LockedPackageNames(lockJson))
        {
            if (allowed.Contains(package, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var denied = DeniedLockedPackagePrefixes.FirstOrDefault(
                prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            violations.Add(denied is null
                ? $"{projectName} restores {package}: the parser child carries {Allowance(allowed)} "
                    + "(06.3.6 AC1, §8)"
                : $"{projectName} restores {package}: {denied}* is a web-stack, network, database, ORM or cloud-SDK "
                    + "package, and the parser child runs without a network or a connection string "
                    + "(06.3.6 AC1, §8 AC4)");
        }

        return violations;
    }

    /// <summary>
    /// Every dependency name in a <c>packages.lock.json</c>, across all of its targets, deduplicated and sorted: the
    /// package ids as the lock file spells them plus the two project entries.
    /// </summary>
    /// <param name="lockJson">The lock file's JSON text.</param>
    /// <returns>The dependency names, in ordinal order.</returns>
    public static IReadOnlyList<string> LockedPackageNames(string lockJson)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(lockJson);

        if (!document.RootElement.TryGetProperty("dependencies", out var targets)
            || targets.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        foreach (var target in targets.EnumerateObject())
        {
            if (target.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var package in target.Value.EnumerateObject())
            {
                names.Add(package.Name);
            }
        }

        return names.ToList();
    }

    /// <summary>
    /// 06.3.1-T1 / 06.3.6 AC1: the contracts project's <c>InternalsVisibleTo</c> set is exactly
    /// <paramref name="allowed"/>. Both entries are needed — the module that owns the profile and the child that links
    /// it construct the report handle — and a third friend would be an assembly building it outside the profile.
    /// </summary>
    /// <param name="projectXml">The contracts project's XML text.</param>
    /// <param name="projectName">The project's name, used in the message.</param>
    /// <param name="allowed">The allowed friend assembly names.</param>
    /// <returns>The violations, in document order.</returns>
    public static IReadOnlyList<string> FriendAssemblyViolations(
        string projectXml,
        string projectName,
        IReadOnlyList<string> allowed) =>
        FriendAssemblyNames(projectXml)
            .Where(friend => !allowed.Contains(friend, StringComparer.Ordinal))
            .Select(friend =>
                $"{projectName} exposes its internals to {friend}: only {Names(allowed)} construct the profile's "
                + "report handle, so no other assembly may see its internals (06.3.1-T1, 06.3.6 AC1)")
            .ToList();

    /// <summary>The <c>InternalsVisibleTo</c> assembly names of a project file, in document order, public-key part
    /// removed.</summary>
    /// <param name="projectXml">The project file XML text.</param>
    /// <returns>The friend assembly names.</returns>
    public static IReadOnlyList<string> FriendAssemblyNames(string projectXml) =>
        ElementAttributes(projectXml, "InternalsVisibleTo", "Include")
            .Select(include => include.Split(',')[0].Trim())
            .Where(name => name.Length > 0)
            .ToList();

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
