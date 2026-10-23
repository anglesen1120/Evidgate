using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
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
/// <param name="CasePrefix">New case-code prefix, or null (13.1.1 AC4/OI-13-101).</param>
/// <param name="ExpectedVersion">The <c>version</c> the caller last read.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record UpdateProjectCommand(
    Guid ProjectId,
    string? Name,
    string? Slug,
    int? RetentionDays,
    string? CasePrefix,
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

        // 13.1.1 AC4 / OI-13-101: the migration's `project_case_prefix_format` check, answered as a 422 with the field
        // path instead of a 23514 turned into a 500. The path is the one the task fixes (`/case_prefix`), spelled here
        // because ValidationBehaviour copies FluentValidation's PropertyName verbatim (CreateRole.cs:57 precedent for the
        // override), and the reason matches the `value_invalid` of the authoring routes.
        RuleFor(command => command.CasePrefix)
            .OverridePropertyName("/case_prefix")
            .Must(TenancyRules.IsCasePrefix)
            .WithErrorCode("value_invalid")
            .When(command => command.CasePrefix is not null);

        RuleFor(command => command.ExpectedVersion)
            .GreaterThan(0)
            .WithErrorCode("required");
    }
}

/// <summary>
/// Handler of <see cref="UpdateProjectCommand"/>. The scope guard runs before any read,
/// so a token scoped to project A learns nothing about project B (AC4: 403
/// <c>project_scope_mismatch</c>); a project the tenant cannot see is 404 (AC3); then
/// 412 on a stale version (AC5), 409 <c>org_suspended</c>, 409 <c>slug_taken</c> and
/// 409 <c>case_prefix_taken</c> (13.1.1 AC4/OI-13-101).
/// </summary>
/// <remarks>
/// The route's permission is <c>project:write</c>, which the built-in catalogue grants to <c>tenant_admin</c> only
/// (<c>contracts/permissions.json</c>, 00 §2:60), so <c>case_prefix</c> — a tenant_admin-editable setting per OI-13-101 —
/// needs no check of its own: the same gate covers every field of this PATCH.
/// </remarks>
[AuditedAction("project.update")]
[Permission(Permissions.Project.Write)]
internal sealed class UpdateProjectCommandHandler(
    ITenantDbContextFactory<TenancyDbContext> contexts,
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<UpdateProjectCommand, Result<ProjectDto>>
{
    /// <summary>The EF property name of <c>project.case_prefix</c>, a shadow property (<see cref="Project"/> is unchanged).</summary>
    private const string CasePrefixProperty = "CasePrefix";

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

        if (command.CasePrefix is { } casePrefix)
        {
            // 13.1.1 AC4 / OI-13-101: `case_prefix` is mapped as a store-generated shadow property
            // (ProjectConfiguration.cs:96-107), which keeps every INSERT sending NULL so the
            // `project_case_prefix_default` trigger derives the prefix from the slug. An explicit value here turns into one
            // `update … set case_prefix = @p` in the same statement as the `version` concurrency token, so a concurrent
            // change is still a 412 and never a lost update. A change rewrites no `manual_test_case.code`: codes are
            // allocated once from the prefix in force at creation (§4), and the prefix column is their source only for
            // later allocations.
            context.Entry(project).Property(CasePrefixProperty).CurrentValue = casePrefix;
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
            catch (DbUpdateException exception) when (TenancyErrors.IsCasePrefixConflict(exception))
            {
                return Result.Failure<ProjectDto>(TenancyErrors.CasePrefixTaken);
            }
        }

        var view = await TenancyReader.ProjectAsync(db, command.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the project {command.ProjectId} was updated in this transaction yet cannot be read back");

        audit.Describe(new AuditObject("project", command.ProjectId.ToString("D")));

        return Result.Success(view);
    }
}
