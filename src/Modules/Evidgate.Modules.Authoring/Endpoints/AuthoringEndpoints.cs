using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Authoring.Endpoints;

/// <summary>
/// The authoring routes of 13.1.1 §5 (twenty-four operations under one base path). Like the catalog and quarantine routes the
/// delegates only bind 00 §9.1's parameters and answer in <c>Result</c> terms: authorization is the C2 pipeline, the
/// tenant is the request's (never the URL's), and the 00 §9.2 envelope is the host's <c>ResultEndpointFilter</c>.
/// <para>
/// The twenty-four operations are split across six route files by subject matter — <c>AuthoringNodeEndpoints</c> (tree
/// and nodes), <c>AuthoringChecklistEndpoints</c> (checklist items), <c>AuthoringManualCaseEndpoints</c> (manual test
/// cases and their links), <c>AuthoringVersionEndpoints</c> (the version timeline and its detail),
/// <c>AuthoringRestoreEndpoints</c> (the three restore routes) and <c>AuthoringImportEndpoints</c> (the import and
/// export of 13.1.1 AC13-AC19) — so each subject's routes, commands and validators stay together. This file owns the
/// one group they share: <c>/v1/projects/{p}/authoring</c>, mapped once so the path placeholder, the tag and (through
/// the returned <c>/v1</c> group) the host's two endpoint filters apply to all twenty-four identically.
/// </para>
/// <para>
/// <b>Contract for the six route files:</b> each exposes one
/// <c>internal static void MapAuthoring…Endpoints(RouteGroupBuilder authoring)</c>
/// (<c>MapAuthoringNodeEndpoints</c>, <c>MapAuthoringChecklistEndpoints</c>, <c>MapAuthoringManualCaseEndpoints</c>,
/// <c>MapAuthoringVersionEndpoints</c>, <c>MapAuthoringRestoreEndpoints</c>, <c>MapAuthoringImportEndpoints</c>), in the
/// same static class as its delegates, and
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

        AiSettingsEndpoints.MapAiSettingsEndpoints(group);

        AuthoringNodeEndpoints.MapAuthoringNodeEndpoints(authoring);
        AuthoringChecklistEndpoints.MapAuthoringChecklistEndpoints(authoring);
        AuthoringManualCaseEndpoints.MapAuthoringManualCaseEndpoints(authoring);
        AuthoringVersionEndpoints.MapAuthoringVersionEndpoints(authoring);
        AuthoringRestoreEndpoints.MapAuthoringRestoreEndpoints(authoring);

        // The pending-changes queue: queue, list, detail, discard and apply on their own base path under the group.
        AuthoringChangeEndpoints.MapAuthoringChangeEndpoints(authoring);

        // 13.1.1-T5: route 25, the import/export pair (its own file because it takes and answers files, not DTOs).
        AuthoringImportEndpoints.MapAuthoringImportEndpoints(authoring);

        return group;
    }
}
