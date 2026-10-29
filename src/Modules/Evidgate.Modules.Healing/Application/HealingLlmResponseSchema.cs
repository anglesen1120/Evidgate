using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Corvus.Json;
using Corvus.Json.Validator;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// 08.5.1-T1: the output constraint of the gateway (AC1). The caller declares the JSON Schema its prompt promises and the
/// answer is validated against it with the repository's one JSON Schema engine — <c>Corvus.Json.Validator</c>, the same
/// 2020-12 implementation the contract schemas use (01.3.2-T2.1) — on the <see cref="ValidationLevel.Flag"/> fast path,
/// because the gateway needs a verdict, not a diagnostic list.
/// </summary>
/// <remarks>
/// A provider schema is deployment data with the same lockdown as <c>contracts/</c>: file-system and HTTP document
/// resolution stay off, so an external <c>$ref</c> fails instead of reaching the network (S4). Compiled schemas are
/// addressed by a digest of their own text, so the engine's per-canonical-URI cache can never answer for a different
/// schema (a fixed URI would), and repeated calls with the same declared schema reuse one compilation.
/// </remarks>
internal static class HealingLlmResponseSchema
{
    /// <summary>
    /// The registry for external <c>$ref</c>s: deliberately empty, so resolution of anything that is not inline fails
    /// instead of touching the file system or the network.
    /// </summary>
    private static readonly PrepopulatedDocumentResolver Resolver = new();

    /// <summary>The same lockdown the contract schema set compiles with (contracts 01.3.2-T2.1 AC3).</summary>
    private static readonly JsonSchema.Options Options = new(
        Resolver,
        allowFileSystemAndHttpResolution: false,
        alwaysAssertFormat: true);

    /// <summary>
    /// Compiles the caller's declared schema, or returns null when the caller declared none (then any non-empty JSON
    /// answer is accepted).
    /// </summary>
    /// <param name="responseSchema">The declared JSON Schema text, or null/blank for "no declared schema".</param>
    /// <exception cref="ArgumentException">
    /// Any compilation failure of the declared text, whatever type the engine raised it as. It is thrown before the
    /// provider is called and before any row is written: a malformed declaration is a caller bug, not a provider failure
    /// to budget for. <see cref="OperationCanceledException"/> is the one exception that passes through unwrapped, and
    /// the declaration's text never enters the message.
    /// </exception>
    internal static JsonSchema? Compile(string? responseSchema)
    {
        if (string.IsNullOrWhiteSpace(responseSchema))
        {
            return null;
        }

        try
        {
            return JsonSchema.FromText(responseSchema, CanonicalUri(responseSchema), Options);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The engine's failure types are not a closed set and a malformed declaration reaches them in many shapes, so
            // every fault is the caller's argument being wrong — except the cancellation this method was never given.
            // The schema text stays out of the message: it is caller content.
            throw new ArgumentException(
                "the declared healing LLM response schema is not a valid JSON Schema document",
                nameof(responseSchema),
                exception);
        }
    }

    /// <summary>
    /// Whether <paramref name="content"/> is an acceptable answer: non-empty JSON, and valid against
    /// <paramref name="schema"/> when one was declared.
    /// </summary>
    /// <param name="schema">The compiled schema, or null when the caller declared none.</param>
    /// <param name="content">The provider's answer body.</param>
    internal static bool Accepts(JsonSchema? schema, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            return schema is null || schema.Value.Validate(document.RootElement, ValidationLevel.Flag).IsValid;
        }
    }

    /// <summary>
    /// The canonical URI of a declared schema: its own text's digest, so two different declarations never share an entry
    /// in the engine's cache and the same declaration always does.
    /// </summary>
    private static string CanonicalUri(string responseSchema) =>
        $"urn:evidgate:healing:llm-response:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(responseSchema)))}";
}
