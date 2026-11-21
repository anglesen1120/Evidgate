using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Domain;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>
/// The input rules shared by catalog routes: the <c>limit</c> bound, the closed taxonomy filters, and the
/// <c>If-Match</c> reading of the write routes. They live here, not in the endpoints, so a route that must not parse
/// a value before the authorization stage can still share the one implementation.
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

    /// <summary>
    /// The three closed taxonomy filters of a catalog list, canonicalised to the catalogue's spelling. An unknown value
    /// is the 422 naming the member, never an empty page; absent stays absent and filters nothing.
    /// </summary>
    /// <param name="platform">The raw <c>platform</c> query value, or null.</param>
    /// <param name="level">The raw <c>level</c> query value, or null.</param>
    /// <param name="purpose">The raw <c>purpose</c> query value, or null.</param>
    /// <param name="canonicalPlatform">The catalogue spelling, or null when the filter is absent.</param>
    /// <param name="canonicalLevel">The catalogue spelling, or null when the filter is absent.</param>
    /// <param name="canonicalPurpose">The catalogue spelling, or null when the filter is absent.</param>
    internal static Error? TaxonomyFilters(
        string? platform,
        string? level,
        string? purpose,
        out string? canonicalPlatform,
        out string? canonicalLevel,
        out string? canonicalPurpose)
    {
        canonicalLevel = null;
        canonicalPurpose = null;

        canonicalPlatform = Taxonomy.Canonical(platform, Taxonomy.Platforms);
        if (platform is not null && canonicalPlatform is null)
        {
            return Error.Invalid(
                "platform must be one of web, api, unit or mobile",
                [new ErrorDetail("platform", "invalid_value")]);
        }

        canonicalLevel = Taxonomy.Canonical(level, Taxonomy.Levels);
        if (level is not null && canonicalLevel is null)
        {
            return Error.Invalid(
                "level must be one of unit, integration, e2e or smoke",
                [new ErrorDetail("level", "invalid_value")]);
        }

        canonicalPurpose = Taxonomy.Canonical(purpose, Taxonomy.Purposes);
        return purpose is not null && canonicalPurpose is null
            ? Error.Invalid(
                "purpose must be one of regression, smoke, release_gate or exploratory",
                [new ErrorDetail("purpose", "invalid_value")])
            : null;
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
