using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using FluentValidation;
using Mediator;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// Renumber a node's sibling group (<c>POST /v1/projects/{p}/authoring/nodes/{n}/reorder</c>, 13.1.1 §5 route 4, AC2).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="NodeId">The <c>{n}</c> of the route: any live node of the group being renumbered.</param>
/// <param name="OrderedIds">Every live sibling of that group exactly once, in the new order.</param>
/// <param name="OrderHash">The <c>If-Match</c> order tag the caller read (AC19).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (AC18).</param>
internal sealed record ReorderNodesCommand(
    Guid ProjectId,
    Guid NodeId,
    IReadOnlyList<Guid> OrderedIds,
    string OrderHash,
    string IdempotencyKey)
    : ICommand<Result<ReorderNodesResponse>>, IIdempotentCommand;

/// <summary>Shape validation of <see cref="ReorderNodesCommand"/>: the set itself is the writer's decision (AC2).</summary>
internal sealed class ReorderNodesCommandValidator : AbstractValidator<ReorderNodesCommand>
{
    /// <summary>
    /// The cap of one reorder list: a group is a sibling set, not a whole project — and it is the <b>same</b> number
    /// <see cref="NodeLimits.MaximumSiblingsPerGroup"/> refuses a create or a move-in at, so a group a caller can build is
    /// always a group they can renumber (review F5/perf F9).
    /// </summary>
    internal const int MaxOrderedIds = NodeLimits.MaximumSiblingsPerGroup;

    /// <summary>Builds the rules; each is renamed to the member it answers (see <see cref="CreateNodeCommandValidator"/>).</summary>
    public ReorderNodesCommandValidator()
    {
        RuleFor(command => command.NodeId)
            .NotEqual(Guid.Empty)
            .WithErrorCode("required")
            .OverridePropertyName("/node_id");

        RuleFor(command => command.OrderedIds)
            .NotEmpty()
            .WithErrorCode("required")
            .OverridePropertyName("/ordered_ids");

        RuleFor(command => command.OrderedIds)
            .Must(ids => ids.Count <= MaxOrderedIds)
            .WithErrorCode("too_many")
            .OverridePropertyName("/ordered_ids")
            .When(command => command.OrderedIds is not null);
    }
}

/// <summary>
/// Handler of <see cref="ReorderNodesCommand"/>: the two-phase renumber, the set equality and the stale-order 412 all live
/// in <see cref="AuthoringVersionWriter.ReorderNodesAsync"/> (AC2/OI-13-110), so this handler only gates the project and
/// maps the outcome.
/// </summary>
/// <remarks>
/// No row lock is taken here: the writer takes the group's advisory lock (<see cref="AuthoringLockGuard.LockSiblingGroupAsync"/>)
/// and then the whole group <c>for update</c> in id order, which is the only lock order this write needs, and taking a
/// partial lock here would invert that order.
/// </remarks>
[AuditedAction("authoring.node.reorder")]
[Permission(Permissions.Authoring.Write)]
internal sealed class ReorderNodesCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    AuthoringVersionWriter writer,
    IAuditTrail audit)
    : ICommandHandler<ReorderNodesCommand, Result<ReorderNodesResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<ReorderNodesResponse>> Handle(ReorderNodesCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ReorderNodesCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<ReorderNodesResponse>(mismatch);
        }

        var orgId = scopes.Require().OrgId;

        if (await db.ScalarAsync<int?>(
                AuthoringNodeReadSql.ProjectExists,
                new { org_id = orgId, project_id = command.ProjectId },
                cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<ReorderNodesResponse>(AuthoringRules.ProjectNotFound);
        }

        var actor = await AuthoringRequestScope.ActorMembershipAsync(db, orgId, principals, cancellationToken).ConfigureAwait(false);

        var outcome = await writer.ReorderNodesAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            command.NodeId,
            command.OrderedIds,
            command.OrderHash,
            new AuthoringAppendContext { Source = AuthoringSource.Manual },
            cancellationToken).ConfigureAwait(false);

        if (!outcome.IsSuccess)
        {
            return Result.Failure<ReorderNodesResponse>(outcome.Error!);
        }

        if (!outcome.Value.Changed)
        {
            // AC5's no-op rule for a renumber: the list already described the stored order, so no version row was written —
            // and AC18 counts effective mutations only, so this one writes no audit row (the rule the item and case
            // handlers apply through the same Suppress).
            audit.Suppress();

            return Result.Success(new ReorderNodesResponse(Changed: false));
        }

        audit.Describe(new AuditObject("feature_node", command.NodeId.ToString("D")));

        return Result.Success(new ReorderNodesResponse(Changed: true));
    }
}
