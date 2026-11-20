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
        var moves = parentId != stored.ParentId;
        int? appendedPosition = null;

        if (moves)
        {
            // The cap belongs to the target group, not to the position: a move-in at an explicit position grows the
            // group exactly like an append does (review N1), and a group past the cap could never be renumbered again
            // because the reorder refuses more than NodeLimits.MaximumSiblingsPerGroup ids. A same-group PATCH therefore
            // never reaches this read.
            var group = await db.QuerySingleOrDefaultAsync<AuthoringSql.NodeGroupRow>(
                AuthoringSql.NodeGroupSummary(parentId is null),
                new { org_id = orgId, project_id = command.ProjectId, parent_id = parentId },
                cancellationToken).ConfigureAwait(false);

            if (group is { } members && members.LiveCount >= NodeLimits.MaximumSiblingsPerGroup)
            {
                return Result.Failure<AuthoringChangeResponse>(NodeLimits.Refusal());
            }

            appendedPosition = group?.NextPosition ?? 0;
        }

        // True when this handler picked the number: the append position of a move-in. A same-group PATCH never does — it
        // keeps the row's own position unless the caller sends one.
        var positionWasDefaulted = moves && command.Position is null;
        var position = command.Position ?? appendedPosition ?? stored.Position;

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
            var refusal = AuthoringErrors.RequestFacing(outcome.Error!);

            // A move-in without a position appends at the number this handler read under the target group's lock; a
            // position-only PATCH of that group takes no group lock (review NEW-2), so it can commit the number in
            // between and make the writer refuse `/position` — a member this caller never sent. That is the retryable
            // 503 of §5, not a 422 of the request.
            return Result.Failure<AuthoringChangeResponse>(
                positionWasDefaulted && AuthoringErrors.IsPositionTaken(refusal)
                    ? AuthoringErrors.Unavailable()
                    : refusal);
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
