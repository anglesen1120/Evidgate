using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Authorization;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Evidgate.Modules.Identity.Options;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// Change a custom role (<c>PATCH /v1/roles/{r}</c>, 02.2.1 §5): rename it and/or
/// add grants to it. Additive grants on purpose — migration 0010 revokes
/// <c>DELETE</c> on <c>role_permission</c> for the app role ("a role is archived,
/// never hard-deleted"), so V1 has no way to remove a grant and must not pretend to
/// (recorded in the 02.2.1 feature plan §3). Idempotent under the client's
/// <c>Idempotency-Key</c> (00 §9.1, TL T11).
/// </summary>
/// <param name="RoleId">The role to change, from the route.</param>
/// <param name="Name">New display name, or null to leave it unchanged.</param>
/// <param name="AddPermissions">Codes to grant in addition to the current set, or null to leave the set unchanged.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record UpdateRoleCommand(
    Guid RoleId,
    string? Name,
    IReadOnlyList<string>? AddPermissions,
    string IdempotencyKey)
    : ICommand<Result<RoleView>>, IIdempotentCommand;

/// <summary>
/// Shape validation of <see cref="UpdateRoleCommand"/> (C2 stage 2): at least one
/// field to change, and — for the ones present — the same code/name/permission
/// shapes creation enforces. Reasons and paths are lower_snake (00 §9.2).
/// </summary>
internal sealed class UpdateRoleCommandValidator : AbstractValidator<UpdateRoleCommand>
{
    /// <summary>Builds the rules.</summary>
    public UpdateRoleCommandValidator()
    {
        RuleFor(command => command)
            .Must(command => command.Name is not null || command.AddPermissions is not null)
            .WithErrorCode("required")
            .WithMessage("at least one of name or add_permissions is required");

        RuleFor(command => command.Name)
            .NotEmpty()
            .WithErrorCode("required")
            .OverridePropertyName("name")
            .When(command => command.Name is not null);

        RuleFor(command => command.Name)
            .MaximumLength(RoleInput.MaxNameLength)
            .WithErrorCode("too_long")
            .OverridePropertyName("name")
            .When(command => command.Name is not null);

        RuleFor(command => command.AddPermissions)
            .Must(permissions => permissions is null || permissions.Count <= RoleInput.MaxPermissions)
            .WithErrorCode("too_many")
            .OverridePropertyName("add_permissions");

        RuleFor(command => command.AddPermissions)
            .Must(permissions => permissions is null || permissions.All(RoleInput.IsPermissionShaped))
            .WithErrorCode("invalid_format")
            .OverridePropertyName("add_permissions");
    }
}

/// <summary>
/// Handler of <see cref="UpdateRoleCommand"/>. The order of its refusals is the
/// contract: an id the tenant cannot see is 404 (never another org's role, RLS
/// decides), a built-in role is 409 <c>builtin_role_immutable</c> whatever the flag
/// says (AC6), the flag-off state is 403, a submitted code the catalogue refuses is
/// 422 before any row changes, and a code the caller does not hold is 403
/// <c>forbidden</c> (W7-SEC-02: adding grants to a role is handing out access, so the
/// same subset rule as <c>CreateRole</c> applies to every added code).
/// </summary>
[AuditedAction("role.update")]
[Permission(Permissions.Role.Write)]
internal sealed class UpdateRoleCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    ICallerPermissionSet permissions,
    IOptions<IdentityOptions> options,
    IAuditTrail audit)
    : ICommandHandler<UpdateRoleCommand, Result<RoleView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RoleView>> Handle(
        UpdateRoleCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(UpdateRoleCommand));

        await using var context = contexts.Create();

        // RLS is the visibility rule: a built-in role or one of the caller's org
        // roles resolves, anything else reads as absent (00 §9.2: 404 for
        // another tenant's id).
        var role = await context.Roles
            .SingleOrDefaultAsync(candidate => candidate.Id == command.RoleId, cancellationToken)
            .ConfigureAwait(false);

        if (role is null)
        {
            return Result.Failure<RoleView>(Error.NotFound("no role with that id is visible to this tenant"));
        }

        if (role.IsBuiltin)
        {
            return Result.Failure<RoleView>(Error.Conflict(
                ApiErrorCode.BuiltinRoleImmutable,
                "a built-in role is immutable (02.2.1 AC6)"));
        }

        if (!options.Value.CustomRolesEnabled)
        {
            return Result.Failure<RoleView>(Error.Forbidden(
                "custom roles are disabled in V1 (EVIDGATE__IDENTITY__CUSTOMROLESENABLED, RECOMMENDATIONS 02-Q3)"));
        }

        if (command.AddPermissions is { } requested
            && await RolePermissionCodes.ValidateAsync(db, requested, cancellationToken).ConfigureAwait(false)
                is { } invalid)
        {
            return Result.Failure<RoleView>(invalid);
        }

        // W7-SEC-02: the added codes must be codes the caller already holds — a role is
        // not a way to hand the caller's own access a permission its role set lacks.
        if (command.AddPermissions is { Count: > 0 } escalated)
        {
            var missing = await SelfEscalation
                .MissingAsync(
                    permissions,
                    scopes.Require().OrgId,
                    principals.Current,
                    escalated.Distinct(StringComparer.Ordinal),
                    cancellationToken)
                .ConfigureAwait(false);

            if (missing.Count > 0)
            {
                return Result.Failure<RoleView>(SelfEscalation.Refused);
            }
        }

        if (command.Name is { } name)
        {
            role.Name = name;
        }

        if (command.AddPermissions is { } additions)
        {
            var current = await context.RolePermissions
                .Where(grant => grant.RoleId == role.Id)
                .Select(grant => grant.PermissionCode)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var existing = current.ToHashSet(StringComparer.Ordinal);

            foreach (var code in additions.Distinct(StringComparer.Ordinal).Where(code => !existing.Contains(code)))
            {
                await context.RolePermissions.AddAsync(
                    new RolePermission
                    {
                        Id = Guid.CreateVersion7(),
                        OrgId = role.OrgId,
                        RoleId = role.Id,
                        PermissionCode = code,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 00 §5.5 C4: the version token the caller's read carried is no longer
            // the row's, so the two writers cannot silently overwrite each other.
            return Result.Failure<RoleView>(Error.PreconditionFailed(
                "the role changed since it was read; re-read it and retry"));
        }

        audit.Describe(new AuditObject("role", role.Id.ToString("D")));

        return Result.Success(new RoleView(
            role.Id,
            role.OrgId,
            role.Code,
            role.Name,
            role.IsBuiltin,
            role.CreatedAt,
            role.Version));
    }
}
