using System.Globalization;

namespace Evidgate.Modules.Policy.Application.Queries;

/// <summary>
/// The deployment's browser-facing base URL, bound from the same section the login routes redirect with; a local type so
/// this module never reads another module's options type. A deployment without a usable URL serves no link.
/// </summary>
internal sealed class RunDecisionOptions
{
    /// <summary>The configuration section carrying <see cref="PublicBaseUrl"/>.</summary>
    public const string SectionName = "EVIDGATE:Login";

    /// <summary>Absolute https origin of the platform as a browser reaches it, or null when the deployment serves only the API.</summary>
    public string? PublicBaseUrl { get; init; }

    /// <summary>
    /// The run-detail page of <paramref name="runId"/> in <paramref name="projectId"/>, or null when this deployment has
    /// no usable public base URL. The link is built from that origin alone — never from the request's host — so a base path
    /// cannot swallow a segment, and credentials or a non-https scheme build nothing rather than a link nobody can open.
    /// </summary>
    public string? RunUrl(Guid projectId, Guid runId)
    {
        if (string.IsNullOrWhiteSpace(PublicBaseUrl)
            || !Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(baseUri.Host)
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{baseUri.GetLeftPart(UriPartial.Authority)}/projects/{projectId:D}/runs/{runId:D}");
    }
}
