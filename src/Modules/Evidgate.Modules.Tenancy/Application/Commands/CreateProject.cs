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
/// Provision a project in the request's org (<c>POST /v1/orgs/{o}/projects</c>,
/// 02.1.1 §5). Slug unique per org (AC2); <c>max_projects</c> reached → 429
/// <c>quota_exceeded</c> (02.1.1-T3.2).
/// </summary>
/// <param name="OrgId">The org from the route; it must be the request's org (else 404).</param>
/// <param name="Name">Display name.</param>
/// <param name="Slug">Slug unique within the org (D-A1).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record CreateProjectCommand(Guid OrgId, string Name, string Slug, string IdempotencyKey)
    : ICommand<Result<ProjectDto>>, IIdempotentCommand;

/// <summary>Shape validation of <see cref="CreateProjectCommand"/> (C2 stage 2).</summary>
internal sealed class CreateProjectCommandValidator : AbstractValidator<CreateProjectCommand>
{
    /// <summary>Builds the rules.</summary>
    public CreateProjectCommandValidator()
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
/// Handler of <see cref="CreateProjectCommand"/>. The org row is locked first
/// (<c>TenancyReadSql.ProjectCreateGate</c>), and the quota count runs as its own
/// statement after the lock is held, which makes the check and the insert one
/// serialised step per org; a suspended org is 409 <c>org_suspended</c>.
/// </summary>
[Permission(Permissions.Project.Create)]
internal sealed class CreateProjectCommandHandler(
    ITenantDbContextFactory<TenancyDbContext> contexts,
    ITenantDb db,
    ITenantScopeAccessor scopes)
    : ICommandHandler<CreateProjectCommand, Result<ProjectDto>>
{
    /// <summary>The <c>error.details</c> path and message noun of the project limit.</summary>
    internal const string MaxProjectsLimit = "max_projects";

    /// <inheritdoc />
    public async ValueTask<Result<ProjectDto>> Handle(
        CreateProjectCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Tenancy, nameof(CreateProjectCommand));

        var orgId = scopes.Require().OrgId;

        if (command.OrgId != orgId)
        {
            return Result.Failure<ProjectDto>(TenancyErrors.OrgNotFound);
        }

        var gate = await db.QuerySingleOrDefaultAsync<ProjectCreateGateRow>(
            TenancyReadSql.ProjectCreateGate,
            new { org_id = orgId },
            cancellationToken).ConfigureAwait(false);

        if (gate is null || gate.Status == TenancyStatus.Deleted)
        {
            return Result.Failure<ProjectDto>(TenancyErrors.OrgNotFound);
        }

        if (gate.Status == TenancyStatus.Suspended)
        {
            return Result.Failure<ProjectDto>(TenancyErrors.OrgSuspended);
        }

        if (gate.MaxProjects is { } limit)
        {
            // A separate statement on purpose: it starts after the lock is held, so its
            // snapshot sees every create that committed while this one waited.
            var count = await db.ScalarAsync<long>(
                TenancyReadSql.ActiveProjectCount,
                new { org_id = orgId, max_projects = limit },
                cancellationToken).ConfigureAwait(false);

            if (count >= limit)
            {
                return Result.Failure<ProjectDto>(TenancyErrors.QuotaExceeded(MaxProjectsLimit, limit));
            }
        }

        await using var context = contexts.Create();

        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            OrgId = orgId,
            Name = command.Name.Trim(),
            Slug = command.Slug,
        };

        await context.Projects.AddAsync(project, cancellationToken).ConfigureAwait(false);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (TenancyErrors.IsSlugConflict(exception))
        {
            return Result.Failure<ProjectDto>(TenancyErrors.SlugTaken);
        }

        var created = await TenancyReader.ProjectAsync(db, project.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the project {project.Id} was inserted in this transaction yet cannot be read back");

        return Result.Success(created, status: 201);
    }
}
