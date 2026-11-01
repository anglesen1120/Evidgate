using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Policy.Application.Evaluation;

/// <summary>
/// Consumes one frozen-evidence event: the reconcile that inserted an <c>evidence_revision</c> wrote the outbox row in
/// its own transaction, so this handler runs exactly for revisions that committed, and the task queue redelivers it
/// at least once.
/// <para>
/// <b>The tenant comes from the task row.</b> The consumer opened a scope from <c>task.org_id</c>; the payload's
/// <c>org_id</c> is metadata and is never read as an identity, so a payload naming another tenant cannot widen the
/// scope.
/// </para>
/// <para>
/// <b>A redelivery is not a second verdict.</b> The command's insert is keyed on the six columns that describe the
/// question, so a second attempt reads the row the first one stored instead of writing another — the handler needs no
/// schedule of its own to be idempotent.
/// </para>
/// </summary>
internal sealed partial class EvidenceRevisionFrozenTaskHandler(
    IServiceScopeFactory serviceScopes,
    ILogger<EvidenceRevisionFrozenTaskHandler> logger) : ITaskHandler<EvidenceRevisionFrozenPayload>
{
    /// <inheritdoc />
    public string TaskType => EvidenceRevisionFrozenContract.OutboxTaskType;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        EvidenceRevisionFrozenPayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (scope is null)
        {
            // The consumer dead-letters a tenant row without an org before it gets here; this is the same refusal for
            // a handler that was invoked directly, because without the scope every read below runs unscoped.
            throw new InvalidOperationException(
                "the frozen-evidence task ran without a tenant scope, so no run can be evaluated");
        }

        if (payload.RunId == Guid.Empty || payload.EvidenceRevisionNo < 1)
        {
            throw new InvalidOperationException(
                "the frozen-evidence task names no run or no revision number, so there is nothing to evaluate");
        }

        // The handler itself is a singleton (one per consumer), and the evaluation handler is scoped, so each attempt
        // resolves its own from a child scope. The ambient tenant scope is untouched by that: it lives in the scope
        // factory's async flow, which the child inherits.
        await using var services = serviceScopes.CreateAsyncScope();
        var evaluate = services.ServiceProvider.GetRequiredService<EvaluateRunCommandHandler>();

        // The revision number is what the row is keyed by; the payload's revision id only names the row for an operator,
        // and reading it instead would let a payload address a revision of another run.
        var result = await evaluate
            .Handle(
                new EvaluateRunCommand(payload.RunId, payload.EvidenceRevisionNo, EvaluationMode.Live),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            // Never swallow: a returned failure would let the consumer mark the row done without a verdict.
            throw new InvalidOperationException(
                $"the evaluation of run {payload.RunId:D} revision {payload.EvidenceRevisionNo} was refused "
                + $"({result.Error?.Code ?? "unknown"}), so the task is retried");
        }

        LogDecided(logger, result.Value.RunId, result.Value.EvidenceRevision, result.Value.Decision, result.Value.Mode);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "run {RunId} revision {RevisionNo} decided {Decision} in the {Mode} slot")]
    private static partial void LogDecided(ILogger logger, Guid runId, int revisionNo, string decision, string mode);
}
