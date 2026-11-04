using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Domain.Changes;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Changes;

/// <summary>
/// Abandon one pending change (<c>POST …/authoring/changes/{c}/discard</c>, 13.1.2 §5): the row moves to
/// <c>discarded</c> and nothing else happens — no version, no item edit, no lineage.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="ChangeId">The change from the route.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record DiscardAuthoringChangeCommand(
    Guid ProjectId,
    Guid ChangeId,
    string IdempotencyKey) : ICommand<Result<DiscardedAuthoringChange>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="DiscardAuthoringChangeCommand"/>. The row state is the whole precondition, so there is no
/// <c>If-Match</c>: a stale read can only produce the one documented conflict, and a replay of a committed discard
/// answers the first response instead of writing twice.
/// </summary>
[AuditedAction("authoring.change.discard")]
[Permission(Permissions.Authoring.Write)]
internal sealed class DiscardAuthoringChangeCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<DiscardAuthoringChangeCommand, Result<DiscardedAuthoringChange>>
{
    /// <inheritdoc />
    public async ValueTask<Result<DiscardedAuthoringChange>> Handle(
        DiscardAuthoringChangeCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(DiscardAuthoringChangeCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<DiscardedAuthoringChange>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false)
            is { } missingProject)
        {
            return Result.Failure<DiscardedAuthoringChange>(missingProject);
        }

        if (await AuthoringRequestScope.ActorMembershipAsync(db, orgId, principals, cancellationToken).ConfigureAwait(false)
            is not { } actor)
        {
            return Result.Failure<DiscardedAuthoringChange>(
                Error.Forbidden("discarding a pending change needs an active membership in this organization"));
        }

        try
        {
            await AuthoringLockGuard.SetLockTimeoutAsync(db, cancellationToken).ConfigureAwait(false);

            var state = await AuthoringChangeApplyReader
                .LockOneStateAsync(db, orgId, command.ProjectId, command.ChangeId, cancellationToken)
                .ConfigureAwait(false);

            if (state is null)
            {
                return Result.Failure<DiscardedAuthoringChange>(
                    AuthoringErrors.NotFound("no change with that id is visible in this project"));
            }

            // The state machine's one reachable refusal: both destinations are terminal, so a second discard and a
            // discard after apply are the same answer and the row keeps its row (state and stamps) unchanged.
            if (!string.Equals(state, AuthoringChangeStates.WireName(AuthoringChangeState.Pending), StringComparison.Ordinal))
            {
                return Result.Failure<DiscardedAuthoringChange>(Error.Conflict(
                    ApiErrorCode.InvalidTransition,
                    "this change was already applied or discarded",
                    [new ErrorDetail("/change_id", "change_not_pending")]));
            }

            var discardedAt = await AuthoringChangeApplyReader
                .DiscardAsync(db, orgId, command.ProjectId, command.ChangeId, actor, cancellationToken)
                .ConfigureAwait(false);

            audit.Describe(new AuditObject(
                "authoring_change",
                command.ChangeId.ToString("D"),
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["outcome"] = AuthoringChangeStates.WireName(AuthoringChangeState.Discarded),
                }));

            return Result.Success(new DiscardedAuthoringChange(
                command.ChangeId,
                AuthoringChangeStates.WireName(AuthoringChangeState.Discarded),
                discardedAt));
        }
        catch (PostgresException exception) when (AuthoringLockGuard.IsContention(exception))
        {
            return Result.Failure<DiscardedAuthoringChange>(AuthoringErrors.Unavailable());
        }
    }
}
