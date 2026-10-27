using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Orchestrator.Application.Attempts;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Orchestrator;

/// <summary>
/// Composition seam of the Orchestrator module (01.1.2-T2.1); internal by design
/// and reached through <c>InternalsVisibleTo</c> (00 §5.5).
/// </summary>
internal static class OrchestratorModule
{
    /// <summary>Registers the module's slice services in the calling host.</summary>
    public static IServiceCollection AddOrchestratorModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IRunRegistry, InMemoryRunRegistry>();

        // 06.1.2-T1: the read-only run-scope lookup upload-url needs (00 §5.5 C5: another module reaches the
        // Orchestrator only through its .Contracts project).
        services.TryAddScoped<IRunPlanReader, RunPlanReader>();

        // 04.1.1-T1: the in-transaction run-scope lookup the atomic event-batch commit calls — the run row FOR SHARE
        // plus the planned-execution membership probe. Scoped like every other module service; it takes the ambient
        // scope through ITenantDb/ITenantScopeAccessor, never a connection of its own.
        services.TryAddScoped<IRunEventScope, RunEventScopeReader>();

        // 05.1.1/05.1.2: the manifest and environment-revision routes validate their bodies against the contract schema
        // set, which is compiled on first use (the routes resolve it per request, never at start-up).
        services.TryAddEvidgateContractValidation();

        // Handlers are resolved eagerly by Mediator at start-up: they receive a Lazy, so the schema set compiles on first use.
        services.TryAddSingleton(provider => new Lazy<ContractSchemaSet>(() => provider.GetRequiredService<ContractSchemaSet>()));

        // 05.2.1: the run-create defaults (T93) are bound and validated at boot like every other EVIDGATE__* group.
        // BindConfiguration resolves IConfiguration from the container, so the module seam keeps its signature.
        services
            .AddOptions<RunsOptions>()
            .BindConfiguration(RunsOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // 05.3.3-T2: the case-attempt writer and the blocked-dependant resolver 04's event-batch commit calls
        // inside its own tenant transaction. Scoped like every other module service; the writer and the resolver
        // take the ambient scope through ITenantDb/ITenantScopeAccessor, never a connection of their own.
        services.TryAddScoped<ICaseAttemptWriter, CaseAttemptWriter>();
        services.TryAddScoped<IBlockedAttemptResolver, BlockedAttemptResolver>();

        // 05.3.1-T1.1: the guarded run state transition (00 §8.1) that 04's event handler (queued -> running),
        // 05.3.2's reconciler (running -> collecting, collecting -> complete|incomplete) and 05.4.1's cancel
        // (* -> canceled) call inside their own tenant transaction, through Orchestrator.Contracts only.
        services.TryAddScoped<IRunStateTransition, TransitionRunStateCommandHandler>();

        // 05.3.2-T2.1/T2.2: the reconciler behind the orchestrator.run.reconcile task — the run row locked FOR NO KEY
        // UPDATE, the shard plan, then one grouped aggregate of the acknowledged events. Scoped like every other module
        // service: it takes the ambient scope through ITenantDb/ITenantScopeAccessor, never a connection of its own, and
        // it never changes the run (T2.4 owns the transition and the evidence_revision insert).
        services.TryAddScoped<IRunReconciler, RunReconciler>();

        // 05.3.2-T2.1: the consumer of the type 05.3.2-T1's finalize enqueues. Registered in both hosts like every
        // module piece; only the Worker runs a queue consumer, and a task_type is claimed exactly while a handler
        // registers it (01.5.1 §11), so this line is what makes the rows finalize already queued claimable.
        services.AddTaskHandler<RunReconcilePayload, ReconcileRunTaskHandler>();

        return services;
    }
}
