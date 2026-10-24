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
using Npgsql;

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
    /// <remarks>
    /// Every rule renames its property to the request member it answers (<c>/kind</c>, <c>/title</c>, <c>/position</c>,
    /// <c>/parent_id</c>): <c>ValidationBehaviour</c> copies FluentValidation's <c>PropertyName</c> verbatim into
    /// <c>details[].path</c>, and the C# property spelling would put a member the client never sent in the answer
    /// (route 2 promises <c>/position</c>; <c>CreateRole.cs:57</c> is the precedent for the override).
    /// </remarks>
    public CreateNodeCommandValidator()
    {
        RuleFor(command => command.Kind)
            .Must(FeatureNodeKinds.IsValid)
            .WithErrorCode("value_invalid")
            .OverridePropertyName("/kind");

        RuleFor(command => command.Title)
            .Must(title => AuthoringText.CodePointLength(title) > 0
                && AuthoringText.Fits(title, AuthoringContent.MaximumNodeTitleLength))
            .WithErrorCode("invalid_length")
            .OverridePropertyName("/title");

        RuleFor(command => command.Position)
            .GreaterThanOrEqualTo(0)
            .WithErrorCode("invalid_value")
            .OverridePropertyName("/position")
            .When(command => command.Position is not null);

        RuleFor(command => command.ParentId)
            .NotEqual(Guid.Empty)
            .WithErrorCode("invalid_value")
            .OverridePropertyName("/parent_id")
            .When(command => command.ParentId is not null);
    }
}

