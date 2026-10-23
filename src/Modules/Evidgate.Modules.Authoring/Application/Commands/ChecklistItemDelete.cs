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
using Evidgate.Modules.Authoring.Domain;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// Soft-delete a checklist item (<c>DELETE /v1/projects/{p}/authoring/checklist-items/{i}</c>, 13.1.1 §5 route 10):
/// the row keeps its id, its history and its links, and only <c>deleted_at</c> and the version move (AC7).
/// <paramref name="ExpectedVersion"/> is the <c>If-Match</c> version (AC19).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="ItemId">The item from the route.</param>
/// <param name="ExpectedVersion">The version the caller last read (<c>If-Match</c>).</param>
/// <param name="Reason">The optional 1..500 character reason recorded on the version.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1, AC18).</param>
internal sealed record DeleteChecklistItemCommand(
    Guid ProjectId,
    Guid ItemId,
    int ExpectedVersion,
    string? Reason,
    string IdempotencyKey)
    : ICommand<Result>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="DeleteChecklistItemCommand"/>: the project gate, the row lock that makes the <c>If-Match</c>
/// check atomic (AC19), then one <c>delete</c> change through the writer, which refuses an item that still holds a live
/// manual case with <c>409 authoring_not_empty</c> / <c>item_has_cases</c> (AC7) and writes nothing.
/// </summary>
[AuditedAction("authoring.checklist_item.delete")]
[Permission(Permissions.Authoring.Write)]
internal sealed class DeleteChecklistItemCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    AuthoringVersionWriter writer,
    IAuditTrail audit)
    : ICommandHandler<DeleteChecklistItemCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(DeleteChecklistItemCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(DeleteChecklistItemCommand));

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure(scopeError);
        }

        var orgId = scopes.Require().OrgId;
        var (locked, lockError) = await ChecklistItemLock
            .LockAsync(db, orgId, command.ProjectId, command.ItemId, cancellationToken)
            .ConfigureAwait(false);

        if (lockError is not null || locked is not { } item)
        {
            return Result.Failure(lockError!);
        }

        if (item.Version != command.ExpectedVersion)
        {
            return Result.Failure(AuthoringRequestScope.Stale("checklist item", item.Version));
        }

        var actor = await AuthoringRequestScope
            .ActorMembershipAsync(db, orgId, principals, cancellationToken)
            .ConfigureAwait(false);

        var appended = await writer.AppendAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            [EntityChange.SoftDelete(AuthoringEntityKind.ChecklistItem, item.ItemId, command.Reason)],
            command.Reason is null ? null : new AuthoringAppendContext { Reason = command.Reason },
            cancellationToken).ConfigureAwait(false);

        if (!appended.IsSuccess)
        {
            return Result.Failure(appended.Error!);
        }

        audit.Describe(new AuditObject("checklist_item", item.ItemId.ToString("D")));

        return Result.Success(StatusCodes.Status204NoContent);
    }
}
