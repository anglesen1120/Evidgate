using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// The bounded JSON body reader the module's anonymous routes share: the declared and the streamed length are
/// both capped at the route's own limit, and the body must be <c>application/json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The media-type rule is a security boundary, not a formality: a cross-site form post can reach a route and
/// set a cookie, and <c>enctype=text/plain</c> is the one encoding that needs no CORS preflight, so requiring
/// <c>application/json</c> is what stops such a form from being read as a JSON document.
/// </para>
/// <para>
/// The limit is enforced while reading, never after parsing: a caller that streams an unbounded document is
/// refused at the first byte past the cap instead of being buffered. The returned document is the caller's to
/// dispose; nothing read here is logged.
/// </para>
/// </remarks>
internal static class BoundedJsonBody
{
    /// <summary>Reads a body no larger than <paramref name="maxBytes"/> as a parsed JSON document.</summary>
    /// <param name="context">The request.</param>
    /// <param name="maxBytes">The route's own body cap.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>
    /// The parsed document (whatever its root kind is, which the caller judges) with a null error, or the
    /// 413/422 the route answers: too large, not <c>application/json</c>, empty, or not JSON.
    /// </returns>
    internal static async Task<(JsonDocument? Document, Error? Error)> ReadAsync(
        HttpContext context,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.ContentLength > maxBytes)
        {
            return (null, TooLarge());
        }

        if (!context.Request.HasJsonContentType())
        {
            return (null, Invalid("the request body must be application/json"));
        }

        using var limited = new MemoryStream();
        var buffer = new byte[1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await limited.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (limited.Length > maxBytes)
            {
                return (null, TooLarge());
            }
        }

        if (limited.Length == 0)
        {
            return (null, Invalid("the request body is required"));
        }

        try
        {
            return (JsonDocument.Parse(limited.ToArray()), null);
        }
        catch (JsonException)
        {
            return (null, Invalid("the request body is not valid JSON"));
        }
    }

    /// <summary>The 413 of a body over the route's cap.</summary>
    internal static Error TooLarge() => new(
        ApiErrorCode.PayloadTooLarge,
        "the request body is too large",
        StatusCodes.Status413PayloadTooLarge);

    /// <summary>The 422 whose <c>details[].path</c> blames the body itself rather than one of its members.</summary>
    /// <param name="message">What is wrong with the body, in the contract's own words.</param>
    internal static Error Invalid(string message) =>
        Error.Invalid(message, [new ErrorDetail("body", "invalid")]);
}
