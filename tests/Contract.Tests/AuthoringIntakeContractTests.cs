using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Shouldly;
using Xunit;

namespace Contract.Tests;

public sealed class AuthoringIntakeContractTests
{
    private const string Collection = "/projects/{p}/authoring/intakes";
    private const string Key = ContractSchemaKeys.AuthoringGenerationIntake;
    private static readonly Lazy<ContractSchemaSet> Schemas = new(() => ContractSchemaSet.Load(ContractPaths.ContractsDirectory));

    [Fact]
    public void CreateAndRead_DeclareTheirPermissionsAndMetadataOnlyResponses()
    {
        var spec = OpenApiSpec.Load();
        var create = spec.Operation(Collection, "post");
        create["x-permission"]?.GetValue<string>().ShouldBe("authoring:generate");
        create["requestBody"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>()
            .ShouldBe("#/components/schemas/CreateIntakeRequest");
        spec.ParameterRefs(Collection, "post").ShouldContain("IdempotencyKey");
        spec.ResponseKeys(Collection, "post").ShouldBe(["200", "201", "400", "401", "403", "404", "409", "413", "422", "429", "503"]);
        AssertResponse(spec, create, "200", ["intake_id", "state", "caps", "version"]);
        AssertResponse(spec, create, "201", ["intake_id", "state", "caps", "version"]);

        var detail = Collection + "/{i}";
        var read = spec.Operation(detail, "get");
        read["x-permission"]?.GetValue<string>().ShouldBe("authoring:read");
        spec.ResponseKeys(detail, "get").ShouldBe(["200", "401", "403", "404", "429", "503"]);
        AssertResponse(spec, read, "200", ["intake_id", "kind", "state", "eta_class", "failure_code", "extracted_text_hash", "extracted_char_count", "caps", "artifact_id", "version"]);
        Schemas.Value.MaxBytes(Key).ShouldBe(1_048_576L);
    }

    [Theory]
    [InlineData("CreateIntakeTextRequest", 0)]
    [InlineData("CreateIntakeDocumentRequest", 1)]
    [InlineData("CreateIntakeImageRequest", 2)]
    public void RequestComponent_RepeatsTheValidatedSchemasKindVariant(string component, int variant)
    {
        var served = OpenApiSpec.Load().Schema(component);
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractPaths.ContractsDirectory, "schemas", Key + ".json")))!["allOf"]![variant]!["then"]!;

        served["additionalProperties"]?.GetValue<bool>().ShouldBe(false);
        Names(served["required"]).ShouldBe(Names(file["required"]));
        Names(Assert.IsType<JsonObject>(served["properties"])).ShouldBe(Names(Assert.IsType<JsonObject>(file["properties"])));
    }

    [Fact]
    public void RequestComponent_OffersExactlyTheKindsTheSchemaAccepts()
    {
        var spec = OpenApiSpec.Load();
        var served = spec.Schema("CreateIntakeRequest")["oneOf"]!.AsArray()
            .Select(variant => spec.Schema(variant!["$ref"]!.GetValue<string>().Split('/')[^1]))
            .SelectMany(variant => Names(variant["properties"]!["kind"]!["enum"]))
            .Order(StringComparer.Ordinal);
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractPaths.ContractsDirectory, "schemas", Key + ".json")))!["$defs"]!["kind"]!["enum"];

        served.ShouldBe(Names(file));
    }

    [Theory]
    [InlineData("text", "text", "artifact_id")]
    [InlineData("docx", "feature_node_id", "text")]
    [InlineData("pdf", "artifact_id", "text")]
    [InlineData("image", "artifact_id", "feature_node_id")]
    public void Request_RequiresKindSpecificFieldsAndForbidsOtherKindsFields(string kind, string required, string forbidden)
    {
        var id = Guid.CreateVersion7().ToString();
        var body = new JsonObject { ["kind"] = kind, ["target_language"] = "vi" };
        if (kind != "image") body["feature_node_id"] = id;
        if (kind != "text") body["artifact_id"] = id;
        else body["text"] = "requirement";

        IsValid(body).ShouldBeTrue();
        body[forbidden] = id;
        IsValid(body).ShouldBeFalse();
        body.Remove(forbidden);
        body["unexpected"] = 1;
        IsValid(body).ShouldBeFalse();
        body.Remove("unexpected");
        body.Remove(required);
        IsValid(body).ShouldBeFalse();
    }

    [Fact]
    public void Request_LeavesTextLengthAndArtifactDeclarationsToCommandValidation()
    {
        var id = Guid.CreateVersion7().ToString();
        var body = new JsonObject { ["kind"] = "text", ["feature_node_id"] = id, ["text"] = "" };
        IsValid(body).ShouldBeTrue();
        body["text"] = new string('x', 200_001);
        IsValid(body).ShouldBeTrue();
        body["kind"] = "spreadsheet";
        IsValid(body).ShouldBeFalse();
        body = new JsonObject { ["kind"] = "image", ["artifact_id"] = id, ["bytes"] = 30_000_000, ["media_type"] = "application/octet-stream" };
        IsValid(body).ShouldBeTrue();
        body["bytes"] = "123";
        IsValid(body).ShouldBeFalse();
    }

    private static bool IsValid(JsonObject body) => Schemas.Value.Validate(Key, JsonSerializer.SerializeToElement(body)).IsValid;

    private static void AssertResponse(OpenApiSpec spec, JsonObject operation, string status, string[] fields)
    {
        var reference = operation["responses"]?[status]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>();
        var schema = spec.Schema(Assert.IsType<string>(reference).Split('/')[^1]);
        schema["additionalProperties"]?.GetValue<bool>().ShouldBe(false);
        var properties = Assert.IsType<JsonObject>(schema["properties"]);
        Assert.Equal(fields.Order(StringComparer.Ordinal), properties.Select(pair => pair.Key).Order(StringComparer.Ordinal));
    }

    private static string[] Names(JsonNode? node) => node switch
    {
        JsonArray items => [.. items.Select(item => item!.GetValue<string>()).Order(StringComparer.Ordinal)],
        JsonObject members => [.. members.Select(pair => pair.Key).Order(StringComparer.Ordinal)],
        _ => [],
    };
}
