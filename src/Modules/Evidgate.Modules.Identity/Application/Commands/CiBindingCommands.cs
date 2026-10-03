using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Application;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using FluentValidation;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// <c>POST /v1/projects/{p}/ci-bindings</c> (02.4.1 AC9). The response carries the one-time challenge, so this
/// command is deliberately NOT idempotent through the response-replay store: that store persists response bodies,
/// and a challenge must never be stored in the clear (00 §5.7 S3). A retried create finds the binding and answers
/// 409 <c>binding_exists</c>; the admin disables it and creates again.
/// </summary>
internal sealed record CreateCiBindingCommand(
    Guid ProjectId,
    string Provider,
    long RepositoryId,
    string RepositoryFullName,
    IReadOnlyList<string> AllowedRefPatterns,
    IReadOnlyList<string> AllowedWorkflowRefs)
    : ICommand<Result<CiBindingCreatedResponse>>;

internal sealed class CreateCiBindingCommandValidator : AbstractValidator<CreateCiBindingCommand>
{
    public CreateCiBindingCommandValidator()
    {
        RuleFor(command => command.Provider).Must(provider => provider == "github_actions").WithErrorCode("unsupported_provider");
        RuleFor(command => command.RepositoryId).GreaterThan(0).WithErrorCode("invalid");
        RuleFor(command => command.RepositoryFullName).Must(CiBindingShapes.IsRepositoryFullName).WithErrorCode("invalid");
        RuleFor(command => command.AllowedRefPatterns).Must(patterns => Valid(patterns, CiBindingShapes.IsRefPattern)).WithErrorCode("invalid");
        RuleFor(command => command.AllowedWorkflowRefs).Must(patterns => Valid(patterns, CiBindingShapes.IsWorkflowPattern)).WithErrorCode("invalid");
    }

    internal static bool Valid(IReadOnlyList<string>? values, Func<string, bool> shape) =>
        values is { Count: > 0 and <= CiBindingShapes.MaxEntries } && values.All(value => value is not null && shape(value));
}

[Permission(Permissions.CiBinding.Write)]
[AuditedAction("ci_binding.create")]
internal sealed class CreateCiBindingCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    CiSecrets secrets,
    TimeProvider clock,
    IAuditTrail audit)
    : ICommandHandler<CreateCiBindingCommand, Result<CiBindingCreatedResponse>>
{
    public async ValueTask<Result<CiBindingCreatedResponse>> Handle(CreateCiBindingCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(CreateCiBindingCommand));

        if (!secrets.CanHash)
        {
            return Result.Failure<CiBindingCreatedResponse>(CiErrors.NotConfigured);
        }

        var challenge = IngestTokenFormat.NewChallenge();
        var (pepperVersion, hash) = secrets.HashCurrent(CiSecrets.Bytes(challenge));
        var binding = new CiBinding
        {
            Id = Guid.CreateVersion7(),
            OrgId = Guid.Empty,
            ProjectId = command.ProjectId,
            Provider = command.Provider,
            RepositoryId = command.RepositoryId,
            RepositoryFullName = command.RepositoryFullName,
            AllowedRefPatterns = [.. command.AllowedRefPatterns],
            AllowedWorkflowRefs = [.. command.AllowedWorkflowRefs],
            Status = CiBindingStatus.PendingVerification,
            BindingChallengeHash = hash,
            ChallengePepperVersion = pepperVersion,
            ChallengeIssuedAt = clock.GetUtcNow(),
            CreatedBy = principals.Current?.UserId,
        };

        await using var context = contexts.Create();
        binding.OrgId = scopes.Require().OrgId;
        await context.CiBindings.AddAsync(binding, cancellationToken).ConfigureAwait(false);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (PgErrors.IsUnique(exception, "ci_binding_project_repository_key"))
        {
            return Result.Failure<CiBindingCreatedResponse>(Error.Conflict(
                ApiErrorCode.BindingExists, "this project already binds that repository"));
        }
        catch (DbUpdateException exception) when (PgErrors.IsForeignKey(exception))
        {
            return Result.Failure<CiBindingCreatedResponse>(Error.NotFound("no such project is visible to this tenant"));
        }

        audit.Describe(new AuditObject("ci_binding", binding.Id.ToString("D")));

        return Result.Success(new CiBindingCreatedResponse(binding.ToView(), challenge), status: 201);
    }
}

/// <summary><c>PATCH /v1/ci-bindings/{b}</c> under <c>If-Match</c>: edit the allowlists, disable, or re-enable.</summary>
internal sealed record UpdateCiBindingCommand(
    Guid BindingId,
    IReadOnlyList<string>? AllowedRefPatterns,
    IReadOnlyList<string>? AllowedWorkflowRefs,
    string? Status,
    int ExpectedVersion,
    string IdempotencyKey)
    : ICommand<Result<CiBindingView>>, IIdempotentCommand;

