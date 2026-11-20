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
/// renumber can never park a moved row onto the value this insert is about to take. A writer that does not take the group
/// lock (a position-only <c>PATCH</c> of the same group) can still commit the append position first: the refusal then
/// names <c>/position</c>, which this request never sent, so it answers the retryable <c>503 unavailable</c> instead
/// (review NEW-2).
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

        var positionWasDefaulted = command.Position is null;
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
            var refusal = AuthoringErrors.RequestFacing(written.Error!);

            // This handler chose the position itself (an append after the group's maximum). A writer that commits that
            // number anyway — a position-only PATCH of the same group takes no group lock (review NEW-2) — refuses a
            // member the caller never sent, so the answer is the retryable 503 of §5, not a 422 of the request.
            return Result.Failure<CreateNodeResponse>(
                positionWasDefaulted && AuthoringErrors.IsPositionTaken(refusal)
                    ? AuthoringErrors.Unavailable()
                    : refusal);
        }

        var version = written.Value.Versions.Count > 0 ? written.Value.Versions[0].VersionNo : 1;

        audit.Describe(new AuditObject("feature_node", nodeId.ToString("D")));

        return Result.Success(new CreateNodeResponse(nodeId, version));
    }
}
