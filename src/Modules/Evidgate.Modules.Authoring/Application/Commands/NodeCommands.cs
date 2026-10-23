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
using FluentValidation;
using Mediator;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// Create a feature node (<c>POST /v1/projects/{p}/authoring/nodes</c>, 13.1.1 §5 route 2, AC1/AC2).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="ParentId">The parent, or null for a root <c>module</c>.</param>
/// <param name="Kind">One of the four kinds (validated by the ladder inside the writer).</param>
/// <param name="Title">The title, 1..200 characters after NFC normalisation.</param>
/// <param name="Position">The explicit position, or null to append after the last live sibling (AC2).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (AC18).</param>
internal sealed record CreateNodeCommand(
    Guid ProjectId,
    Guid? ParentId,
    string? Kind,
    string? Title,
    int? Position,
    string IdempotencyKey)
    : ICommand<Result<CreateNodeResponse>>, IIdempotentCommand;

/// <summary>Shape validation of <see cref="CreateNodeCommand"/>: the members the endpoint could not type.</summary>
internal sealed class CreateNodeCommandValidator : AbstractValidator<CreateNodeCommand>
{
    /// <summary>Builds the rules.</summary>
    public CreateNodeCommandValidator()
    {
        RuleFor(command => command.Kind)
            .Must(FeatureNodeKinds.IsValid)
            .WithErrorCode("value_invalid");

        RuleFor(command => command.Title)
            .Must(title => !string.IsNullOrWhiteSpace(title) && title.Length <= AuthoringContent.MaximumNodeTitleLength)
            .WithErrorCode("invalid_length");

        RuleFor(command => command.Position)
            .GreaterThanOrEqualTo(0)
            .WithErrorCode("invalid_value")
            .When(command => command.Position is not null);

        RuleFor(command => command.ParentId)
            .NotEqual(Guid.Empty)
            .WithErrorCode("invalid_value")
            .When(command => command.ParentId is not null);
    }
}

/// <summary>
/// Handler of <see cref="CreateNodeCommand"/>: the ID is minted here (a UUIDv7), the position is defaulted to
/// <c>max(live sibling) + 1</c> (AC2) and the whole write goes through <see cref="AuthoringVersionWriter"/> — the ladder,
/// the parent-existence and <c>position_taken</c> guards live there (AC1/AC2), so this handler adds none of them.
/// </summary>
/// <remarks>
/// <b>The default position is a read, not a lock</b> (AC2): two concurrent creates that read the same maximum both send
/// the same position, and the partial unique index refuses the loser with the writer's <c>422 position_taken</c> — a retry
/// from a fresh read succeeds. Serialising creates on the group instead would turn a harmless race into a queue, and the
/// index already makes the outcome safe.
/// </remarks>
[AuditedAction("authoring.node.create")]
[Permission(Permissions.Authoring.Write)]
internal sealed class CreateNodeCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    AuthoringVersionWriter writer,
    IAuditTrail audit)
    : ICommandHandler<CreateNodeCommand, Result<CreateNodeResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<CreateNodeResponse>> Handle(CreateNodeCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(CreateNodeCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<CreateNodeResponse>(mismatch);
        }

        var orgId = scopes.Require().OrgId;

        if (await db.ScalarAsync<int?>(
                AuthoringNodeReadSql.ProjectExists,
                new { org_id = orgId, project_id = command.ProjectId },
                cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<CreateNodeResponse>(AuthoringRules.ProjectNotFound);
        }

        var actor = await AuthoringRequestScope.ActorMembershipAsync(db, orgId, principals, cancellationToken).ConfigureAwait(false);

        var position = command.Position ?? (await db.ScalarAsync<int?>(
            AuthoringSql.NodeNextPosition(command.ParentId is null),
            new { org_id = orgId, project_id = command.ProjectId, parent_id = command.ParentId },
            cancellationToken).ConfigureAwait(false) ?? 0);

        if (position is < 0 or > AuthoringVersionWriter.MaximumSiblingPosition)
        {
            // The validators are registered by the module (13.1.1-T2.1); this bound is repeated here because a position
            // past the cap would overflow the reorder's offset arithmetic and must answer 422, never a 500.
            return Result.Failure<CreateNodeResponse>(AuthoringErrors.Invalid(
                "/position",
                "out_of_range",
                "a sibling position is between 0 and 1000000000"));
        }

        var nodeId = Guid.CreateVersion7();
        var content = NodeContent.Of(command.Kind!, command.Title!, position, command.ParentId);

        var written = await writer.AppendAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            [EntityChange.Create(AuthoringEntityKind.FeatureNode, nodeId, content)],
            new AuthoringAppendContext { Source = AuthoringSource.Manual },
            cancellationToken).ConfigureAwait(false);

        if (!written.IsSuccess)
        {
            return Result.Failure<CreateNodeResponse>(written.Error!);
        }

        var version = written.Value.Versions.Count > 0 ? written.Value.Versions[0].VersionNo : 1;

        audit.Describe(new AuditObject("feature_node", nodeId.ToString("D")));

        return Result.Success(new CreateNodeResponse(nodeId, version));
    }
}

