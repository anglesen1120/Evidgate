namespace Evidgate.Modules.Identity.Sessions.Http;

/// <summary>
/// Marks an endpoint that is deliberately outside the antiforgery rule (02.3.2-T1, D6). It is the exception, not
/// the default: the enforcement is host-wide middleware, so a new mutating route is covered without being
/// annotated, and a route that must be exempt has to say so here — where the served-route-table test sees it and
/// fails when the exempt set drifts (a route renamed or removed leaves a stale exemption, a new exemption
/// appears in the diff of that test's expected set).
/// </summary>
/// <remarks>
/// The only legitimate use is a route that SETS a credential cookie rather than authenticating with one
/// (<c>POST /v1/auth/invite-token</c>): the antiforgery rule protects a request whose authority came from a
/// cookie, and such a request has none to protect. A route that authenticates with a cookie is never exempt,
/// however harmless its mutation looks — the header is defence in depth on top of <c>SameSite=Lax</c>, not a
/// substitute for it.
/// </remarks>
internal sealed class XsrfExemptMetadata
{
    /// <summary>The single instance every exempt endpoint is annotated with.</summary>
    internal static XsrfExemptMetadata Instance { get; } = new();

    /// <summary>Private: <see cref="Instance"/> is the only instance, so metadata comparison is a reference check.</summary>
    private XsrfExemptMetadata()
    {
    }
}
