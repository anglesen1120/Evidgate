using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Application.Commands;
using Evidgate.Modules.Identity.Application.Queries;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Invites;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// The membership routes of 02.1.2 §5 — <c>/v1/orgs/{o}/memberships…</c> plus the
/// two role routes under it. Like <see cref="IdentityEndpoints"/>, the delegates do
/// nothing but bind 00 §9.1's parameters, read the <c>Idempotency-Key</c>/<c>If-Match</c>
/// headers and answer in <see cref="Result"/> terms: authorization is the C2 pipeline
/// (<c>[Permission]</c> on the handlers), the tenant is the request's (never the URL's),
/// and the 00 §9.2 envelope is the host's <c>ResultEndpointFilter</c>.
/// </summary>
/// <remarks>
/// The routes carry no <c>:guid</c> constraint on <c>{o}</c>/<c>{m}</c>/<c>{r}</c> on
/// purpose: the served OpenAPI document must spell the path exactly as
/// <c>contracts/openapi.yaml</c> does (<c>/orgs/{o}/memberships</c>), and the
/// generator emits the template verbatim. A bound value that is not a uuid fails the
/// framework's conversion and leaves a bodiless 400 that the host's status-code
/// middleware wraps in the envelope.
/// </remarks>
public static class MembershipEndpoints
{
    /// <summary>
    /// Maps the membership routes and returns the <c>/v1</c> group so the host can
    /// apply its <c>ResultEndpointFilter</c> (00 §5.5 C8).
    /// </summary>
    public static RouteGroupBuilder MapMembershipEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("identity");

        // ASP.NET derives a response for the default status (200) from the delegate's
        // return type whenever the declared success status is not 200 — and that type is
        // the C8 `Result<T>` wrapper, which is never the wire shape (the host's
        // ResultEndpointFilter renders it). The bodiless `.Produces(200)` below replaces
        // that inferred response, so `ResultOfMembershipView`/`Result`/`Error`/
        // `ErrorDetail` never reach the served OpenAPI document (`ServedDocumentTests`
        // diffs the served schema names against `contracts/openapi.yaml`).
        // 02.1.2-T2: the three invite routes answer `InviteIssued` — the membership plus the plaintext
        // token on the one response that minted it. The `.Produces(200)` line after each declared
        // success status replaces the wrapper-derived response (see the remark above).
        group.MapPost("/orgs/{o}/memberships", InviteMemberAsync)
            .Produces<InviteIssuedView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Invite a member; returns invite_token once (02)");

        group.MapPost("/orgs/{o}/memberships/{m}/resend", ResendInviteAsync)
            .Produces<InviteIssuedView>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Rotate a pending invite's token; 409 invite_not_pending (02, AC6)");

