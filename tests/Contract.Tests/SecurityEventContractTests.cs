using System.Text.Json;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// 11.5.1-T5.2: the security-events read is one typed contract on both sides of the wire — the served document
/// carries <c>SecurityEventList</c>/<c>SecurityEventItem</c> (so a client generated from the spec binds the same
/// shape the host serialises), the spec declares the same two schemas with the same eight fields, and its path
/// item names the five filters (<c>project_id</c>, <c>type</c>, <c>severity</c>, <c>from</c>, <c>to</c>).
/// </summary>
/// <remarks>
/// The parameter half is asserted against the spec's text: <see cref="OpenApiSpec"/> deliberately parses only
/// path KEYS and components.schemas, because several path descriptions use folded block scalars its reader
/// rejects. The served document cannot carry the filters at all — the endpoint reads them from
/// <c>HttpContext.Request.Query</c>, so ASP.NET binds no query parameter — which is why the five are pinned on
/// the spec (the SDK's source of truth) and the typed schemas on both.
/// <para>
/// The closed <c>type</c>/<c>severity</c> sets are compared with the documented V1 sets declared below, not with
/// the endpoint's own arrays: <c>SecurityEventEndpoints.Types</c>/<c>Severities</c> are private, and widening them
/// for a test would mean an <c>InternalsVisibleTo</c> edit to a production file outside this change's footprint.
/// A value the endpoint accepts is caught by the edge tests
/// (<c>BadInput_Is422RequestInvalid_WithDetailsPath</c>), and a spec that drifts from the plan is caught here.
/// </para>
/// </remarks>
public sealed class SecurityEventContractTests
{
    private const string ServedPath = "/v1/orgs/{o}/security-events";

    private const string SpecPath = "/orgs/{o}/security-events";

    /// <summary>The item's eight wire fields, in the spec's declaration order.</summary>
    private static readonly string[] ItemProperties =
    [
        "id", "project_id", "type", "severity", "actor", "details", "request_id", "created_at",
    ];

    /// <summary>The five filters of the path item, by their <c>components.parameters</c> name.</summary>
    private static readonly (string Name, string Query)[] Filters =
    [
        ("SecurityEventProjectFilter", "project_id"),
        ("SecurityEventType", "type"),
        ("SecurityEventSeverity", "severity"),
        ("SecurityEventFrom", "from"),
        ("SecurityEventTo", "to"),
    ];

    /// <summary>
    /// The closed V1 <c>type</c> set (11.5.1 §4; <c>injection_attempt</c> arrives in V2, 11.4.1-T2), restated here
    /// on purpose: the expectation must not come from the endpoint's own array
    /// (<c>SecurityEventEndpoints.Types</c> is private, and this assembly references no module endpoint), so the
    /// spec is compared with the documented set and the edge tests pin the same five.
    /// </summary>
    private static readonly string[] DocumentedTypes =
    [
        "auth_failure", "cross_tenant_probe", "secret_scanner_alert", "kill_switch_used", "support_access",
    ];

    /// <summary>The closed <c>severity</c> set (11.5.1 §4/§6), restated from the plan for the reason above.</summary>
    private static readonly string[] DocumentedSeverities = ["info", "warning", "critical"];

    [Fact]
    public async Task Served_Document_CarriesTheTypedItemAndPage()
    {
        await using var factory = new EvidgateApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;

        var operation = root.GetProperty("paths").GetProperty(ServedPath).GetProperty("get");
        var schema = operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal("#/components/schemas/SecurityEventList", schema.GetProperty("$ref").GetString());

        var schemas = root.GetProperty("components").GetProperty("schemas");
        var list = schemas.GetProperty("SecurityEventList");
        Assert.Equal(
            ["items", "next_cursor"],
            list.GetProperty("properties").EnumerateObject().Select(member => member.Name).ToArray());

        var item = schemas.GetProperty("SecurityEventItem");
        var servedProperties = item.GetProperty("properties").EnumerateObject().Select(member => member.Name).ToList();
        foreach (var property in ItemProperties)
        {
            servedProperties.ShouldContain(property, $"the served SecurityEventItem must carry '{property}'");
        }

        servedProperties.Count.ShouldBe(ItemProperties.Length, "SecurityEventItem declares the eight wire fields and no others");
    }

    [Fact]
    public void Spec_DeclaresTheSameItemAndPageFields()
    {
        var lines = File.ReadAllLines(Path.Combine(ContractPaths.ContractsDirectory, "openapi.yaml"));

        PropertyNames(lines, "    SecurityEventItem:").ShouldBe(ItemProperties);
        PropertyNames(lines, "    SecurityEventList:").ShouldBe(["items", "next_cursor"]);
    }

    [Fact]
    public void Spec_Path_DeclaresTheFiveFilters()
    {
        var lines = File.ReadAllLines(Path.Combine(ContractPaths.ContractsDirectory, "openapi.yaml"));

        var path = Block(lines, "  " + SpecPath + ":");
        foreach (var (name, query) in Filters)
        {
            path.ShouldContain(
                "        - $ref: '#/components/parameters/" + name + "'",
                $"the {SpecPath} path item must carry the {query} filter as {name}");

            var parameter = Block(lines, "    " + name + ":");
            parameter.ShouldContain("      name: " + query, $"{name} must describe the query parameter {query}");
            parameter.ShouldContain("      in: query", $"{name} must be a query parameter");
        }
    }

