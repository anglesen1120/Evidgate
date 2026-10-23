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
using Evidgate.Modules.Policy.Domain;
using Mediator;

namespace Evidgate.Modules.Policy.Application.Waivers;

/// <summary>Request a waiver (<c>POST /v1/waivers</c>, 07.3.1 §5).</summary>
/// <param name="ProjectId">The project the waiver belongs to.</param>
/// <param name="Scope"><c>case</c> or <c>suite</c>.</param>
/// <param name="TargetId">The case or suite the waiver covers.</param>
/// <param name="Reason">Justification, 1–2000 characters, no NUL.</param>
/// <param name="ExpiresAt">A date-time strictly in the future.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record CreateWaiverCommand(
    Guid ProjectId,
    string? Scope,
    Guid TargetId,
    string? Reason,
    DateTimeOffset? ExpiresAt,
    string IdempotencyKey)
    : ICommand<Result<WaiverDto>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="CreateWaiverCommand"/>: the caller must be a member, the project must be inside the
/// credential's scope and live, the body must be valid, and the row is inserted in state <c>requested</c> with the
/// caller as <c>requested_by</c> (07.3.1 §5, AC1).
/// </summary>
[AuditedAction("waiver.create")]
[Permission(Permissions.Policy.Write)]
internal sealed class CreateWaiverCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    TimeProvider clock,
    IAuditTrail audit)
    : ICommandHandler<CreateWaiverCommand, Result<WaiverDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<WaiverDto>> Handle(CreateWaiverCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(CreateWaiverCommand));

        if (principals.Current?.UserId is not { } requestedBy)
        {
            return Result.Failure<WaiverDto>(Error.Forbidden("only a member may request a waiver"));
        }

        if (WaiverScopeGuard.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<WaiverDto>(mismatch);
        }

        var live = await db.ScalarAsync<int?>(WaiverSql.ProjectExists, new { project_id = command.ProjectId }, cancellationToken).ConfigureAwait(false);
        if (live is null)
        {
            return Result.Failure<WaiverDto>(WaiverErrors.ProjectNotFound);
        }

        if (Validate(command, clock, out var expiresAt) is { } invalid)
        {
            return Result.Failure<WaiverDto>(invalid);
        }

        var id = Guid.CreateVersion7();
        var row = await db.QuerySingleOrDefaultAsync<WaiverDto>(
            WaiverSql.Insert,
            new
            {
                id,
                org_id = scopes.Require().OrgId,
                project_id = command.ProjectId,
                scope = command.Scope,
                target_id = command.TargetId,
                reason = command.Reason,
                requested_by = requestedBy,
                // The column is timestamptz and the Dapper path carries no DateTimeOffset handler (it is read-only
                // by design), so the instant travels as a UTC DateTime — the shape TaskQueueWriter uses too.
                expires_at = expiresAt.UtcDateTime,
            },
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("the waiver insert returned no row");

        audit.Describe(new AuditObject("waiver", id.ToString("D")));

        return Result.Success(row, 201);
    }

    /// <summary>
    /// Null when the body is acceptable, else the 422 with one <see cref="ErrorDetail"/> per offending field;
    /// <paramref name="expiresAt"/> is the validated expiry (07.3.1 §5).
    /// </summary>
    private static Error? Validate(CreateWaiverCommand command, TimeProvider clock, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        var details = new List<ErrorDetail>();

        if (!WaiverScope.IsKnown(command.Scope))
        {
            details.Add(new ErrorDetail("scope", "unknown"));
        }

        if (command.TargetId == Guid.Empty)
        {
            details.Add(new ErrorDetail("target_id", "required"));
        }

        if (string.IsNullOrEmpty(command.Reason))
        {
            details.Add(new ErrorDetail("reason", "empty"));
        }
        else if (command.Reason.Length > WaiverRules.ReasonMaxLength)
        {
            details.Add(new ErrorDetail("reason", "too_long"));
        }
        else if (command.Reason.Contains('\0'))
        {
            details.Add(new ErrorDetail("reason", "invalid_value"));
        }

        if (command.ExpiresAt is not { } expiry)
        {
            details.Add(new ErrorDetail("expires_at", "required"));
        }
        else
        {
            expiresAt = expiry;
            if (expiry <= clock.GetUtcNow())
            {
                details.Add(new ErrorDetail("expires_at", "not_in_future"));
            }
        }

        return details.Count == 0 ? null : Error.Invalid("the waiver request is not valid", details);
    }
}

