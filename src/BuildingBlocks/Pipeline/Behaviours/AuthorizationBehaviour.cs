using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.BuildingBlocks.Pipeline.Behaviours;

/// <summary>
/// C2 stage 4 (00 §5.5 C2, authorization): reads the handler's
/// <see cref="PermissionAttribute"/> and checks it through
/// <see cref="IPermissionEvaluator"/>, inside the tenant scope the previous stage
/// opened (membership, role and role_permission rows are tenant rows under RLS, so
/// the check must run there — 02 §4.5).
/// <para>
/// Deny by default, in four steps: a handler that declares no permission is denied
/// (Architecture.Tests fails such a handler at build time, 00 §5.7 S2); a request
/// the host resolved no principal for is denied; a request with no resolved tenant
/// throws <see cref="TenantContextMissingException"/> (the host maps it to 401
/// <c>unauthenticated</c> — no tenant means no authenticated caller); and the
/// evaluator's verdict carries the 403 code the envelope renders
/// (<c>forbidden</c>, <c>project_scope_mismatch</c>, <c>no_membership</c>).
/// </para>
/// </summary>
public sealed class AuthorizationBehaviour<TMessage, TResponse>(
    IServiceProvider services,
    IPermissionEvaluator permissions,
    IRequestTenantContextAccessor tenant,
    IRequestPrincipalAccessor principals,
    IAuditTrail audit)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!ResultFactory.IsResultContract(typeof(TResponse)))
        {
            return await next(message, cancellationToken).ConfigureAwait(false);
        }

        var permission = HandlerLookup.PermissionOf(services, typeof(TMessage), typeof(TResponse));

        if (permission is null
            && HandlerLookup.IsAuthenticatedMemberOnly(services, typeof(TMessage), typeof(TResponse)))
        {
            // 02.1.1 POST /v1/orgs: the tenant is being created, so no membership can
            // hold a permission yet. Only a resolved member (a user, never a service
            // token or an operator) may run it; everything else stays denied.
            _ = tenant.Require();

            return principals.Current is { Kind: PrincipalKind.Member, UserId: not null }
                ? await RunAuditedAsync(message, next, cancellationToken).ConfigureAwait(false)
                : ResultFactory.Failure<TResponse>(Error.Forbidden(
                    "only an authenticated user may create an organization (02.1.1 §5)"));
        }

        if (permission is null
            && HandlerLookup.IsCredentialAuthenticated(services, typeof(TMessage), typeof(TResponse)))
        {
            // 02.4.1/02.4.2: the endpoint verified a CI credential before the pipeline and published the tenant and
            // a service principal from it. Nothing else may reach these handlers.
            _ = tenant.Require();

            return principals.Current is { Kind: PrincipalKind.ServicePrincipal }
                ? await RunAuditedAsync(message, next, cancellationToken).ConfigureAwait(false)
                : ResultFactory.Failure<TResponse>(Error.Forbidden("a verified CI credential is required"));
        }

        if (permission is null)
        {
            return ResultFactory.Failure<TResponse>(Error.Forbidden(
                "the handler declares no permission (00 §5.7 S2)"));
        }

        // The tenant first: the evaluator's member path reads tenant rows under RLS,
        // and a request with no resolved tenant is unauthenticated, not forbidden.
        var context = tenant.Require();

        var principal = principals.Current;
        var decision = principal is null
            ? PermissionDecision.Denied(ApiErrorCode.Forbidden)
            : await permissions.EvaluateAsync(permission, principal, context, cancellationToken).ConfigureAwait(false);

        return decision.IsGranted
            ? await RunAuditedAsync(message, next, cancellationToken).ConfigureAwait(false)
            : ResultFactory.Failure<TResponse>(decision.ToError());
    }

    /// <summary>
    /// Runs the rest of the pipeline; when the handler is <c>[AuditedAction]</c> and the result is
    /// a success, queues the audit entry in the same unit of work (02.7.1-T2.1). A failure
    /// queues nothing, and the tenant stage rolls the transaction back.
    /// </summary>
    private async ValueTask<TResponse> RunAuditedAsync(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        var response = await next(message, cancellationToken).ConfigureAwait(false);

        if (!ResultFactory.TryGetFailure(response, out _)
            && HandlerLookup.AuditActionOf(services, typeof(TMessage), typeof(TResponse)) is { } action)
        {
            audit.Record(action, AuditActorResolver.From(principals.Current));
        }

        return response;
    }
}
