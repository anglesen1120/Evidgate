using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Evaluation;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Policy.Application;
using Evidgate.Modules.Policy.Application.Evaluation;
using Evidgate.Modules.Policy.Application.Queries;
using Evidgate.Modules.Policy.Contracts;
using Evidgate.Modules.Policy.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Policy;

/// <summary>
/// Composition seam of the Policy module: internal by design and reached only through <c>InternalsVisibleTo</c>, so the
/// host wires the module without exposing its types.
/// </summary>
internal static class PolicyModule
{
    /// <summary>Registers the module's services in the calling host.</summary>
    public static IServiceCollection AddPolicyModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The EF write model and the append-only policy-revision store its commands write through.
        services.AddEvidgateDbContext<PolicyDbContext>(typeof(PolicyDbContext).Assembly);
        services.TryAddScoped<PolicyRevisionStore>();

        // The sole producer of policy_revision.hash.
        services.TryAddSingleton<IPolicyHasher, PolicyHasher>();

        // The activation store (the pointer write) and the in-process surface other modules call: pin a run and read a
        // revision's required artifact kinds. All scoped, so they run in the caller's tenant transaction.
        services.TryAddScoped<PolicyActivationStore>();
        services.TryAddScoped<GetActivePolicyQuery>();
        services.TryAddScoped<GetRequiredArtifactKindsQuery>();
        services.TryAddScoped<GetLiveEvaluationsQuery>();
        services.TryAddScoped<GetLatestLiveDecisionsQuery>();
        services.TryAddScoped<IPolicyContracts, PolicyContracts>();

        // The policy routes validate their bodies against the contract schema set, which is compiled on first use (the
        // routes resolve it per request, never at start-up).
        services.TryAddEvidgateContractValidation();

        // Handlers are resolved eagerly by Mediator at start-up: they receive a Lazy, so the schema set compiles on first use.
        services.TryAddSingleton(provider => new Lazy<ContractSchemaSet>(() => provider.GetRequiredService<ContractSchemaSet>()));

        // The waiver create validates `expires_at` against the server clock.
        services.TryAddSingleton(TimeProvider.System);

        // The decision body's run-page link is built from the deployment's browser-facing base URL, the same key the login
        // routes redirect with. No validator: a bad value only withholds the link, it never fails the host.
        services.AddOptions<RunDecisionOptions>().BindConfiguration(RunDecisionOptions.SectionName);

        // The evaluator is pure and stateless, so one instance serves every attempt; it is registered here rather than in
        // the evaluation library because only this module composes it.
        services.TryAddSingleton<IPolicyEvaluator>(_ => new PolicyEvaluator());

        // The run evaluation: the input reader and the command handler run inside the caller's tenant scope (the task
        // consumer's), and the worker's frozen-evidence task dispatches the handler directly — a nested Mediator send
        // would try to open a second tenant scope in one async flow.
        services.TryAddScoped<EvaluationInputReader>();
        services.TryAddScoped<ExceptionsSnapshotResolver>();
        services.TryAddScoped<EvaluateRunCommandHandler>();
        services.AddTaskHandler<EvidenceRevisionFrozenPayload, EvidenceRevisionFrozenTaskHandler>();

        return services;
    }
}
