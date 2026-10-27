using Architecture.Tests.Rules;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// 06.3.6 AC1 and §8: the parser child is a console process with no network and no database that runs the 06.3.1
/// profile by linking the profile sources. Every isolation rule is exercised twice — once against a synthetic project
/// file or source text that must produce a violation, and once against the real project (which must be clean). The
/// real-solution tests assert the scan saw the files it reasons about, so a missing project or a broken
/// <c>&lt;Compile Include&gt;</c> glob fails instead of passing vacuously.
/// </summary>
public class ParserChildIsolationTests
{
    /// <summary>
    /// The child's own files plus the six profile files it links; a scan that sees fewer filtered the tree away (or
    /// the link glob stopped resolving) and would pass vacuously.
    /// </summary>
    private const int MinimumScannedFiles = 6;

    /// <summary>The number of files in <c>…/Evidence/Infrastructure/Parsing/</c> the child links.</summary>
    private const int LinkedProfileFiles = 6;

    [Fact]
    public void Synthetic_Allow_Listed_Project_References_Are_Not_A_Violation()
    {
        var project = ProjectXml(
            "    <ProjectReference Include=\"..\\BuildingBlocks\\SharedKernel\\Evidgate.BuildingBlocks.SharedKernel.csproj\" />\n"
            + "    <ProjectReference Include=\"../Modules/Evidgate.Modules.Evidence.Contracts/Evidgate.Modules.Evidence.Contracts.csproj\" />");

        var references = ParserChildIsolationRules.ProjectReferenceNames(project);
        var expected = new List<string> { "Evidgate.BuildingBlocks.SharedKernel", "Evidgate.Modules.Evidence.Contracts" };

        // Backslash and slash spellings normalize to the same project names — non-vacuous: the rule saw both.
        references.ShouldBe(expected);

        ParserChildIsolationRules.ProjectReferenceViolations(
                project,
                ParserChildIsolationRules.ProjectName,
                ParserChildIsolationRules.AllowedProjectReferences)
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("..\\Modules\\Evidgate.Modules.Evidence\\Evidgate.Modules.Evidence.csproj", "Evidgate.Modules.Evidence")]
    [InlineData("..\\BuildingBlocks\\Persistence\\Evidgate.BuildingBlocks.Persistence.csproj", "Evidgate.BuildingBlocks.Persistence")]
    [InlineData("../BuildingBlocks/Pipeline/Evidgate.BuildingBlocks.Pipeline.csproj", "Evidgate.BuildingBlocks.Pipeline")]
    [InlineData("..\\BuildingBlocks\\Observability\\Evidgate.BuildingBlocks.Observability.csproj", "Evidgate.BuildingBlocks.Observability")]
    [InlineData("..\\Evidgate.Worker\\Evidgate.Worker.csproj", "Evidgate.Worker")]
    public void Synthetic_Project_References_Outside_The_Allow_List_Are_Detected(string include, string name)
    {
        var project = ProjectXml($"    <ProjectReference Include=\"{include}\" />");

        var violation = ParserChildIsolationRules.ProjectReferenceViolations(
                project,
                ParserChildIsolationRules.ProjectName,
                ParserChildIsolationRules.AllowedProjectReferences)
            .ShouldHaveSingleItem();

        violation.ShouldContain(name);
        violation.ShouldContain(ParserChildIsolationRules.ProjectName);
    }

