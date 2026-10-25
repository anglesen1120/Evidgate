using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Authoring.Endpoints;

/// <summary>
/// The authoring routes of 13.1.1 §5 (26 operations under one base path). Like the catalog and quarantine routes the
/// delegates only bind 00 §9.1's parameters and answer in <c>Result</c> terms: authorization is the C2 pipeline, the
/// tenant is the request's (never the URL's), and the 00 §9.2 envelope is the host's <c>ResultEndpointFilter</c>.
/// <para>
/// The 26 operations are split across three route files by entity — <c>AuthoringNodeEndpoints</c> (tree and nodes),
/// <c>AuthoringChecklistEndpoints</c> (checklist items) and <c>AuthoringManualCaseEndpoints</c> (manual test cases) —
/// so each entity's routes, commands and validators stay together. This file owns the one group they share:
/// <c>/v1/projects/{p}/authoring</c>, mapped once so the path placeholder, the tag and (through the returned
/// <c>/v1</c> group) the host's two endpoint filters apply to all 26 identically.
/// </para>
/// <para>
/// <b>Contract for the three route files:</b> each exposes
/// <c>internal static void MapAuthoringNodeEndpoints(RouteGroupBuilder authoring)</c> /
/// <c>MapAuthoringChecklistEndpoints(RouteGroupBuilder authoring)</c> /
/// <c>MapAuthoringManualCaseEndpoints(RouteGroupBuilder authoring)</c>, in the same static class as its delegates, and
/// maps its routes as relative paths on the group it is given (<c>authoring.MapGet("/tree", …)</c>,
/// <c>authoring.MapPost("/nodes", …)</c>) — never with their own <c>/v1/projects/{p}/authoring</c> prefix. Reads take
/// <c>[Permission(Permissions.Authoring.Read)]</c>, writes <c>[Permission(Permissions.Authoring.Write)]</c> on the
/// handler class (never in the endpoint), the shared edge readers are <see cref="AuthoringEndpointSupport"/>, and a
/// mutating delegate returns <c>Result&lt;…&gt;</c> so the envelope stays the host's.
/// </para>
/// </summary>
public static class AuthoringEndpoints
{
    /// <summary>
    /// Maps the authoring routes and returns the <c>/v1</c> group so the host can apply its filters (00 §5.5 C8).
    /// </summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    public static RouteGroupBuilder MapAuthoringEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("authoring");

        // One group for the whole feature tree: the project id in the path is the addressed project, and the C2
        // pipeline still takes the tenant from the request principal (00 §9.1). A credential scoped to a sibling
        // project is refused 403 `project_scope_mismatch` before a handler runs, and another org's project is 404
        // because RLS hides it — no route here re-implements either rule.
        var authoring = group.MapGroup("/projects/{p}/authoring");

        AuthoringNodeEndpoints.MapAuthoringNodeEndpoints(authoring);
        AuthoringChecklistEndpoints.MapAuthoringChecklistEndpoints(authoring);
        AuthoringManualCaseEndpoints.MapAuthoringManualCaseEndpoints(authoring);
        AuthoringVersionEndpoints.MapAuthoringVersionEndpoints(authoring);
        AuthoringRestoreEndpoints.MapAuthoringRestoreEndpoints(authoring);

        return group;
    }
}
