using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Domain;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Catalog.Endpoints;

/// <summary>
/// The reading rules every catalog write shares (00 §9.1): a capped raw body, a closed set of body members and a bounded
/// page size. The body is read raw, not model-bound, because the answer for an unknown member must name it — a model
/// bound with <c>Disallow</c> can only say that the document did not match.
/// </summary>
internal static class CatalogRequestRules
{
    /// <summary>The body, capped before it is buffered, so an oversize request costs one header check and a 413.</summary>
    /// <param name="context">The request.</param>
    /// <param name="limit">The cap in bytes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<(JsonElement Body, Error? Error)> ReadJsonAsync(HttpContext context, int limit, CancellationToken cancellationToken)
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

            return (document.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return (default, Error.BadRequest("the body is not valid JSON"));
        }
    }

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
}