/// <summary>Approve a waiver (<c>POST /v1/waivers/{w}/approve</c>, 07.3.1 §5, AC1/AC2).</summary>
/// <param name="WaiverId">The waiver from the route (<c>{w}</c>).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record ApproveWaiverCommand(Guid WaiverId, string IdempotencyKey)
    : ICommand<Result<WaiverDto>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="ApproveWaiverCommand"/>: the row is loaded under RLS (an unknown id, another tenant's row or
/// a project outside the token scope is 404), the caller must be a member, and the self-approval check runs before
/// any write (AC1). The transition itself is one conditional statement, so two concurrent approvals race on the row
/// and exactly one wins (AC2); the loser gets 409 <c>waiver_state_conflict</c>.
/// </summary>
[AuditedAction("waiver.approve")]
[Permission(Permissions.Policy.Write)]
internal sealed class ApproveWaiverCommandHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<ApproveWaiverCommand, Result<WaiverDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<WaiverDto>> Handle(ApproveWaiverCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(ApproveWaiverCommand));

        var current = await db.QuerySingleOrDefaultAsync<WaiverDto>(WaiverSql.ById, new { id = command.WaiverId }, cancellationToken).ConfigureAwait(false);
        if (current is null || WaiverScopeGuard.Project(current.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<WaiverDto>(WaiverErrors.NotFound);
        }

        if (principals.Current?.UserId is not { } approver)
        {
            return Result.Failure<WaiverDto>(Error.Forbidden("only a member may approve a waiver"));
        }

        if (WaiverRules.IsSelfApproval(current.RequestedBy, approver))
        {
            return Result.Failure<WaiverDto>(WaiverErrors.SelfApproval);
        }

        // The domain rule answers the same 409 the conditional statement would, without a pointless UPDATE; the
        // statement below stays the authority, so two concurrent approvals still race on the row.
        if (!WaiverRules.CanApprove(current.State))
        {
            return Result.Failure<WaiverDto>(WaiverErrors.StateConflict);
        }

        var approved = await db.QuerySingleOrDefaultAsync<WaiverDto>(
            WaiverSql.Approve,
            new { id = command.WaiverId, approved_by = approver },
            cancellationToken).ConfigureAwait(false);
        if (approved is null)
        {
            return Result.Failure<WaiverDto>(WaiverErrors.StateConflict);
        }

        audit.Describe(new AuditObject("waiver", command.WaiverId.ToString("D")));

        return Result.Success(approved);
    }
}

/// <summary>Revoke a waiver (<c>POST /v1/waivers/{w}/revoke</c>, 07.3.1 §5).</summary>
/// <param name="WaiverId">The waiver from the route (<c>{w}</c>).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record RevokeWaiverCommand(Guid WaiverId, string IdempotencyKey)
    : ICommand<Result<WaiverDto>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="RevokeWaiverCommand"/>: the row is loaded under RLS (404 as for approve), then one
/// conditional statement moves <c>requested</c> or <c>approved</c> to <c>revoked</c>; a terminal or expired waiver
/// matches no row and is 409 <c>waiver_state_conflict</c> (07.3.1 §6).
/// </summary>
[AuditedAction("waiver.revoke")]
[Permission(Permissions.Policy.Write)]
internal sealed class RevokeWaiverCommandHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<RevokeWaiverCommand, Result<WaiverDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<WaiverDto>> Handle(RevokeWaiverCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(RevokeWaiverCommand));

        var current = await db.QuerySingleOrDefaultAsync<WaiverDto>(WaiverSql.ById, new { id = command.WaiverId }, cancellationToken).ConfigureAwait(false);
        if (current is null || WaiverScopeGuard.Project(current.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<WaiverDto>(WaiverErrors.NotFound);
        }

        // Same 409 the conditional statement would answer, without a pointless UPDATE on a terminal waiver.
        if (!WaiverRules.CanRevoke(current.State))
        {
            return Result.Failure<WaiverDto>(WaiverErrors.StateConflict);
        }

        var revoked = await db.QuerySingleOrDefaultAsync<WaiverDto>(
            WaiverSql.Revoke,
            new { id = command.WaiverId },
            cancellationToken).ConfigureAwait(false);
        if (revoked is null)
        {
            return Result.Failure<WaiverDto>(WaiverErrors.StateConflict);
        }

        audit.Describe(new AuditObject("waiver", command.WaiverId.ToString("D")));

        return Result.Success(revoked);
    }
}
