using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.BuildingBlocks.Persistence.Idempotency;

/// <summary>
/// Registration of the idempotency store (01.5.1-T6.1). The store is stateless
/// over the ambient tenant scope, so it is a singleton like <c>ITenantDb</c>; it
/// needs the tenancy registration (<c>AddEvidgateTenancy</c>) to be present, and
/// the C2 idempotency behaviour resolves it from there (00 §5.5 C2, step
/// "idempotency").
/// </summary>
public static class IdempotencyServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IIdempotencyStore"/> over the registered tenancy path.</summary>
    public static IServiceCollection AddEvidgateIdempotency(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IIdempotencyStore, IdempotencyStore>();

        return services;
    }
}
