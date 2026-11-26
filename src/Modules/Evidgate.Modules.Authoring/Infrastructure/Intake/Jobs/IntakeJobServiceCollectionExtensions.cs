using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Authoring.Application.Intake;
using Evidgate.Modules.Authoring.Application.Intake.Jobs;
using Evidgate.Modules.Authoring.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Authoring.Infrastructure.Intake.Jobs;

/// <summary>Registers the intake extraction job.</summary>
internal static class IntakeJobServiceCollectionExtensions
{
    /// <summary>
    /// Registers the extraction handler, the job and its store. All are singletons: the handler opens its own tenant
    /// scopes per attempt, and the reader it uses (registered by the Evidence module) keeps no state. The extractor
    /// stores and hashes NFC text, so a host that cannot compose refuses here instead of storing decomposed text.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    internal static IServiceCollection AddAuthoringIntakeJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        AuthoringText.EnsureNfcSupported();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DocumentTextExtractor>();
        services.TryAddSingleton<IntakeExtractionStore>();
        services.TryAddSingleton<ExtractIntakeJob>();
        services.AddTransactionlessTaskHandler<ExtractIntakePayload, ExtractIntakeTaskHandler>();

        return services;
    }
}
