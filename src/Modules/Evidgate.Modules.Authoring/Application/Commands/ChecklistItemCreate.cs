using System.Text.Json;
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
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// Create a checklist item under a node (<c>POST /v1/projects/{p}/authoring/checklist-items</c>, 13.1.1 §5 route 8).
/// The item is created <c>active</c> with <c>source = manual</c> (AC3); the body is carried as the parsed JSON the
/// endpoint already member-checked, so the handler can name <c>/priority</c> or <c>/title</c> in its own 422 instead of
/// the writer's internal <c>/changes/0/content/…</c> path.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Body">The parsed request body (13.1.1 §5 route 8 members only).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1, AC18).</param>
internal sealed record CreateChecklistItemCommand(Guid ProjectId, JsonElement Body, string IdempotencyKey)
    : ICommand<Result<CreateChecklistItemResponse>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="CreateChecklistItemCommand"/>: the project scope gate, the project's liveness (404 before the
/// body is read, like the node and manual-case creates), the request shape, then one
/// <see cref="AuthoringVersionWriter.AppendAsync"/> call carrying one <c>create</c> change (AC5). The node's kind, its
/// project and its liveness are the writer's own locked decision (AC3's <c>parent_kind_invalid</c>,
/// <c>parent_not_found</c>, <c>parent_deleted</c>), so nothing here repeats it.
/// </summary>
/// <remarks>
/// An authoring write is audit-once (AC18): the pipeline appends the row described here, and an idempotency replay
/// suppresses it (<c>AuditTrail.Suppress</c>) because the original request already wrote its own.
/// </remarks>
[AuditedAction("authoring.checklist_item.create")]
[Permission(Permissions.Authoring.Write)]
internal sealed class CreateChecklistItemCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    AuthoringVersionWriter writer,
    IAuditTrail audit)
    : ICommandHandler<CreateChecklistItemCommand, Result<CreateChecklistItemResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<CreateChecklistItemResponse>> Handle(
        CreateChecklistItemCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(CreateChecklistItemCommand));

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<CreateChecklistItemResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        // AC10 / 00 §9.3: a project the tenant cannot see — or one that is soft-deleted — is 404 before any write, like
        // the node and manual-case creates (NodeCommands.cs:99-105, ManualCaseCommands.cs:77). Without it the writer's
        // parent probe answers 422 parent_not_found and the route would report an unknown project as a placement problem.
        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false)
            is { } missingProject)
        {
            return Result.Failure<CreateChecklistItemResponse>(missingProject);
        }

        var (draft, shapeError) = ChecklistItemFields.ReadCreate(command.Body);
        if (shapeError is not null || draft is null)
        {
            return Result.Failure<CreateChecklistItemResponse>(shapeError!);
        }

        var actor = await AuthoringRequestScope
            .ActorMembershipAsync(db, orgId, principals, cancellationToken)
            .ConfigureAwait(false);

        var position = draft.Position ?? await db
            .ScalarAsync<int?>(
                AuthoringChecklistReadSql.NextPosition,
                new { org_id = orgId, project_id = command.ProjectId, node_id = draft.NodeId },
                cancellationToken)
            .ConfigureAwait(false) ?? 0;

        var itemId = Guid.CreateVersion7();
        var content = ChecklistItemFields.Content(
            draft.NodeId,
            draft.Title,
            draft.Description,
            draft.Priority,
            ChecklistItemStateMachine.Active,
            position);

        var appended = await writer.AppendAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            [EntityChange.Create(AuthoringEntityKind.ChecklistItem, itemId, content)],
            null,
            cancellationToken).ConfigureAwait(false);

        if (!appended.IsSuccess)
        {
            // Route 8's members are the body's (`/node_id`, `/title`, …); the writer's `/changes/0/content/` prefix is its
            // own batch envelope and `feature_node_id` is the stored column, not a member the caller sent.
            return Result.Failure<CreateChecklistItemResponse>(
                AuthoringErrors.RequestFacing(appended.Error!, ChecklistItemFields.RequestMemberNames));
        }

        if (appended.Value is not { } outcome)
        {
            return Result.Failure<CreateChecklistItemResponse>(appended.Error!);
        }

        // A create always appends: the entity did not exist, so there is no previous snapshot it could equal (AC5).
        var version = outcome.Versions.Count > 0 ? outcome.Versions[0].VersionNo : 1;
        audit.Describe(new AuditObject("checklist_item", itemId.ToString("D")));

        return Result.Success(
            new CreateChecklistItemResponse(itemId, version, ChecklistItemStateMachine.Active),
            StatusCodes.Status201Created);
    }
}
