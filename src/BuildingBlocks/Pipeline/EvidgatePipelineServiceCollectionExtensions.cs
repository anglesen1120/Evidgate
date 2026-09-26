using Evidgate.BuildingBlocks.Persistence.Idempotency;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Behaviours;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.BuildingBlocks.Pipeline;

/// <summary>
/// Registration of the C2 pipeline (00 §5.5 C2): the ambient request tenant, the
/// fail-closed permission stub, the stages' persistence collaborators and the five
/// behaviours in <see cref="PipelineBehaviourOrder.C2"/>'s exact order.
/// <para>
/// Mediator itself stays registered by the entry-point host (<c>AddMediator</c>):
/// its source generator emits the <c>Mediator</c> implementation per-assembly, and
/// a second one here would shadow the host's and see only this project's message
/// scan — so the pipeline registers the behaviours it owns and composes on the
/// host's mediator.
/// </para>
/// </summary>
public static class EvidgatePipelineServiceCollectionExtensions
{
    /// <summary>
    /// The complete C2 pipeline: behaviours in the fixed order plus the services
    /// they resolve. All uses are <c>TryAdd*</c> so a host's own registration wins
    /// (the Api's explicit <c>AddEvidgateTenancy</c>, or a test's fake
    /// <see cref="IPermissionChecker"/>).
    /// </summary>
    public static IServiceCollection AddEvidgatePipeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The ambient request tenant (00 §5.5 C2): the host's principal resolver
        // (02.6.2) writes it per request; the tenant transaction stage reads it.
        services.TryAddSingleton<RequestTenantContextAccessor>();
        services.TryAddSingleton<IRequestTenantContextAccessor>(
            provider => provider.GetRequiredService<RequestTenantContextAccessor>());
        services.TryAddSingleton<IRequestTenantContextSetter>(
            provider => provider.GetRequiredService<RequestTenantContextAccessor>());

        // The stages' collaborators — the single data-access path (01.4.2-T1), the
        // idempotency store (01.5.1-T6) and the outbox writer (01.5.1-T2) — are part
        // of this registration so a host that forgets them fails to resolve at boot,
        // not mid-request; each is itself TryAdd*-idempotent.
        services.AddEvidgateTenancy();
        services.AddEvidgateIdempotency();
        services.AddEvidgateOutbox();

        // 02's permission catalogue replaces this (01.4.2 plan §3): until then every
        // pipeline-managed message is denied — a host that forgot the real checker
        // fails closed, never open.
        services.TryAddSingleton<IPermissionChecker, DenyAllPermissionChecker>();

        return services.AddEvidgatePipelineBehaviours();
    }

    /// <summary>
    /// Registers the behaviours of <see cref="PipelineBehaviourOrder.C2"/> as
    /// <see cref="IPipelineBehavior{TMessage,TResponse}"/> open generics, in order —
    /// Mediator's generated mediator resolves
    /// <c>IEnumerable&lt;IPipelineBehavior&lt;TMessage,TResponse&gt;&gt;</c> from DI,
    /// so registration order IS pipeline order for the non-AOT path the hosts run
    /// (Mediator README: pipeline behaviours may be registered manually when not
    /// compiling NativeAOT). <c>Add</c>, never <c>TryAdd*</c>: the position of each
    /// open generic is the contract the architecture test asserts (AC4).
    /// </summary>
    public static IServiceCollection AddEvidgatePipelineBehaviours(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        foreach (var behaviour in PipelineBehaviourOrder.C2)
        {
            services.AddSingleton(typeof(IPipelineBehavior<,>), behaviour);
        }

        return services;
    }
}
