using Evidgate.Api.Middleware;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Evidgate.Api.OperatorIngress;

/// <summary>
/// The operator-ingress gate (01.5.1 §5, 00 §9.1): the routes under
/// <c>/internal/operator/*</c> exist for the platform operator, not a tenant
/// caller, so the gate answers "does this request carry the
/// <c>platform_operator</c> grant (with the audited support grant that 00 §9.1
/// requires)" — a yes lets the request through, anything else is a denial.
/// </summary>
public interface IOperatorGate
{
    /// <summary>True when <paramref name="httpContext"/> carries the platform-operator credential.</summary>
    ValueTask<bool> IsOperatorAsync(HttpContext httpContext);
}

/// <summary>
/// The fail-closed default (01.5.1-T4.2, mirroring the
/// <c>DenyAllPermissionEvaluator</c> precedent of
/// <c>src/BuildingBlocks/Pipeline/Authorization</c>): until the 02.x
/// authentication story wires the real operator identity, every call denies — a
/// host that forgot the real gate fails closed, never open. Tests substitute
/// their own <see cref="IOperatorGate"/>; nobody registers a permissive default.
/// </summary>
public sealed class DenyAllOperatorGate : IOperatorGate
{
    /// <inheritdoc />
    public ValueTask<bool> IsOperatorAsync(HttpContext httpContext) => ValueTask.FromResult(false);
}

/// <summary>
/// The endpoint filter that guards the <c>/internal/operator/*</c> group with
/// <see cref="IOperatorGate"/> (01.5.1 §5). A denial is the 00 §9.2 envelope:
/// 401 <c>unauthenticated</c> when the request presents no authenticated
/// principal, 403 <c>forbidden</c> when it presents one that is not the
/// platform operator — the same distinction AuthorizationBehaviour makes for
/// <c>/v1</c>, just against the operator credential instead of a tenant
/// permission. Until 02.x's authentication lands, a real request can only ever
/// take the 401 path; the 403 path is wired so a principal-bearing request (a
/// test, or the eventual auth middleware's output) is not reported as
/// unauthenticated.
/// </summary>
/// <remarks>
/// The gate is resolved per request, not captured in the constructor, so a test
/// host that replaces the singleton takes effect.
/// </remarks>
public sealed class RequireOperatorFilter : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;
        var gate = httpContext.RequestServices.GetRequiredService<IOperatorGate>();

        if (await gate.IsOperatorAsync(httpContext).ConfigureAwait(false))
        {
            return await next(context).ConfigureAwait(false);
        }

        var denied = httpContext.User.Identity?.IsAuthenticated == true
            ? new Error(ApiErrorCode.Forbidden, "the request does not carry the platform_operator grant", 403)
            : new Error(ApiErrorCode.Unauthenticated, "the request is not authenticated", 401);

        return TypedResults.Json(
            new ErrorEnvelope(new ErrorBody(
                Code: denied.Code,
                Message: denied.Message,
                RequestId: httpContext.RequestId(),
                Retryable: false,
                Details: [])),
            EvidgateJsonContext.Default.ErrorEnvelope,
            contentType: "application/json",
            statusCode: denied.Status);
    }
}
