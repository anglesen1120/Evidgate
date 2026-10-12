using Architecture.Tests.Rules;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// 06.3.1-T1.1 (plan §11, 00 §5.7 S4/S5): no production parse path bypasses the profile. The rule is exercised
/// twice — once against synthetic files that must produce a violation, and once against the real solution
/// (which must be clean). Only <c>src/**/*.cs</c> is scanned: a test may build its own reader to prove the
/// profile's behaviour, and no test is shipped.
/// </summary>
public class ParserConstructionTests
{
    /// <summary>
    /// A clean checkout has several hundred hand-written <c>src/**/*.cs</c> files; a scan that sees fewer than
    /// this filtered the tree away (e.g. a checkout under a <c>bin</c> directory) and would pass vacuously.
    /// </summary>
    private const int MinimumScannedFiles = 200;

    [Theory]
    [InlineData("var reader = XmlReader.Create(stream, settings);")]
    [InlineData("var settings = new XmlReaderSettings();")]
    [InlineData("var document = XDocument.Load(stream);")]
    [InlineData("var document = XDocument.Parse(text);")]
    [InlineData("var document = new XmlDocument();")]
    [InlineData("using var archive = new ZipArchive(stream, ZipArchiveMode.Read);")]
    [InlineData("using var archive = ZipFile.OpenRead(path);")]
    public void Synthetic_Xml_Or_Archive_Reader_Outside_The_Profile_Is_Detected(string text)
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Orchestrator/Application/Manifests/UploadManifest.cs", text),
        };

        var violations = ParserConstructionRules.FindViolations(
            files,
            ParserConstructionRules.ProfileDirectory,
            ParserConstructionRules.ArtifactModuleDirectory);

        violations.ShouldHaveSingleItem().ShouldContain("UploadManifest.cs");
    }

    [Fact]
    public void Synthetic_Json_Parse_Inside_The_Artifact_Module_Is_Detected()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Evidence/Application/ArtifactCompletionVerifier.cs", "var document = JsonDocument.Parse(prefix);"),
        };

        var violations = ParserConstructionRules.FindViolations(
            files,
            ParserConstructionRules.ProfileDirectory,
            ParserConstructionRules.ArtifactModuleDirectory);

        violations.ShouldHaveSingleItem().ShouldContain("ArtifactCompletionVerifier.cs");
    }

    [Fact]
    public void Json_Parse_In_Another_Module_Is_Not_A_Violation()
    {
        // Policy, Orchestrator, Catalog and Contracts.Validation read request bodies and stored rows with
        // JsonDocument: those bytes are not artifacts, so the artifact profile is not their parser.
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Policy/Domain/PolicyRevisionRules.cs", "using var document = JsonDocument.Parse(body);"),
            ("src/BuildingBlocks/Contracts.Validation/ContractSchemaSet.cs", "resolver.AddDocument(CanonicalUri(key), JsonDocument.Parse(document.Text));"),
        };

        ParserConstructionRules.FindViolations(
                files,
                ParserConstructionRules.ProfileDirectory,
                ParserConstructionRules.ArtifactModuleDirectory)
            .ShouldBeEmpty();
    }

    [Fact]
    public void Constructs_Inside_The_Profile_Are_Allowed()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Evidence/Infrastructure/Parsing/SafeXml.cs", "XmlReader.Create(stream, CreateSettings(maxBytes));"),
            ("src/Modules/Evidgate.Modules.Evidence/Infrastructure/Parsing/SafeXml.cs", "internal static XmlReaderSettings CreateSettings(long maxBytes) => new();"),
            ("src/Modules/Evidgate.Modules.Evidence/Infrastructure/Parsing/SafeJson.cs", "JsonDocument.Parse(stream, Options);"),
            ("src/Modules/Evidgate.Modules.Evidence/Infrastructure/Parsing/SafeZipArchive.cs", "using var archive = new ZipArchive(stream, ZipArchiveMode.Read);"),
        };

        ParserConstructionRules.FindViolations(
                files,
                ParserConstructionRules.ProfileDirectory,
                ParserConstructionRules.ArtifactModuleDirectory)
            .ShouldBeEmpty();
    }

    [Fact]
    public void Mentions_Of_The_Reader_Names_Outside_The_Profile_Are_Detected()
    {
        // The rule matches the construct, not the file: a module that merely mentions XmlReaderSettings is
        // configuring a reader, and that configuration belongs in the profile.
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Ingestion/Infrastructure/Adapters/AdapterReader.cs", "var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit };"),
        };

        ParserConstructionRules.FindViolations(
                files,
                ParserConstructionRules.ProfileDirectory,
                ParserConstructionRules.ArtifactModuleDirectory)
            .ShouldHaveSingleItem();
    }

    [Fact]
    public void Real_Solution_Constructs_Untrusted_Parsers_Only_In_The_Profile()
    {
        var files = SourceFiles();

        files.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumScannedFiles,
            $"only {files.Count} src/**/*.cs files were scanned; the generated-file filter is hiding the tree");

        var violations = ParserConstructionRules.FindViolations(
            files,
            ParserConstructionRules.ProfileDirectory,
            ParserConstructionRules.ArtifactModuleDirectory);

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    /// <summary>Every hand-written <c>src/**/*.cs</c> file, repo-relative with '/'.</summary>
    private static List<(string Path, string Text)> SourceFiles()
    {
        var root = Path.Combine(SolutionLayout.RepoRoot, "src");

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGenerated(root, path))
            .Select(path => (Relative(path), File.ReadAllText(path)))
            .ToList();
    }

    /// <summary>
    /// True when a directory below <paramref name="srcRoot"/> is a build output (<c>bin</c> or <c>obj</c>).
    /// Only the part of the path below the source root is inspected, so the directories the checkout itself
    /// lives in never count.
    /// </summary>
    private static bool IsGenerated(string srcRoot, string path)
    {
        var relative = Path.GetRelativePath(srcRoot, path);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments[..^1].Any(segment =>
            string.Equals(segment, "bin", StringComparison.Ordinal)
            || string.Equals(segment, "obj", StringComparison.Ordinal));
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(SolutionLayout.RepoRoot, path).Replace('\\', '/');
}
