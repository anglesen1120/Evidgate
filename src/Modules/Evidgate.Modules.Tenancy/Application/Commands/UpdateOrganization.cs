using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Modules.Tenancy.Domain;
using Evidgate.Modules.Tenancy.Infrastructure.Persistence;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Tenancy.Application.Commands;

/// <summary>
/// Change an organization's settings (<c>PATCH /v1/orgs/{o}</c>, 02.1.1 §5). A null
/// field is left unchanged. <paramref name="ExpectedVersion"/> is the <c>If-Match</c>
/// version; a stale one is 412 with the current version (AC5), and the EF concurrency
/// token enforces it inside the UPDATE itself.
/// </summary>
/// <param name="OrgId">The org from the route; it must be the request's org (else 404).</param>
/// <param name="Name">New display name, or null.</param>
/// <param name="Slug">New slug, or null.</param>
/// <param name="RetentionDefaultDays">New default retention, or null.</param>
/// <param name="AllowedEmailDomains">New domain allowlist, or null.</param>
/// <param name="ExpectedVersion">The <c>version</c> the caller last read.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record UpdateOrganizationCommand(
    Guid OrgId,
    string? Name,
    string? Slug,
    int? RetentionDefaultDays,
    IReadOnlyList<string>? AllowedEmailDomains,
    int ExpectedVersion,
    string IdempotencyKey)
    : ICommand<Result<OrganizationDto>>, IIdempotentCommand;

/// <summary>Shape validation of <see cref="UpdateOrganizationCommand"/> (C2 stage 2).</summary>
internal sealed class UpdateOrganizationCommandValidator : AbstractValidator<UpdateOrganizationCommand>
{
    /// <summary>Builds the rules.</summary>
    public UpdateOrganizationCommandValidator()
    {
        RuleFor(command => command.Name)
            .Must(TenancyRules.IsName)
            .WithErrorCode("invalid_length")
            .When(command => command.Name is not null);

        RuleFor(command => command.Slug)
            .Must(TenancyRules.IsSlug)
            .WithErrorCode("invalid_format")
            .When(command => command.Slug is not null);

        RuleFor(command => command.RetentionDefaultDays)
            .InclusiveBetween(1, TenancyRules.MaxRetentionDays)
            .WithErrorCode("out_of_range")
            .When(command => command.RetentionDefaultDays is not null);

        RuleFor(command => command.AllowedEmailDomains)
            .Must(domains => domains!.Count <= TenancyRules.MaxEmailDomains)
            .WithErrorCode("too_many")
            .When(command => command.AllowedEmailDomains is not null);

        RuleForEach(command => command.AllowedEmailDomains)
            .Must(TenancyRules.IsDomain)
            .WithErrorCode("invalid_format")
            .When(command => command.AllowedEmailDomains is { Count: <= TenancyRules.MaxEmailDomains });

        RuleFor(command => command.ExpectedVersion)
            .GreaterThan(0)
            .WithErrorCode("required");
    }
}

/// <summary>
/// Handler of <see cref="UpdateOrganizationCommand"/>: 404 for an org the tenant cannot
/// see (or a <c>deleted</c> one), 412 for a stale version, 409 <c>org_suspended</c>
/// for a suspended org, 409 <c>slug_taken</c> when the new slug is used (AC2). A body
/// that changes nothing answers the current row without a write (no version bump).
/// </summary>
[Permission(Permissions.Organization.Write)]
internal sealed class UpdateOrganizationCommandHandler(
    ITenantDbContextFactory<TenancyDbContext> contexts,
    ITenantDb db,
    ITenantScopeAccessor scopes)
    : ICommandHandler<UpdateOrganizationCommand, Result<OrganizationDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<OrganizationDto>> Handle(
        UpdateOrganizationCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Tenancy, nameof(UpdateOrganizationCommand));

        if (command.OrgId != scopes.Require().OrgId)
        {
            return Result.Failure<OrganizationDto>(TenancyErrors.OrgNotFound);
        }

        await using var context = contexts.Create();

        var organization = await context.Organizations
            .SingleOrDefaultAsync(row => row.Id == command.OrgId, cancellationToken)
            .ConfigureAwait(false);

        if (organization is null || organization.Status == TenancyStatus.Deleted)
        {
            return Result.Failure<OrganizationDto>(TenancyErrors.OrgNotFound);
        }

        if (organization.Version != command.ExpectedVersion)
        {
            return Result.Failure<OrganizationDto>(TenancyErrors.Stale("organization", organization.Version));
        }

        if (organization.Status == TenancyStatus.Suspended)
        {
            return Result.Failure<OrganizationDto>(TenancyErrors.OrgSuspended);
        }

        if (command.Name is { } name)
        {
            organization.Name = name.Trim();
        }

        if (command.Slug is { } slug)
        {
            organization.Slug = slug;
        }

        if (command.RetentionDefaultDays is { } retention)
        {
            organization.RetentionDefaultDays = retention;
        }

        if (command.AllowedEmailDomains is { } domains)
        {
            // Stored normalised and de-duplicated, in the caller's order.
            organization.AllowedEmailDomains = [.. domains.Distinct(StringComparer.Ordinal)];
        }

        if (context.ChangeTracker.HasChanges())
        {
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                var latest = await TenancyReader.OrganizationAsync(db, command.OrgId, cancellationToken)
                    .ConfigureAwait(false);

                return Result.Failure<OrganizationDto>(
                    TenancyErrors.Stale("organization", latest?.Version ?? organization.Version));
            }
            catch (DbUpdateException exception) when (TenancyErrors.IsSlugConflict(exception))
            {
                return Result.Failure<OrganizationDto>(TenancyErrors.SlugTaken);
            }
        }

        var view = await TenancyReader.OrganizationAsync(db, command.OrgId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the organization {command.OrgId} was updated in this transaction yet cannot be read back");

        return Result.Success(view);
    }
}
