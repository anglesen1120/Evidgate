using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;
using Evidgate.Modules.Catalog.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Modules.Catalog.Endpoints;

/// <summary>
/// The reading rules every catalog write shares: a capped raw body, a closed set of body members and a bounded page size.
/// The body is read raw, not model-bound, because an unknown member must be named in the answer.
/// </summary>
internal static class CatalogRequestRules
{
    /// <summary>
    /// The body read every catalog write shares. An anonymous caller is refused here, before any byte is buffered, so
    /// it gets the pipeline's own 401 rather than a 400/413/422 for a body no caller was allowed to send.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="principals">The caller of the current async flow.</param>
    /// <param name="limit">The cap in bytes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="refuseUnstorableValues">As <see cref="ReadJsonAsync"/>.</param>
    public static async Task<(JsonElement Body, Error? Error)> ReadAuthenticatedJsonAsync(
        HttpContext context,
        IRequestPrincipalAccessor principals,
        int limit,
        CancellationToken cancellationToken,
        bool refuseUnstorableValues = true)
    {
        if (principals.Current is null)
        {
            // The envelope middleware renders this as the pipeline's own 401, so the answer keeps one copy.
            throw new TenantContextMissingException();
        }

        // A session with an unreadable organization header is published as a principal but resolves no
        // tenant, so the pipeline's own tenant 401 is raised here too, before a byte of the body is read.
        if (context.RequestServices.GetRequiredService<IRequestTenantContextAccessor>().Current is null)
        {
            throw new TenantContextMissingException();
        }

        return await ReadJsonAsync(context, limit, cancellationToken, refuseUnstorableValues).ConfigureAwait(false);
    }

    /// <summary>
    /// The body, capped before it is buffered, so an oversize request costs one header check and a 413. A body whose
    /// text cannot be stored (an unpaired surrogate or NUL in any name or value) is refused here, before any caller
    /// reads a member name.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="limit">The cap in bytes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="refuseUnstorableValues">
    /// False only for a route that must answer a bad value with its own field path: then just an unstorable member
    /// name is refused here and the route owns the value check.
    /// </param>
    private static async Task<(JsonElement Body, Error? Error)> ReadJsonAsync(
        HttpContext context,
        int limit,
        CancellationToken cancellationToken,
        bool refuseUnstorableValues = true)
    {
        if (context.Request.ContentLength > limit)
        {
            return (default, TooLarge());
        }

        try
        {
            using var buffered = new MemoryStream();
            var buffer = new byte[1024];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (buffered.Length > limit)
                {
                    return (default, TooLarge());
                }
            }

            using var document = JsonDocument.Parse(buffered.ToArray());

            var unstorable = refuseUnstorableValues
                ? UnstorableJsonText.HasUnstorableText(document.RootElement)
                : UnstorableJsonText.HasUnstorableName(document.RootElement);
            if (unstorable)
            {
                return (default, UnstorableText());
            }

            return (document.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return (default, Error.BadRequest("the body is not valid JSON"));
        }
    }

    /// <summary>The 422 of text that cannot be stored; it names no member and no value.</summary>
    public static Error UnstorableText() => Error.Invalid(
        "the body carries text that cannot be stored",
        [new ErrorDetail(string.Empty, "invalid_value")]);

    /// <summary>The 413 of a body over the route's cap.</summary>
    public static Error TooLarge() => new(ApiErrorCode.PayloadTooLarge, "the request body is too large", StatusCodes.Status413PayloadTooLarge);

    /// <summary>
    /// Refuses a body that is not a JSON object or that carries a member outside <paramref name="allowed"/>. The member
    /// name is echoed only after a brief, so an offending key that happens to be a secret is never reflected in full.
    /// </summary>
    /// <param name="body">The parsed body.</param>
    /// <param name="allowed">The members the route declares.</param>
    /// <param name="resource">What the body describes, for the message.</param>
    public static Error? UnknownMember(JsonElement body, string[] allowed, string resource)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return Error.Invalid("the body must be a JSON object", [new ErrorDetail(string.Empty, "invalid_type")]);
        }

        foreach (var member in body.EnumerateObject())
        {
            if (!allowed.Contains(member.Name, StringComparer.Ordinal))
            {
                var shown = TextRules.Brief(member.Name, 64);

                return Error.Invalid($"'{shown}' is not a field of {resource}", [new ErrorDetail(shown, "unknown_field")]);
            }
        }

        return null;
    }

    /// <summary>The <c>limit</c> query member: absent means the default, an integer in range is the page size, anything else is the 422.</summary>
    /// <param name="raw">The raw query value.</param>
    /// <param name="defaultLimit">The size when absent.</param>
    /// <param name="maxLimit">The largest accepted size.</param>
    /// <param name="limit">The parsed size.</param>
    public static Error? Limit(string? raw, int defaultLimit, int maxLimit, out int limit)
    {
        limit = defaultLimit;

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1 || parsed > maxLimit)
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"limit must be an integer between 1 and {maxLimit} (00 §9.1)"),
                [new ErrorDetail("limit", "out_of_range")]);
        }

        limit = parsed;

        return null;
    }

    /// <summary>
    /// The <c>limit</c> of a route whose contract separates the two failures: a value that is not the canonical decimal
    /// spelling of a signed integer is <c>400</c> (undecodable input), while a canonical integer outside the range — a
    /// negative one, and a magnitude past <see cref="int"/> too — is the <c>422</c> the caller can fix. Unlike
    /// <see cref="Limit"/> an absent member and an empty one differ.
    /// </summary>
    /// <param name="present">Whether the query carried the member at all.</param>
    /// <param name="raw">The raw query value.</param>
    /// <param name="defaultLimit">The size when absent.</param>
    /// <param name="maxLimit">The largest accepted size.</param>
    /// <param name="limit">The parsed size.</param>
    public static Error? RouteLimit(bool present, string raw, int defaultLimit, int maxLimit, out int limit)
    {
        limit = defaultLimit;

        if (!present)
        {
            return null;
        }

        // Read unbounded so a canonical integer of any magnitude is the range answer, not the malformed one; the
        // canonical spelling check then refuses `+5`, `01` and a padded value, whose value alone would pass.
        if (!BigInteger.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            || !string.Equals(parsed.ToString(CultureInfo.InvariantCulture), raw, StringComparison.Ordinal))
        {
            return Error.BadRequest(
                "limit must be the canonical decimal spelling of an integer",
                [new ErrorDetail("limit", "malformed")]);
        }

        if (parsed < 1 || parsed > maxLimit)
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"limit must be an integer between 1 and {maxLimit} (00 §9.1)"),
                [new ErrorDetail("limit", "out_of_range")]);
        }

        limit = (int)parsed;

        return null;
    }
}
