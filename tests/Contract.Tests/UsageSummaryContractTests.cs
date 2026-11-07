using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.Modules.Usage.Contracts;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// The wire shape of <c>GET /v1/orgs/{o}/usage/summary</c> as the host serves it: the real
/// <see cref="UsageSummaryDto"/> serialized with the host's snake_case policy validates against the spec schema, for an
/// answer that carries an unavailable metric, a metered zero and a blocked project counter alike.
/// </summary>
/// <remarks>
/// The instance is built from the module's own records, never a hand-written literal, so a member renamed, dropped or
/// retyped on either side fails here rather than in a generated client. The negative controls change the scope label of
/// a metric entry and of a project row, which the schemas' closed enums must reject, and drop the required
/// `next_cursor`; the valid case is proven against a live schema.
/// </remarks>
public sealed class UsageSummaryContractTests
{
    private const string SchemaName = "UsageSummaryDto";
    private const string SchemaUri = "urn:evidgate:contract-spec:UsageSummaryDocument";

    /// <summary>The operation and the three response schemas are documented where the SDK reads them.</summary>
    [Fact]
    public void Spec_Declares_TheSummaryOperationAndItsSchemas()
    {
        var spec = OpenApiSpec.Load();

        spec.PathKeys.ShouldContain("/orgs/{o}/usage/summary");
        spec.SchemaNames.ShouldContain(SchemaName);
        spec.SchemaNames.ShouldContain("UsageSummaryMetricDto");
        spec.SchemaNames.ShouldContain("UsageSummaryProjectDto");
    }

    /// <summary>Every member the schema requires is written, an unavailable metric's as explicit nulls.</summary>
    [Fact]
    public void Summary_Dto_ValidatesAgainstTheSpecSchema()
    {
        var project = Guid.CreateVersion7();
        var blockedAt = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
        var summary = new UsageSummaryDto(
            "2026-11",
            project,
        [
            new UsageSummaryMetricDto(
                UsageLedgerMetricNames.Results,
                UsageSummaryScopes.Tenant,
                Used: 12m,
                Limit: 100m,
                WarnedAt: blockedAt.AddHours(-1),
                BlockedAt: null,
                RetryAfterSeconds: null,
                UnavailableReason: null,
                Projects:
                [
                    new UsageSummaryProjectDto(
                        project,
                        UsageSummaryScopes.Project,
                        Used: 100m,
                        Limit: 100m,
                        WarnedAt: blockedAt.AddHours(-1),
                        BlockedAt: blockedAt,
                        RetryAfterSeconds: 2_419_200),
                ]),
            new UsageSummaryMetricDto(
                UsageLedgerMetricNames.EgressGb,
                UsageSummaryScopes.Tenant,
                Used: null,
                Limit: null,
                WarnedAt: null,
                BlockedAt: null,
                RetryAfterSeconds: null,
                UnavailableReason: "the object store does not report egress bytes yet",
                Projects: []),
        ],
        NextCursor: "page-after-project-2");

        var json = JsonSerializer.Serialize(summary, SnakeCase);

        json.ShouldContain("\"period\":\"2026-11\"", Case.Sensitive);
        json.ShouldContain("\"retry_after_seconds\":2419200", Case.Sensitive);
        json.ShouldContain("\"unavailable_reason\":\"the object store does not report egress bytes yet\"", Case.Sensitive);
        json.ShouldContain("\"projects\":[]", Case.Sensitive);
        json.ShouldContain("\"next_cursor\":\"page-after-project-2\"", Case.Sensitive);

        var failures = ContractSchema.Failures(SummaryDocument(OpenApiSpec.Load()), SchemaUri, json);
        failures.ShouldBeEmpty($"a usage summary body must validate against components.schemas.{SchemaName}: {string.Join(" | ", failures)}");
    }

    /// <summary>
    /// The operation declares the 400 of a project_id that cannot bind and the 422 of invalid paging inputs, so a client
    /// can tell the two refusals apart.
    /// </summary>
    [Fact]
    public void Spec_SummaryOperation_DeclaresTheRejectedInputs()
    {
        var responses = OpenApiSpec.Load().ResponseKeys("/orgs/{o}/usage/summary", "get");

        responses.ShouldContain("200");
        responses.ShouldContain("400");
        responses.ShouldContain("422");
    }

    /// <summary>The operation pages projects with the spec's shared cursor parameters, not a second copy of them.</summary>
    [Fact]
    public void Spec_SummaryOperation_DeclaresTheSharedPagingParameters()
    {
        var parameters = OpenApiSpec.Load().ParameterRefs("/orgs/{o}/usage/summary", "get");

        parameters.ShouldContain("Cursor");
        parameters.ShouldContain("Limit");
    }

