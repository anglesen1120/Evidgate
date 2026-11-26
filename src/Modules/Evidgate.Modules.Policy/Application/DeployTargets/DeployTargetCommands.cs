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
using Evidgate.Modules.Policy.Application.Quarantine;
using Evidgate.Modules.Policy.Domain;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Policy.Application.DeployTargets;

/// <summary>Create a deploy target (<c>POST /v1/projects/{p}/deploy-targets</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Name">The slug.</param>
/// <param name="Order">Display position, or null for 0.</param>
/// <param name="PolicyRevisionId">Optional pin to a policy revision of the project.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record CreateDeployTargetCommand(Guid ProjectId, string? Name, int? Order, Guid? PolicyRevisionId, string IdempotencyKey)
    : ICommand<Result<DeployTargetDto>>, IIdempotentCommand;

/// <summary>Change a deploy target (<c>PATCH</c>): a null field is left unchanged, a pin may be cleared on purpose.</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="TargetId">The target from the route.</param>
/// <param name="ExpectedVersion">The version from <c>If-Match</c>.</param>
/// <param name="Name">New slug, or null.</param>
/// <param name="Order">New position, or null.</param>
/// <param name="PolicyRevisionGiven">True when the body names <c>policy_revision_id</c> (possibly null, which clears the pin).</param>
/// <param name="PolicyRevisionId">The new pin when <paramref name="PolicyRevisionGiven"/>.</param>
/// <param name="State"><c>active</c> or <c>disabled</c>, or null.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record UpdateDeployTargetCommand(
    Guid ProjectId,
    Guid TargetId,
    int ExpectedVersion,
    string? Name,
    int? Order,
    bool PolicyRevisionGiven,
    Guid? PolicyRevisionId,
    string? State,
    string IdempotencyKey)
    : ICommand<Result<DeployTargetDto>>, IIdempotentCommand;

/// <summary>Disable a deploy target (<c>DELETE</c>): the row stays, <c>active</c> becomes <c>disabled</c>.</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="TargetId">The target from the route.</param>
/// <param name="ExpectedVersion">The version from <c>If-Match</c>.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record DisableDeployTargetCommand(Guid ProjectId, Guid TargetId, int ExpectedVersion, string IdempotencyKey)
    : ICommand<Result<DeployTargetDto>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="CreateDeployTargetCommand"/>: scope, body and project checks, then the cap and the pin under
/// the project row lock, then the insert. The unique name index decides a duplicate, so two concurrent creates of one
/// name leave one row and one 409 <c>target_exists</c>.
/// </summary>
[AuditedAction("deploy_target.create")]
[Permission(Permissions.Project.Write)]
internal sealed class CreateDeployTargetCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<CreateDeployTargetCommand, Result<DeployTargetDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<DeployTargetDto>> Handle(CreateDeployTargetCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(CreateDeployTargetCommand));

        if (QuarantineScopeGuard.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<DeployTargetDto>(mismatch);
        }

        if (DeployTargetValidation.Check(command.Name, true, command.Order, command.PolicyRevisionId is not null, command.PolicyRevisionId, null) is { } invalid)
        {
            return Result.Failure<DeployTargetDto>(invalid);
        }

        var orgId = scopes.Require().OrgId;
        var locked = await db.ScalarAsync<int?>(DeployTargetSql.LockProject, new { project_id = command.ProjectId }, cancellationToken).ConfigureAwait(false);
        if (locked is null)
        {
            return Result.Failure<DeployTargetDto>(DeployTargetErrors.ProjectNotFound);
        }

        var count = await db.ScalarAsync<long>(DeployTargetSql.Count, new { org_id = orgId, project_id = command.ProjectId }, cancellationToken).ConfigureAwait(false);
        if (count >= DeployTargetRules.MaxPerProject)
        {
            return Result.Failure<DeployTargetDto>(DeployTargetErrors.TooMany);
        }

        if (command.PolicyRevisionId is { } pin
            && await DeployTargetWrites.PinMissingAsync(db, orgId, command.ProjectId, pin, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<DeployTargetDto>(DeployTargetErrors.UnknownPolicyRevision);
        }

        var id = Guid.CreateVersion7();
        DeployTargetDto row;
        try
        {
            row = await db.QuerySingleOrDefaultAsync<DeployTargetDto>(
                DeployTargetSql.Insert,
                new
                {
                    id,
                    org_id = orgId,
                    project_id = command.ProjectId,
                    name = command.Name,
                    position = command.Order ?? 0,
                    policy_revision_id = command.PolicyRevisionId,
                },
                cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("the deploy target insert returned no row");
        }
        catch (PostgresException error) when (DeployTargetWrites.Refusal(error) is { } refusal)
        {
            return Result.Failure<DeployTargetDto>(refusal);
        }

        audit.Describe(new AuditObject("deploy_target", id.ToString("D"), ProjectId: command.ProjectId));

        return Result.Success(row, 201);
    }
}

