using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Orchestrator.Application.Attempts;
using Evidgate.Modules.Orchestrator.Application.Evaluation;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Application.Releases;
using Evidgate.Modules.Orchestrator.Contracts;
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
    /// <summary>Registers the module's services in the calling host.</summary>
    public static IServiceCollection AddOrchestratorModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 06.1.2-T1: the read-only run-scope lookup upload-url needs (00 §5.5 C5: another module reaches the
        // Orchestrator only through its .Contracts project).
        services.TryAddScoped<IRunPlanReader, RunPlanReader>();

        // The digest-match surface the evaluation path reads (build_mismatch): the stored bindings of a release/run
        // pair and the release components the run does not match, derived on read from the two rows, never stored.
        services.TryAddScoped<IReleaseBindingReader, ReleaseBindingReader>();

        // The release read's binding section: insertion order over the binding key, so no query here can become a
        // "latest run" join.
        services.TryAddScoped<BoundRunsQuery>();

        // The plan the Policy evaluator judges a run against: the run's pinned scope, the manifest's planned count and
        // every planned execution, read over the ambient tenant scope through Orchestrator.Contracts only.
        services.TryAddScoped<IOrchestratorEvaluationPlan, EvaluationPlanReader>();

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

        // The cancel windows are a group of their own: the soft-stop and kill deadlines are stored on the request row, so
        // a bad value would put an immutable deadline out of order, and boot validation is what keeps that impossible.
        services
            .AddOptions<RunCancelOptions>()
            .BindConfiguration(RunCancelOptions.SectionName)
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
        // it stays read-only (T2.3/T2.4's finisher composes after it, in the same transaction).
        services.TryAddScoped<IRunReconciler, RunReconciler>();

        // 05.3.2-T2.3/T2.4: the requirement seam (the pinned policy's artifact kinds through Policy.Contracts, D-S123-Q3)
        // and the finisher that freezes the evidence, appends the one evidence_revision row, moves the run's pointer and
        // moves collecting -> complete|incomplete. Scoped over the ambient tenant transaction; the finisher never opens
        // one of its own, so the consumer's commit is the only commit.
        services.TryAddScoped<IRequiredArtifactResolver, RequiredArtifactResolver>();
        services.TryAddScoped<IReconcileFinisher, ReconcileFinisher>();

        // 05.3.2-T2.1: the consumer of the type 05.3.2-T1's finalize enqueues. Registered in both hosts like every
        // module piece; only the Worker runs a queue consumer, and a task_type is claimed exactly while a handler
        // registers it (01.5.1 §11), so this line is what makes the rows finalize already queued claimable.
        services.AddTaskHandler<RunReconcilePayload, ReconcileRunTaskHandler>();

        // The cancel route writes its kill row straight onto the caller's transaction because an outbox row carries no
        // due time; the writer needs only the app-role data source, so the Api composes it without the Worker's queue.
        services.TryAddSingleton<ITaskQueueWriter, TaskQueueWriter>();

        // The forced kill of an accepted cancel, due at the request row's kill_at. Registered in both hosts like every
        // module piece; only the Worker runs a consumer, and a task_type is claimed exactly while a handler registers it,
        // so this line is what makes the rows a cancel queues claimable.
        services.AddTaskHandler<CancelKillPayload, CancelKillTaskHandler>();

        // The collection-deadline sweep, and the schedule that fires it: a run is otherwise reconciled only by the
        // finalize that closed its last producer slot, so a run whose last shard never closes would never be
        // attempted at all. The handler only enqueues the trigger above; registering it in both hosts is what makes
        // the type claimable, and the schedule definition is what a deployed Worker's schedule runner fires.
        services.AddTaskHandler<ReconcileDeadlineSweepPayload, ReconcileDeadlineSweepTaskHandler>();
        services.AddTaskSchedule(ReconcileDeadlineSweeps.Sweep);

        return services;
    }
}