    /// <summary>`next_cursor` is required and nullable, so a last page writes null rather than dropping the member.</summary>
    [Fact]
    public void Spec_SummarySchema_RequiresANullableNextCursor()
    {
        var schema = OpenApiSpec.Load().Schema(SchemaName);

        schema["required"]!.AsArray().Select(member => member!.GetValue<string>())
            .ShouldContain("next_cursor");

        var nextCursor = schema["properties"]!["next_cursor"]!.AsObject();
        nextCursor["type"]!.AsArray().Select(type => type!.GetValue<string>())
            .ShouldBe(NullableStringTypes, ignoreOrder: true);
    }

    /// <summary>The negative control: a body without `next_cursor` must be rejected, or the required member proves nothing.</summary>
    [Fact]
    public void Summary_WithoutNextCursor_IsRejected()
    {
        var summary = new UsageSummaryDto(
            "2026-11",
            null,
        [
            new UsageSummaryMetricDto(
                UsageLedgerMetricNames.Results,
                UsageSummaryScopes.Tenant,
                Used: 0m,
                Limit: null,
                WarnedAt: null,
                BlockedAt: null,
                RetryAfterSeconds: null,
                UnavailableReason: null,
                Projects: []),
        ],
        NextCursor: null);
        var instance = JsonNode.Parse(JsonSerializer.Serialize(summary, SnakeCase))!.AsObject();
        instance.Remove("next_cursor");

        ContractSchema.Failures(SummaryDocument(OpenApiSpec.Load()), SchemaUri, instance.ToJsonString())
            .ShouldNotBeEmpty("a summary page always writes next_cursor, null on the last page, so its absence must be rejected");
    }

    /// <summary>The negative control: a metric entry claiming another scope label must be rejected.</summary>
    [Fact]
    public void Metric_WithProjectScope_IsRejected()
    {
        var summary = new UsageSummaryDto(
            "2026-11",
            null,
        [
            new UsageSummaryMetricDto(
                UsageLedgerMetricNames.Results,
                UsageSummaryScopes.Tenant,
                Used: 0m,
                Limit: null,
                WarnedAt: null,
                BlockedAt: null,
                RetryAfterSeconds: null,
                UnavailableReason: null,
                Projects: []),
        ],
        NextCursor: null);
        var instance = JsonNode.Parse(JsonSerializer.Serialize(summary, SnakeCase))!.AsObject();
        instance["metrics"]![0]!["scope"] = UsageSummaryScopes.Project;

        ContractSchema.Failures(SummaryDocument(OpenApiSpec.Load()), SchemaUri, instance.ToJsonString())
            .ShouldNotBeEmpty("a metric entry is always the organization-wide counter, so the project label must be rejected");
    }

    /// <summary>The negative control: a project row claiming another scope label must be rejected.</summary>
    [Fact]
    public void Project_Row_WithTenantScope_IsRejected()
    {
        var project = Guid.CreateVersion7();
        var summary = new UsageSummaryDto(
            "2026-11",
            project,
        [
            new UsageSummaryMetricDto(
                UsageLedgerMetricNames.Results,
                UsageSummaryScopes.Tenant,
                Used: 0m,
                Limit: null,
                WarnedAt: null,
                BlockedAt: null,
                RetryAfterSeconds: null,
                UnavailableReason: null,
                Projects:
                [
                    new UsageSummaryProjectDto(
                        project,
                        UsageSummaryScopes.Project,
                        Used: 0m,
                        Limit: null,
                        WarnedAt: null,
                        BlockedAt: null,
                        RetryAfterSeconds: null),
                ]),
        ],
        NextCursor: null);
        var instance = JsonNode.Parse(JsonSerializer.Serialize(summary, SnakeCase))!.AsObject();
        instance["metrics"]![0]!["projects"]![0]!["scope"] = "tenant";

        ContractSchema.Failures(SummaryDocument(OpenApiSpec.Load()), SchemaUri, instance.ToJsonString())
            .ShouldNotBeEmpty("a project counter whose scope claims a tenant counter must be rejected, or the valid case proves nothing");
    }

    /// <summary>
    /// The summary schema and the two it references as one document rooted at the summary: the validator resolves
    /// <c>#/components/schemas/...</c> inside the document, so all three entries are read from the spec under that path.
    /// </summary>
    private static string SummaryDocument(OpenApiSpec spec)
    {
        var schemas = new JsonObject();
        foreach (var name in new[] { SchemaName, "UsageSummaryMetricDto", "UsageSummaryProjectDto" })
        {
            var entry = spec.Schema(name).DeepClone().AsObject();
            entry.Remove("$schema");
            entry.Remove("$id");
            schemas[name] = entry;
        }

        return new JsonObject
        {
            ["$schema"] = OpenApiSpec.SchemaDialect,
            ["$id"] = SchemaUri,
            ["components"] = new JsonObject { ["schemas"] = schemas },
            ["$ref"] = $"#/components/schemas/{SchemaName}",
        }.ToJsonString();
    }

    /// <summary>The host's wire policy (<c>ApiHost</c>): snake_case, nulls written.</summary>
    private static JsonSerializerOptions SnakeCase { get; } =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>A nullable JSON Schema type list, as the spec writes <c>[string, 'null']</c>.</summary>
    private static readonly string[] NullableStringTypes = ["string", "null"];
}
