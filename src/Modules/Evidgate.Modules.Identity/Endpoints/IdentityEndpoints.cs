using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Application.Commands;
using Evidgate.Modules.Identity.Application.Queries;
using Evidgate.Modules.Identity.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// The identity module's public routes (02 §4.2): <c>GET /v1/permissions</c> and
/// <c>GET /v1/roles</c> under <c>role:read</c> — any member may read the permission
/// model (02.2.1 AC6) — plus the flagged custom-role writes
/// <c>POST /v1/roles</c> and <c>PATCH /v1/roles/{r}</c> under <c>role:write</c>.
/// Authorization lives in the C2 pipeline (<c>[Permission]</c> on the handlers,
/// 00 §5.7 S2), never here, and the request bodies' shape is the C2 validation
/// stage's (<c>CreateRoleCommandValidator</c>/<c>UpdateRoleCommandValidator</c>);
/// the delegates only bind the 00 §9.1 cursor and <c>Idempotency-Key</c> inputs and
/// answer in <see cref="Result"/> terms.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the write routes exist in V1 although custom roles do not.</b> The two
/// commands behind them are gated by <c>EVIDGATE__IDENTITY__CUSTOMROLESENABLED</c>
/// (off, RECOMMENDATIONS 02-Q3), and the documented V1 answer is 403
/// <c>forbidden</c> — not 404 — so the route surface of 02 §4.2 is complete and the
/// V2 editor is a configuration change. A built-in role is refused with 409
/// <c>builtin_role_immutable</c> whatever the flag says (AC6).
/// </para>
/// <para>
/// <b>The host composes the routes and applies the envelope filter.</b> This
/// extension maps the routes on the caller's <c>IEndpointRouteBuilder</c> and
/// returns the <c>/v1</c> group; the entry point adds the C8
/// <c>ResultEndpointFilter</c> that turns a <see cref="Result"/> into the 00 §9.2
/// envelope. The filter lives in <c>Evidgate.Api</c> — a module must not reference
/// the host — so the direction stays host → module, and
/// <c>ApiHost.BuildApp</c> is the one place that wires both (a host that forgets the
/// filter fails <c>Identity.IntegrationTests</c>' route tests, which boot the real
/// composition).
/// </para>
/// <para>
/// The Worker composes the same module set without HTTP and never calls this.
/// </para>
/// </remarks>
public static class IdentityEndpoints
{
    /// <summary>
    /// Maps the module's <c>/v1</c> routes and returns the group so the host can
    /// apply its <c>ResultEndpointFilter</c>.
    /// </summary>
    public static RouteGroupBuilder MapIdentityEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("identity");

        group.MapGet("/permissions", ListPermissionsAsync)
            .Produces<PermissionListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of the permission catalogue (02)");

        group.MapGet("/roles", ListRolesAsync)
            .Produces<RoleListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of roles: built-ins plus the caller's org rows (02)");

        // 02.2.1-T2.2: the flagged custom-role writes. Both carry an
        // `Idempotency-Key` (00 §9.1, TL T11) — the C2 idempotency stage claims it, so
        // a retried create replays its 201 instead of colliding with its own unique
        // (org_id, code).
        //
        // The bodiless 200 on the create is deliberate: when no response metadata
        // covers the return type's default status, the OpenAPI generator derives one
        // from the delegate's `Result<RoleView>` and publishes the INTERNAL wrapper as
        // a served schema (`ResultOfRoleView`, plus `Result`/`Error`/`ErrorDetail`),
        // which Contract.Tests' served-document diff reports as an undocumented DTO.
        // Declaring the default status explicitly keeps the served document to the wire
        // shapes: 201 `RoleView` on success, the 00 §9.2 envelope otherwise.
        group.MapPost("/roles", CreateRoleAsync)
            .Produces<RoleView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Create a custom role (flagged off in V1: 403, 02)");

        group.MapPatch("/roles/{r}", UpdateRoleAsync)
            .Produces<RoleView>(StatusCodes.Status200OK)
            .WithSummary("Rename a custom role and/or add grants to it (flagged, 02)");

        // The signed-in account's own profile: pre-tenant, resolved from the session cookie alone, so it
        // sits on `/v1` beside the tenant routes without ever entering one.
        MyProfileEndpoints.Map(group);

        return group;
    }

    private static async Task<Result<PermissionListResponse>> ListPermissionsAsync(
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (EdgeInputs.LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<PermissionListResponse>(error);
        }

        return await mediator.Send(
            new ListPermissionsQuery(EdgeInputs.Cursor(context), limit),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<RoleListResponse>> ListRolesAsync(
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (EdgeInputs.LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<RoleListResponse>(error);
        }

        return await mediator.Send(
            new ListRolesQuery(EdgeInputs.Cursor(context), limit),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /v1/roles</c> (02.2.1 §5): binds the body and the
    /// <c>Idempotency-Key</c> and hands the command to the pipeline, which validates
    /// its shape (stage 2), authorizes it (<c>role:write</c>, the handler's attribute)
    /// and validates the codes against the catalogue (422 <c>unknown_permission</c> /
    /// <c>permission_not_assignable</c>).
    /// </summary>
    private static async Task<Result<RoleView>> CreateRoleAsync(
        CreateRoleRequest request,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(
            new CreateRoleCommand(request.Code, request.Name, request.Permissions, EdgeInputs.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// <c>PATCH /v1/roles/{r}</c> (02.2.1 §5): same inputs, then the command's own
    /// rules — 404 for a role this tenant cannot see, 409
    /// <c>builtin_role_immutable</c> for a catalogue role, 403 while the flag is off.
    /// </summary>
    private static async Task<Result<RoleView>> UpdateRoleAsync(
        Guid r,
        UpdateRoleRequest request,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(
            new UpdateRoleCommand(r, request.Name, request.AddPermissions, EdgeInputs.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
}
