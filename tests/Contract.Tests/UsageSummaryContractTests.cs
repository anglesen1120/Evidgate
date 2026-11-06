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
/// retyped on either side fails here rather than in a generated client. The negative control changes the scope label of
/// a project row, which the schema's closed enum must reject, so the valid case is proven against a live schema.
/// </remarks>
public sealed class UsageSummaryContractTests
{
    private const string SchemaName = "UsageSummaryDto";

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
        ]);

        var json = JsonSerializer.Serialize(summary, SnakeCase);

        json.ShouldContain("\"period\":\"2026-11\"", Case.Sensitive);
        json.ShouldContain("\"retry_after_seconds\":2419200", Case.Sensitive);
        json.ShouldContain("\"unavailable_reason\":\"the object store does not report egress bytes yet\"", Case.Sensitive);
        json.ShouldContain("\"projects\":[]", Case.Sensitive);

        ContractSchema.AssertValid(OpenApiSpec.Load(), SchemaName, json, "a usage summary body");
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
        ]);
        var instance = JsonNode.Parse(JsonSerializer.Serialize(summary, SnakeCase))!.AsObject();
        instance["metrics"]![0]!["projects"]![0]!["scope"] = "tenant";

        ContractSchema.AssertInvalid(
            OpenApiSpec.Load(),
            SchemaName,
            instance.ToJsonString(),
            "a project counter whose scope claims a tenant counter");
    }

    /// <summary>The host's wire policy (<c>ApiHost</c>): snake_case, nulls written.</summary>
    private static JsonSerializerOptions SnakeCase { get; } =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}
