using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// The template registry: two read operations, their view schemas and the one closed filter they accept.
/// The served document is read as well, so the spec and the host cannot drift apart without failing here.
/// </summary>
public sealed class TemplateContractTests
{
    private const string CollectionPath = "/templates";
    private const string ObjectPath = "/templates/{t}";

    /// <summary>The summary view's wire fields, in the DTO's declaration order.</summary>
    private static readonly string[] SummaryFields = ["id", "kind", "name", "version", "body_ref", "created_at"];

    [Fact]
    public void Spec_DeclaresTheTwoTemplateReadsAndTheirSchemas()
    {
        var spec = OpenApiSpec.Load();

        spec.Operation(CollectionPath, "get")["operationId"]!.GetValue<string>().ShouldBe("listTemplates");
        spec.Operation(ObjectPath, "get")["operationId"]!.GetValue<string>().ShouldBe("getTemplate");

        foreach (var schema in new[] { "TemplateDto", "TemplateDetailDto", "TemplateListResponse" })
        {
            spec.SchemaNames.ShouldContain(schema);
        }
    }

    [Fact]
    public void Spec_PinsThePermissionTheFiltersAndTheRefusals()
    {
        var spec = OpenApiSpec.Load();

        spec.Operation(CollectionPath, "get")["x-permission"]!.GetValue<string>().ShouldBe("case:read");
        spec.Operation(ObjectPath, "get")["x-permission"]!.GetValue<string>().ShouldBe("case:read");
        spec.Operation(CollectionPath, "get")["x-version"]!.GetValue<string>().ShouldBe("v1");

        spec.ParameterRefs(CollectionPath, "get").ShouldContain("TemplateKindFilter");
        spec.ParameterRefs(CollectionPath, "get").ShouldContain("Cursor");
        spec.ParameterRefs(CollectionPath, "get").ShouldContain("Limit");
        spec.ParameterRefs(ObjectPath, "get").ShouldContain("TemplateId");

        spec.ResponseKeys(CollectionPath, "get").ShouldContain("200");
        spec.ResponseKeys(CollectionPath, "get").ShouldContain("401");
        spec.ResponseKeys(CollectionPath, "get").ShouldContain("403");
        spec.ResponseKeys(CollectionPath, "get").ShouldContain("422");
        spec.ResponseKeys(ObjectPath, "get").ShouldContain("404");
        spec.Operation(CollectionPath, "get")["requestBody"].ShouldBeNull("both template reads are bodiless GETs");
        spec.Operation(ObjectPath, "get")["requestBody"].ShouldBeNull();
    }

    [Fact]
    public void Spec_PinsTheViewsAndTheBodyReferenceShape()
    {
        var spec = OpenApiSpec.Load();
        var summary = spec.Schema("TemplateDto")["properties"]!.AsObject();
        summary.Select(pair => pair.Key).Order(StringComparer.Ordinal)
            .ShouldBe(SummaryFields.Order(StringComparer.Ordinal), "TemplateDto carries the six summary fields");
        summary["body_ref"]!["pattern"]!.GetValue<string>().ShouldBe("^sha256:[0-9a-f]{64}$");
        summary["name"]!["maxLength"]!.GetValue<long>().ShouldBe(64L);

        var detail = spec.Schema("TemplateDetailDto")["properties"]!.AsObject();
        string[] all = [.. SummaryFields, "conformance_rules"];
        detail.Select(pair => pair.Key).Order(StringComparer.Ordinal)
            .ShouldBe(all.Order(StringComparer.Ordinal), "TemplateDetailDto adds the stored rules to the summary");
    }

    [Fact]
    public async Task Served_Document_CarriesTheTwoOperations()
    {
        await using var factory = new EvidgateApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var paths = document.RootElement.GetProperty("paths");

        (string Path, string Method)[] operations =
        [
            ("/v1/templates", "get"),
            ("/v1/templates/{t}", "get"),
        ];
        foreach (var (path, method) in operations)
        {
            paths.GetProperty(path).TryGetProperty(method, out _).ShouldBeTrue($"{method} {path} is served");
        }

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var summary = schemas.GetProperty("TemplateDto").GetProperty("properties").EnumerateObject()
            .Select(member => member.Name).ToList();
        summary.ShouldBe(SummaryFields.ToList(), "the served TemplateDto carries the same fields, in the same order");
    }
}