/// <summary>
/// Handler of <see cref="CreateNodeCommand"/>: the ID is minted here (a UUIDv7), the position is defaulted to
/// <c>max(live sibling) + 1</c> (AC2) and the whole write goes through <see cref="AuthoringVersionWriter"/> — the ladder,
/// the parent-existence and <c>position_taken</c> guards live there (AC1/AC2), so this handler adds none of them.
/// </summary>
/// <remarks>
/// <b>The position is read under the group's lock, not next to it</b> (AC2; review F1/F6). The group's size and its
/// append position are one read (<see cref="AuthoringSql.NodeGroupSummary"/>) taken after this command holds the group's
/// advisory lock (<see cref="AuthoringLockGuard.LockSiblingGroupAsync"/>), and a renumber of that group takes the same
/// lock before it locks the sibling rows. Two concurrent creates therefore append one after the other instead of both
/// sending the same number — the loser used to answer <c>422 position_taken</c> for a position it never sent — and a
/// renumber can never park a moved row onto the value this insert is about to take.
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

        // The group lock is taken here, not in the writer, so the writer's own bound covers none of it: the same 5 s
        // lock_timeout is applied first and 55P03/40P01 answer the retryable 503 instead of a 500.
        await AuthoringLockGuard.SetLockTimeoutAsync(db, cancellationToken).ConfigureAwait(false);

        try
        {
            return await HandleCoreAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (AuthoringLockGuard.IsContention(exception))
        {
            return Result.Failure<CreateNodeResponse>(AuthoringErrors.Unavailable());
        }
    }

    /// <summary>The handler body, inside the bounded wait and contention mapping of <see cref="Handle"/>.</summary>
    private async ValueTask<Result<CreateNodeResponse>> HandleCoreAsync(
        CreateNodeCommand command,
        CancellationToken cancellationToken)
    {
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

        // AC2's group lock: a renumber of this group and every other create (or move-in) of it queue behind it, so the
        // count and the maximum read next are the group as it will be when this insert commits.
        await AuthoringLockGuard
            .LockSiblingGroupAsync(db, command.ProjectId, command.ParentId, cancellationToken)
            .ConfigureAwait(false);

        var group = await db.QuerySingleOrDefaultAsync<AuthoringSql.NodeGroupRow>(
            AuthoringSql.NodeGroupSummary(command.ParentId is null),
            new { org_id = orgId, project_id = command.ProjectId, parent_id = command.ParentId },
            cancellationToken).ConfigureAwait(false);

        if (group is { } members && members.LiveCount >= NodeLimits.MaximumSiblingsPerGroup)
        {
            return Result.Failure<CreateNodeResponse>(NodeLimits.Refusal());
        }

        var position = command.Position ?? group?.NextPosition ?? 0;

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
            // §5 route 2 names `/position` (AC2) and AC1 names `/parent_id`: the writer reports one change's members under
            // its batch envelope, so the single-change prefix is dropped at this edge.
            return Result.Failure<CreateNodeResponse>(AuthoringErrors.RequestFacing(written.Error!));
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
    /// <summary>Builds the rules; each is renamed to the member it answers (see <see cref="CreateNodeCommandValidator"/>).</summary>
    public UpdateNodeCommandValidator()
    {
        RuleFor(command => command.NodeId)
            .NotEqual(Guid.Empty)
            .WithErrorCode("required")
            .OverridePropertyName("/node_id");

        RuleFor(command => command.ExpectedVersion)
            .GreaterThan(0)
            .WithErrorCode("required")
            .OverridePropertyName("/If-Match");

        RuleFor(command => command.Title)
            .Must(title => AuthoringText.CodePointLength(title) > 0
                && AuthoringText.Fits(title, AuthoringContent.MaximumNodeTitleLength))
            .WithErrorCode("invalid_length")
            .OverridePropertyName("/title")
            .When(command => command.Title is not null);

        RuleFor(command => command.Position)
            .GreaterThanOrEqualTo(0)
            .WithErrorCode("invalid_value")
            .OverridePropertyName("/position")
            .When(command => command.Position is not null);

        RuleFor(command => command.ParentId)
            .NotEqual(Guid.Empty)
            .WithErrorCode("invalid_value")
            .OverridePropertyName("/parent_id")
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
/// <para>
/// <b>Lock order.</b> When the request names a parent, that group's advisory lock is taken <em>before</em> the node's own
/// row — the same order the create and the renumber use (group, then rows) — so a PATCH that moves a node into a group
/// cannot enter it while a renumber of that group is parking its siblings. A PATCH that names no parent never leaves its
/// group and needs no group lock: the row lock below already waits for a renumber, which locks every live sibling
/// <c>for update</c>.
/// </para>
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

        // The group lock and the <c>for update</c> read below run before the writer applies its own bound, so the same
        // 5 s lock_timeout is applied here and 55P03/40P01 answer the retryable 503 instead of a 500.
        await AuthoringLockGuard.SetLockTimeoutAsync(db, cancellationToken).ConfigureAwait(false);

        try
        {
            return await HandleCoreAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (AuthoringLockGuard.IsContention(exception))
        {
            return Result.Failure<AuthoringChangeResponse>(AuthoringErrors.Unavailable());
        }
    }

    /// <summary>The handler body, inside the bounded wait and contention mapping of <see cref="Handle"/>.</summary>
    private async ValueTask<Result<AuthoringChangeResponse>> HandleCoreAsync(
        UpdateNodeCommand command,
        CancellationToken cancellationToken)
    {
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

        if (command.ParentId is { } target)
        {
            await AuthoringLockGuard
                .LockSiblingGroupAsync(db, command.ProjectId, target, cancellationToken)
                .ConfigureAwait(false);
        }

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
        // spurious `position_taken` for a number the caller never chose. The append position is read under the target
        // group's lock of this call, so it cannot meet a renumber's parked value.
        var parentId = command.ParentId ?? stored.ParentId;
        int position;

        if (command.Position is { } explicitPosition)
        {
            position = explicitPosition;
        }
        else if (parentId != stored.ParentId)
        {
            var group = await db.QuerySingleOrDefaultAsync<AuthoringSql.NodeGroupRow>(
                AuthoringSql.NodeGroupSummary(parentId is null),
                new { org_id = orgId, project_id = command.ProjectId, parent_id = parentId },
                cancellationToken).ConfigureAwait(false);

            if (group is { } members && members.LiveCount >= NodeLimits.MaximumSiblingsPerGroup)
            {
                return Result.Failure<AuthoringChangeResponse>(NodeLimits.Refusal());
            }

            position = group?.NextPosition ?? 0;
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
            // Route 3's members are `/title`, `/parent_id` and `/position`; the writer's `/changes/0/content/` prefix is
            // its batch envelope, not this route's request.
            return Result.Failure<AuthoringChangeResponse>(AuthoringErrors.RequestFacing(outcome.Error!));
        }

        if (outcome.Value.Versions.Count == 0)
        {
            return Unchanged(stored.Version);
        }

        audit.Describe(new AuditObject("feature_node", command.NodeId.ToString("D")));

        return Result.Success(new AuthoringChangeResponse(outcome.Value.Versions[0].VersionNo, Changed: true));
    }

    /// <summary>
    /// The AC5 no-op answer: the unchanged version, <c>changed: false</c>, no version row — and no audit row either,
    /// because AC18 counts successful <em>effective</em> mutations, the rule the checklist item and manual case handlers
    /// already apply.
    /// </summary>
    /// <param name="version">The version the row still holds.</param>
    /// <returns>The 200 the caller receives.</returns>
    private Result<AuthoringChangeResponse> Unchanged(int version)
    {
        audit.Suppress();

        return Result.Success(new AuthoringChangeResponse(version, Changed: false));
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
    /// <summary>
    /// The reason bound of §5 (<c>≤500</c>), the same the writer enforces, counted in code points like every other
    /// authoring text (<see cref="AuthoringText"/>) — and with the whitespace rule of the manual case route
    /// (<c>ManualCaseBody.OptionalReason</c>: a reason is a non-empty string, not a non-blank one), so the two entity
    /// families refuse exactly the same reasons.
    /// </summary>
    internal const int MaxReasonLength = 500;

    /// <summary>Builds the rules; each is renamed to the member it answers (see <see cref="CreateNodeCommandValidator"/>).</summary>
    public DeleteNodeCommandValidator()
    {
        RuleFor(command => command.NodeId)
            .NotEqual(Guid.Empty)
            .WithErrorCode("required")
            .OverridePropertyName("/node_id");

        RuleFor(command => command.ExpectedVersion)
            .GreaterThan(0)
            .WithErrorCode("required")
            .OverridePropertyName("/If-Match");

        RuleFor(command => command.Reason)
            .Must(reason => AuthoringText.CodePointLength(reason) is > 0 and <= MaxReasonLength)
            .WithErrorCode("invalid_length")
            .OverridePropertyName("/reason")
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

        // The <c>for update</c> read below runs before the writer applies its own bound, so the same 5 s lock_timeout is
        // applied here and 55P03/40P01 answer the retryable 503 instead of a 500.
        await AuthoringLockGuard.SetLockTimeoutAsync(db, cancellationToken).ConfigureAwait(false);

        try
        {
            return await HandleCoreAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (AuthoringLockGuard.IsContention(exception))
        {
            return Result.Failure<bool>(AuthoringErrors.Unavailable());
        }
    }

    /// <summary>The handler body, inside the bounded wait and contention mapping of <see cref="Handle"/>.</summary>
    private async ValueTask<Result<bool>> HandleCoreAsync(DeleteNodeCommand command, CancellationToken cancellationToken)
    {
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
/// The limits of one sibling group (AC2). The reorder's list cap and the size a group may grow to live here together, so
/// a group a caller can build is exactly a group they can renumber (review F5/perf F9: the reorder refused more than
/// <see cref="ReorderNodesCommandValidator.MaxOrderedIds"/> ids while node create had no cap at all, which left a group
/// past the cap that no renumber could ever order).
/// </summary>
internal static class NodeLimits
{
    /// <summary>
    /// The most live siblings one parent group may hold — the reorder validator's list cap, because the two-phase renumber
    /// takes the whole set in one request. A create or a move into a full group is refused with
    /// <see cref="Refusal"/>: <c>422 request_invalid</c>, <c>details[].path = /parent_id</c>,
    /// <c>details[].reason = children_limit_exceeded</c>.
    /// </summary>
    internal const int MaximumSiblingsPerGroup = 1000;

    /// <summary>The <c>422</c> of a group that already holds <see cref="MaximumSiblingsPerGroup"/> live siblings.</summary>
    internal static Error Refusal() => AuthoringErrors.Invalid(
        "/parent_id",
        "children_limit_exceeded",
        "a sibling group holds at most "
            + MaximumSiblingsPerGroup.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " nodes; renumber or move some of them first");
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
