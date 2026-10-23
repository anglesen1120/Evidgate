using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// Registration of the transactional outbox write path (01.5.1-T2.2). The writer
/// is stateless over the ambient tenant scope, so it is a singleton like
/// <c>ITenantDb</c>; it needs the tenancy registration
/// (<c>AddEvidgateTenancy</c>) to be present, and the C2 outbox-flush behaviour
/// resolves it from there (00 §5.5 C2, step "outbox flush").
/// </summary>
public static class OutboxServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IOutboxWriter"/> over the registered tenancy path.</summary>
    public static IServiceCollection AddEvidgateOutbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IOutboxWriter, OutboxWriter>();

        return services;
    }
}
