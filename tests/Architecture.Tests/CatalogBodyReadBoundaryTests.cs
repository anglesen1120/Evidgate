using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Every Catalog route that reads a request body goes through <c>CatalogRequestRules.ReadJsonAsync</c>: the size cap, the
/// closed-member set and the unstorable-text refusal only hold when the body is read there and nowhere else. A route
/// that parsed the body itself would skip the refusal and could answer 500 on a name it cannot read, so a second reader
/// is a boundary break, not a style choice.
/// </summary>
/// <remarks>
/// A source scan, like the healing-switch boundary: the needles are the BCL calls a direct read would use, so a second
/// reader in an endpoint file trips the scan even though it compiles. The rules file is exempt because it is the one
/// reader; the guard and call-site halves pin that it is still used and still classifies the parsed body.
/// </remarks>
public sealed class CatalogBodyReadBoundaryTests
{
    private const string EndpointsDirectory = "src/Modules/Evidgate.Modules.Catalog/Endpoints";
    private const string RulesFile = EndpointsDirectory + "/CatalogRequestRules.cs";
    private const string SyntheticRouteFile = EndpointsDirectory + "/SyntheticRoute.cs";

    /// <summary>
    /// The six raw-body routes (suite revision with its dry run, requirement revision, case revision, fixture, fixture
    /// revision, git binding). A route that stops reading its body drops below this count and has to be looked at.
    /// </summary>
    private const int RawBodyRoutes = 6;

    /// <summary>The calls a route would use to read a request body without the shared cap and refusal.</summary>
    private static readonly string[] DirectBodyReadNeedles =
    [
        "JsonDocument.Parse",
        "ReadFromJsonAsync",
        "Request.Body",
        "BodyReader",
        "JsonSerializer.Deserialize<JsonElement>(",
    ];

    [Fact]
    public void CatalogEndpoints_RawBodyReads_UseSharedBoundary()
    {
        var files = Endpoints();

        files.Length.ShouldBeGreaterThanOrEqualTo(4);
        files.Count(file => file.Path == RulesFile).ShouldBe(1);
        Scan(files).ShouldBeEmpty("Catalog endpoints must read request bodies through CatalogRequestRules.ReadJsonAsync");
    }

    [Fact]
    public void CatalogBodyRoutes_ReadThroughTheSharedReader()
    {
        var calls = Endpoints()
            .Where(file => file.Path != RulesFile)
            .Sum(file => Occurrences(file.Text, "ReadJsonAsync("));

        calls.ShouldBeGreaterThanOrEqualTo(RawBodyRoutes);
    }

    [Fact]
    public void ReadJsonAsync_ParsedBody_IsClassifiedBeforeItIsReturned()
    {
        var text = File.ReadAllText(Path.Combine(SolutionLayout.RepoRoot, RulesFile));
        var start = text.IndexOf(" ReadJsonAsync(", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "CatalogRequestRules must still declare ReadJsonAsync");
        var end = text.IndexOf("\n    }", start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);

        var reader = text[start..end];
        reader.ShouldContain("UnstorableJsonText.HasUnstorableText(");
        reader.ShouldContain("UnstorableJsonText.HasUnstorableName(");
    }

    /// <summary>
    /// The mutant proof: a file calling the shared reader is clean and a file reading the body itself is flagged, so a
    /// green <see cref="CatalogEndpoints_RawBodyReads_UseSharedBoundary"/> cannot come from a scan that matches nothing.
    /// </summary>
    [Theory]
    [InlineData("JsonDocument.Parse(requestText)")]
    [InlineData("JsonDocument.ParseAsync(context.Request.Body)")]
    [InlineData("context.Request.ReadFromJsonAsync<JsonElement>()")]
    [InlineData("context.Request.Body.ReadAsync(buffer)")]
    [InlineData("context.Request.BodyReader.ReadAsync()")]
    [InlineData("request.BodyReader.ReadAsync()")]
    [InlineData("JsonSerializer.Deserialize<JsonElement>(requestText)")]
    public void Scan_DirectBodyReadOutsideRules_FlagsFile(string source)
    {
        (string Path, string Text) sharedRead =
            (SyntheticRouteFile, "CatalogRequestRules.ReadJsonAsync(context, limit, cancellationToken)");
        (string Path, string Text) direct = (SyntheticRouteFile, source);

        Scan([sharedRead]).ShouldBeEmpty();
        Scan([direct]).ShouldBe([SyntheticRouteFile]);
    }

    /// <summary>The shared reader owns the raw read and the parse, so those calls are clean inside it.</summary>
    [Theory]
    [InlineData("JsonDocument.Parse(requestText)")]
    [InlineData("context.Request.ReadFromJsonAsync<JsonElement>()")]
    [InlineData("context.Request.Body.ReadAsync(buffer)")]
    [InlineData("context.Request.BodyReader.ReadAsync()")]
    [InlineData("JsonSerializer.Deserialize<JsonElement>(requestText)")]
    public void Scan_DirectBodyReadInsideRules_AllowsFile(string source) =>
        Scan([(RulesFile, source)]).ShouldBeEmpty();

    private static (string Path, string Text)[] Endpoints() =>
        Directory
            .EnumerateFiles(Path.Combine(SolutionLayout.RepoRoot, EndpointsDirectory), "*.cs", SearchOption.AllDirectories)
            .Select(file => (
                Path: Path.GetRelativePath(SolutionLayout.RepoRoot, file).Replace('\\', '/'),
                Text: File.ReadAllText(file)))
            .ToArray();

    private static string[] Scan(IEnumerable<(string Path, string Text)> files) =>
        files
            .Where(file => file.Path != RulesFile)
            .Where(file => DirectBodyReadNeedles.Any(needle => file.Text.Contains(needle, StringComparison.Ordinal)))
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
