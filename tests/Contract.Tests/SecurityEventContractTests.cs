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
