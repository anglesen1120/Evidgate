using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Modules.Evidence;

/// <summary>
/// Composition seam of the Evidence module (01.1.2-T2.1); internal by design and
/// reached through <c>InternalsVisibleTo</c> (00 §5.5).
/// </summary>
internal static class EvidenceModule
{
    /// <summary>Registers the module's services in the calling host.</summary>
    public static IServiceCollection AddEvidenceModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 06.1.1-T1: the EF write model of artifacts and their kind policies (00 §5.5 C4).
        services.AddEvidgateDbContext<EvidenceDbContext>(typeof(EvidenceDbContext).Assembly);

        return services;
    }
}