/// <summary>
/// Change a node's title, parent or position (<c>PATCH /v1/projects/{p}/authoring/nodes/{n}</c>, 13.1.1 §5 route 3).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="NodeId">The <c>{n}</c> of the route.</param>
/// <param name="Title">The new title, or null to leave it.</param>
/// <param name="ParentId">The new parent, or null to leave it (null is never "move to the root").</param>
/// <param name="Position">The new sibling position, or null to leave it (AC2's renumber applies when it moves).</param>
/// <param name="ExpectedVersion">The <c>If-Match</c> version (AC19).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (AC18).</param>
internal sealed record UpdateNodeCommand(
    Guid ProjectId,
    Guid NodeId,
    string? Title,
    Guid? ParentId,
    int? Position,
    int ExpectedVersion,
    string IdempotencyKey)
    : ICommand<Result<AuthoringChangeResponse>>, IIdempotentCommand;

/// <summary>Shape validation of <see cref="UpdateNodeCommand"/>.</summary>
internal sealed class UpdateNodeCommandValidator : AbstractValidator<UpdateNodeCommand>
{
    /// <summary>Builds the rules.</summary>
    public UpdateNodeCommandValidator()
    {
        RuleFor(command => command.NodeId)
            .NotEqual(Guid.Empty)
            .WithErrorCode("required");

        RuleFor(command => command.ExpectedVersion)
            .GreaterThan(0)
            .WithErrorCode("required");

        RuleFor(command => command.Title)
            .Must(title => !string.IsNullOrWhiteSpace(title) && title.Length <= AuthoringContent.MaximumNodeTitleLength)
            .WithErrorCode("invalid_length")
            .When(command => command.Title is not null);

        RuleFor(command => command.Position)
            .GreaterThanOrEqualTo(0)
            .WithErrorCode("invalid_value")
            .When(command => command.Position is not null);

        RuleFor(command => command.ParentId)
            .NotEqual(Guid.Empty)
            .WithErrorCode("invalid_value")
            .When(command => command.ParentId is not null);
    }
}

