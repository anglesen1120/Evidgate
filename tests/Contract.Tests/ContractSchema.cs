using System.Text.Json;
using Corvus.Json;
using Corvus.Json.Validator;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// The suite's one JSON Schema validator: an instance is checked against a schema of <c>contracts/</c> with Corvus,
/// formats asserted and no filesystem or HTTP resolution, so a schema reference cannot pull in a second document.
/// </summary>
/// <remarks>
/// Each schema is read from the repository — the spec's <c>components.schemas</c> entry or a schema file — never a
/// copy. Callers keep the labels: a failure names the instance that broke the shape.
/// </remarks>
internal static class ContractSchema
{
    /// <summary>Validates an instance against a schema document; one row per failure, empty when it validates.</summary>
    /// <param name="schemaJson">The JSON Schema document.</param>
    /// <param name="canonicalUri">The schema's base URI, used to resolve its own references.</param>
    /// <param name="instanceJson">The instance under test.</param>
    public static string[] Failures(string schemaJson, string canonicalUri, string instanceJson)
    {
        var schema = JsonSchema.FromText(
            schemaJson,
            canonicalUri,
            new JsonSchema.Options(allowFileSystemAndHttpResolution: false, alwaysAssertFormat: true));

        using var instance = JsonDocument.Parse(instanceJson);
        return
        [
            .. schema.Validate(instance.RootElement, ValidationLevel.Detailed).Results
                .Where(result => !result.Valid)
                .Select(result => $"{(result.Location is { } location ? location.Item3.ToString() : string.Empty)} {result.Message}".Trim()),
        ];
    }

    /// <summary>Validates an instance against one <c>components.schemas</c> entry of <c>contracts/openapi.yaml</c>.</summary>
    /// <param name="spec">The loaded spec.</param>
    /// <param name="schemaName">The <c>components.schemas</c> name.</param>
    /// <param name="instanceJson">The instance under test.</param>
    public static string[] Failures(OpenApiSpec spec, string schemaName, string instanceJson) =>
        Failures(spec.Schema(schemaName).ToJsonString(), $"urn:evidgate:contract-spec:{schemaName}", instanceJson);

    /// <summary>Asserts the instance validates against the named spec schema.</summary>
    /// <param name="spec">The loaded spec.</param>
    /// <param name="schemaName">The <c>components.schemas</c> name.</param>
    /// <param name="instanceJson">The instance under test.</param>
    /// <param name="label">What the instance is, for the failure message.</param>
    public static void AssertValid(OpenApiSpec spec, string schemaName, string instanceJson, string label)
    {
        var failures = Failures(spec, schemaName, instanceJson);
        Assert.True(
            failures.Length == 0,
            $"{label} must validate against components.schemas.{schemaName}: {string.Join(" | ", failures)}");
    }

    /// <summary>Asserts the instance does NOT validate — the negative control for the valid cases beside it.</summary>
    /// <param name="spec">The loaded spec.</param>
    /// <param name="schemaName">The <c>components.schemas</c> name.</param>
    /// <param name="instanceJson">The instance under test.</param>
    /// <param name="label">What the instance is, for the failure message.</param>
    public static void AssertInvalid(OpenApiSpec spec, string schemaName, string instanceJson, string label)
    {
        var failures = Failures(spec, schemaName, instanceJson);
        Assert.True(
            failures.Length > 0,
            $"{label} must NOT validate against components.schemas.{schemaName}, or the valid cases above prove nothing");
    }
}
