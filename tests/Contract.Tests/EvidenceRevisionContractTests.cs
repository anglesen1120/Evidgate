using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// The wire shape of <c>GET /v1/runs/{run_id}/evidence-revisions</c>: the real
/// <see cref="EvidenceRevisionPage"/>/<see cref="EvidenceRevision"/> records, serialized with the host's snake_case
/// policy, validate against the schemas the spec declares — so a member renamed, dropped or invented on either side
/// fails here rather than in a generated client.
/// </summary>
/// <remarks>
/// The instances are the real records, never a hand-written literal, or the test would pin its own idea of the shape
/// while the record drifts. The negative controls run the other way: an unknown member, a missing required member and
/// a reason code outside the spec's closed list must each be rejected, or the valid cases prove nothing.
/// </remarks>
public sealed class EvidenceRevisionContractTests
{
    /// <summary>The base URI of the composed page document, so its own <c>#/components/schemas</c> references resolve.</summary>
    private const string PageSchemaUri = "urn:evidgate:contract-spec:evidence-revision-page";

    private static readonly Guid LiveEvaluation = Guid.Parse("018f0000-0000-7000-8000-000000000123");

    private static readonly DateTimeOffset Frozen = new(2026, 11, 1, 10, 30, 0, TimeSpan.Zero);

    /// <summary>A page of real revisions validates, with a null <c>evaluation_id</c> and a live one.</summary>
    [Fact]
    public void Page_OfRealRevisions_ValidatesAgainstTheSpecSchemas()
    {
        var undecided = new EvidenceRevision(2, Frozen, "incomplete", ["artifact_missing", "missing_shard"], null);
        var evaluated = new EvidenceRevision(1, Frozen, "complete", [], LiveEvaluation);
        var page = new EvidenceRevisionPage([undecided, evaluated], "1");
        var json = JsonSerializer.Serialize(page, SnakeCase);

        // Both arms of the nullable id are written: absent evidence is a null the schema requires, not an omission.
        json.ShouldContain("\"evaluation_id\":null", Case.Sensitive);
        json.ShouldContain($"\"evaluation_id\":\"{LiveEvaluation}\"", Case.Sensitive);

        var spec = OpenApiSpec.Load();
        AssertValidPage(spec, json, "a page of two revisions");
        ContractSchema.AssertValid(
            spec,
            "EvidenceRevision",
            JsonSerializer.Serialize(undecided, SnakeCase),
            "a revision with no stored evaluation");
        ContractSchema.AssertValid(
            spec,
            "EvidenceRevision",
            JsonSerializer.Serialize(evaluated, SnakeCase),
            "a revision whose live evaluation is named");
    }

    /// <summary>The empty last page keeps both members, <c>next_cursor</c> as a null the schema requires.</summary>
    [Fact]
    public void EmptyPage_ValidatesAgainstTheSpecSchema()
    {
        var json = JsonSerializer.Serialize(new EvidenceRevisionPage([], null), SnakeCase);

        json.ShouldBe("""{"items":[],"next_cursor":null}""");
        AssertValidPage(OpenApiSpec.Load(), json, "the empty last page");
    }

    /// <summary>
    /// The schema rejects what a drifted DTO or spec would produce: an undeclared member on a row (the schema closes
    /// the object), a row without its required <c>missing[]</c>, a reason code outside the closed set, and a page
    /// without the required <c>next_cursor</c>.
    /// </summary>
    [Fact]
    public void SpecSchemas_RejectAnUnknownMemberAMissingMemberAndAnUnknownReasonCode()
    {
        var spec = OpenApiSpec.Load();
        var revision = new EvidenceRevision(1, Frozen, "incomplete", ["artifact_missing"], null);
        var page = new EvidenceRevisionPage([revision], null);

        ContractSchema.AssertInvalid(
            spec,
            "EvidenceRevision",
            Mutate(revision, row => row["shadow_evaluation_id"] = LiveEvaluation.ToString()),
            "a member the schema does not declare");
        ContractSchema.AssertInvalid(
            spec,
            "EvidenceRevision",
            Mutate(revision, row => row.Remove("missing")),
            "a row without the required missing[]");
        ContractSchema.AssertInvalid(
            spec,
            "EvidenceRevision",
            Mutate(revision, row => row["missing"] = new JsonArray("shard_missing")),
            "a reason code outside the closed list");
        ContractSchema.Failures(PageDocument(spec), PageSchemaUri, Mutate(page, listing => listing.Remove("next_cursor")))
            .ShouldNotBeEmpty("a page without the required next_cursor must not validate");
        ContractSchema.Failures(
                PageDocument(spec),
                PageSchemaUri,
                Mutate(page, listing => listing["items"]![0]!.AsObject().Remove("missing")))
            .ShouldNotBeEmpty("a page whose row lacks the required missing[] must not validate");
    }

    /// <summary>Asserts a page instance validates against the spec's two schemas, composed as one document.</summary>
    private static void AssertValidPage(OpenApiSpec spec, string instanceJson, string label)
    {
        var failures = ContractSchema.Failures(PageDocument(spec), PageSchemaUri, instanceJson);
        failures.ShouldBeEmpty($"{label} must validate against components.schemas.EvidenceRevisionPage: {string.Join(" | ", failures)}");
    }

    /// <summary>
    /// The spec's <c>EvidenceRevision</c> and <c>EvidenceRevisionPage</c> entries as one JSON Schema document whose
    /// root is the page: the page's <c>items</c> reference is <c>#/components/schemas/EvidenceRevision</c>, so the
    /// validator needs both entries under that path. Both are read from the spec; nothing here restates a shape.
    /// </summary>
    private static string PageDocument(OpenApiSpec spec)
    {
        var document = new JsonObject
        {
            ["$schema"] = OpenApiSpec.SchemaDialect,
            ["$id"] = PageSchemaUri,
            ["components"] = new JsonObject
            {
                ["schemas"] = new JsonObject
                {
                    ["EvidenceRevision"] = Entry(spec, "EvidenceRevision"),
                    ["EvidenceRevisionPage"] = Entry(spec, "EvidenceRevisionPage"),
                },
            },
            ["$ref"] = "#/components/schemas/EvidenceRevisionPage",
        };

        return document.ToJsonString();
    }

    /// <summary>One spec schema entry, without the base URI <see cref="OpenApiSpec.Schema"/> stamps on it.</summary>
    private static JsonObject Entry(OpenApiSpec spec, string name)
    {
        var entry = spec.Schema(name).DeepClone().AsObject();
        entry.Remove("$schema");
        entry.Remove("$id");
        return entry;
    }

    /// <summary>The serialized <paramref name="dto"/> with one member changed.</summary>
    private static string Mutate<T>(T dto, Action<JsonObject> mutate)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(dto, SnakeCase))!.AsObject();
        mutate(node);
        return node.ToJsonString();
    }

    /// <summary>The host's wire policy (<c>ApiHost</c>): snake_case, nulls written.</summary>
    private static JsonSerializerOptions SnakeCase { get; } =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}