/// <summary>Handler of <see cref="UpdateDeployTargetCommand"/>: 404, then 412 on a stale version, then the versioned write.</summary>
[AuditedAction("deploy_target.update")]
[Permission(Permissions.Project.Write)]
internal sealed class UpdateDeployTargetCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<UpdateDeployTargetCommand, Result<DeployTargetDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<DeployTargetDto>> Handle(UpdateDeployTargetCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(UpdateDeployTargetCommand));

        if (QuarantineScopeGuard.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<DeployTargetDto>(mismatch);
        }

        if (command.Name is null && command.Order is null && !command.PolicyRevisionGiven && command.State is null)
        {
            return Result.Failure<DeployTargetDto>(DeployTargetErrors.NothingToChange);
        }

        if (DeployTargetValidation.Check(command.Name, command.Name is not null, command.Order, command.PolicyRevisionGiven, command.PolicyRevisionId, command.State) is { } invalid)
        {
            return Result.Failure<DeployTargetDto>(invalid);
        }

        var result = await DeployTargetWrites.ApplyAsync(
            db,
            scopes.Require().OrgId,
            command,
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            audit.Describe(new AuditObject("deploy_target", command.TargetId.ToString("D"), ProjectId: command.ProjectId));
        }

        return result;
    }
}

/// <summary>Handler of <see cref="DisableDeployTargetCommand"/>: the update path with <c>state = disabled</c>; an already disabled target is 409.</summary>
[AuditedAction("deploy_target.disable")]
[Permission(Permissions.Project.Write)]
internal sealed class DisableDeployTargetCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<DisableDeployTargetCommand, Result<DeployTargetDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<DeployTargetDto>> Handle(DisableDeployTargetCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(DisableDeployTargetCommand));

        if (QuarantineScopeGuard.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<DeployTargetDto>(mismatch);
        }

        var patch = new UpdateDeployTargetCommand(
            command.ProjectId, command.TargetId, command.ExpectedVersion, null, null, false, null, DeployTargetState.Disabled, command.IdempotencyKey);
        var result = await DeployTargetWrites.ApplyAsync(db, scopes.Require().OrgId, patch, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            audit.Describe(new AuditObject("deploy_target", command.TargetId.ToString("D"), ProjectId: command.ProjectId));
        }

        return result;
    }
}

/// <summary>The read-check-write shared by the patch and the disable, and the database-refusal mapping of every write.</summary>
internal static class DeployTargetWrites
{
    internal static async Task<bool> PinMissingAsync(ITenantDb db, Guid orgId, Guid projectId, Guid pin, CancellationToken cancellationToken) =>
        await db.ScalarAsync<int?>(
            DeployTargetSql.PinExists,
            new { org_id = orgId, project_id = projectId, policy_revision_id = pin },
            cancellationToken).ConfigureAwait(false) is null;

    /// <summary>
    /// 409 for the unique name index, 422 for the composite pin FK (a revision of another project, or one that vanished
    /// between the check and the write); null for any other database error, which must propagate.
    /// </summary>
    internal static Error? Refusal(PostgresException error) => error.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation when error.ConstraintName == "deploy_target_name_key" => DeployTargetErrors.Exists,
        PostgresErrorCodes.ForeignKeyViolation when error.ConstraintName == "deploy_target_policy_revision_fk" => DeployTargetErrors.UnknownPolicyRevision,
        _ => null,
    };

    internal static async Task<Result<DeployTargetDto>> ApplyAsync(
        ITenantDb db,
        Guid orgId,
        UpdateDeployTargetCommand command,
        CancellationToken cancellationToken)
    {
        var key = new { org_id = orgId, project_id = command.ProjectId, id = command.TargetId };
        var current = await db.QuerySingleOrDefaultAsync<DeployTargetDto>(DeployTargetSql.Get, key, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Result.Failure<DeployTargetDto>(DeployTargetErrors.NotFound);
        }

        if (current.Version != command.ExpectedVersion)
        {
            return Result.Failure<DeployTargetDto>(DeployTargetErrors.Stale(current.Version));
        }

        if (command.State == DeployTargetState.Disabled && current.State == DeployTargetState.Disabled && command.Name is null && command.Order is null && !command.PolicyRevisionGiven)
        {
            return Result.Failure<DeployTargetDto>(DeployTargetErrors.AlreadyDisabled);
        }

        if (command.PolicyRevisionId is { } pin
            && await PinMissingAsync(db, orgId, command.ProjectId, pin, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<DeployTargetDto>(DeployTargetErrors.UnknownPolicyRevision);
        }

        try
        {
            var updated = await db.QuerySingleOrDefaultAsync<DeployTargetDto>(
                DeployTargetSql.Update,
                new
                {
                    org_id = orgId,
                    project_id = command.ProjectId,
                    id = command.TargetId,
                    version = command.ExpectedVersion,
                    name = command.Name,
                    position = command.Order,
                    set_pin = command.PolicyRevisionGiven,
                    policy_revision_id = command.PolicyRevisionId,
                    state = command.State,
                },
                cancellationToken).ConfigureAwait(false);
            if (updated is not null)
            {
                return Result.Success(updated);
            }
        }
        catch (PostgresException error) when (Refusal(error) is { } refusal)
        {
            return Result.Failure<DeployTargetDto>(refusal);
        }

        // The row changed between the read and the write: answer with the version that won.
        var latest = await db.QuerySingleOrDefaultAsync<DeployTargetDto>(DeployTargetSql.Get, key, cancellationToken).ConfigureAwait(false);

        return Result.Failure<DeployTargetDto>(latest is null ? DeployTargetErrors.NotFound : DeployTargetErrors.Stale(latest.Version));
    }
}
