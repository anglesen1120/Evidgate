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
    [InlineData("var reader = XmlDictionaryReader.Create(stream);")]
    [InlineData("var reader = new XmlTextReader(stream);")]
    [InlineData("using var archive = ZipFile.Open(path, ZipArchiveMode.Read);")]
    [InlineData("var element = XElement.Load(stream);")]
    [InlineData("var element = XElement.Parse(text);")]
    [InlineData("var document = new XPathDocument(stream);")]
    [InlineData("var serializer = new XmlSerializer(typeof(Payload));")]
    [InlineData("ZipFile.ExtractToDirectory(archivePath, destination);")]
    [InlineData("using var tar = new TarReader(stream);")]
    [InlineData("return new ParsedXmlReport(() => OpenReader(chunks));")]
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

    [Theory]
    [InlineData("using var document = await JsonDocument.ParseAsync(stream);")]
    [InlineData("JsonDocument.TryParseValue(ref reader, out var document);")]
    [InlineData("var node = JsonNode.Parse(prefix);")]
    [InlineData("var obj = JsonObject.Parse(prefix);")]
    [InlineData("var arr = JsonArray.Parse(prefix);")]
    [InlineData("var dto = JsonSerializer.Deserialize<Payload>(body);")]
    [InlineData("var reader = new Utf8JsonReader(payload);")]
    public void Synthetic_Json_Reader_Inside_The_Artifact_Module_Is_Detected(string text)
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Evidence/Application/ArtifactCompletionVerifier.cs", text),
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
    public void Write_Mode_Archive_Outside_The_Profile_Is_Not_A_Parse()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Authoring/Application/Import/Formats/XlsxWriter.cs", "using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);"),
        };

        ParserConstructionRules.FindViolations(
                files,
                ParserConstructionRules.ProfileDirectory,
                ParserConstructionRules.ArtifactModuleDirectory)
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("using var archive = new ZipArchive(stream, ZipArchiveMode.Update);")]
    [InlineData("using var archive = new ZipArchive(stream);")]
    [InlineData("var a = new ZipArchive(output, ZipArchiveMode.Create); using var b = new ZipArchive(input, ZipArchiveMode.Read);")]
    public void Non_Create_Mode_Archive_Next_To_A_Write_Mode_One_Is_Still_Detected(string text)
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Authoring/Application/Import/Formats/XlsxWriter.cs", text),
        };

        ParserConstructionRules.FindViolations(
                files,
                ParserConstructionRules.ProfileDirectory,
                ParserConstructionRules.ArtifactModuleDirectory)
            .ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("using var archive = new ZipArchive(upload, ZipArchiveMode.Read /* ZipArchiveMode.Create */);")]
    [InlineData("using var archive = new ZipArchive(upload, ZipArchiveMode.Read, false, Encoding.UTF8 /* ZipArchiveMode.Create */);")]
    [InlineData("var pair = (new ZipArchive(upload, ZipArchiveMode.Read), new ZipArchive(output, ZipArchiveMode.Create));")]
    [InlineData("using var archive = new ZipArchive(upload, write ? ZipArchiveMode.Create : ZipArchiveMode.Read);")]
    [InlineData("using var archive = new ZipArchive(upload, ZipArchiveMode.Create || true ? ZipArchiveMode.Read : mode);")]
    [InlineData("using var archive = new ZipArchive(upload, ZipMode());")]
    [InlineData("using var archive = new ZipArchive(upload, ZipArchiveMode.Create).Entries.Count > 0 ? Read() : new ZipArchive(copy, ZipArchiveMode.Read);")]
    public void The_Write_Mode_Exemption_Cannot_Be_Borrowed(string text)
    {
        // The exemption belongs to one constructor call: its own argument list, its own mode argument. A comment, a
        // neighbouring constructor, a ternary or a call that merely evaluates to Create does not make a reader a writer.
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Authoring/Application/Import/Formats/XlsxWriter.cs", text),
        };

        ParserConstructionRules.FindViolations(
                files,
                ParserConstructionRules.ProfileDirectory,
                ParserConstructionRules.ArtifactModuleDirectory)
            .ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("var note = \"new ZipArchive(stream, ZipArchiveMode.Read);\";")]
    [InlineData("// new ZipArchive(stream, ZipArchiveMode.Read);")]
    [InlineData("/* new ZipArchive(stream, ZipArchiveMode.Read); */")]
    [InlineData("var note = \"\"\"\n  new ZipArchive(stream, ZipArchiveMode.Read);\n  \"\"\";")]
    public void A_Mention_Inside_Trivia_Is_Not_A_Construction(string text)
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Authoring/Application/Import/Formats/XlsxWriter.cs", text),
        };

        ParserConstructionRules.FindViolations(
                files,
                ParserConstructionRules.ProfileDirectory,
                ParserConstructionRules.ArtifactModuleDirectory)
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("using var archive = new ZipArchive(new MemoryStream(), ZipArchiveMode.Create, leaveOpen: true);")]
    [InlineData("using var archive = new ZipArchive(Sized(output, 4096), ZipArchiveMode.Create);")]
    [InlineData("using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);")]
    public void The_Write_Mode_Exemption_Covers_A_Nested_First_Argument(string text)
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Authoring/Application/Import/Formats/XlsxWriter.cs", text),
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