    /// <summary>
    /// The spec's closed sets: the <c>type</c> and <c>severity</c> query parameters and the same two members of
    /// <c>SecurityEventItem</c> declare exactly the documented types and severities. A value added on one side and
    /// not the other would let a client generated from the spec send or bind something the edge rejects (422).
    /// </summary>
    [Fact]
    public void Spec_ClosedSets_MatchTheDocumentedTypeAndSeveritySets()
    {
        var lines = File.ReadAllLines(Path.Combine(ContractPaths.ContractsDirectory, "openapi.yaml"));

        EnumValues(Block(lines, "    SecurityEventType:")).ShouldBe(DocumentedTypes);
        EnumValues(Block(lines, "    SecurityEventSeverity:")).ShouldBe(DocumentedSeverities);
        EnumValues(ItemProperty(lines, "type")).ShouldBe(DocumentedTypes);
        EnumValues(ItemProperty(lines, "severity")).ShouldBe(DocumentedSeverities);
    }

    /// <summary>
    /// The served document names the item's <c>type</c> and <c>severity</c> as plain strings, because the wire DTO
    /// holds <c>string</c> and the edge enforces the closed sets (422 <c>request_invalid</c> on an unknown value).
    /// Pinned so the binding surface stays honest — the sets live in the spec — and so a served schema that ever
    /// declares an enum declares the documented one.
    /// </summary>
    [Fact]
    public async Task Served_ItemTypeAndSeverity_AreStringsOrTheDocumentedSets()
    {
        await using var factory = new EvidgateApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var properties = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("SecurityEventItem").GetProperty("properties");

        foreach (var (name, documented) in new[] { ("type", DocumentedTypes), ("severity", DocumentedSeverities) })
        {
            var member = properties.GetProperty(name);
            Assert.Equal("string", member.GetProperty("type").GetString());

            if (member.TryGetProperty("enum", out var served))
            {
                Assert.Equal(documented, served.EnumerateArray().Select(value => value.GetString()!).ToArray());
            }
        }
    }

    /// <summary>
    /// The lines of one property of the <c>SecurityEventItem</c> schema: its name line to the next line indented no
    /// deeper that is not blank. Needed because the item declares two enums (<c>type</c> and <c>severity</c>), so the
    /// whole schema block cannot be read for one of them.
    /// </summary>
    /// <param name="lines">The spec's lines.</param>
    /// <param name="property">The property name, without its colon.</param>
    private static string[] ItemProperty(string[] lines, string property)
    {
        var item = Block(lines, "    SecurityEventItem:");
        var header = "        " + property + ":";
        var start = Array.FindIndex(item, line => string.Equals(line, header, StringComparison.Ordinal));
        Assert.True(start >= 0, $"SecurityEventItem declares no '{property}' property");

        var end = start + 1;
        while (end < item.Length)
        {
            var line = item[end];
            if (!string.IsNullOrWhiteSpace(line) && line.Length - line.TrimStart().Length <= 8)
            {
                break;
            }

            end++;
        }

        return item[start..end];
    }

    /// <summary>
    /// The values of the one <c>enum</c> a block declares, in document order: the bracketed run from the keyword to
    /// its closing <c>]</c>, whether it is written on one line or spread over several. Text, not YAML, for the same
    /// reason <see cref="PropertyNames"/> is.
    /// </summary>
    /// <param name="block">The block whose single enum is read.</param>
    private static string[] EnumValues(string[] block)
    {
        var start = Array.FindIndex(block, line => line.TrimStart().StartsWith("enum:", StringComparison.Ordinal));
        Assert.True(start >= 0, "the block declares no enum");

        var text = string.Join(' ', block[start..].Select(line => line.Trim()));
        var open = text.IndexOf('[', StringComparison.Ordinal);
        var close = text.IndexOf(']', StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, "the enum is not a bracketed sequence");

        return [.. text[(open + 1)..close]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Trim('\'', '"'))];
    }

    /// <summary>
    /// The property names a spec schema block declares, in document order. Only its <c>properties:</c> mapping is
    /// read: the reader is text, because these schemas carry folded (<c>&gt;-</c>) descriptions that
    /// <see cref="YamlSubset"/> refuses by design.
    /// </summary>
    /// <param name="lines">The spec's lines.</param>
    /// <param name="header">The schema's header line, indentation included.</param>
    private static string[] PropertyNames(string[] lines, string header)
    {
        var block = Block(lines, header);
        var properties = Array.FindIndex(block, line => string.Equals(line, "      properties:", StringComparison.Ordinal));
        Assert.True(properties >= 0, $"{header.Trim()} declares no properties mapping");

        var names = new List<string>();
        for (var index = properties + 1; index < block.Length; index++)
        {
            var line = block[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var width = line.Length - line.TrimStart().Length;
            if (width <= 6)
            {
                break;
            }

            if (width == 8 && line.EndsWith(':'))
            {
                names.Add(line.Trim()[..^1]);
            }
        }

        return [.. names];
    }

    /// <summary>
    /// The lines of the mapping entry whose header line is <paramref name="header"/>, from the header to the next
    /// line indented no deeper that is not blank (its own or a shallower sibling), header included.
    /// </summary>
    private static string[] Block(string[] lines, string header)
    {
        var start = Array.FindIndex(lines, line => string.Equals(line, header, StringComparison.Ordinal));
        Assert.True(start >= 0, $"contracts/openapi.yaml has no '{header.Trim()}' entry");

        var indent = lines[start].Length - lines[start].TrimStart().Length;
        var end = start + 1;
        while (end < lines.Length)
        {
            var line = lines[end];
            var width = line.Length - line.TrimStart().Length;
            if (!string.IsNullOrWhiteSpace(line) && width <= indent)
            {
                break;
            }

            end++;
        }

        return lines[start..end];
    }
}
