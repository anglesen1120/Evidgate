using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Modules.Tenancy.Domain;
using Evidgate.Modules.Tenancy.Endpoints;
using Evidgate.Modules.Tenancy.Infrastructure.Persistence;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Evidgate.Modules.Tenancy.Application.Commands;

/// <summary>
/// Create an organization (<c>POST /v1/orgs</c>, 02.1.1 §5) and make the caller its
/// first member with <c>tenant_admin</c> and <c>release_approver</c> (AC8, T98), in
/// one transaction. <paramref name="OrgId"/> is the id the endpoint derived from the
/// caller and the <c>Idempotency-Key</c> (<c>OrganizationIds.For</c>) and published as
/// the request tenant, so the app policy's <c>WITH CHECK (id = current_org_id())</c>
/// holds for the insert and a retry lands in the same tenant (AC6).
/// </summary>
/// <param name="OrgId">The new org's id; equal to the request tenant.</param>
/// <param name="Name">Display name.</param>
/// <param name="Slug">Globally unique slug (D-A1).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record CreateOrganizationCommand(Guid OrgId, string Name, string Slug, string IdempotencyKey)
    : ICommand<Result<OrganizationDto>>, IIdempotentCommand;

/// <summary>Shape validation of <see cref="CreateOrganizationCommand"/> (C2 stage 2).</summary>
internal sealed class CreateOrganizationCommandValidator : AbstractValidator<CreateOrganizationCommand>
{
    /// <summary>Builds the rules.</summary>
    public CreateOrganizationCommandValidator()
    {
        RuleFor(command => command.Name)
            .Must(TenancyRules.IsName)
            .WithErrorCode("invalid_length");

        RuleFor(command => command.Slug)
            .Must(TenancyRules.IsSlug)
            .WithErrorCode("invalid_format");
    }
}

/// <summary>
/// Handler of <see cref="CreateOrganizationCommand"/>. Gated by
/// <see cref="AuthenticatedMemberAttribute"/>, not a permission: the org does not exist
/// yet, so no membership can hold <c>organization:write</c> in it. Only a member
/// principal with a user id runs it, and the creator's membership is bound to the
/// principal's verified email — a caller without one is refused (403), because the
/// membership row cannot exist without an address (0011).
/// </summary>
[AuditedAction("organization.create")]
[AuthenticatedMember]
internal sealed class CreateOrganizationCommandHandler(
    ITenantDbContextFactory<TenancyDbContext> contexts,
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IOrganizationFounder founder,
    IAuditTrail audit)
    : ICommandHandler<CreateOrganizationCommand, Result<OrganizationDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<OrganizationDto>> Handle(
        CreateOrganizationCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Tenancy, nameof(CreateOrganizationCommand));

        if (principals.Current is not { UserId: { } userId, VerifiedEmail: { Length: > 0 } email })
        {
            return Result.Failure<OrganizationDto>(Error.Forbidden(
                "creating an organization needs a signed-in user with a verified email (02.1.1 AC8)"));
        }

        // The id must be the one derived from this caller and key, and the scope must be
        // that id: the command never creates (or founds) an org in a tenant the caller
        // did not derive (02.1.1 security review, defence in depth).
        if (command.OrgId != OrganizationIds.For(userId, command.IdempotencyKey)
            || command.OrgId != scopes.Require().OrgId)
        {
            return Result.Failure<OrganizationDto>(TenancyErrors.OrgNotFound);
        }

        await using var context = contexts.Create();

        await context.Organizations.AddAsync(
            new Organization
            {
                Id = command.OrgId,
                Name = command.Name.Trim(),
                Slug = command.Slug,
            },
            cancellationToken).ConfigureAwait(false);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (TenancyErrors.IsSlugConflict(exception))
        {
            return Result.Failure<OrganizationDto>(TenancyErrors.SlugTaken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "organization_pkey",
        })
        {
            // Same caller, same key, after the idempotency record expired (≥ 24 h): the
            // derived id already exists, and the key cannot be reused for a new org.
            return Result.Failure<OrganizationDto>(Error.Conflict(
                ApiErrorCode.IdempotencyConflict,
                "this Idempotency-Key already created an organization; send a new key"));
        }

        await founder.GrantFounderAsync(command.OrgId, userId, email, cancellationToken).ConfigureAwait(false);

        var created = await TenancyReader.OrganizationAsync(db, command.OrgId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the organization {command.OrgId} was inserted in this transaction yet cannot be read back");

        audit.Describe(new AuditObject("organization", command.OrgId.ToString("D")));

        return Result.Success(created, status: 201);
    }
}
