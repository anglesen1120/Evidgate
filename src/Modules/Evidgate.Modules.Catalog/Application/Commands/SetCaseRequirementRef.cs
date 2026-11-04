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

namespace Evidgate.Modules.Catalog.Application.Commands;

/// <summary>
/// Sets or clears the external requirement pointer of one case. The pointer is metadata: it is not part of any
/// revision, so this command appends no <c>test_revision</c> and leaves every stored hash unchanged.
/// </summary>
/// <param name="ProjectId">The project the route names; a case outside it is not found.</param>
/// <param name="CaseId">The case from the route.</param>
/// <param name="RequirementRef">The pointer to store, or null to clear it.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record SetCaseRequirementRefCommand(
    Guid ProjectId,
    Guid CaseId,
    string? RequirementRef,
    string IdempotencyKey)
    : ICommand<Result<CaseDto>>, IIdempotentCommand;

/// <summary>Shape validation of <see cref="SetCaseRequirementRefCommand"/> (C2 stage 2).</summary>
internal sealed class SetCaseRequirementRefCommandValidator : AbstractValidator<SetCaseRequirementRefCommand>
{
    /// <summary>Builds the rules; null means "clear", any other value must be a usable pointer.</summary>
    public SetCaseRequirementRefCommandValidator()
    {
        // One refusal reason per value: the size rule and the character rule are mutually exclusive because
        // Taxonomy.RequirementRefRefusal names a single reason for any value, so a 422 carries exactly one detail.
        RuleFor(command => command.RequirementRef)
            .Must(raw => Taxonomy.RequirementRefRefusal(raw!) != "invalid_length")
            .WithErrorCode("invalid_length")
            .OverridePropertyName("requirement_ref")
            .When(command => command.RequirementRef is not null);

        RuleFor(command => command.RequirementRef)
            .Must(raw => Taxonomy.RequirementRefRefusal(raw!) != "invalid_value")
            .WithErrorCode("invalid_value")
            .OverridePropertyName("requirement_ref")
            .When(command => command.RequirementRef is not null);
    }
}

/// <summary>
/// Handler of <see cref="SetCaseRequirementRefCommand"/>: 404 for a case the tenant cannot see, for one in another
/// project, and for one outside the credential's project scope; a concurrent write of the same row is 412.
/// </summary>
[AuditedAction("case.requirement_ref")]
[Permission(Permissions.Case.Write)]
internal sealed class SetCaseRequirementRefCommandHandler(
    ITenantDbContextFactory<CatalogDbContext> contexts,
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<SetCaseRequirementRefCommand, Result<CaseDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<CaseDto>> Handle(SetCaseRequirementRefCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(SetCaseRequirementRefCommand));

        // Null clears; every other value was validated, so a failure here would be a bug rather than a client error.
        string? value = null;
        if (command.RequirementRef is not null && !Taxonomy.TryRequirementRef(command.RequirementRef, out value))
        {
            return Result.Failure<CaseDto>(Error.Invalid(
                "the requirement reference is not a trimmed 1..128 character value of A-Z a-z 0-9 . _ : / # -",
                [new ErrorDetail("requirement_ref", Taxonomy.RequirementRefRefusal(command.RequirementRef) ?? "invalid_value")]));
        }

        await using var context = contexts.Create();

        var row = await context.Cases
            .SingleOrDefaultAsync(found => found.Id == command.CaseId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null
            || row.ProjectId != command.ProjectId
            || CatalogScope.Project(row.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<CaseDto>(CatalogErrors.CaseNotFound);
        }

        if (!string.Equals(row.RequirementRef, value, StringComparison.Ordinal))
        {
            row.RequirementRef = value;

            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                var latest = await CaseReader.CaseAsync(db, command.CaseId, cancellationToken).ConfigureAwait(false);

                return Result.Failure<CaseDto>(CatalogErrors.Stale("case", latest?.Version ?? row.Version));
            }
        }

        var view = await CaseReader.CaseAsync(db, command.CaseId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"the case {command.CaseId} was updated in this transaction yet cannot be read back"));

        audit.Describe(new AuditObject("test_case", command.CaseId.ToString("D")));

        return Result.Success(view);
    }
}
