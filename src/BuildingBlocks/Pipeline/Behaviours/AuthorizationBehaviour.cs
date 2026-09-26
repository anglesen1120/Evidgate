using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.BuildingBlocks.Pipeline.Behaviours;

/// <summary>
/// C2 stage 4 (00 §5.5 C2, authorization): reads the handler's
/// <see cref="PermissionAttribute"/> and checks it through
/// <see cref="IPermissionChecker"/>, inside the tenant scope the previous stage
/// opened (membership/role rows are tenant rows under RLS). A handler declaring
/// no permission is denied — the same fail-closed as the 02-pending
/// <see cref="DenyAllPermissionChecker"/>, with Architecture.Tests failing such a
/// handler at build time (00 §5.7 S2).
/// </summary>
public sealed class AuthorizationBehaviour<TMessage, TResponse>(
    IServiceProvider services,
    IPermissionChecker permissions,
    IRequestTenantContextAccessor tenant)
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
        var context = tenant.Require();

        if (permission is null
            || !await permissions.IsGrantedAsync(permission, context, cancellationToken).ConfigureAwait(false))
        {
            return ResultFactory.Failure<TResponse>(Error.Forbidden(permission is null
                ? "the handler declares no permission (00 §5.7 S2)"
                : "the principal is not allowed to perform this operation"));
        }

        return await next(message, cancellationToken).ConfigureAwait(false);
    }
}
