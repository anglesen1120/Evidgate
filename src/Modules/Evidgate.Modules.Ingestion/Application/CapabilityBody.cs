using System.Runtime.InteropServices;
using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// The capability-body rules of 04.7.1-T2.2/T2.3 (task file §5 shapes, §8 S5): the byte cap, the JSON parse,
/// the unstorable-text guard and the Corvus schema run, in the one order that turns every refusal into a 422
/// <c>capability_invalid</c> with JSON-pointer details instead of a 400 or a 500.
/// <para>
/// The order matters: the schema engine throws on a lone surrogate, so the unstorable-text guard runs before
/// it — the same reason the Orchestrator's <c>JsonBody</c> does. The path rules are this module's own copy (a
/// module never reaches into another module's internals, 00 §5.5) and differ in one way: an
/// <c>additional_properties</c> error keeps the member it names, because AC3 requires the closed root to
/// report the offending member.
/// </para>
/// </summary>
internal static class CapabilityBody
{
    /// <summary>Map members whose names are caller data: a path never names them.</summary>
    private static readonly HashSet<string> MapMembers = new(StringComparer.Ordinal) { "supports" };

    /// <summary>
    /// Validates the raw capability bytes (04.7.1-T2.2): the <c>x-max-bytes</c> cap, the parse, the
    /// unstorable-text guard, then the <c>capability.v1</c> schema. On success <paramref name="document"/> is
    /// the parsed root and the caller disposes it; on failure it is null and the returned error is the 422
    /// <c>capability_invalid</c>.
    /// </summary>
    /// <param name="body">The raw UTF-8 capability document.</param>
    /// <param name="schemas">The compiled contract schema set.</param>
    /// <param name="document">The parsed root on success, else null.</param>
    internal static Error? Validate(ReadOnlyMemory<byte> body, ContractSchemaSet schemas, out JsonDocument? document)
    {
        document = null;

        if (schemas.MaxBytes(ContractSchemaKeys.Capability) is { } maxBytes && body.Length > maxBytes)
        {
            return Invalid(
                "the capability document exceeds the transport cap",
                [new ErrorDetail(string.Empty, ContractValidationError.BodyTooLargeReason)]);
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Invalid(
                "the capability document is not JSON",
                [new ErrorDetail(string.Empty, ContractValidationError.InvalidJsonReason)]);
        }

        if (HasUnstorableText(parsed.RootElement))
        {
            parsed.Dispose();

            return Invalid(
                "the capability document carries a string that cannot be stored",
                [new ErrorDetail(string.Empty, "invalid_value")]);
        }

        var verdict = schemas.Validate(ContractSchemaKeys.Capability, parsed.RootElement);
        if (!verdict.IsValid)
        {
            parsed.Dispose();

            return Invalid("the capability document does not match capability.v1", Details(verdict));
        }

        document = parsed;

        return null;
    }

    /// <summary>
    /// Validates an already-parsed capability element (04.7.1-T2.3: the first-send envelope is parsed once and
    /// its bytes were capped against <c>event-envelope.v1</c>): the <c>capability.v1</c> byte cap on the
    /// element's own raw UTF-8, the unstorable-text guard, then the <c>capability.v1</c> schema.
    /// </summary>
    /// <param name="document">The <c>ext.capability</c> object of the first envelope.</param>
    /// <param name="schemas">The compiled contract schema set.</param>
    /// <param name="pathPrefix">
    /// The JSON pointer of <paramref name="document"/> inside the request body, so a detail path stays absolute
    /// (<c>/ext/capability</c> on the first-send path, empty when the body is the document itself).
    /// </param>
    internal static Error? Validate(JsonElement document, ContractSchemaSet schemas, string pathPrefix = "")
    {
        // The first-send envelope is capped against event-envelope.v1 (1 MiB), so the capability element
        // needs its own capability.v1 cap here: the raw UTF-8 length of the element, no re-encode.
        if (schemas.MaxBytes(ContractSchemaKeys.Capability) is { } maxBytes
            && JsonMarshal.GetRawUtf8Value(document).Length > maxBytes)
        {
            return Invalid(
                "the capability document exceeds the transport cap",
                [new ErrorDetail(pathPrefix, ContractValidationError.BodyTooLargeReason)]);
        }

        if (HasUnstorableText(document))
        {
            return Invalid(
                "the capability document carries a string that cannot be stored",
                [new ErrorDetail(pathPrefix, "invalid_value")]);
        }

        var verdict = schemas.Validate(ContractSchemaKeys.Capability, document);

        return verdict.IsValid
            ? null
            : Invalid("the capability document does not match capability.v1", Details(verdict, pathPrefix));
    }

    /// <summary>The 422 <c>capability_invalid</c> envelope (00 §9.2, contracts/adapter/README.md).</summary>
    /// <param name="message">Secret-free summary.</param>
    /// <param name="details">Per-member reasons.</param>
    internal static Error Invalid(string message, IReadOnlyList<ErrorDetail> details) =>
        Error.Unprocessable(ApiErrorCode.CapabilityInvalid, message, details);

    /// <summary>The schema errors as 00 §9.2 details, with this module's path rules.</summary>
    /// <param name="verdict">The failed schema verdict.</param>
    /// <param name="pathPrefix">The JSON pointer of the validated document inside the request body; empty when it is the body.</param>
    internal static IReadOnlyList<ErrorDetail> Details(ContractValidationResult verdict, string pathPrefix = "") =>
        [.. verdict.Errors.Select(error => new ErrorDetail(pathPrefix + SafePath(error), error.Reason, error.Index))];

    /// <summary>
    /// The path of a schema error that names no caller data: a member of <c>supports</c> becomes <c>*</c> and
    /// the path is capped at 200 characters. Unlike the Orchestrator's copy, an <c>additional_properties</c>
    /// error keeps the member it names — AC3 requires the closed root to report the offending member.
    /// </summary>
    /// <param name="error">The schema error to describe.</param>
    internal static string SafePath(ContractValidationError error)
    {
        var segments = error.Path.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            if (MapMembers.Contains(segments[i - 1]))
            {
                segments[i] = "*";
            }
        }

        var path = string.Join('/', segments);

        return path.Length > 200 ? path[..200] : path;
    }

    /// <summary>
    /// True when a member name or string anywhere in <paramref name="element"/> is no text PostgreSQL and the
    /// RFC 8785 form can carry: an unpaired UTF-16 surrogate (which also makes the schema engine throw) or
    /// U+0000 (refused by jsonb). Checked before the schema runs, so such a body is a 422, never a 400 or a 500.
    /// </summary>
    /// <param name="element">The parsed document root or capability object.</param>
    internal static bool HasUnstorableText(JsonElement element)
    {
        try
        {
            return Walk(element);
        }
        catch (InvalidOperationException)
        {
            // System.Text.Json refuses to transcode an invalid escape sequence.
            return true;
        }

        static bool Walk(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => Unstorable(element.GetString()!),
            JsonValueKind.Array => element.EnumerateArray().Any(Walk),
            JsonValueKind.Object => element.EnumerateObject().Any(member => Unstorable(member.Name) || Walk(member.Value)),
            _ => false,
        };
    }

    /// <summary>NUL, or a surrogate without its partner.</summary>
    private static bool Unstorable(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0')
            {
                return true;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }
}
