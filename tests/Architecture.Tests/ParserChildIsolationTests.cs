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
    public void Synthetic_Forbidden_Api_Only_Named_In_Trivia_Is_Not_A_Use(string text)
    {
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
    [InlineData("var stream = new FileStream(path, FileMode.Open);", "FileStream")]
    [InlineData("Process.Start(info);", "Process.")]
    [InlineData("var cap = Environment.GetEnvironmentVariable(\"EVIDGATE__PARSE__MAX_BYTES\");", "Environment.GetEnvironmentVariable")]
    public void Synthetic_Child_File_Using_File_Process_Or_Environment_Is_Detected(string text, string api)
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Parser/Program.cs", text),
        };

        ParserChildIsolationRules.ChildIoViolations(files).ShouldHaveSingleItem().ShouldContain(api);
    }

    [Theory]
    [InlineData("var buffer = Console.OpenStandardInput();")]
    [InlineData("Console.Error.WriteLine(\"parse child: usage error\");")]
    [InlineData("var exitCode = 3;")]
    [InlineData("var name = \"File.ReadAllBytes\";")]
    [InlineData("var archive = ZipFile.OpenRead(path);")]
    public void Synthetic_Child_Transport_And_Complete_File_Names_Are_Not_A_Violation(string text)
    {
        // `ZipFile.OpenRead` is not `File.` — the match needs a boundary, or every complete-file type would trip it.
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Parser/Program.cs", text),
        };

        ParserChildIsolationRules.ChildIoViolations(files).ShouldBeEmpty();
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

        // The transport half is the child's own files only: the archive arm of the profile materialises entries into
        // the temp directory the launcher owns, so `File.`/`FileStream` are legitimate there and not here.
        var ioViolations = ParserChildIsolationRules.ChildIoViolations(ParserChildIsolationRules.ReadSources(own));
        ioViolations.ShouldBeEmpty(string.Join(Environment.NewLine, ioViolations));
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
}
