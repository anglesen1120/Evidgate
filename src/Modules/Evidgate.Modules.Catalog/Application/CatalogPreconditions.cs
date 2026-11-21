using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>
/// The <c>If-Match</c> reading of the catalog's write routes, kept out of the endpoints so a route that must not parse
/// a header before the authorization stage can still share the one implementation.
/// </summary>
internal static class CatalogPreconditions
{
    /// <summary>The page size of a catalog list when the caller sends no <c>limit</c>.</summary>
    internal const int DefaultListLimit = 50;

    /// <summary>The largest accepted page size of a catalog list.</summary>
    internal const int MaxListLimit = 200;

    /// <summary>
    /// The <c>limit</c> query member: absent means the default, an integer in range is the page size, anything else
    /// is the 422. It lives here, not in the endpoint, so a route whose query is validated after authorization can
    /// call it and an unauthorized caller never learns its page bound; the endpoint wrapper delegates to this.
    /// </summary>
    /// <param name="raw">The raw query value.</param>
    /// <param name="defaultLimit">The size when absent.</param>
    /// <param name="maxLimit">The largest accepted size.</param>
    /// <param name="limit">The parsed size.</param>
    internal static Error? Limit(string? raw, int defaultLimit, int maxLimit, out int limit)
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

    /// <summary>Null and the version when the header is the ETag this module issued, else the 422 to answer.</summary>
    /// <param name="raw">The raw header value, or null.</param>
    /// <param name="what">What the ETag belongs to, for the message.</param>
    /// <param name="version">The version the header names; 0 when it carries none.</param>
    internal static Error? IfMatch(string? raw, string what, out int version)
    {
        version = 0;

        if (string.IsNullOrEmpty(raw))
        {
            return Error.Invalid(
                $"If-Match is required: send the ETag of the {what} you read (00 §9.1)",
                [new ErrorDetail("If-Match", "required")]);
        }

        var value = raw.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        value = value.Trim('"');

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out version) && version >= 1
            ? null
            : Error.Invalid(
                $"If-Match must be the ETag this endpoint issued: the {what}'s version (00 §9.1)",
                [new ErrorDetail("If-Match", "invalid")]);
    }
}
