using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.Modules.Orchestrator.Domain;
using Mediator;

namespace Evidgate.Modules.Orchestrator.Application.Commands;

/// <summary>
/// Skeleton command of the sprint-1 slice: plan the executions of an existing
/// run (permission <c>run:create</c> — it creates work, like run creation).
/// </summary>
internal sealed record PlanExecutions(string RunId) : ICommand<PlanResult>;

/// <summary>The executions planned for the run.</summary>
internal sealed record PlanResult(string RunId, IReadOnlyList<string> ExecutionIds);

/// <summary>Handler of <see cref="PlanExecutions"/>: one execution per suite revision of the run.</summary>
[Permission("run:create")]
internal sealed class PlanExecutionsHandler(IRunRegistry runs) : ICommandHandler<PlanExecutions, PlanResult>
{
    /// <inheritdoc />
    public ValueTask<PlanResult> Handle(PlanExecutions command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(PlanExecutions));

        var run = runs.Find(command.RunId)
            ?? throw new InvalidOperationException($"Run {command.RunId} is unknown; create it before planning.");

        var executionIds = run.SuiteKeys.Select(ExecutionId).ToList();
        run.Plan(executionIds);

        activity?.SetTag("evidgate.run_id", run.RunId);

        return ValueTask.FromResult(new PlanResult(run.RunId, executionIds));
    }

    /// <summary>
    /// Stand-in for the real <c>execution_key</c> of 00 §7
    /// (<c>sha256(case_id ‖ variant_hash ‖ target_env)</c>): the skeleton has no
    /// case ids yet, so it hashes the suite revision pair — same
    /// <c>sha256:&lt;hex&gt;</c> shape and deterministic per input.
    /// </summary>
    private static string ExecutionId(string suiteKey) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(suiteKey)));
}