        group.MapPost("/orgs/{o}/memberships/{m}/cancel-invite", CancelInviteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Cancel a pending invite (pending -> revoked); 409 invite_not_pending (02, AC6)");

        group.MapGet("/orgs/{o}/memberships", ListMembershipsAsync)
            .Produces<MembershipListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of the org's memberships (02)");

        group.MapGet("/orgs/{o}/memberships/{m}", GetMembershipAsync)
            .Produces<MembershipView>(StatusCodes.Status200OK)
            .WithSummary("One membership; another org's id is 404 (02, AC7)");

        group.MapPatch("/orgs/{o}/memberships/{m}", UpdateMembershipAsync)
            .Produces<MembershipView>(StatusCodes.Status200OK)
            .WithSummary("Disable a member (status removed) under If-Match (02)");

        group.MapDelete("/orgs/{o}/memberships/{m}", RemoveMemberAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Remove a member; the last active tenant_admin is protected (02, AC4)");

        group.MapPost("/orgs/{o}/memberships/{m}/roles", GrantRoleAsync)
            .Produces<MembershipView>(StatusCodes.Status200OK)
            .WithSummary("Grant one role to a membership (02)");

        group.MapDelete("/orgs/{o}/memberships/{m}/roles/{r}", RevokeRoleAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Revoke one role from a membership; the last admin is protected (02, AC4)");

        return group;
    }

    private static async Task<Result<InviteIssuedView>> InviteMemberAsync(
        Guid o,
        InviteMemberRequest request,
        HttpContext context,
        IMediator mediator,
        InviteSecretCarrier carrier,
        CancellationToken cancellationToken)
    {
        // The slot opens before the dispatch so the handler's write is visible up here; the token is
        // then attached to the body of this one answer only (design D-S78-Q1).
        using var slot = carrier.BeginRequest();

        var result = await mediator.Send(
            new InviteMemberCommand(o, request.Email, request.Roles, EdgeInputs.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        return WithInviteToken(context, result, carrier);
    }

    private static async Task<Result<InviteIssuedView>> ResendInviteAsync(
        Guid o,
        Guid m,
        HttpContext context,
        IMediator mediator,
        InviteSecretCarrier carrier,
        CancellationToken cancellationToken)
    {
        using var slot = carrier.BeginRequest();

        var result = await mediator.Send(
            new ResendInviteCommand(o, m, EdgeInputs.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        return WithInviteToken(context, result, carrier);
    }

    private static async Task<Result> CancelInviteAsync(
        Guid o,
        Guid m,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(
            new CancelInviteCommand(o, m, EdgeInputs.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Attaches the minted token to the body of the answer that produced it, and publishes the row's
    /// version as <c>ETag</c> (00 §9.1). An <c>Idempotency-Key</c> replay finds no token in the slot
    /// — the stored response was written without one — so it answers the stored body unchanged.
    /// </summary>
    private static Result<InviteIssuedView> WithInviteToken(
        HttpContext context,
        Result<InviteIssuedView> result,
        InviteSecretCarrier carrier)
    {
        var token = carrier.Take();

        if (!result.IsSuccess)
        {
            return result;
        }

        var view = result.Value;

        context.Response.Headers.ETag = string.Create(CultureInfo.InvariantCulture, $"\"{view.Version}\"");

        return token is null
            ? result
            : Result.Success(view with { InviteToken = token }, status: result.Status);
    }

    private static async Task<Result<MembershipListResponse>> ListMembershipsAsync(
        Guid o,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (EdgeInputs.LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<MembershipListResponse>(error);
        }

        return await mediator.Send(
            new ListMembershipsQuery(o, EdgeInputs.Cursor(context), limit),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<MembershipView>> GetMembershipAsync(
        Guid o,
        Guid m,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new GetMembershipQuery(o, m),
            cancellationToken).ConfigureAwait(false);

        return WithETag(context, result);
    }

    private static async Task<Result<MembershipView>> UpdateMembershipAsync(
        Guid o,
        Guid m,
        UpdateMembershipRequest request,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (IfMatchError(context, out var version) is { } error)
        {
            return Result.Failure<MembershipView>(error);
        }

        var result = await mediator.Send(
            new UpdateMembershipCommand(o, m, request.Status, version, EdgeInputs.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        return WithETag(context, result);
    }

    private static async Task<Result> RemoveMemberAsync(
        Guid o,
        Guid m,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(
            new RemoveMemberCommand(o, m, EdgeInputs.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

    private static async Task<Result<MembershipView>> GrantRoleAsync(
        Guid o,
        Guid m,
        GrantRoleRequest request,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new GrantRoleCommand(o, m, request.RoleId, EdgeInputs.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        return WithETag(context, result);
    }

    private static async Task<Result> RevokeRoleAsync(
        Guid o,
        Guid m,
        Guid r,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(
            new RevokeRoleCommand(o, m, r, EdgeInputs.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Publishes the row's <c>version</c> as the response's <c>ETag</c> (00 §9.1) so a
    /// client can carry it back in <c>If-Match</c>. Set before the envelope filter
    /// writes the body; a failure carries no ETag.
    /// </summary>
    private static Result<T> WithETag<T>(HttpContext context, Result<T> result)
    {
        if (result.IsSuccess && result.SuccessValue is MembershipView view)
        {
            context.Response.Headers.ETag = string.Create(CultureInfo.InvariantCulture, $"\"{view.Version}\"");
        }

        return result;
    }

    /// <summary>
    /// Parses <c>If-Match</c> into the row's <c>version</c> (00 §9.1). Accepts the
    /// strong and weak quoted forms an <c>ETag</c> header may carry
    /// (<c>"3"</c>, <c>W/"3"</c>, <c>3</c>); anything else — including an absent
    /// header, which 00 §9.1 makes required on this route — is 422, so the command
    /// never sees a version the client did not mean.
    /// </summary>
    private static Error? IfMatchError(HttpContext context, out int version)
    {
        version = 0;
        var raw = context.Request.Headers.IfMatch.ToString();

        if (string.IsNullOrEmpty(raw))
        {
            return Error.Invalid(
                "If-Match is required: send the ETag of the membership you read (00 §9.1)",
                [new ErrorDetail("If-Match", "required")]);
        }

        var value = raw.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        value = value.Trim('"');

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out version) || version < 1)
        {
            return Error.Invalid(
                "If-Match must be the ETag this endpoint issued: the membership's version (00 §9.1)",
                [new ErrorDetail("If-Match", "invalid")]);
        }

        return null;
    }
}
