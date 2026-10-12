using System.Text.Json;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.SharedKernel.Api;

namespace Evidgate.Modules.Ingestion.Domain;

/// <summary>
/// The 04.7.1-T2.3 first-send seam: a pure extractor over the first envelope of a run. The reporter
/// (<c>packages/adapter-playwright</c>) sends its capability as the first envelope — <c>type</c>
/// <c>run.log</c>, <c>sequence</c> 1, <c>ext.schema</c> <c>&lt;adapter-name&gt;/&lt;protocol-major&gt;</c>
/// and <c>ext.capability</c> the <c>capability.v1</c> document — so the events handler (04.1.1-T1)
/// calls this before accepting the first event. An envelope that carries no capability is not a carrier
/// (no registration); a carrier whose shape is wrong is a 422 <c>capability_invalid</c> with the path of
/// the offending member (<c>/type</c>, <c>/ext/schema</c> or <c>/ext/capability</c>).
/// <para>
/// The member is <c>type</c>, not <c>kind</c> (S33 fix): <c>contracts/schemas/event-envelope.v1.json</c>
/// is closed (<c>additionalProperties: false</c>) and the Playwright adapter sends <c>type</c>, so reading
/// <c>kind</c> made every real carrier look like a plain envelope and no capability was ever registered.
/// </para>
/// </summary>
internal static partial class FirstSendCapabilityExtractor
{
    /// <summary>
    /// The <c>ext.schema</c> shape: an adapter name (the shared <see cref="AdapterName"/> grammar), a
    /// slash, the protocol major.
    /// </summary>
    [GeneratedRegex(
        "^(?<name>" + AdapterName.Pattern + @")/(?<major>[0-9]+)\z",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex SchemaShape();

    /// <summary>Extracts the capability carrier from one envelope, or reports why it is malformed.</summary>
    /// <param name="envelope">The parsed first envelope (00 §9.4).</param>
    internal static FirstSendExtraction Extract(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object
            || !envelope.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
        {
            // Not the capability carrier: an envelope without a string type has nothing to register.
            return FirstSendExtraction.NotACarrier;
        }

        // A lone surrogate in a carrier string makes GetString throw; the shared guard turns it into a
        // 422 before the decode, exactly as the capability body does (UnstorableText.Contains).
        if (UnstorableText.Contains(type))
        {
            return FirstSendExtraction.Invalid([new ErrorDetail("/type", "invalid_value")]);
        }

        if (!string.Equals(type.GetString(), "run.log", StringComparison.Ordinal)
            || !envelope.TryGetProperty("sequence", out var sequence)
            || sequence.ValueKind != JsonValueKind.Number
            || !sequence.TryGetInt32(out var number)
            || number != 1
            || !envelope.TryGetProperty("ext", out var ext)
            || ext.ValueKind != JsonValueKind.Object
            || !ext.TryGetProperty("capability", out var capability))
        {
            // Not the capability carrier: an envelope without ext.capability has nothing to register.
            return FirstSendExtraction.NotACarrier;
        }

        if (!ext.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String)
        {
            return FirstSendExtraction.Invalid([new ErrorDetail("/ext/schema", "invalid_format")]);
        }

        if (UnstorableText.Contains(schema))
        {
            return FirstSendExtraction.Invalid([new ErrorDetail("/ext/schema", "invalid_value")]);
        }

        if (SchemaShape().Match(schema.GetString()!) is not { Success: true } match)
        {
            return FirstSendExtraction.Invalid([new ErrorDetail("/ext/schema", "invalid_format")]);
        }

        if (capability.ValueKind != JsonValueKind.Object)
        {
            return FirstSendExtraction.Invalid([new ErrorDetail("/ext/capability", "invalid_type")]);
        }

        return FirstSendExtraction.Carrier(match.Groups["name"].Value, capability);
    }
}

/// <summary>The outcome of <see cref="FirstSendCapabilityExtractor.Extract"/>.</summary>
/// <param name="IsCarrier">True when the envelope carries a capability document to register.</param>
/// <param name="AdapterName">The adapter name taken from the <c>ext.schema</c> prefix; null otherwise.</param>
/// <param name="Document">The <c>ext.capability</c> object; undefined when not a carrier.</param>
/// <param name="Errors">The 422 details of a malformed carrier; empty otherwise.</param>
internal sealed record FirstSendExtraction(
    bool IsCarrier,
    string? AdapterName,
    JsonElement Document,
    IReadOnlyList<ErrorDetail> Errors)
{
    /// <summary>The envelope is not a capability carrier: nothing to register.</summary>
    internal static FirstSendExtraction NotACarrier { get; } = new(false, null, default, []);

    /// <summary>The envelope claims to be a carrier but its shape is wrong.</summary>
    internal static FirstSendExtraction Invalid(IReadOnlyList<ErrorDetail> errors) =>
        new(false, null, default, errors);

    /// <summary>The envelope carries a well-formed capability carrier.</summary>
    internal static FirstSendExtraction Carrier(string adapterName, JsonElement document) =>
        new(true, adapterName, document, []);
}
