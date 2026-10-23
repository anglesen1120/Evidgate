using System.Globalization;
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
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Evidgate.Modules.Catalog.Application.Commands;

/// <summary>
/// Change a case's metadata (<c>PATCH /v1/cases/{c}</c>, 03.1.1 §5). A null field is left unchanged;
/// <paramref name="ExpectedVersion"/> is the <c>If-Match</c> version. Identity fields are not part of the command:
/// the endpoint rejects them with 422 <c>case_field_immutable</c> before it gets here.
/// </summary>
/// <param name="CaseId">The case from the route.</param>
/// <param name="Name">New display name, or null.</param>
/// <param name="OwnerMembershipId">New owner (an active membership of the org), or null.</param>
/// <param name="Status"><c>active</c> or <c>archived</c>, or null; <c>missing</c> is system-set.</param>
/// <param name="Labels">Replacement label set (at most 32), or null.</param>
/// <param name="ExpectedVersion">The <c>version</c> the caller last read.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record UpdateCaseCommand(
    Guid CaseId,
    string? Name,
    Guid? OwnerMembershipId,
    string? Status,
    IReadOnlyList<string>? Labels,
    int ExpectedVersion,
    string IdempotencyKey)
    : ICommand<Result<CaseDto>>, IIdempotentCommand;

/// <summary>Shape validation of <see cref="UpdateCaseCommand"/> (C2 stage 2).</summary>
internal sealed class UpdateCaseCommandValidator : AbstractValidator<UpdateCaseCommand>
{
    internal const int MaxLabels = 32;
    internal const int MaxLabelLength = 64;
    internal const int MaxNameLength = 512;

    /// <summary>Builds the rules.</summary>
    public UpdateCaseCommandValidator()
    {
        RuleFor(command => command.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxNameLength && TextRules.IsClean(name))
            .WithErrorCode("invalid_length")
            .When(command => command.Name is not null);

        RuleFor(command => command.Status)
            .Must(status => status is CaseStatus.Active or CaseStatus.Archived)
            .WithErrorCode("invalid_value")
            .When(command => command.Status is not null);

        RuleFor(command => command.OwnerMembershipId)
            .NotEqual(Guid.Empty)
            .WithErrorCode("invalid_value")
            .When(command => command.OwnerMembershipId is not null);

        RuleFor(command => command.Labels)
            .Must(labels => labels!.Count <= MaxLabels)
            .WithErrorCode("too_many")
            .When(command => command.Labels is not null);

        RuleForEach(command => command.Labels)
            .Must(label => !string.IsNullOrWhiteSpace(label) && label.Length <= MaxLabelLength && TextRules.IsClean(label))
            .WithErrorCode("invalid_length")
            .When(command => command.Labels is not null);

        RuleFor(command => command.ExpectedVersion)
            .GreaterThan(0)
            .WithErrorCode("required");
    }
}

/// <summary>
/// Handler of <see cref="UpdateCaseCommand"/>: 404 for a case the tenant cannot see (also for one outside the
/// credential's project scope), 412 on a stale version, 409 <c>invalid_transition</c> for a status move the state
/// machine of 03.1.1 §6 does not allow (<c>archived → active</c> and <c>active | missing → archived</c> only).
/// </summary>
[AuditedAction("case.update")]
[Permission(Permissions.Case.Write)]
internal sealed class UpdateCaseCommandHandler(
    ITenantDbContextFactory<CatalogDbContext> contexts,
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<UpdateCaseCommand, Result<CaseDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<CaseDto>> Handle(UpdateCaseCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(UpdateCaseCommand));

        await using var context = contexts.Create();

        var row = await context.Cases
            .SingleOrDefaultAsync(found => found.Id == command.CaseId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null || CatalogScope.Project(row.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<CaseDto>(CatalogErrors.CaseNotFound);
        }

        if (row.Version != command.ExpectedVersion)
        {
            return Result.Failure<CaseDto>(CatalogErrors.Stale("case", row.Version));
        }

        if (command.Status is { } status && status != row.Status)
        {
            var allowed = status == CaseStatus.Archived
                ? row.Status is CaseStatus.Active or CaseStatus.Missing
                : row.Status == CaseStatus.Archived;

            if (!allowed)
            {
                return Result.Failure<CaseDto>(Error.Conflict(
                    ApiErrorCode.InvalidTransition,
                    $"a {row.Status} case cannot become {status}"));
            }

            row.Status = status;
        }

        if (command.Name is { } name)
        {
            row.Name = name.Trim();
        }

        if (command.OwnerMembershipId is { } owner)
        {
            row.OwnerMembershipId = owner;
        }

        if (command.Labels is { } labels)
        {
            row.Labels = [.. labels.Select(label => label.Trim()).Distinct(StringComparer.Ordinal)];
        }

        if (context.ChangeTracker.HasChanges())
        {
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                var latest = await CaseReader.CaseAsync(db, command.CaseId, cancellationToken).ConfigureAwait(false);

                return Result.Failure<CaseDto>(CatalogErrors.Stale("case", latest?.Version ?? row.Version));
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
            {
                return Result.Failure<CaseDto>(Error.Invalid(
                    "the owner is not a member of this organization",
                    [new ErrorDetail("owner_membership_id", "unknown")]));
            }
        }

        var view = await CaseReader.CaseAsync(db, command.CaseId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"the case {command.CaseId} was updated in this transaction yet cannot be read back"));

        audit.Describe(new AuditObject("test_case", command.CaseId.ToString("D")));

        return Result.Success(view);
    }
}
