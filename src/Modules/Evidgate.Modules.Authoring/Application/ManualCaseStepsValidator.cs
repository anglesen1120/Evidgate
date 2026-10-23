using System.Buffers;
using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>
/// Validates the manual test case <c>steps</c> document against
/// <c>contracts/schemas/authoring-steps.v1.json</c> through the shared
/// <see cref="ContractSchemaSet"/>, the same way the catalog reads its sidecar
/// (<c>SidecarManifestReader</c>): the schema is the contract, this type adds no rule of its own.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes reach the routes of 13.1.1: the create/patch bodies (routes 13 and 15) carry the
/// step <em>array</em> under <c>steps</c>, while the stored <c>manual_test_case.steps</c> jsonb
/// value (and therefore every <c>authoring_version.content</c> snapshot) is the whole document
/// <c>{"steps":[…]}</c> whose CHECK is <c>jsonb_typeof(steps) = 'object'</c>.
/// <see cref="ValidateSteps"/> wraps a request value into the document shape, so a route never
/// hand-builds JSON; <see cref="ValidateDocument"/> validates a value that is already a document.
/// </para>
/// <para>
/// NFC is not applied here: every text field is composed by
/// <see cref="Evidgate.Modules.Authoring.Domain.AuthoringText.Normalize"/> before it reaches this validator, so the stored
/// bytes and the validated bytes are the same value (13.1.1 AC14). The verdict is the whole
/// mapping the error envelope needs — each <see cref="ContractValidationError"/> is one
/// <c>details[]</c> row (<c>path</c> + <c>reason</c>), reported as <c>steps_invalid</c> on import
/// (AC13) and as <c>request_invalid</c> on the case routes — and it never echoes step text (S3).
/// </para>
/// </remarks>
internal sealed class ManualCaseStepsValidator(Lazy<ContractSchemaSet> schemaSet)
{
    /// <summary>The schema key of <c>contracts/schemas/authoring-steps.v1.json</c>.</summary>
    internal const string SchemaKey = ContractSchemaKeys.AuthoringSteps;

    /// <summary>Validates a value that is already the stored <c>{"steps":[…]}</c> document.</summary>
    /// <param name="document">The parsed document.</param>
    /// <returns>The verdict; <see cref="ContractValidationResult.IsValid"/> is false for any violation.</returns>
    public ContractValidationResult ValidateDocument(JsonElement document) =>
        schemaSet.Value.Validate(SchemaKey, document);

    /// <summary>
    /// Validates the <c>steps</c> member of a case request body by wrapping it into the stored
    /// document shape. A missing or null member is the schema's own <c>required</c> violation, so
    /// the caller reports one detail row instead of inventing a second reason.
    /// </summary>
    /// <param name="steps">The <c>steps</c> value of the request body, or null when it is absent.</param>
    /// <returns>The verdict on <c>{"steps": steps}</c>.</returns>
    public ContractValidationResult ValidateSteps(JsonElement? steps)
    {
        if (steps is not { } value || value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return MissingSteps;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("steps");
            value.WriteTo(writer);
            writer.WriteEndObject();
        }

        return schemaSet.Value.Validate(SchemaKey, buffer.WrittenMemory);
    }

    /// <summary>The single <c>details[]</c> row a request without <c>steps</c> produces.</summary>
    private static ContractValidationResult MissingSteps { get; } =
        new([new ContractValidationError(null, "/steps", "required")]);
}
