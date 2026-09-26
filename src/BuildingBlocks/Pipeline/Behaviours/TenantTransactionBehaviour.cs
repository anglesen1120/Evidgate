using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Mediator;

namespace Evidgate.BuildingBlocks.Pipeline.Behaviours;

/// <summary>
/// C2 stage 3 (00 §5.5 C2, tenant transaction): opens the one
/// <see cref="ITenantScope"/> a pipeline-managed message runs in — read-only for
/// an <see cref="IBaseQuery"/> message (C1/T95), read-write otherwise — from the
/// request tenant the host resolved before the pipeline. Missing context throws
/// <see cref="TenantContextMissingException"/> (fail closed; the host maps it to
/// 401, and no organization is ever picked silently).
/// <para>
/// The outbox flush the C2 order names between the handler and the commit needs
/// no code here: the handler's <c>IOutboxWriter</c> writes its row on this very
/// scope's connection and transaction (01.5.1-T2.2), so nothing is buffered — this
/// behaviour owns the ordering boundary: a failed <see cref="Result"/> or a thrown
/// exception disposes the scope uncommitted (the business change and its outbox
/// row roll back together), and only a success reaches <see cref="ITenantScope.CommitAsync"/>.
/// </para>
/// <para>
/// The skeleton slice's DTO messages (01.1.2-T3) carry no tenant scope and no
/// permission (01.1.2 §8), so this stage and the two that follow pass them
/// through; 04/05 replace those routes with Result-carrying commands, and
/// Architecture.Tests keeps the exemption pinned meanwhile.
/// </para>
/// </summary>
public sealed class TenantTransactionBehaviour<TMessage, TResponse>(
    ITenantScopeFactory scopes,
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

        var context = tenant.Require();
        var readOnly = typeof(IBaseQuery).IsAssignableFrom(typeof(TMessage));

        await using var scope = await scopes
            .BeginAsync(context.OrgId, context.ProjectId, readOnly, cancellationToken)
            .ConfigureAwait(false);

        var response = await next(message, cancellationToken).ConfigureAwait(false);

        if (ResultFactory.TryGetFailure(response, out _))
        {
            // A denied or invalid-in-scope command rolls back (00 §5.5 C2): the
            // scope disposes uncommitted.
            return response;
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return response;
    }
}
