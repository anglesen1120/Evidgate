using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Ingestion.Application;
using Evidgate.Modules.Ingestion.Application.Commands;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Ingestion.Domain;
using Evidgate.Modules.Ingestion.Endpoints;
using Evidgate.Modules.Ingestion.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Ingestion;

/// <summary>
/// Composition seam of the Ingestion module (01.1.2-T2.1): both entry points call
/// it with the same module set. Internal on purpose — 00 §5.5 keeps only
/// <c>.Contracts</c>/<c>.Endpoints</c> public, so the Api and Worker reach it
/// through <c>InternalsVisibleTo</c> (declared in the .csproj).
/// </summary>
internal static class IngestionModule
{
    /// <summary>Registers the module's services in the calling host.</summary>
    public static IServiceCollection AddIngestionModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 04.7.1-T2.1: the pure negotiation rule, one instance for the registration path (04.7.1-T2.2/T2.3) and
        // the 05.7.x claim path.
        services.AddSingleton<IProtocolNegotiator, ProtocolNegotiator>();

        // 04.7.1-T2.2/T2.3: the capability store and the registration path shared by both entries.
        services.TryAddScoped<AdapterCapabilityStore>();
        services.TryAddScoped<AdapterCapabilityRegistrar>();

        // 04.1.1-T1 (D11): the first-send seam both the raw-bytes command (04.7.1-T2.3) and the events
        // handler call, so the first envelope is parsed once per request either way.
        services.TryAddScoped<FirstSendCapabilityService>();

        // 04.1.1-T3: the gzip decode caps are bound and validated at boot like every other
        // EVIDGATE__* group (01.1.2-T1). BindConfiguration resolves IConfiguration from the container,
        // so the module seam keeps its signature; the two composition roots need no extra wiring.
        services
            .AddOptions<IngestionLimits>()
            .BindConfiguration(IngestionLimits.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The events route's burst budget: one bound group for both entry points, one
        // resident limiter per process (the partition map is bounded, see the limiter).
        services
            .AddOptions<IngestionRateLimits>()
            .BindConfiguration(IngestionRateLimits.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IngestionRateLimiter>();

        // The events route's quota admission and the per-request carrier of the block's Retry-After.
        services.TryAddScoped<IngestQuotaAdmission>();
        services.TryAddSingleton<IngestQuotaVeto>();

        // 04.1.2-T4.1: the EF write model of producer slots (00 §5.5 C4).
        services.AddEvidgateDbContext<IngestionDbContext>(typeof(IngestionDbContext).Assembly);

        // 04.1.2-T4.2: the producer registry 05's run-create, 06.1.2's upload-url and 05.3.2-T1's
        // finalize call (T9/T54/T85).
        services.TryAddScoped<IProducerRegistry, ProducerRegistry>();

        // The accepted-batch metering task reads its batch and meters it through the Usage ledger, so the ingest commit's
        // outbox row reaches a handler in every host that composes this module.
        services.TryAddScoped<IMeterEventBatchReader, MeterEventBatchStore>();
        services.AddTaskHandler<MeterEventBatchPayload, MeterEventBatchTaskHandler>();

        // The capability body validates against contracts/adapter/capability.v1.json; the compiled set is built on
        // first use, so a host that never registers a capability pays nothing at start-up.
        services.TryAddEvidgateContractValidation();
        services.TryAddSingleton(provider => new Lazy<ContractSchemaSet>(() => provider.GetRequiredService<ContractSchemaSet>()));

        return services;
    }
}
