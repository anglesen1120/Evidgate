using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Policy.Domain;
using Evidgate.Modules.Policy.Infrastructure.Persistence;

namespace Evidgate.Modules.Policy.Application.Releases;

/// <summary>Aggregates one release's per-component decisions into an append-only <c>release_decision</c> row.</summary>
/// <param name="ReleaseId">The release to aggregate.</param>
internal sealed record AggregateReleaseDecisionCommand(Guid ReleaseId);

/// <summary>
/// Handler of <see cref="AggregateReleaseDecisionCommand"/>. Only the outbox consumers call it, inside the task's tenant
/// scope; no route does, so a read can never write. It reads the release side through the Orchestrator contract, the
/// newest live verdicts in one statement, applies <see cref="ReleaseAggregationRules"/> and inserts with
/// <c>on conflict do nothing</c> on the inputs hash: a redelivered task, or a second event for the same inputs, reads
/// the stored row back instead of writing another. Like the evaluation handler it is dispatched directly: a nested
/// Mediator send would open a second tenant scope in one async flow.
/// </summary>
internal sealed class AggregateReleaseDecisionCommandHandler(
    IReleaseAggregationReader releases,
    ITenantDb db,
    ITenantScopeAccessor scopes)
{
    private static readonly JsonSerializerOptions HashOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>
    /// Returns the id of the stored aggregate (new or existing), or null when there is nothing to store: the release is
    /// not visible, or none of its components has a live verdict yet, so there is no policy revision to store under.
    /// </summary>
    /// <param name="command">The release to aggregate.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<Guid?> Handle(AggregateReleaseDecisionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(AggregateReleaseDecisionCommand));

        var inputs = await releases.GetInputsAsync(command.ReleaseId, cancellationToken).ConfigureAwait(false);
        if (inputs is null)
        {
            return null;
        }

        var orgId = scopes.Require().OrgId;
        var runIds = inputs.Bindings
            .Where(b => !string.Equals(b.Purpose, "replay", StringComparison.Ordinal))
            .Select(b => b.RunId)
            .Distinct()
            .ToArray();
        var verdicts = runIds.Length == 0
            ? []
            : (await db.QueryAsync<VerdictRow>(
                    ReleaseDecisionSql.LiveVerdicts,
                    new { org_id = orgId, ids = runIds },
                    cancellationToken).ConfigureAwait(false))
                .ToDictionary(
                    v => v.RunId,
                    v => new AggregationVerdict(v.RunId, v.EvaluationId, v.EvidenceRevision, v.PolicyRevisionId, v.Decision));

        var aggregate = ReleaseAggregationRules.Aggregate(
            [.. inputs.Components.Select(c => (c.ComponentKey, c.Digest))],
            [.. inputs.Bindings.Select(b => new AggregationBinding(b.RunId, b.ComponentKey, b.Purpose, b.RunDigest))],
            verdicts);
        if (aggregate.PolicyRevisionId is not { } policyRevisionId)
        {
            return null;
        }

        var componentsJson = JsonSerializer.Serialize(aggregate.Components);
        var inputsHash = InputsHash(command.ReleaseId, policyRevisionId, aggregate.Components);
        var id = Guid.CreateVersion7();
        var inserted = await db.QuerySingleOrDefaultAsync<Guid?>(
            ReleaseDecisionSql.Insert,
            new
            {
                id,
                org_id = orgId,
                release_id = command.ReleaseId,
                policy_revision_id = policyRevisionId,
                component_evaluations = componentsJson,
                completeness = aggregate.Completeness,
                decision = aggregate.Decision,
                reasons = string.Join(EvaluationSql.ArraySeparator, aggregate.Reasons),
                array_separator = EvaluationSql.ArraySeparator,
                inputs_hash = inputsHash,
            },
            cancellationToken).ConfigureAwait(false);
        if (inserted is not null)
        {
            return inserted;
        }

        return await db.QuerySingleOrDefaultAsync<Guid?>(
            ReleaseDecisionSql.Winner,
            new { org_id = orgId, release_id = command.ReleaseId, policy_revision_id = policyRevisionId, inputs_hash = inputsHash },
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the conflicting release decision of release {command.ReleaseId:D} could not be read back, so the task is retried");
    }

    /// <summary>
    /// <c>sha256:&lt;hex&gt;</c> over the RFC 8785 form of the release, the policy revision and the component facts,
    /// so equal inputs hash equally whatever the order the JSON was written in.
    /// </summary>
    internal static string InputsHash(Guid releaseId, Guid policyRevisionId, IReadOnlyList<ComponentEvaluation> components) =>
        CanonicalHash.Compute(JsonSerializer.Serialize(
            new { release_id = releaseId, policy_revision_id = policyRevisionId, component_evaluations = components },
            HashOptions));

    private sealed class VerdictRow
    {
        public Guid RunId { get; set; }

        public Guid EvaluationId { get; set; }

        public int EvidenceRevision { get; set; }

        public Guid PolicyRevisionId { get; set; }

        public string Decision { get; set; } = string.Empty;
    }
}
