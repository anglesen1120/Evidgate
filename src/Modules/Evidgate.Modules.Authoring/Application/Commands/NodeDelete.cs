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
