using System.Diagnostics;

namespace Evidgate.Modules.Identity.Login;

/// <summary>
/// Keeps the federated-login query string out of distributed traces (02.3.1 §5, 00 §5.7 S3: no
/// authorization code and no <c>state</c> is ever logged). The browser arrives at
/// <c>/v1/auth/oidc/{provider}/callback?code=…&amp;state=…</c>; the ASP.NET Core instrumentation copies the
/// query into the server span — <c>url.query</c> on its own, and inside <c>url.full</c>/<c>http.url</c> —
/// the two login routes have both taken away from the activity before it is exported.
/// </summary>
/// <remarks>
/// The rule is a path prefix on a code constant, not a tag-value pattern: the redaction has to hold for a
/// value the routes themselves receive from the client, and only these routes carry an
/// <c>authorization code</c>. Nothing else about the span changes, so a covered span is still traceable by
/// route, method, status code and duration.
/// </remarks>
internal static class LoginTraceRedaction
{
    /// <summary>The route prefix both federated-login routes are mounted under (<c>LoginEndpoints</c>).</summary>
    internal const string LoginPathPrefix = "/v1/auth/oidc/";

    /// <summary>The span tag that carries the query string on its own.</summary>
    internal const string QueryTag = "url.query";

    /// <summary>The literal start of the tracker callback route (<c>/v1/tracker-connections/{provider}/callback</c>).</summary>
    internal const string TrackerCallbackPrefix = "/v1/tracker-connections/";

    /// <summary>The literal end of the tracker callback route.</summary>
    internal const string TrackerCallbackSuffix = "/callback";

    /// <summary>
    /// The span tags that carry the whole URL, query included: <c>url.full</c> of the current HTTP semantic
    /// conventions and the legacy <c>http.url</c> the instrumentation still sets by default.
    /// </summary>
    internal static readonly string[] UrlTags = ["url.full", "http.url"];

    /// <summary>Whether <paramref name="path"/> is a route whose query carries an authorization code and a state.</summary>
    /// <param name="path">The request path (never the query string).</param>
    internal static bool IsLoginRoute(string? path) =>
        !string.IsNullOrEmpty(path)
        && (path.StartsWith(LoginPathPrefix, StringComparison.OrdinalIgnoreCase)
            || IsTrackerCallbackRoute(path));

    /// <summary>
    /// Whether <paramref name="path"/> is <c>/v1/tracker-connections/{provider}/callback</c>, whose query carries the
    /// tracker's authorization <c>code</c> and the consent <c>state</c>. One non-empty segment sits between the two
    /// literals; the trailing-slash form routes to the same endpoint.
    /// </summary>
    /// <param name="path">The request path (never the query string).</param>
    internal static bool IsTrackerCallbackRoute(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var trimmed = WithoutTrailingSlash(path);

        return trimmed.Length > TrackerCallbackPrefix.Length + TrackerCallbackSuffix.Length
            && trimmed.StartsWith(TrackerCallbackPrefix, StringComparison.OrdinalIgnoreCase)
            && trimmed.EndsWith(TrackerCallbackSuffix, StringComparison.OrdinalIgnoreCase)
            && trimmed.IndexOf('/', TrackerCallbackPrefix.Length) == trimmed.Length - TrackerCallbackSuffix.Length;
    }

    /// <summary>
    /// <paramref name="path"/> without a trailing slash, so tracker callbacks resolve to the same
    /// route as the form without it — routing accepts both, and so must every rule keyed on the path.
    /// </summary>
    /// <param name="path">A request path or route constant.</param>
    internal static string WithoutTrailingSlash(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    /// <summary>
    /// Removes the query of a login request from <paramref name="activity"/>: the dedicated
    /// <see cref="QueryTag"/> is deleted and every <see cref="UrlTags"/> value loses its query (and
    /// fragment). Reports whether anything changed, so a caller can assert the rule fired.
    /// </summary>
    /// <param name="activity">The span being enriched (null outside a sampled request: nothing to do).</param>
    /// <param name="path">The request path the span belongs to.</param>
    /// <returns><c>true</c> when a tag was redacted.</returns>
    internal static bool Redact(Activity? activity, string? path)
    {
        if (activity is null || !IsLoginRoute(path))
        {
            return false;
        }

        var redacted = false;

        // SetTag(key, null) deletes the tag: the query is not replaced by a placeholder, it is not there.
        if (activity.GetTagItem(QueryTag) is not null)
        {
            activity.SetTag(QueryTag, null);
            redacted = true;
        }

        foreach (var tag in UrlTags)
        {
            if (activity.GetTagItem(tag) is not string url)
            {
                continue;
            }

            var stripped = WithoutQuery(url);
            if (!string.Equals(stripped, url, StringComparison.Ordinal))
            {
                activity.SetTag(tag, stripped);
                redacted = true;
            }
        }

        return redacted;
    }

    /// <summary><paramref name="url"/> up to its query or fragment delimiter (unchanged when it has neither).</summary>
    /// <param name="url">A URL-shaped span tag value.</param>
    internal static string WithoutQuery(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var end = url.IndexOfAny(['?', '#']);

        return end < 0 ? url : url[..end];
    }
}
