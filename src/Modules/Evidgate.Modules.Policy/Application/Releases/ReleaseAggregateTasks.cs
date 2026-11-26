using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Modules.Policy.Application.Releases;

/// <summary>The names and payload of the release aggregation triggers.</summary>
internal static class ReleaseAggregateContract
{
    /// <summary>Outbox type written with a live evaluation insert; the dispatcher turns it into a task of the same type.</summary>
    internal const string EvaluationTaskType = "policy.release_aggregate";

    /// <summary>The aggregate type of that outbox row: the evaluation.</summary>
    internal const string AggregateType = "evaluation";

    /// <summary>The delivery task this module's subscription creates for each release bind.</summary>
    internal const string BoundTaskType = "policy.release_aggregate.bound";

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The outbox key of one evaluation: a function of the row it announces, so a repeat is one row.</summary>
    /// <param name="evaluationId">The inserted evaluation.</param>
    internal static string IdempotencyKey(Guid evaluationId) =>
        string.Create(CultureInfo.InvariantCulture, $"policy:release-aggregate:{evaluationId:D}");

    /// <summary>The payload of one live evaluation: ids only.</summary>
    /// <param name="orgId">The tenant, from the attempt's scope.</param>
    /// <param name="runId">The evaluated run.</param>
    /// <param name="evaluationId">The inserted evaluation.</param>
    internal static string PayloadJson(Guid orgId, Guid runId, Guid evaluationId) =>
        JsonSerializer.Serialize(new EvaluationCreatedPayload(orgId, runId, evaluationId), Options);
}

/// <summary>Payload of <see cref="ReleaseAggregateContract.EvaluationTaskType"/>.</summary>
/// <param name="OrgId">The tenant; must agree with the task's scope.</param>
/// <param name="RunId">The evaluated run.</param>
/// <param name="EvaluationId">The evaluation the event announces.</param>
internal sealed record EvaluationCreatedPayload(Guid OrgId, Guid RunId, Guid EvaluationId);

/// <summary>The members of an <c>orchestrator.release.bound</c> row this module reads; the rest is ignored.</summary>
/// <param name="ReleaseId">The release that was bound.</param>
/// <param name="RunId">The run that was bound.</param>
internal sealed record ReleaseBoundPayload(Guid ReleaseId, Guid RunId);

/// <summary>
/// Aggregates every release a freshly evaluated run is bound to. The tenant is the task's scope, and a payload naming
/// another org is a misrouted row, so it raises instead of aggregating under either identity.
/// </summary>
internal sealed class ReleaseAggregateOnEvaluationTaskHandler(IServiceScopeFactory serviceScopes)
    : ITaskHandler<EvaluationCreatedPayload>
{
    /// <inheritdoc />
    public string TaskType => ReleaseAggregateContract.EvaluationTaskType;

    /// <inheritdoc />
    public async ValueTask HandleAsync(EvaluationCreatedPayload payload, ITenantScope? scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (scope is null || payload.OrgId != scope.OrgId)
        {
            throw new InvalidOperationException("the release aggregation task has no tenant scope or names another org");
        }

        await using var services = serviceScopes.CreateAsyncScope();
        var releases = services.ServiceProvider.GetRequiredService<IReleaseAggregationReader>();
        var aggregate = services.ServiceProvider.GetRequiredService<AggregateReleaseDecisionCommandHandler>();
        // One global lock order (ascending id) keeps two fan-outs that share releases from waiting on each other.
        foreach (var releaseId in (await releases.GetReleaseIdsBoundToRunAsync(payload.RunId, cancellationToken).ConfigureAwait(false)).Order())
        {
            await aggregate.Handle(new AggregateReleaseDecisionCommand(releaseId), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Aggregates the release of a bind event, so a run evaluated before it was bound still reaches the release.</summary>
internal sealed class ReleaseAggregateOnBindTaskHandler(IServiceScopeFactory serviceScopes)
    : ITaskHandler<ReleaseBoundPayload>
{
    /// <inheritdoc />
    public string TaskType => ReleaseAggregateContract.BoundTaskType;

    /// <inheritdoc />
    public async ValueTask HandleAsync(ReleaseBoundPayload payload, ITenantScope? scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (scope is null)
        {
            throw new InvalidOperationException("the release bind aggregation task ran without a tenant scope");
        }

        await using var services = serviceScopes.CreateAsyncScope();
        var aggregate = services.ServiceProvider.GetRequiredService<AggregateReleaseDecisionCommandHandler>();
        await aggregate.Handle(new AggregateReleaseDecisionCommand(payload.ReleaseId), cancellationToken).ConfigureAwait(false);
    }
}
