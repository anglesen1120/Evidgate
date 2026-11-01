namespace Evidgate.Modules.Policy.Application.Queries;

/// <summary>
/// The deployment's browser-facing base URL, as the decision body's <c>url</c> needs it. It is the same
/// <c>EVIDGATE:Login</c> key the login routes redirect with, read through a local options type so this module does not
/// reach into another module's internals; a deployment without it serves no link.
/// </summary>
internal sealed class RunDecisionOptions
{
    /// <summary>The configuration section carrying <see cref="PublicBaseUrl"/>.</summary>
    public const string SectionName = "EVIDGATE:Login";

    /// <summary>Absolute https origin of the platform as a browser reaches it, or null when the deployment serves only the API.</summary>
    public string? PublicBaseUrl { get; init; }

    /// <summary>
    /// The run-detail page of <paramref name="runId"/> in <paramref name="projectId"/>, or null when this deployment has
    /// no usable public base URL. The link is a convenience: it is never derived from the request's own host, and a base
    /// that is not an absolute https origin builds nothing rather than a link nobody can open.
    /// </summary>
    public string? RunUrl(Guid projectId, Guid runId)
    {
        if (string.IsNullOrWhiteSpace(PublicBaseUrl)
            || !Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(baseUri.Host))
        {
            return null;
        }

        return new Uri(
            baseUri,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"projects/{projectId:D}/runs/{runId:D}")).ToString();
    }
}
