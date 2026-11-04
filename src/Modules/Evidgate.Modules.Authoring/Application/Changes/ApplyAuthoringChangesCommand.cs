using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain.Changes;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Changes;

/// <summary>
/// Apply a selection of pending changes as one version (<c>POST …/authoring/changes:apply</c>, 13.1.2 §5):
/// <paramref name="IfMatch"/> is the batch ETag the selection was read with, and every selected change commits or none
/// does.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="ChangeIds">The selected pending changes, 1..200.</param>
/// <param name="IfMatch">The raw <c>If-Match</c> header: the batch ETag of the selection.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record ApplyAuthoringChangesCommand(
    Guid ProjectId,
    IReadOnlyList<Guid> ChangeIds,
    string? IfMatch,
    string IdempotencyKey) : ICommand<Result<ApplyAuthoringChangesResult>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="ApplyAuthoringChangesCommand"/>: one transaction that locks the changes, locks the union of
/// their affected items, refuses a stale selection or a non-empty delete target without writing, then writes one
/// <c>batch_id</c>'s versions and flips every selected row to <c>applied</c>.
/// </summary>
/// <remarks>
/// The two lock statements sort before they lock, and the item statement covers the union of every change's affected set
/// — targets <b>and</b> payload items — so two appliers take the same rows in the same order and a stale selection is
/// found by the item versions, not only by the header. Every refusal happens before the first write: the pipeline rolls
/// the transaction back on a failed <see cref="Result"/>, and the handler additionally proves the "nothing written"
/// answer by checking first.
/// </remarks>
[AuditedAction("authoring.change.apply")]
[Permission(Permissions.Authoring.Write)]
internal sealed class ApplyAuthoringChangesCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    AuthoringVersionWriter writer,
    TimeProvider clock,
    IAuditTrail audit)
    : ICommandHandler<ApplyAuthoringChangesCommand, Result<ApplyAuthoringChangesResult>>
{
    /// <inheritdoc />
    public async ValueTask<Result<ApplyAuthoringChangesResult>> Handle(
        ApplyAuthoringChangesCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ApplyAuthoringChangesCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Refuse(scopeError);
        }

        if (AuthoringChangeApplyRules.SelectionError(command.ChangeIds) is { } selectionError)
        {
            return Refuse(selectionError);
        }

        if (AuthoringChangeApplyRules.NormalizeTag(command.IfMatch) is not { } presented)
        {
            return Refuse(AuthoringChangeApplyRules.MissingIfMatch());
        }

        var orgId = scopes.Require().OrgId;

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false)
            is { } missingProject)
        {
            return Refuse(missingProject);
        }

        if (await AuthoringRequestScope.ActorMembershipAsync(db, orgId, principals, cancellationToken).ConfigureAwait(false)
            is not { } actor)
        {
            return Refuse(Error.Forbidden("applying pending changes needs an active membership in this organization"));
        }

        var (plan, refusal) = await PrepareAsync(command, orgId, presented, cancellationToken).ConfigureAwait(false);
        if (refusal is not null || plan is null)
        {
            return Refuse(refusal!);
        }

        var batchId = Guid.CreateVersion7();

        var (versions, writeError) = await AppendAsync(orgId, command.ProjectId, batchId, actor, plan, cancellationToken)
            .ConfigureAwait(false);
        if (writeError is not null || versions is null)
        {
            return Refuse(writeError!);
        }

        if (await FlipAsync(orgId, command.ProjectId, batchId, actor, command.ChangeIds, cancellationToken)
                .ConfigureAwait(false) != command.ChangeIds.Count)
        {
            return Refuse(AuthoringChangeApplyRules.ChangeNotPending());
        }

        audit.Describe(new AuditObject(
            "authoring_change",
            batchId.ToString("D"),
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = command.ChangeIds.Count.ToString(CultureInfo.InvariantCulture),
                ["outcome"] = AuthoringApplyOutcomes.Applied,
            }));

        EvidgateTelemetry.RecordAuthoringApply(AuthoringApplyOutcomes.Applied);
        EvidgateTelemetry.RecordAuthoringApplyBatchSize(command.ChangeIds.Count);

        return Result.Success(new ApplyAuthoringChangesResult(
            batchId,
            command.ChangeIds,
            versions,
            [],
            clock.GetUtcNow()));
    }

    /// <summary>
    /// The pure-read half: every lock, every precondition and the non-empty re-check, in the order two appliers must
    /// share. Nothing here writes, so a refusal leaves the transaction exactly as it opened.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="presented">The <c>If-Match</c> tag with its quoting removed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The plan, or the refusal to answer.</returns>
    private async ValueTask<(AuthoringChangePlan? Plan, Error? Error)> PrepareAsync(
        ApplyAuthoringChangesCommand command,
        Guid orgId,
        string presented,
        CancellationToken cancellationToken)
    {
        try
        {
            await AuthoringLockGuard.SetLockTimeoutAsync(db, cancellationToken).ConfigureAwait(false);

            var changes = await AuthoringChangeApplyReader
                .LockChangesAsync(db, orgId, command.ProjectId, command.ChangeIds, cancellationToken)
                .ConfigureAwait(false);

            if (changes.Count != command.ChangeIds.Count)
            {
                return (null, AuthoringErrors.NotFound("no pending change with that id is visible in this project"));
            }

            if (NotPendingIndex(changes, command.ChangeIds) is var pendingIndex and >= 0)
            {
                return (null, AuthoringChangeApplyRules.ChangeNotPending(pendingIndex));
            }

            var plan = AuthoringChangePlanning.Plan(changes);

            var items = await AuthoringChangeApplyReader
                .LockItemsAsync(db, orgId, command.ProjectId, plan.AffectedItemIds, cancellationToken)
                .ConfigureAwait(false);

            // The item stamps come first: a moved affected item must answer `target_changed` naming that item, which
            // the header can never do — the tag only says the selection moved, not which row did.
            if (AffectedRefusal(plan, items) is { } affectedRefusal)
            {
                return (null, affectedRefusal);
            }

            if (!string.Equals(presented, AuthoringChangeApplyRules.BatchEtag(items), StringComparison.Ordinal))
            {
                return (null, AuthoringChangeApplyRules.BatchStale());
            }

            if (plan.NotApplicableIndex >= 0)
            {
                return (null, AuthoringChangeApplyRules.ChangeNotApplicable(plan.NotApplicableIndex));
            }

            if (plan.DeleteTargets.Count > 0
                && await AuthoringChangeApplyReader
                    .FirstLiveCaseAsync(db, orgId, command.ProjectId, plan.DeleteTargets, cancellationToken)
                    .ConfigureAwait(false) is { } busyItem)
            {
                return (null, AuthoringChangeApplyRules.ItemHasCases(busyItem));
            }

            return (plan, null);
        }
        catch (PostgresException exception) when (AuthoringLockGuard.IsContention(exception))
        {
            return (null, AuthoringErrors.Unavailable());
        }
    }

    /// <summary>
    /// The first selected change that is not <c>pending</c>, by the caller's own order, or -1. The row was locked before
    /// this read, so a competing applier that committed first is seen here and answered as the documented conflict.
    /// </summary>
    /// <param name="rows">The locked change rows.</param>
    /// <param name="changeIds">The caller's selection.</param>
    private static int NotPendingIndex(IReadOnlyList<AuthoringChangeRow> rows, IReadOnlyList<Guid> changeIds)
    {
        var byId = rows.ToDictionary(row => row.Id);
        var pending = AuthoringChangeStates.WireName(AuthoringChangeState.Pending);

        for (var index = 0; index < changeIds.Count; index++)
        {
            if (byId.TryGetValue(changeIds[index], out var row)
                && !string.Equals(row.State, pending, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// The first affected item that is gone, soft-deleted or no longer at the version the change was stamped with.
    /// </summary>
    /// <param name="plan">The plan of the selection.</param>
    /// <param name="items">The locked affected items.</param>
    private static Error? AffectedRefusal(AuthoringChangePlan plan, IReadOnlyList<AuthoringAffectedItemRow> items)
    {
        var byId = items.ToDictionary(item => item.Id);

        foreach (var change in plan.Changes)
        {
            foreach (var (itemId, stamped) in change.Affected)
            {
                if (!byId.TryGetValue(itemId, out var item) || item.DeletedAt is not null)
                {
                    return AuthoringChangeApplyRules.TargetDeleted(itemId);
                }

                if (item.Version != stamped)
                {
                    return AuthoringChangeApplyRules.TargetChanged(itemId);
                }
            }
        }

        return null;
    }

    /// <summary>The one version write of the batch, through the module's writer; a comment-only batch writes none.</summary>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="batchId">The batch of this call.</param>
    /// <param name="actor">The caller's membership.</param>
    /// <param name="plan">The plan of the selection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<(IReadOnlyList<AppliedAuthoringVersion>? Versions, Error? Error)> AppendAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid actor,
        AuthoringChangePlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.Writes.Count == 0)
        {
            return ([], null);
        }

        var appended = await writer
            .AppendAsync(orgId, projectId, batchId, actor, plan.Writes, context: null, cancellationToken)
            .ConfigureAwait(false);

        return appended.IsSuccess
            ? ([
                .. appended.Value!.Versions.Select(version => new AppliedAuthoringVersion(
                    AuthoringWireValues.EntityKindName(version.EntityKind),
                    version.EntityId,
                    version.VersionNo)),
            ], null)
            : (null, appended.Error);
    }

    /// <summary>Flips every selected row to <c>applied</c> under the batch, answering how many rows moved.</summary>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="batchId">The batch of this call.</param>
    /// <param name="actor">The caller's membership.</param>
    /// <param name="changeIds">The selection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private Task<int> FlipAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid actor,
        IReadOnlyList<Guid> changeIds,
        CancellationToken cancellationToken) =>
        db.ExecuteAsync(
            AuthoringChangeApplySql.ApplyChanges,
            new { org_id = orgId, project_id = projectId, batch_id = batchId, actor, ids = changeIds.ToArray() },
            cancellationToken);

    /// <summary>Answers a refusal, counting it under the plan's closed outcome vocabulary.</summary>
    /// <param name="error">The refusal.</param>
    private static Result<ApplyAuthoringChangesResult> Refuse(Error error)
    {
        if (error.Status is 412)
        {
            EvidgateTelemetry.RecordAuthoringApply(AuthoringApplyOutcomes.Stale);
        }
        else if (error.Status is 409)
        {
            EvidgateTelemetry.RecordAuthoringApply(AuthoringApplyOutcomes.Conflict);
        }

        return Result.Failure<ApplyAuthoringChangesResult>(error);
    }
}

/// <summary>The closed <c>outcome</c> vocabulary of the apply counter (13.1.2 §8): never a change type or an id.</summary>
internal static class AuthoringApplyOutcomes
{
    /// <summary>The batch committed.</summary>
    internal const string Applied = "applied";

    /// <summary>A precondition failed: the selection or an affected item moved.</summary>
    internal const string Stale = "stale";

    /// <summary>A selected change already left <c>pending</c>.</summary>
    internal const string Conflict = "conflict";
}
