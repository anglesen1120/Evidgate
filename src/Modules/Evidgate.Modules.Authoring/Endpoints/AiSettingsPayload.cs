using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Endpoints;

/// <summary>
/// The edge reading of the two settings write routes: the request's members, its 32 KiB body cap and the
/// <c>If-Match</c> version tag. Member <em>names</em> are decided here; member <em>values</em> are the command's.
/// </summary>
/// <remarks>
/// The tag is this route family's own version number, not the authoring entry hash: its absent, quoted or
/// malformed spelling is a 422 on <c>/If-Match</c> before the handler reads a row, and the stale comparison
/// (a well-formed tag that no longer names the head) stays a 412 in the handler, where the head is known.
/// </remarks>
internal static class AiSettingsPayload
{
    /// <summary>The body cap of the settings document, which is far smaller than an authoring entry.</summary>
    internal const int MaxBodyBytes = 32 * 1024;

    private static readonly string[] RootMembers = ["settings", "template", "reason"];

    private static readonly string[] TemplateMembers = ["code", "version"];

    /// <summary>
    /// Reads one save request: the object body, its <c>If-Match</c> version and the bounded member set.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>The request, or the 413/422 the caller returns unchanged.</returns>
    internal static async Task<(SaveAiSettingsRequest? Request, Error? Error)> ReadAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IfMatch(context.Request, out var version) is { } tagError)
        {
            return (null, tagError);
        }

        var (body, bodyError) = await AuthoringEndpointSupport
            .ReadBodyAsync(context, MaxBodyBytes, cancellationToken)
            .ConfigureAwait(false);

        if (bodyError is not null)
        {
            return (null, bodyError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, RootMembers, "AI settings save") is { } unknown)
        {
            return (null, unknown);
        }

        if (!body.TryGetProperty("settings", out var settings))
        {
            return (null, Refuse("/settings", "required"));
        }

        if (settings.ValueKind is not JsonValueKind.Object)
        {
            return (null, Refuse("/settings", "invalid"));
        }

        var (templateCode, templateVersion, templateError) = ReadTemplate(body);
        if (templateError is not null)
        {
            return (null, templateError);
        }

        var (reason, reasonError) = ReadReason(body);
        if (reasonError is not null)
        {
            return (null, reasonError);
        }

        return (new SaveAiSettingsRequest(settings, templateCode, templateVersion, reason, version), null);
    }

    /// <summary>
    /// Writes the version <c>ETag</c> the settings reads answer: the version number in quotes, so a client
    /// round-trips it as the <c>If-Match</c> of the next save.
    /// </summary>
    /// <param name="context">The request being answered.</param>
    /// <param name="versionNo">The version the answer carries.</param>
    internal static void SetVersionETag(HttpContext context, int versionNo) =>
        context.Response.Headers.ETag = "\"" + versionNo.ToString(CultureInfo.InvariantCulture) + "\"";

    private static Error? IfMatch(HttpRequest request, out int version)
    {
        version = 0;

        if (request.Headers.IfMatch.Count == 0)
        {
            return Refuse("/If-Match", "required");
        }

        if (request.Headers.IfMatch.Count > 1)
        {
            return Refuse("/If-Match", "invalid");
        }

        var raw = request.Headers.IfMatch[0]?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return Refuse("/If-Match", "required");
        }

        // `"3"` is the exact spelling a client echoes from GET; the unquoted form is accepted because the
        // documented tag is the number. A weak tag, a list or a signed number is not this route's tag.
        var value = raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;

        return value.Length > 0
            && value.All(static character => character is >= '0' and <= '9')
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out version)
            ? null
            : Refuse("/If-Match", "invalid");
    }

    private static (string? Code, int? Version, Error? Error) ReadTemplate(JsonElement body)
    {
        if (!body.TryGetProperty("template", out var template) || template.ValueKind is JsonValueKind.Null)
        {
            return (null, null, null);
        }

        if (template.ValueKind is not JsonValueKind.Object)
        {
            return (null, null, Refuse("/template", "invalid"));
        }

        if (AuthoringEndpointSupport.UnknownMembers(template, TemplateMembers, "AI settings template") is { } unknown)
        {
            return (null, null, unknown);
        }

        if (!template.TryGetProperty("code", out var code) || code.ValueKind is not JsonValueKind.String)
        {
            return (null, null, Refuse("/template/code", "required"));
        }

        if (!template.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number))
        {
            return (null, null, Refuse("/template/version", "required"));
        }

        return (code.GetString(), number, null);
    }

    private static (string? Reason, Error? Error) ReadReason(JsonElement body)
    {
        if (!body.TryGetProperty("reason", out var reason) || reason.ValueKind is JsonValueKind.Null)
        {
            return (null, null);
        }

        return reason.ValueKind is JsonValueKind.String
            ? (reason.GetString(), null)
            : (null, Refuse("/reason", "invalid"));
    }

    private static Error Refuse(string path, string reason) =>
        Error.Invalid("the AI settings request is not valid", [new ErrorDetail(path, reason)]);
}

/// <summary>One parsed save request: the document, its provenance pair, the reason and the read version.</summary>
/// <param name="Settings">The <c>settings</c> member, already a JSON object.</param>
/// <param name="TemplateCode">The template code, or null.</param>
/// <param name="TemplateVersion">The template version, or null.</param>
/// <param name="Reason">The reason, or null.</param>
/// <param name="Version">The version the <c>If-Match</c> named.</param>
internal sealed record SaveAiSettingsRequest(
    JsonElement Settings,
    string? TemplateCode,
    int? TemplateVersion,
    string? Reason,
    int Version);