    [Fact]
    public void Synthetic_Project_Without_References_Is_Not_A_Violation_By_Itself()
    {
        // The project-reference rule is a subset check, so an empty set is legal here; the real-solution test asserts
        // the child does reference the artifact contracts, so the pair cannot pass vacuously.
        ParserChildIsolationRules.ProjectReferenceViolations(
                ProjectXml(string.Empty),
                ParserChildIsolationRules.ProjectName,
                ParserChildIsolationRules.AllowedProjectReferences)
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Npgsql", "Npgsql")]
    [InlineData("Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.EntityFrameworkCore.Design", "Microsoft.EntityFrameworkCore")]
    [InlineData("Dapper", "Dapper")]
    [InlineData("Mediator.Abstractions", "Mediator")]
    [InlineData("FluentValidation", "FluentValidation")]
    [InlineData("AWSSDK.S3", "AWSSDK")]
    [InlineData("Amazon.Lambda.Core", "Amazon")]
    [InlineData("OpenTelemetry.Api", "OpenTelemetry")]
    [InlineData("Serilog", "Serilog")]
    public void Synthetic_Package_Reference_Is_Detected(string package, string named)
    {
        // The allow-list is empty, so every package is a violation; the message also names the denied family.
        var project = ProjectXml($"    <PackageReference Include=\"{package}\" />");

        var violation = ParserChildIsolationRules.PackageReferenceViolations(
                project,
                ParserChildIsolationRules.ProjectName,
                ParserChildIsolationRules.AllowedPackageReferences)
            .ShouldHaveSingleItem();

        violation.ShouldContain(named);
    }

    [Fact]
    public void Synthetic_Package_On_The_Allow_List_Is_Not_A_Violation()
    {
        var project = ProjectXml("    <PackageReference Include=\"Something.Allowed\" />");

        ParserChildIsolationRules.PackageReferenceViolations(
                project,
                ParserChildIsolationRules.ProjectName,
                ParserChildIsolationRules.AllowedPackageReferences)
            .ShouldHaveSingleItem()
            .ShouldContain("Something.Allowed");

        // The allow-list is consulted rather than ignored: the same package passes once it is listed.
        var allowed = new List<string> { "Something.Allowed" };

        ParserChildIsolationRules.PackageReferenceViolations(
                project,
                ParserChildIsolationRules.ProjectName,
                allowed)
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.App")]
    [InlineData("microsoft.aspnetcore.app")]
    public void Synthetic_AspNetCore_Framework_Reference_Is_Detected(string framework)
    {
        var project = ProjectXml($"    <FrameworkReference Include=\"{framework}\" />");

        ParserChildIsolationRules.FrameworkReferenceViolations(project, ParserChildIsolationRules.ProjectName)
            .ShouldHaveSingleItem()
            .ShouldContain(ParserChildIsolationRules.ForbiddenFrameworkReference);
    }

    [Fact]
    public void Synthetic_Other_Framework_Reference_Is_Not_A_Violation()
    {
        var project = ProjectXml("    <FrameworkReference Include=\"Microsoft.NETCore.App\" />");

        ParserChildIsolationRules.FrameworkReferenceViolations(project, ParserChildIsolationRules.ProjectName)
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("using System.Net.Http;")]
    [InlineData("using System.Net;")]
    [InlineData("var client = new HttpClient();")]
    [InlineData("var listener = new TcpClient();")]
    [InlineData("var addresses = Dns.GetHostAddresses(host);")]
    [InlineData("using Npgsql;")]
    [InlineData("using Microsoft.EntityFrameworkCore;")]
    [InlineData("private readonly DbContext context;")]
    [InlineData("using Microsoft.AspNetCore.Http;")]
    [InlineData("using Evidgate.BuildingBlocks.Persistence;")]
    public void Synthetic_Forbidden_Api_In_A_Linked_Profile_File_Is_Detected(string text)
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Evidence/Infrastructure/Parsing/SafeXml.cs", text),
        };

        ParserChildIsolationRules.ForbiddenApiViolations(files).ShouldHaveSingleItem().ShouldContain("SafeXml.cs");
    }

