using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Mediator;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// Change a checklist item (<c>PATCH /v1/projects/{p}/authoring/checklist-items/{i}</c>, 13.1.1 §5 route 9). Only the
/// members the caller sent change; <paramref name="ExpectedVersion"/> is the <c>If-Match</c> version (AC19). Moving the
/// item to another node is not a member of this route, and the step that does it (13.1.2's apply) goes through the
/// writer's own placement rules.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="ItemId">The item from the route.</param>
/// <param name="ExpectedVersion">The version the caller last read (<c>If-Match</c>).</param>
/// <param name="Body">The parsed request body (13.1.1 §5 route 9 members only).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1, AC18).</param>
internal sealed record UpdateChecklistItemCommand(
    Guid ProjectId,
    Guid ItemId,
    int ExpectedVersion,
    JsonElement Body,
    string IdempotencyKey)
    : ICommand<Result<AuthoringChangeResponse>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="UpdateChecklistItemCommand"/>: the project gate, the request shape, the row lock that makes the
/// <c>If-Match</c> check atomic (AC19), the §6 state rule and then one <c>update</c> change through the writer (AC5).
/// </summary>
/// <remarks>
/// Three outcomes share the 200 answer of §5 route 9: an edit that changed the snapshot (<c>changed = true</c>, exactly
/// one <c>authoring_version</c> row and one audit row), an edit whose result equals the stored snapshot — including a
/// <c>state</c> equal to the current one — (<c>changed = false</c>, no version row, the AC5 no-op rule) and an empty
/// body (<c>changed = false</c>, nothing written). A retired item is 409 <c>invalid_transition</c> for **any** edit,
/// which the writer also enforces; deciding it here keeps the answer free of a rolled-back write (AC20).
/// </remarks>
[AuditedAction("checklist_item.update")]
[Permission(Permissions.Authoring.Write)]
internal sealed class UpdateChecklistItemCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    AuthoringVersionWriter writer,
    IAuditTrail audit)
    : ICommandHandler<UpdateChecklistItemCommand, Result<AuthoringChangeResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AuthoringChangeResponse>> Handle(
        UpdateChecklistItemCommand command,
        CancellationToken cancellationToken)
    {
        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AuthoringChangeResponse>(scopeError);
        }

        var (edit, shapeError) = ChecklistItemFields.ReadUpdate(command.Body);
        if (shapeError is not null || edit is not { } changes)
        {
            return Result.Failure<AuthoringChangeResponse>(shapeError!);
        }

        var orgId = scopes.Require().OrgId;
        var (locked, lockError) = await ChecklistItemLock
            .LockAsync(db, orgId, command.ProjectId, command.ItemId, cancellationToken)
            .ConfigureAwait(false);

        if (lockError is not null || locked is not { } item)
        {
            return Result.Failure<AuthoringChangeResponse>(lockError!);
        }

        if (item.Version != command.ExpectedVersion)
        {
            return Result.Failure<AuthoringChangeResponse>(AuthoringRequestScope.Stale("checklist item", item.Version));
        }

        // The writer refuses every update of a retired row before it compares snapshots
        // (AuthoringVersionWriter.UpdateAsync runs CheckEditable first), so the edge answers the same 409 for an
        // empty or identical PATCH too - AC20's "any edit of a retired item", measured at the writer.
        if (item.State == ChecklistItemStateMachine.Retired)
        {
            return Result.Failure<AuthoringChangeResponse>(AuthoringErrors.InvalidTransition(
                "a retired checklist item cannot be edited; restore it as a new version instead"));
        }

        if (!changes.HasTitle
            && !changes.HasDescription
            && !changes.HasPriority
            && !changes.HasPosition
            && !changes.HasState
            && !changes.HasReason)
        {
            return Unchanged(item.Version);
        }

        var targetState = changes.HasState ? changes.State! : item.State;
        if (ChecklistItemFields.CheckReason(item.State, targetState, changes.HasReason ? changes.Reason : null) is { } reasonError)
        {
            return Result.Failure<AuthoringChangeResponse>(reasonError);
        }

        var edited = item with
        {
            Title = changes.HasTitle ? changes.Title! : item.Title,
            Description = changes.HasDescription ? changes.Description : item.Description,
            Priority = changes.HasPriority ? changes.Priority! : item.Priority,
            Position = changes.HasPosition ? changes.Position!.Value : item.Position,
            State = targetState,
        };

        var actor = await AuthoringRequestScope
            .ActorMembershipAsync(db, orgId, principals, cancellationToken)
            .ConfigureAwait(false);

        var appended = await writer.AppendAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            [EntityChange.Update(AuthoringEntityKind.ChecklistItem, item.ItemId, ChecklistItemFields.Content(edited))],
            changes.HasReason ? new AuthoringAppendContext { Reason = changes.Reason } : null,
            cancellationToken).ConfigureAwait(false);

        if (!appended.IsSuccess || appended.Value is not { } outcome)
        {
            return Result.Failure<AuthoringChangeResponse>(appended.Error!);
        }

        if (outcome.UnchangedEntityIds.Contains(item.ItemId) || outcome.Versions.Count == 0)
        {
            return Unchanged(item.Version);
        }

        audit.Describe(new AuditObject("checklist_item", item.ItemId.ToString("D")));

        return Result.Success(new AuthoringChangeResponse(outcome.Versions[0].VersionNo, Changed: true));
    }

    /// <summary>
    /// The AC5 no-op answer: the current version, <c>changed: false</c>, no version row — and no audit row either,
    /// because the command changed nothing (AC18 counts successful <em>mutating</em> commands).
    /// </summary>
    /// <param name="version">The row's version.</param>
    /// <returns>The 200 the caller receives.</returns>
    private Result<AuthoringChangeResponse> Unchanged(int version)
    {
        audit.Suppress();

        return Result.Success(new AuthoringChangeResponse(version, Changed: false));
    }
}
