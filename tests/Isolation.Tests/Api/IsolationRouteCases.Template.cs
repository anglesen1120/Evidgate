namespace Isolation.Tests.Api;

/// <summary>
/// The template registry (03.5.1): both reads are org-level routes scoped by <c>X-Evidgate-Org</c>. Org A's header
/// on an org-B member is refused at tenant resolution, a service credential cannot select an org at all, and the
/// caller's own org answers the page (or the 404 of an id no row carries).
/// </summary>
public static partial class IsolationRouteCases
{
    private static IEnumerable<RouteCase> TemplateCases()
    {
        // This world seeds no template row, so the own-org control is the empty page, not a walked set; the
        // registry's own pagination is Tenancy.IntegrationTests' subject.
        yield return HeaderCase("GET", "/v1/templates", _ => new(HttpMethod.Get, "/v1/templates?limit=1"),
            ownOrg: (route, w) => OwnObject(route, "own org answers 200", OrgBAdmin(w),
                new(HttpMethod.Get, "/v1/templates?limit=1")));

        yield return HeaderCase("GET", "/v1/templates/{t}", _ => new(HttpMethod.Get, $"/v1/templates/{Guid.CreateVersion7()}"),
            ownOrg: (route, w) => new(route, "own org, unknown template id answers 404", runner => runner.RefusedAsync(
                route,
                "own org, unknown template id answers 404",
                OrgBAdmin(w),
                new(HttpMethod.Get, $"/v1/templates/{Guid.CreateVersion7()}"))));
    }
}