    [Theory]
    [InlineData("using System.Net.Http;")]
    [InlineData("HttpClient client = null;")]
    [InlineData("using Microsoft.AspNetCore.Http;")]
    public void Synthetic_Forbidden_Api_In_The_Childs_Own_File_Is_Detected(string text)
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Parser/Program.cs", text),
        };

        ParserChildIsolationRules.ForbiddenApiViolations(files).ShouldHaveSingleItem().ShouldContain("Program.cs");
    }

    [Theory]
    [InlineData("// the child never opens System.Net.Http\r\nvar buffer = Console.OpenStandardInput();")]
    [InlineData("var note = \"System.Net.Http is not reachable from this process\";")]
    [InlineData("/* HttpClient would need a network\r\n   Socket, TcpClient, Dns. */")]
    [InlineData("var note = \"\"\"\r\n  using Microsoft.AspNetCore.Http;\r\n  \"\"\";")]
    [InlineData("var note = \"pre{System.Net.Http}post\";")]
    [InlineData("var note = \"\"\"\r\n  {\"System.Net.Http\"}\r\n  \"\"\";")]
    [InlineData("var note = $\"{{{{new HttpClient()}}}}\";")]
    [InlineData("var note = $$$\"\"\"\r\n  {new HttpClient()}\r\n  \"\"\";")]
    public void Synthetic_Forbidden_Api_Only_Named_In_Trivia_Is_Not_A_Use(string text)
    {
        // The last two cases are the negative side of the interpolation rule: braces are a hole only with a `$`
        // prefix, so a plain literal that happens to contain braces stays text. The final two pin the runs that
        // never open a hole in an interpolated literal either — an even run of four is two escaped braces, and a raw
        // run shorter than the dollar count is text — so a `new HttpClient()` written between them is not code.
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Parser/Program.cs", text),
        };

        ParserChildIsolationRules.ForbiddenApiViolations(files).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("using Evidgate.Modules.Evidence.Application;", "Evidgate.Modules.Evidence.Application")]
    [InlineData("var budget = Evidgate.Modules.Usage.Limits.Budget;", "Evidgate.Modules.Usage")]
    [InlineData("using Evidgate.Modules.Evidence.Infrastructure;", "Evidgate.Modules.Evidence.Infrastructure")]
    public void Synthetic_Module_Namespace_Outside_The_Contracts_Is_Detected(string text, string name)
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Parser/Program.cs", text),
        };

        ParserChildIsolationRules.ForbiddenApiViolations(files).ShouldHaveSingleItem().ShouldContain(name);
    }

    [Theory]
    [InlineData("using Evidgate.Modules.Evidence.Contracts;")]
    [InlineData("namespace Evidgate.Modules.Evidence.Infrastructure.Parsing;")]
    [InlineData("var sniffer = Evidgate.Modules.Evidence.Infrastructure.Parsing.MediaSniffer.Sniff(prefix);")]
    [InlineData("using Evidgate.BuildingBlocks.SharedKernel.Results;")]
    public void Synthetic_Allowed_Namespaces_Are_Not_A_Violation(string text)
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Evidence/Infrastructure/Parsing/SafeXml.cs", text),
        };

        ParserChildIsolationRules.ForbiddenApiViolations(files).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("var bytes = File.ReadAllBytes(path);", "File.")]
    [InlineData("Directory.CreateDirectory(root);", "Directory.")]
    [InlineData("var stream = new FileStream(path, FileMode.Open);", "FileMode.")]
    [InlineData("var stream = new FileStream(path, new FileStreamOptions());", "FileStreamOptions")]
    [InlineData("var info = new FileInfo(path);", "FileInfo")]
    [InlineData("var info = new DirectoryInfo(root);", "DirectoryInfo")]
    [InlineData("using var reader = new StreamReader(path);", "StreamReader(")]
    [InlineData("using var writer = new StreamWriter(path);", "StreamWriter(")]
    [InlineData("var temp = Path.GetTempPath();", "Path.GetTemp")]
    [InlineData("var temp = Path.GetTempFileName();", "Path.GetTemp")]
    [InlineData("using var archive = ZipFile.OpenRead(path);", "ZipFile.")]
    [InlineData("Process.Start(info);", "Process")]
    [InlineData("var child = new Process();", "Process")]
    [InlineData("Process child = new();", "Process")]
    [InlineData("var current = System.Diagnostics.Process.GetCurrentProcess();", "System.Diagnostics.Process")]
    [InlineData("var info = new ProcessStartInfo(\"dotnet\");", "ProcessStartInfo")]
    [InlineData("[DllImport(\"libc\", EntryPoint = \"setrlimit\")] private static extern int SetRlimit(int resource, ref Limit limit);", "DllImport")]
    [InlineData("[LibraryImport(\"libc\", EntryPoint = \"setrlimit\")] private static partial int SetRlimit(int resource, ref Limit limit);", "LibraryImport")]
    [InlineData("var libc = NativeLibrary.Load(\"libc\");", "NativeLibrary")]
    [InlineData("var buffer = Marshal.AllocHGlobal(size);", "Marshal.")]
    [InlineData("var cap = Environment.GetEnvironmentVariable(\"EVIDGATE__PARSE__MAX_BYTES\");", "Environment.GetEnvironmentVariable")]
    [InlineData("Environment.SetEnvironmentVariable(\"EVIDGATE__PARSE__MAX_BYTES\", cap);", "Environment.SetEnvironmentVariable")]
    [InlineData("var root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);", "Environment.GetFolderPath")]
    [InlineData("var path = Environment.ExpandEnvironmentVariables(\"%TEMP%\");", "Environment.ExpandEnvironmentVariables")]
    [InlineData("var cwd = Environment.CurrentDirectory;", "Environment.CurrentDirectory")]
    [InlineData("using static System.IO.File;\nvar bytes = ReadAllBytes(path);", "System.IO")]
    [InlineData("using F = System.IO.File;\nvar bytes = F.ReadAllBytes(path);", "System.IO")]
    [InlineData("using System.IO.Pipes;\nvar server = new AnonymousPipeServerStream();", "System.IO")]
    [InlineData("var path = System.IO.Path.Combine(root, name);", "System.IO")]
    public void Synthetic_Child_File_Using_File_Process_Or_Environment_Is_Detected(string text, string api)
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Parser/Program.cs", text),
        };

        var violations = ParserChildIsolationRules.ChildIoViolations(files);

        // The tokens overlap on purpose (`System.IO` covers a `using static` alias, a qualified use and
        // `System.IO.Pipes` at once, and the bare `Process` identifier is skipped when a token already named the
        // same use), so the count is not the assertion: the rule must name this API for this file.
        violations.ShouldNotBeEmpty();
        string.Join(Environment.NewLine, violations).ShouldContain(api);
    }

    [Theory]
    [InlineData("var stdout = new FileStream(new SafeFileHandle((nint)1, ownsHandle: false), FileAccess.Write, bufferSize: 0);")]
    [InlineData("using var stdin = Console.OpenStandardInput();")]
    public void Synthetic_Descriptor_Stream_Over_Stdout_Is_Not_A_File(string text)
    {
        // The child's one `FileStream` is the standard-output descriptor, not a path: the console stream swallows a
        // broken pipe on Unix, so the descriptor stream is what reports it (06.3.6 §7 AC3). Every path constructor of
        // FileStream names FileMode or FileStreamOptions, and those two are what the rule bans.
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Parser/Program.cs", text),
        };

        ParserChildIsolationRules.ChildIoViolations(files).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("var shell = GridFile.Open();")]
    [InlineData("using var stdout = new BufferedStream(Console.OpenStandardOutput(), 64 * 1024);")]
    [InlineData("var name = \"File.ReadAllBytes\";")]
    [InlineData("// File.ReadAllBytes would need a file: the child reads stdin only")]
    [InlineData("var count = DirectorySeparator();")]
    [InlineData("var processed = Grid.Processed(count);")]
    [InlineData("var order = PlatformProcessing(count);")]
    [InlineData("var name = \"[DllImport] and Marshal.AllocHGlobal are not reachable from this process\";")]
    public void Synthetic_Child_Transport_And_Complete_File_Names_Are_Not_A_Violation(string text)
    {
        // The match needs a boundary and blanked trivia: `File.` inside `GridFile.Open` is a longer identifier, a
        // message or a comment naming a file API is text, and `BufferedStream` is the stdout transport — the child
        // streams stdin and stdout and touches no path. The identifier rule needs the same care on both sides: the
        // `Process` inside `Processed` and `PlatformProcessing` is part of a longer name, so it is not a use of the
        // denied type.
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Parser/Program.cs", text),
        };

        ParserChildIsolationRules.ChildIoViolations(files).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("var note = $\"{File.ReadAllText(path)}\";", "File.")]
    [InlineData("var note = $\"{(flag ? File.ReadAllText(a) : File.ReadAllText(b))}\";", "File.")]
    [InlineData("var note = $\"size={new FileInfo(path).Length}\";", "FileInfo")]
    [InlineData("var note = $\"cap={Environment.GetEnvironmentVariable(\"CAP\")}\";", "Environment.GetEnvironmentVariable")]
    [InlineData("var note = $\"\"\"\n  {File.ReadAllText(path)}\n  \"\"\";", "File.")]
    [InlineData("var note = @$\"{File.ReadAllText(path)}\";", "File.")]
    [InlineData("var note = $\"{{{File.ReadAllText(path)}}}\";", "File.")]
    [InlineData("var note = $$\"\"\"\n  {{{File.ReadAllText(path)}}}\n  \"\"\";", "File.")]
    public void Synthetic_Child_Code_In_An_Interpolation_Hole_Is_A_Use(string text, string api)
    {
        // An interpolation hole is code, not string text: blanking it the way the literal segments are blanked would
        // let a real file read pass as a message. The last two cases are the mixed run: in `$"{{{…}}}"` an escaped
        // `{{` is followed by the hole's `{`, and in `$$"""{{{…}}}"""` the hole is the last two of three braces with
        // one literal brace in front of it, so a rule that keys the hole off the run's exact length reads both as
        // text and hides the read.
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Parser/Program.cs", text),
        };

        ParserChildIsolationRules.ChildIoViolations(files).ShouldHaveSingleItem().ShouldContain(api);
    }

    [Theory]
    [InlineData("var note = $\"{(flag ? \"x\" : \"y\")}\";")]
    [InlineData("var note = $\"File.ReadAllText is not reachable from this process\";")]
    [InlineData("var note = $\"{{File.ReadAllText}} is an escaped brace\";")]
    [InlineData("var note = $\"{{{{File.ReadAllText(path)}}}}\";")]
    [InlineData("var note = $\"{prefix}{count} bytes\";")]
    [InlineData("var note = $$$\"\"\"\n  {File.ReadAllText(path)}\n  \"\"\";")]
    [InlineData("var note = $\"\"\"\n  File.ReadAllText is not reachable\n  \"\"\";")]
    [InlineData("var note = $\"\"\"\n  {(flag ? \"x\" : \"y\")}\n  \"\"\";")]
    public void Synthetic_Literal_Text_Of_An_Interpolated_String_Is_Not_A_Use(string text)
    {
        // The literal segments stay text — including the escaped braces and the strings nested inside a hole, which
        // the scan blanks one level down — so only the hole's own code can be a use. A run of four is two escaped
        // braces and no hole, and a raw run shorter than the dollar count is text, so both keep the read as text.
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Parser/Program.cs", text),
        };

        ParserChildIsolationRules.ChildIoViolations(files).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("await $\"{new HttpClient().GetStringAsync(url)}\";")]
    [InlineData("var client = $\"\"\"\n  {new HttpClient()}\n  \"\"\";")]
    [InlineData("var note = $\"{(socket ?? HttpClient())}\";")]
    [InlineData("var note = $\"{{{new HttpClient().GetStringAsync(url)}}}\";")]
    [InlineData("var client = $$\"\"\"\n  {{{new HttpClient()}}}\n  \"\"\";")]
    public void Synthetic_Network_Api_In_An_Interpolation_Hole_Is_Detected(string text)
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Parser/Program.cs", text),
        };

        ParserChildIsolationRules.ForbiddenApiViolations(files).ShouldHaveSingleItem().ShouldContain("Program.cs");
    }

    [Theory]
    [InlineData("HttpRequestMessage request = null;")]
    [InlineData("var response = new HttpResponseMessage();")]
    [InlineData("var handler = new HttpMessageHandler();")]
    [InlineData("var content = new StringContent(\"{}\");")]
    public void Synthetic_Implicit_Network_Type_In_A_Child_File_Is_Detected(string text)
    {
        // `ImplicitUsings` is on for every project, so the child compiles with `global using System.Net.Http;`: these
        // types are in scope with no using line at all, and the rule has to catch them by name.
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Parser/Program.cs", text),
        };

        ParserChildIsolationRules.ForbiddenApiViolations(files).ShouldHaveSingleItem().ShouldContain("Program.cs");
    }

    [Theory]
    [InlineData("var entries = SafeZipArchive.Extract(content, cap, options, token);")]
    [InlineData("using var handle = new ArchiveExtraction(root, entries);")]
    [InlineData("var walk = Walk(content, cap, options, extract: true, token);")]
    [InlineData("ZipFile.ExtractToDirectory(archivePath, destination);")]
    public void Synthetic_Child_File_Naming_The_File_Creating_Archive_Arm_Is_Detected(string text)
    {
        // The profile's file-creating arm is linked into the child, so nothing but this rule stops a later edit from
        // calling it: the child reports archive entries through the non-extracting walk.
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Parser/Program.cs", text),
        };

        ParserChildIsolationRules.ChildExtractionCodeViolations(files).ShouldHaveSingleItem().ShouldContain("Program.cs");
    }

    [Fact]
    public void Synthetic_Child_File_Reporting_Archive_Entries_Is_Not_The_Extracting_Arm()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Parser/Program.cs", "var result = parsing.EnumerateArchive(content, cap, CancellationToken.None);"),
            ("src/Evidgate.Parser/Program.cs", "var size = info.CompressedBytes + info.ExpandedBytes;"),
        };

        ParserChildIsolationRules.ChildExtractionCodeViolations(files).ShouldBeEmpty();
    }

    [Fact]
    public void Synthetic_Legal_Closure_Is_Clean()
    {
        var graph = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Evidgate.Parser"] = ["Evidgate.Modules.Evidence.Contracts", "Evidgate.BuildingBlocks.SharedKernel"],
            ["Evidgate.Modules.Evidence.Contracts"] = ["Evidgate.BuildingBlocks.SharedKernel"],
            ["Evidgate.BuildingBlocks.SharedKernel"] = [],
        };

        ParserChildIsolationRules.ClosureViolations(
                graph,
                ParserChildIsolationRules.ProjectName,
                ParserChildIsolationRules.AllowedClosureProjects)
            .ShouldBeEmpty();
    }

    [Fact]
    public void Synthetic_Closure_Outside_The_Contracts_And_The_Shared_Kernel_Is_Detected()
    {
        // One hop from the child reaches a module implementation, and two hops reach persistence through it.
        var graph = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Evidgate.Parser"] = ["Evidgate.Modules.Evidence", "Evidgate.Modules.Evidence.Contracts"],
            ["Evidgate.Modules.Evidence"] = ["Evidgate.Modules.Evidence.Contracts", "Evidgate.BuildingBlocks.Persistence"],
            ["Evidgate.Modules.Evidence.Contracts"] = ["Evidgate.BuildingBlocks.SharedKernel"],
            ["Evidgate.BuildingBlocks.SharedKernel"] = [],
            ["Evidgate.BuildingBlocks.Persistence"] = [],
        };

        var violations = ParserChildIsolationRules.ClosureViolations(
            graph,
            ParserChildIsolationRules.ProjectName,
            ParserChildIsolationRules.AllowedClosureProjects);

        violations.Count.ShouldBe(2, string.Join(Environment.NewLine, violations));
        violations.ShouldContain(v => v.Contains("Evidgate.Modules.Evidence through", StringComparison.Ordinal));
        violations.ShouldContain(v => v.Contains("Evidgate.BuildingBlocks.Persistence", StringComparison.Ordinal));
    }

    [Fact]
    public void Synthetic_Closure_Missing_The_Child_Or_A_Referenced_Project_Is_A_Violation()
    {
        var withoutChild = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Evidgate.BuildingBlocks.SharedKernel"] = [],
        };

        ParserChildIsolationRules.ClosureViolations(
                withoutChild,
                ParserChildIsolationRules.ProjectName,
                ParserChildIsolationRules.AllowedClosureProjects)
            .ShouldHaveSingleItem()
            .ShouldContain("Evidgate.slnx");

        // A reference the solution does not list cannot be walked, so it is reported instead of skipped.
        var dangling = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Evidgate.Parser"] = ["Evidgate.Modules.Evidence.Contracts", "Evidgate.BuildingBlocks.SharedKernel"],
            ["Evidgate.BuildingBlocks.SharedKernel"] = [],
        };

        ParserChildIsolationRules.ClosureViolations(
                dangling,
                ParserChildIsolationRules.ProjectName,
                ParserChildIsolationRules.AllowedClosureProjects)
            .ShouldHaveSingleItem()
            .ShouldContain("Evidgate.Modules.Evidence.Contracts");
    }

    [Theory]
    [InlineData("Npgsql", "Npgsql")]
    [InlineData("Microsoft.AspNetCore.Http.Abstractions", "Microsoft.AspNetCore")]
    [InlineData("System.Net.Http.WinHttpHandler", "System.Net")]
    [InlineData("Microsoft.EntityFrameworkCore.Relational", "Microsoft.EntityFrameworkCore")]
    [InlineData("Dapper", "Dapper")]
    [InlineData("AWSSDK.S3", "AWSSDK")]
    [InlineData("Serilog", "Serilog")]
    public void Synthetic_Forbidden_Package_In_The_Lock_File_Is_Detected(string package, string named)
    {
        // The lock file is the resolved graph — transitive packages included — so a package the csproj never names
        // still lands in the child. The allow-list is exact, and the message names the family too.
        var violation = ParserChildIsolationRules.LockedPackageViolations(
                LockJson(package),
                ParserChildIsolationRules.ProjectName,
                ParserChildIsolationRules.AllowedLockedPackages)
            .ShouldHaveSingleItem();

        violation.ShouldContain(package);
        violation.ShouldContain(named);
    }

    [Fact]
    public void Synthetic_Lock_File_With_Only_Allowed_Dependencies_Is_Clean()
    {
        var lockJson = LockJson(
            "Microsoft.VisualStudio.Threading.Analyzers",
            "evidgate.buildingblocks.sharedkernel",
            "evidgate.modules.evidence.contracts");

        // Non-vacuous: the names are read out of the JSON, case-insensitively matched against the allow-list.
        ParserChildIsolationRules.LockedPackageNames(lockJson).Count.ShouldBe(3);

        ParserChildIsolationRules.LockedPackageViolations(
                lockJson,
                ParserChildIsolationRules.ProjectName,
                ParserChildIsolationRules.AllowedLockedPackages)
            .ShouldBeEmpty();

        // A lock file with no dependency target at all fails the allow-list rather than passing silently: the real
        // test asserts the two project entries are seen, so an unreadable file cannot pass there either.
        ParserChildIsolationRules.LockedPackageNames("{\"version\":2}").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Evidgate.Worker")]
    [InlineData("Evidgate.Modules.Evidence.Application")]
    [InlineData("Evidgate.Api")]
    public void Synthetic_Third_Friend_Assembly_Is_Detected(string friend)
    {
        var project = ProjectXml($"    <InternalsVisibleTo Include=\"{friend}\" />");

        ParserChildIsolationRules.FriendAssemblyViolations(
                project,
                "Evidgate.Modules.Evidence.Contracts",
                ParserChildIsolationRules.AllowedFriendAssemblies)
            .ShouldHaveSingleItem()
            .ShouldContain(friend);
    }

    [Fact]
    public void Synthetic_Friend_Assemblies_On_The_Allow_List_Are_Not_A_Violation()
    {
        var project = ProjectXml(
            "    <InternalsVisibleTo Include=\"Evidgate.Modules.Evidence\" />\n"
            + "    <InternalsVisibleTo Include=\"Evidgate.Parser\" />");

        // Non-vacuous: both entries are read out of the XML, so the rule is consulting them, not returning empty.
        string.Join(",", ParserChildIsolationRules.FriendAssemblyNames(project))
            .ShouldBe("Evidgate.Modules.Evidence,Evidgate.Parser");

        ParserChildIsolationRules.FriendAssemblyViolations(
                project,
                "Evidgate.Modules.Evidence.Contracts",
                ParserChildIsolationRules.AllowedFriendAssemblies)
            .ShouldBeEmpty();
    }

    [Fact]
    public void Real_Parser_References_Only_The_Contracts_And_The_Shared_Kernel()
    {
        var project = RealProjectXml();

        // Non-vacuous: the linked profile sources use the artifact contracts' types, so the child must reference the
        // contracts project — a test that saw no reference would have found the wrong file.
        ParserChildIsolationRules.ProjectReferenceNames(project)
            .ShouldContain("Evidgate.Modules.Evidence.Contracts");

        var violations = ParserChildIsolationRules.ProjectReferenceViolations(
            project,
            ParserChildIsolationRules.ProjectName,
            ParserChildIsolationRules.AllowedProjectReferences);

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Real_Parser_Takes_No_Package()
    {
        var violations = ParserChildIsolationRules.PackageReferenceViolations(
            RealProjectXml(),
            ParserChildIsolationRules.ProjectName,
            ParserChildIsolationRules.AllowedPackageReferences);

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Real_Parser_References_No_AspNetCore_Framework()
    {
        var violations = ParserChildIsolationRules.FrameworkReferenceViolations(
            RealProjectXml(),
            ParserChildIsolationRules.ProjectName);

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Real_Parser_Sources_Use_No_Forbidden_Api_And_No_Child_Io()
    {
        var project = RealProjectXml();
        var includes = ParserChildIsolationRules.CompileIncludes(project);
        includes.ShouldNotBeEmpty();

        var directory = RealProjectDirectory();
        var own = ParserChildIsolationRules.ProjectSources(directory);
        var linked = ParserChildIsolationRules.ResolveLinkedSources(directory, includes);

        // The child's own files plus every linked profile file: the count proves the link glob resolved and the scan
        // is not empty, so a clean result means something.
        linked.Count.ShouldBeGreaterThanOrEqualTo(LinkedProfileFiles);
        own.ShouldNotBeEmpty();

        var all = ParserChildIsolationRules.ReadSources(own.Concat(linked));
        all.Count.ShouldBeGreaterThanOrEqualTo(MinimumScannedFiles);

        var apiViolations = ParserChildIsolationRules.ForbiddenApiViolations(all);
        apiViolations.ShouldBeEmpty(string.Join(Environment.NewLine, apiViolations));

        // The transport half is the child's own files only: the profile's Extract arm really does create files under
        // a temporary root it owns (Path.GetTempPath, Directory.CreateDirectory, new FileStream(FileMode.CreateNew)),
        // and that code is linked into this project — so `File.`/`FileStream` are legitimate in the profile files and
        // not in the child's. What keeps that arm away from the child is not the linker (a linked method is
        // callable) but the extraction rule below: no child file names Extract, ArchiveExtraction or `extract:`.
        var ownSources = ParserChildIsolationRules.ReadSources(own);

        var ioViolations = ParserChildIsolationRules.ChildIoViolations(ownSources);
        ioViolations.ShouldBeEmpty(string.Join(Environment.NewLine, ioViolations));

        var extractionViolations = ParserChildIsolationRules.ChildExtractionCodeViolations(ownSources);
        extractionViolations.ShouldBeEmpty(string.Join(Environment.NewLine, extractionViolations));

        // Non-vacuous: the linked profile really is the file-creating code this rule reasons about, so a rule that
        // read the wrong files (or no files) would pass here for the wrong reason.
        linked.ShouldContain(path => path.EndsWith("/SafeZipArchive.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Real_Parser_Lock_File_Resolves_Only_The_Allowed_Dependencies()
    {
        var lockPath = Path.Combine(SolutionLayout.RepoRoot, ParserChildIsolationRules.LockFile);

        File.Exists(lockPath).ShouldBeTrue($"{ParserChildIsolationRules.LockFile} does not exist");

        var lockJson = File.ReadAllText(lockPath);
        var names = ParserChildIsolationRules.LockedPackageNames(lockJson);

        // Non-vacuous: the parse saw the two project entries and the analyzer the SDK adds, so an empty or unread
        // lock file would fail here instead of passing.
        names.ShouldContain("evidgate.modules.evidence.contracts");
        names.ShouldContain("Microsoft.VisualStudio.Threading.Analyzers");

        var violations = ParserChildIsolationRules.LockedPackageViolations(
            lockJson,
            ParserChildIsolationRules.ProjectName,
            ParserChildIsolationRules.AllowedLockedPackages);

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Real_Contracts_Expose_Their_Internals_To_The_Profile_And_The_Child_Only()
    {
        var path = Path.Combine(SolutionLayout.RepoRoot, ParserChildIsolationRules.ContractsProjectFile);

        File.Exists(path).ShouldBeTrue($"{ParserChildIsolationRules.ContractsProjectFile} does not exist");

        var project = File.ReadAllText(path);
        var friends = ParserChildIsolationRules.FriendAssemblyNames(project);

        // Non-vacuous and exact: the set is the two assemblies, so a missing entry (the child could not construct the
        // report through the profile's factory) and an extra one (a third assembly could) both fail.
        friends.ShouldBe(
            ParserChildIsolationRules.AllowedFriendAssemblies,
            ignoreOrder: true,
            string.Join(Environment.NewLine, friends));

        var violations = ParserChildIsolationRules.FriendAssemblyViolations(
            project,
            "Evidgate.Modules.Evidence.Contracts",
            ParserChildIsolationRules.AllowedFriendAssemblies);

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Real_Parser_Sources_Stay_Clean_Under_The_Implicit_Usings_The_Repository_Sets()
    {
        // Decision for the ImplicitUsings question (06.3.6 review, S7): `Directory.Build.props` enables
        // ImplicitUsings for every project, so a `Microsoft.NET.Sdk` console project compiles with
        // `global using System.IO;` and `global using System.Net.Http;` — a bare `File.ReadAllText`, `new
        // StreamReader(path)` or `new HttpClient()` needs no using line, and requiring
        // `<ImplicitUsings>disable</ImplicitUsings>` in the child's csproj would mean an explicit using in each of its
        // four files while still being undone by any `global using` added to a props file later. The rule therefore
        // keeps the implicit usings and denies what they put in scope by name: the file types in
        // ForbiddenChildApis and the System.Net.Http types in the network list, both scanned here. This test pins the
        // premise — if ImplicitUsings is ever switched off, the name lists are what a reader re-checks.
        var propsPath = Path.Combine(SolutionLayout.RepoRoot, "Directory.Build.props");

        File.Exists(propsPath).ShouldBeTrue("Directory.Build.props does not exist");
        File.ReadAllText(propsPath).ShouldContain("<ImplicitUsings>enable</ImplicitUsings>");

        var project = RealProjectXml();
        var directory = RealProjectDirectory();
        var own = ParserChildIsolationRules.ProjectSources(directory);
        var linked = ParserChildIsolationRules.ResolveLinkedSources(
            directory,
            ParserChildIsolationRules.CompileIncludes(project));
        var all = ParserChildIsolationRules.ReadSources(own.Concat(linked));

        all.ShouldNotBeEmpty();

        var violations = ParserChildIsolationRules.ForbiddenApiViolations(all)
            .Concat(ParserChildIsolationRules.ChildIoViolations(ParserChildIsolationRules.ReadSources(own)))
            .ToList();

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Real_Parser_Closure_Reaches_Only_The_Contracts_And_The_Shared_Kernel()
    {
        var graph = SolutionLayout.ProjectReferenceGraph();

        graph.ContainsKey(ParserChildIsolationRules.ProjectName).ShouldBeTrue(
            $"{ParserChildIsolationRules.ProjectName} is not a project in Evidgate.slnx");

        var violations = ParserChildIsolationRules.ClosureViolations(
            graph,
            ParserChildIsolationRules.ProjectName,
            ParserChildIsolationRules.AllowedClosureProjects);

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string RealProjectXml()
    {
        var path = Path.Combine(SolutionLayout.RepoRoot, ParserChildIsolationRules.ProjectFile);

        File.Exists(path).ShouldBeTrue($"{ParserChildIsolationRules.ProjectFile} does not exist");

        return File.ReadAllText(path);
    }

    private static string RealProjectDirectory()
    {
        var directory = Path.Combine(SolutionLayout.RepoRoot, ParserChildIsolationRules.ProjectDirectory);

        Directory.Exists(directory).ShouldBeTrue($"{ParserChildIsolationRules.ProjectDirectory} does not exist");

        return directory;
    }

    private static string ProjectXml(string items) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" + items + "\n  </ItemGroup>\n</Project>\n";

    /// <summary>A minimal <c>packages.lock.json</c> whose single <c>net10.0</c> target declares these dependencies.</summary>
    /// <param name="packages">The dependency ids.</param>
    private static string LockJson(params string[] packages)
    {
        var entries = string.Join(
            ",\n",
            packages.Select(package => $"      \"{package}\": {{ \"type\": \"Direct\" }}"));

        return "{\n  \"version\": 2,\n  \"dependencies\": {\n    \"net10.0\": {\n" + entries + "\n    }\n  }\n}\n";
    }
}