internal sealed class UpdateCiBindingCommandValidator : AbstractValidator<UpdateCiBindingCommand>
{
    public UpdateCiBindingCommandValidator()
    {
        RuleFor(command => command.ExpectedVersion).GreaterThan(0).WithErrorCode("required");
        When(command => command.AllowedRefPatterns is not null, () =>
            RuleFor(command => command.AllowedRefPatterns)
                .Must(patterns => CreateCiBindingCommandValidator.Valid(patterns, CiBindingShapes.IsRefPattern)).WithErrorCode("invalid"));
        When(command => command.AllowedWorkflowRefs is not null, () =>
            RuleFor(command => command.AllowedWorkflowRefs)
                .Must(patterns => CreateCiBindingCommandValidator.Valid(patterns, CiBindingShapes.IsWorkflowPattern)).WithErrorCode("invalid"));
        When(command => command.Status is not null, () =>
            RuleFor(command => command.Status)
                .Must(status => status is CiBindingStatus.Disabled or CiBindingStatus.Active).WithErrorCode("unsupported_status"));
    }
}

[Permission(Permissions.CiBinding.Write)]
[AuditedAction("ci_binding.update")]
internal sealed class UpdateCiBindingCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    IAuditTrail audit)
    : ICommandHandler<UpdateCiBindingCommand, Result<CiBindingView>>
{
    public async ValueTask<Result<CiBindingView>> Handle(UpdateCiBindingCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(UpdateCiBindingCommand));

        await using var context = contexts.Create();
        var binding = await context.CiBindings
            .SingleOrDefaultAsync(row => row.Id == command.BindingId, cancellationToken)
            .ConfigureAwait(false);

        if (binding is null)
        {
            return Result.Failure<CiBindingView>(Error.NotFound("no CI binding with that id is visible to this tenant"));
        }

        if (binding.Version != command.ExpectedVersion)
        {
            return Result.Failure<CiBindingView>(CiErrors.Stale(binding.Version));
        }

        if (command.Status == CiBindingStatus.Active)
        {
            // Re-enable only a binding that proved repository control once; the challenge is never re-issued.
            if (binding.ActivatedAt is null)
            {
                return Result.Failure<CiBindingView>(Error.Conflict(
                    ApiErrorCode.InvalidTransition, "a binding that never proved repository control cannot be enabled"));
            }

            binding.Status = CiBindingStatus.Active;
        }
        else if (command.Status == CiBindingStatus.Disabled)
        {
            binding.Status = CiBindingStatus.Disabled;
        }

        if (command.AllowedRefPatterns is not null)
        {
            binding.AllowedRefPatterns = [.. command.AllowedRefPatterns];
        }

        if (command.AllowedWorkflowRefs is not null)
        {
            binding.AllowedWorkflowRefs = [.. command.AllowedWorkflowRefs];
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<CiBindingView>(CiErrors.Stale(binding.Version));
        }
        catch (DbUpdateException exception) when (PgErrors.IsUnique(exception, "ci_binding_active_repository_key"))
        {
            return Result.Failure<CiBindingView>(Error.Conflict(
                ApiErrorCode.BindingConflict, "another organization holds the active binding of that repository"));
        }

        audit.Describe(new AuditObject(
            "ci_binding",
            binding.Id.ToString("D"),
            Metadata: new Dictionary<string, string> { ["status"] = binding.Status }));

        // Re-read for the trigger-maintained version.
        await context.Entry(binding).ReloadAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(binding.ToView());
    }
}

/// <summary><c>DELETE /v1/ci-bindings/{b}</c>: disables the binding; its row stays (§6).</summary>
internal sealed record DisableCiBindingCommand(Guid BindingId, string IdempotencyKey)
    : ICommand<Result>, IIdempotentCommand;

[Permission(Permissions.CiBinding.Write)]
[AuditedAction("ci_binding.disable")]
internal sealed class DisableCiBindingCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    IAuditTrail audit)
    : ICommandHandler<DisableCiBindingCommand, Result>
{
    public async ValueTask<Result> Handle(DisableCiBindingCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(DisableCiBindingCommand));

        await using var context = contexts.Create();
        var binding = await context.CiBindings
            .SingleOrDefaultAsync(row => row.Id == command.BindingId, cancellationToken)
            .ConfigureAwait(false);

        if (binding is null)
        {
            return Result.Failure(Error.NotFound("no CI binding with that id is visible to this tenant"));
        }

        if (binding.Status == CiBindingStatus.Disabled)
        {
            audit.Suppress();

            return Result.Success(StatusCodes.Status204NoContent);
        }

        binding.Status = CiBindingStatus.Disabled;

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(Error.PreconditionFailed("the binding changed since it was read; retry"));
        }

        audit.Describe(new AuditObject("ci_binding", binding.Id.ToString("D")));

        return Result.Success(StatusCodes.Status204NoContent);
    }
}

/// <summary>Errors shared by the CI commands.</summary>
internal static class CiErrors
{
    public static Error NotConfigured { get; } = new(
        ApiErrorCode.Unavailable,
        "CI authentication is not configured on this deployment",
        StatusCodes.Status503ServiceUnavailable,
        Retryable: true);

    public static Error Stale(int currentVersion) => new(
        ApiErrorCode.PreconditionFailed,
        $"the resource changed since it was read (current version {currentVersion})",
        StatusCodes.Status412PreconditionFailed,
        Retryable: false,
        Details: [new ErrorDetail("If-Match", "stale")]);
}
