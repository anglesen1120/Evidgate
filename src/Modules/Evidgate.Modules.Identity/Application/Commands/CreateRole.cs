using Evidgate.BuildingBlocks.Observability;
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
using Npgsql;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// Create a custom role in the caller's org (<c>POST /v1/roles</c>, 02.2.1 §5).
/// V1 ships the route with <c>custom_roles_enabled</c> off
/// (RECOMMENDATIONS 02-Q3), so this command answers 403 until the flag is on; the
/// flag is checked here rather than by hiding the route, because the documented
/// V1 behaviour of the route is 403 <c>forbidden</c>, not 404. Idempotent under the
/// client's <c>Idempotency-Key</c> (00 §9.1, TL T11), so a retried create replays
/// its 201 instead of colliding with its own unique <c>(org_id, code)</c>.
/// </summary>
/// <param name="Code">Role code, shape-checked at the C2 validation stage.</param>
/// <param name="Name">Display name, shape-checked at the C2 validation stage.</param>
/// <param name="Permissions">Catalogue codes to grant; validated against the catalogue here.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record CreateRoleCommand(
    string Code,
    string Name,
    IReadOnlyList<string> Permissions,
    string IdempotencyKey)
    : ICommand<Result<RoleView>>, IIdempotentCommand;

/// <summary>
/// Shape validation of <see cref="CreateRoleCommand"/> (C2 stage 2): the code, the
/// name and the permission strings, all of which the database also constrains
/// (<c>role_code_format_check</c>, <c>role_name_length_check</c>, the permission
/// shape). Reasons and paths are lower_snake, the wire spelling of 00 §9.2.
/// </summary>
internal sealed class CreateRoleCommandValidator : AbstractValidator<CreateRoleCommand>
{
    /// <summary>Builds the rules.</summary>
    public CreateRoleCommandValidator()
    {
        RuleFor(command => command.Code)
            .NotEmpty()
            .WithErrorCode("required")
            .OverridePropertyName("code");

        RuleFor(command => command.Code)
            .MaximumLength(RoleInput.MaxCodeLength)
            .WithErrorCode("too_long")
            .OverridePropertyName("code")
            .When(command => !string.IsNullOrEmpty(command.Code));

        RuleFor(command => command.Code)
            .Must(code => RoleInput.CodeShape.IsMatch(code))
            .WithErrorCode("invalid_format")
            .OverridePropertyName("code")
            .When(command => !string.IsNullOrEmpty(command.Code));

        RuleFor(command => command.Name)
            .NotEmpty()
            .WithErrorCode("required")
            .OverridePropertyName("name");

        RuleFor(command => command.Name)
            .MaximumLength(RoleInput.MaxNameLength)
            .WithErrorCode("too_long")
            .OverridePropertyName("name")
            .When(command => !string.IsNullOrEmpty(command.Name));

        RuleFor(command => command.Permissions)
            .NotNull()
            .WithErrorCode("required")
            .OverridePropertyName("permissions");

        RuleFor(command => command.Permissions)
            .Must(permissions => permissions is null || permissions.Count <= RoleInput.MaxPermissions)
            .WithErrorCode("too_many")
            .OverridePropertyName("permissions");

        RuleFor(command => command.Permissions)
            .Must(permissions => permissions is null || permissions.All(RoleInput.IsPermissionShaped))
            .WithErrorCode("invalid_format")
            .OverridePropertyName("permissions");
    }
}

/// <summary>
/// Handler of <see cref="CreateRoleCommand"/>: gate → catalogue validation →
/// self-escalation → one insert transaction (the C2 tenant behaviour owns that
/// transaction), so a rejected set writes no role. The role is an org row with
/// <c>is_builtin = false</c> and <c>kind = 'member'</c>: a custom role is a member
/// role (02.2.1 §5, 0010's <c>role_kind_check</c>), and the RLS insert policy can
/// satisfy <c>org_id = evidgate.current_org_id()</c> for nothing else (02 §4.5).
/// <para>
/// W7-SEC-02: every submitted code must be one the caller already holds, so a
/// <c>tenant_admin</c> cannot write itself the <c>artifact:read_sensitive</c> that
/// 00 §2 says it never has (the write route is <c>role:write</c>, which
/// <c>tenant_admin</c> holds). Refused with 403 <c>forbidden</c> — the existing code of
/// the route's 403 response, recorded as a [DECISION] in both feature plans.
/// </para>
/// </summary>
[Permission(Permissions.Role.Write)]
internal sealed class CreateRoleCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    ICallerPermissionSet permissions,
    IOptions<IdentityOptions> options)
    : ICommandHandler<CreateRoleCommand, Result<RoleView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RoleView>> Handle(
        CreateRoleCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(CreateRoleCommand));

        if (!options.Value.CustomRolesEnabled)
        {
            return Result.Failure<RoleView>(Error.Forbidden(
                "custom roles are disabled in V1 (EVIDGATE__IDENTITY__CUSTOMROLESENABLED, RECOMMENDATIONS 02-Q3)"));
        }

        var orgId = scopes.Require().OrgId;

        if (await RolePermissionCodes.ValidateAsync(db, command.Permissions, cancellationToken).ConfigureAwait(false)
            is { } invalid)
        {
            return Result.Failure<RoleView>(invalid);
        }

        var missing = await SelfEscalation
            .MissingAsync(
                permissions,
                orgId,
                principals.Current,
                command.Permissions.Distinct(StringComparer.Ordinal),
                cancellationToken)
            .ConfigureAwait(false);

        if (missing.Count > 0)
        {
            return Result.Failure<RoleView>(SelfEscalation.Refused);
        }

        await using var context = contexts.Create();

        // A custom role may not shadow a built-in code: the built-ins are the 00 §2
        // contract (AC1/AC6), and the partial unique index only covers the
        // org_id-null rows, so the collision is refused explicitly.
        if (await context.Roles
                .AnyAsync(role => role.OrgId == null && role.Code == command.Code, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result.Failure<RoleView>(Error.Conflict(
                ApiErrorCode.BuiltinRoleImmutable,
                "a built-in role carries that code and is immutable (02.2.1 AC6)"));
        }

        var granted = new Role
        {
            Id = Guid.CreateVersion7(),
            OrgId = orgId,
            Code = command.Code,
            Name = command.Name,
            IsBuiltin = false,
            Kind = RoleKind.Member,
        };

        await context.Roles.AddAsync(granted, cancellationToken).ConfigureAwait(false);

        foreach (var code in command.Permissions.Distinct(StringComparer.Ordinal))
        {
            await context.RolePermissions.AddAsync(
                new RolePermission
                {
                    Id = Guid.CreateVersion7(),
                    OrgId = orgId,
                    RoleId = granted.Id,
                    PermissionCode = code,
                },
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsDuplicateCode(exception))
        {
            // `unique (org_id, code)` is the arbiter, not a pre-read: two concurrent
            // creates of the same code must produce one row and one 409.
            return Result.Failure<RoleView>(Error.Conflict(
                ApiErrorCode.RoleCodeTaken,
                "the org already has a role with that code"));
        }

        return Result.Success(
            new RoleView(
                granted.Id,
                granted.OrgId,
                granted.Code,
                granted.Name,
                granted.IsBuiltin,
                granted.CreatedAt,
                granted.Version),
            status: 201);
    }

    /// <summary>True when the failure is the <c>role_org_code_key</c> unique violation (SQLSTATE 23505).</summary>
    private static bool IsDuplicateCode(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
