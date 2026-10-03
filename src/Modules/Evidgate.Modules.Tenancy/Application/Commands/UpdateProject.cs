using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
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
/// Change a project's settings (<c>PATCH /v1/projects/{p}</c>, 02.1.1 §5, T21). A null
/// field is left unchanged; <paramref name="ExpectedVersion"/> is the <c>If-Match</c>
/// version (AC5).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Name">New display name, or null.</param>
/// <param name="Slug">New slug, or null.</param>
/// <param name="RetentionDays">New retention override, or null.</param>
/// <param name="ExpectedVersion">The <c>version</c> the caller last read.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record UpdateProjectCommand(
    Guid ProjectId,
    string? Name,
    string? Slug,
    int? RetentionDays,
    int ExpectedVersion,
    string IdempotencyKey)
    : ICommand<Result<ProjectDto>>, IIdempotentCommand;

/// <summary>Shape validation of <see cref="UpdateProjectCommand"/> (C2 stage 2).</summary>
internal sealed class UpdateProjectCommandValidator : AbstractValidator<UpdateProjectCommand>
{
    /// <summary>Builds the rules.</summary>
    public UpdateProjectCommandValidator()
    {
        RuleFor(command => command.Name)
            .Must(TenancyRules.IsName)
            .WithErrorCode("invalid_length")
            .When(command => command.Name is not null);

        RuleFor(command => command.Slug)
            .Must(TenancyRules.IsSlug)
            .WithErrorCode("invalid_format")
            .When(command => command.Slug is not null);

        RuleFor(command => command.RetentionDays)
            .InclusiveBetween(1, TenancyRules.MaxRetentionDays)
            .WithErrorCode("out_of_range")
            .When(command => command.RetentionDays is not null);

        RuleFor(command => command.ExpectedVersion)
            .GreaterThan(0)
            .WithErrorCode("required");
    }
}

/// <summary>
/// Handler of <see cref="UpdateProjectCommand"/>. The scope guard runs before any read,
/// so a token scoped to project A learns nothing about project B (AC4: 403
/// <c>project_scope_mismatch</c>); a project the tenant cannot see is 404 (AC3); then
/// 412 on a stale version (AC5), 409 <c>org_suspended</c> and 409 <c>slug_taken</c>.
/// </summary>
[Permission(Permissions.Project.Write)]
internal sealed class UpdateProjectCommandHandler(
    ITenantDbContextFactory<TenancyDbContext> contexts,
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : ICommandHandler<UpdateProjectCommand, Result<ProjectDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<ProjectDto>> Handle(
        UpdateProjectCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Tenancy, nameof(UpdateProjectCommand));

        if (ScopeGuard.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<ProjectDto>(mismatch);
        }

        await using var context = contexts.Create();

        var project = await context.Projects
            .SingleOrDefaultAsync(row => row.Id == command.ProjectId, cancellationToken)
            .ConfigureAwait(false);

        if (project is null || project.Status == TenancyStatus.Deleted)
        {
            return Result.Failure<ProjectDto>(TenancyErrors.ProjectNotFound);
        }

        if (project.Version != command.ExpectedVersion)
        {
            return Result.Failure<ProjectDto>(TenancyErrors.Stale("project", project.Version));
        }

        var orgStatus = await db.ScalarAsync<string>(
            TenancyReadSql.OrganizationStatus,
            new { org_id = project.OrgId },
            cancellationToken).ConfigureAwait(false);

        if (orgStatus == TenancyStatus.Suspended)
        {
            return Result.Failure<ProjectDto>(TenancyErrors.OrgSuspended);
        }

        if (command.Name is { } name)
        {
            project.Name = name.Trim();
        }

        if (command.Slug is { } slug)
        {
            project.Slug = slug;
        }

        if (command.RetentionDays is { } retention)
        {
            project.RetentionDays = retention;
        }

        if (context.ChangeTracker.HasChanges())
        {
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                var latest = await TenancyReader.ProjectAsync(db, command.ProjectId, cancellationToken)
                    .ConfigureAwait(false);

                return Result.Failure<ProjectDto>(TenancyErrors.Stale("project", latest?.Version ?? project.Version));
            }
            catch (DbUpdateException exception) when (TenancyErrors.IsSlugConflict(exception))
            {
                return Result.Failure<ProjectDto>(TenancyErrors.SlugTaken);
            }
        }

        var view = await TenancyReader.ProjectAsync(db, command.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the project {command.ProjectId} was updated in this transaction yet cannot be read back");

        return Result.Success(view);
    }
}