/// <summary>
/// Handler of <see cref="UpdateNodeCommand"/>: 404 for a node the tenant cannot see (and for a tombstone), 412 on a stale
/// <c>If-Match</c>, 200 <c>changed:false</c> for a no-op (AC5), and the ladder/<c>position_taken</c> refusals from the
/// writer (AC1/AC2) — never re-implemented here.
/// </summary>
/// <remarks>
/// <b>The lock, and why the version check is here rather than only in the writer.</b> The row is read
/// <c>for update</c> before the snapshot is merged, inside the command's one transaction: a second writer therefore waits
/// here, then compares its own <c>If-Match</c> with the version the first writer committed and answers 412 (AC19). A check
/// on an unlocked read would let both writers pass, and the writer's out-of-step guard cannot tell them apart (each of them
/// appends exactly one version above the last).
/// </remarks>
[AuditedAction("authoring.node.update")]
[Permission(Permissions.Authoring.Write)]
internal sealed class UpdateNodeCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    AuthoringVersionWriter writer,
    IAuditTrail audit)
    : ICommandHandler<UpdateNodeCommand, Result<AuthoringChangeResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AuthoringChangeResponse>> Handle(UpdateNodeCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(UpdateNodeCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<AuthoringChangeResponse>(mismatch);
        }

        var orgId = scopes.Require().OrgId;

        if (await db.ScalarAsync<int?>(
                AuthoringNodeReadSql.ProjectExists,
                new { org_id = orgId, project_id = command.ProjectId },
                cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<AuthoringChangeResponse>(AuthoringRules.ProjectNotFound);
        }

        var actor = await AuthoringRequestScope.ActorMembershipAsync(db, orgId, principals, cancellationToken).ConfigureAwait(false);

        var stored = await NodeRead.LockedAsync(db, orgId, command.ProjectId, command.NodeId, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.DeletedAt is not null)
        {
            return Result.Failure<AuthoringChangeResponse>(AuthoringErrors.EntityNotFound(AuthoringEntityKind.FeatureNode));
        }

        if (stored.Version != command.ExpectedVersion)
        {
            return Result.Failure<AuthoringChangeResponse>(AuthoringRequestScope.Stale("feature node", stored.Version));
        }

        // An omitted member is the unchanged one. A parent change re-applies AC1 inside the writer and, when no position
        // is given, appends the node at the end of its NEW group (route 3: "parent_id change re-applies AC1 and the
        // renumber of AC2") — reusing the old group's number could hit a live sibling of the new group and answer a
        // spurious `position_taken` for a number the caller never chose.
        var parentId = command.ParentId ?? stored.ParentId;
        int position;

        if (command.Position is { } explicitPosition)
        {
            position = explicitPosition;
        }
        else if (parentId != stored.ParentId)
        {
            position = await db.ScalarAsync<int?>(
                AuthoringSql.NodeNextPosition(parentId is null),
                new { org_id = orgId, project_id = command.ProjectId, parent_id = parentId },
                cancellationToken).ConfigureAwait(false) ?? 0;
        }
        else
        {
            position = stored.Position;
        }

        if (position is < 0 or > AuthoringVersionWriter.MaximumSiblingPosition)
        {
            return Result.Failure<AuthoringChangeResponse>(AuthoringErrors.Invalid(
                "/position",
                "out_of_range",
                "a sibling position is between 0 and 1000000000"));
        }

        var content = NodeContent.Of(stored.Kind, command.Title ?? stored.Title, position, parentId);

        var outcome = await writer.AppendAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            [EntityChange.Update(AuthoringEntityKind.FeatureNode, command.NodeId, content)],
            new AuthoringAppendContext { Source = AuthoringSource.Manual },
            cancellationToken).ConfigureAwait(false);

        if (!outcome.IsSuccess)
        {
            return Result.Failure<AuthoringChangeResponse>(outcome.Error!);
        }

        var changed = outcome.Value.Versions.Count > 0;
        var version = changed ? outcome.Value.Versions[0].VersionNo : stored.Version;

        audit.Describe(new AuditObject("feature_node", command.NodeId.ToString("D")));

        return Result.Success(new AuthoringChangeResponse(version, changed));
    }
}

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
    /// <summary>The cap of one reorder list: a group is a sibling set, not a whole project.</summary>
    internal const int MaxOrderedIds = 1000;

    /// <summary>Builds the rules.</summary>
    public ReorderNodesCommandValidator()
    {
        RuleFor(command => command.NodeId)
            .NotEqual(Guid.Empty)
            .WithErrorCode("required");

        RuleFor(command => command.OrderedIds)
            .NotEmpty()
            .WithErrorCode("required");

        RuleFor(command => command.OrderedIds)
            .Must(ids => ids.Count <= MaxOrderedIds)
            .WithErrorCode("too_many")
            .When(command => command.OrderedIds is not null);
    }
}

/// <summary>
/// Handler of <see cref="ReorderNodesCommand"/>: the two-phase renumber, the set equality and the stale-order 412 all live
/// in <see cref="AuthoringVersionWriter.ReorderNodesAsync"/> (AC2/OI-13-110), so this handler only gates the project and
/// maps the outcome.
/// </summary>
/// <remarks>
/// No row lock is taken here: the writer locks the anchor and then the whole group <c>for update</c> in id order, which
/// is the only lock order this write needs, and taking a partial lock here would invert that order.
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

        audit.Describe(new AuditObject("feature_node", command.NodeId.ToString("D")));

        return Result.Success(new ReorderNodesResponse(outcome.Value.Changed));
    }
}

/// <summary>
/// Soft-delete a node (<c>DELETE /v1/projects/{p}/authoring/nodes/{n}</c>, 13.1.1 §5 route 5, AC7).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="NodeId">The <c>{n}</c> of the route.</param>
/// <param name="Reason">An optional 1..500 character reason (the route's <c>{reason?}</c> body).</param>
/// <param name="ExpectedVersion">The <c>If-Match</c> version (AC19).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (AC18).</param>
internal sealed record DeleteNodeCommand(
    Guid ProjectId,
    Guid NodeId,
    string? Reason,
    int ExpectedVersion,
    string IdempotencyKey)
    : ICommand<Result<bool>>, IIdempotentCommand;

/// <summary>Shape validation of <see cref="DeleteNodeCommand"/>.</summary>
internal sealed class DeleteNodeCommandValidator : AbstractValidator<DeleteNodeCommand>
{
    /// <summary>The reason bound of §5 (<c>≤500</c>), the same the writer enforces.</summary>
    internal const int MaxReasonLength = 500;

