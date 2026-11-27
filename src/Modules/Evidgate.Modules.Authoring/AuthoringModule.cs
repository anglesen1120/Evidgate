using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Application.Import;
using Evidgate.Modules.Authoring.Application.Intake;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Evidgate.Modules.Authoring.Trackers.Infrastructure;
using Evidgate.Modules.Evidence.Contracts;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Authoring;

/// <summary>
/// Composition seam of the Authoring module (13.1.1-T1.3/T1.4, 00 §5.5): internal by design and reached through
/// <c>InternalsVisibleTo</c>, exactly like <c>CatalogModule</c>. Both hosts register it (<c>ApiHost.CreateBuilder</c>,
/// <c>WorkerHost.CreateBuilder</c>) because Mediator registers the handlers in each; only the Api maps the routes
/// (<c>Endpoints.AuthoringEndpoints.MapAuthoringEndpoints</c> in <c>ApiHost.BuildApp</c>), so a test constructs the
/// module through this method exactly as the hosts do.
/// </summary>
internal static class AuthoringModule
{
    /// <summary>Registers the module's write model, its one writer and the code allocator.</summary>
    /// <param name="services">The calling host's collection.</param>
    public static IServiceCollection AddAuthoringModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A host running with InvariantGlobalization=true has string.Normalize(FormC) as a silent no-op — every
        // authoring text column would then store whatever spelling the caller sent, and the composed-equals-decomposed
        // rule would silently stop holding. Fail at composition, not at the first write.
        AuthoringText.EnsureNfcSupported();

        // 13.1.1-T1.3: the EF Core 10 write model of the five tables of migration 0039 (00 §5.5 C4).
        services.AddEvidgateDbContext<AuthoringDbContext>(typeof(AuthoringDbContext).Assembly);

        // 13.1.1-T1.4: the only code path that mutates an entity or appends an authoring_version row, exposed to the
        // other modules through the contracts interface (13.1.2's apply, 13.3.1's generation).
        services.TryAddScoped<AuthoringVersionWriter>();
        services.TryAddScoped<IAuthoringVersionWriter, AuthoringVersionWriterContract>();

        // The requirement head writer and its scoped reads (migration 0132); routes consume it with later tasks.
        services.TryAddScoped<RequirementWriter>();

        // 13.1.1-T1.3: the per-project manual case code allocator (§4, AC4).
        services.TryAddScoped<AuthoringCodeAllocator>();

        // 13.1.1-T1.2: contracts/schemas/authoring-steps.v1.json is validated through Corvus; the compiled schema set is
        // built on first use, so a host that never writes a case pays nothing at start-up.
        services.TryAddEvidgateContractValidation();
        services.TryAddSingleton(provider =>
            new Lazy<ContractSchemaSet>(() => provider.GetRequiredService<ContractSchemaSet>()));
        services.TryAddScoped<ManualCaseStepsValidator>();

        // 13.1.1-T2.2: the C2 pipeline's validation stage resolves IEnumerable<IValidator<TMessage>>.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidator<CreateNodeCommand>, CreateNodeCommandValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidator<UpdateNodeCommand>, UpdateNodeCommandValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidator<ReorderNodesCommand>, ReorderNodesCommandValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidator<DeleteNodeCommand>, DeleteNodeCommandValidator>());

        // 13.1.1-T5.4: route 25's two application services — the importer (whole file or nothing, one batch id) and the
        // exporter (one read-only walk of the tree). Both are stateless over the caller's open tenant scope, so they take
        // the same lifetime as the writer and the allocator above.
        services.TryAddScoped<AuthoringImporter>();
        services.TryAddScoped<AuthoringExporter>();

        // 13.1.1-T5.2: reading an XLSX workbook means reading a zip under caps, which is the 06.3.1 parser profile's job
        // (IParsing, the sanctioned module-to-module route: .Contracts only, 00 §5.5 C7). It is resolved LAZILY, exactly
        // like the contract schema set above, so a host that composes Authoring without Evidence starts and serves CSV and
        // JSON imports; in such a host — and in one whose parser refuses the archive — an XLSX upload answers the 422
        // `unsupported_format` the route already has for an unknown token, never a 500 and never a silent CSV reading.
        services.TryAddSingleton(provider => new Lazy<IParsing?>(() => provider.GetService<IParsing>()));

        // The requirement intake: the planner judges a request before any write, the resolver reads the caps section
        // (absent members keep the decided defaults, an out-of-range member stops the host at start).
        services.AddOptions<IntakeCapsOptions>().BindConfiguration(IntakeCapsOptions.SectionName).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<IntakeCapsOptions>, IntakeCapsOptionsValidator>());
        services.TryAddSingleton<IntakeCapsResolver>();
        services.TryAddScoped<IntakePlanner>();

        // The read-only tracker connections (Jira Cloud, Linear): OAuth clients, the allowlisted provider HTTP client,
        // the callback and the store. An absent client leaves its provider unavailable rather than failing the host.
        services.AddTrackerConnections();

        return services;
    }
}