    /// <summary>Builds the rules.</summary>
    public DeleteNodeCommandValidator()
    {
        RuleFor(command => command.NodeId)
            .NotEqual(Guid.Empty)
            .WithErrorCode("required");

        RuleFor(command => command.ExpectedVersion)
            .GreaterThan(0)
            .WithErrorCode("required");

        RuleFor(command => command.Reason)
            .Must(reason => !string.IsNullOrWhiteSpace(reason) && reason.Length <= MaxReasonLength)
            .WithErrorCode("invalid_length")
            .When(command => command.Reason is not null);
    }
}

/// <summary>
/// Handler of <see cref="DeleteNodeCommand"/>: 404 for an absent or already deleted node, 412 on a stale version, and the
/// AC7 non-empty guard from inside the writer (<c>409 authoring_not_empty</c>, <c>node_has_children</c> /
/// <c>node_has_items</c>) — this handler must not repeat it.
/// </summary>
[AuditedAction("authoring.node.delete")]
[Permission(Permissions.Authoring.Write)]
internal sealed class DeleteNodeCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    AuthoringVersionWriter writer,
    IAuditTrail audit)
    : ICommandHandler<DeleteNodeCommand, Result<bool>>
{
    /// <inheritdoc />
    public async ValueTask<Result<bool>> Handle(DeleteNodeCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(DeleteNodeCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<bool>(mismatch);
        }

        var orgId = scopes.Require().OrgId;

        if (await db.ScalarAsync<int?>(
                AuthoringNodeReadSql.ProjectExists,
                new { org_id = orgId, project_id = command.ProjectId },
                cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<bool>(AuthoringRules.ProjectNotFound);
        }

        var actor = await AuthoringRequestScope.ActorMembershipAsync(db, orgId, principals, cancellationToken).ConfigureAwait(false);

        // A tombstone is 404 here, not 409: the row exists, but the entity is gone for every route that addresses it
        // (AC7 "it disappears from every list and from the tree"), and the restore route is the one that revives it.
        var stored = await NodeRead.LockedAsync(db, orgId, command.ProjectId, command.NodeId, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.DeletedAt is not null)
        {
            return Result.Failure<bool>(AuthoringErrors.EntityNotFound(AuthoringEntityKind.FeatureNode));
        }

        if (stored.Version != command.ExpectedVersion)
        {
            return Result.Failure<bool>(AuthoringRequestScope.Stale("feature node", stored.Version));
        }

        var outcome = await writer.AppendAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            [EntityChange.SoftDelete(AuthoringEntityKind.FeatureNode, command.NodeId, command.Reason)],
            new AuthoringAppendContext { Source = AuthoringSource.Manual },
            cancellationToken).ConfigureAwait(false);

        if (!outcome.IsSuccess)
        {
            return Result.Failure<bool>(outcome.Error!);
        }

        audit.Describe(new AuditObject("feature_node", command.NodeId.ToString("D")));

        return Result.Success(true);
    }
}

/// <summary>
/// The one builder of a node's <c>content</c> snapshot (§4: <c>{kind, title, position, parent_id}</c>) as the
/// <see cref="JsonElement"/> an <see cref="EntityChange"/> carries, plus the <c>for update</c> read the two single-node
/// writes share.
/// </summary>
internal static class NodeContent
{
    /// <summary>The content object of a node, NFC-normalised exactly as the writer will validate it.</summary>
    /// <param name="kind">A valid kind.</param>
    /// <param name="title">The title.</param>
    /// <param name="position">The sibling position.</param>
    /// <param name="parentId">The parent, or null for a root.</param>
    internal static JsonElement Of(string kind, string title, int position, Guid? parentId)
    {
        using var document = JsonDocument.Parse(
            AuthoringContent.FeatureNode(kind, title, position, parentId).ToJsonString());

        return document.RootElement.Clone();
    }
}

/// <summary>The locked read of one node, shared by <see cref="UpdateNodeCommandHandler"/> and <see cref="DeleteNodeCommandHandler"/>.</summary>
internal static class NodeRead
{
    /// <summary>The stored row <c>for update</c>, or null when the project has no such node.</summary>
    /// <param name="db">The tenant connection of the command's transaction.</param>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="nodeId">The node.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static Task<AuthoringNodeReadSql.LockedNodeRow?> LockedAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        Guid nodeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.QuerySingleOrDefaultAsync<AuthoringNodeReadSql.LockedNodeRow>(
            AuthoringNodeReadSql.LockNode,
            new { org_id = orgId, project_id = projectId, id = nodeId },
            cancellationToken);
    }
}
