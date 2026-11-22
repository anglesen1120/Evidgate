using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// Creates one manual test case (route 13, 13.1.1 §5): it validates the body, allocates the case's
/// <c>code</c> in its own short transaction and then appends the case through the version writer.
/// </summary>
/// <remarks>
/// The order of the two writes is AC4's and is not interchangeable: the allocator commits its own connection before the
/// request transaction ends, so a create that fails afterwards burns its number (the plan's notes at :519 accept that as
/// the price of a lock-free counter), and a create whose steps are invalid must fail <em>before</em> the allocation so it
/// burns none. Every refusal below therefore runs ahead of <see cref="AuthoringCodeAllocator.AllocateAsync"/>: the body
/// shape, the steps schema and the target item's placement probe (the writer's own
/// <c>CaseParentRefusalAsync</c> answer for an unknown, deleted or retired item).
/// </remarks>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Body">The parsed request body; its members are validated by the handler so one bad member cannot hide another.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1, AC18).</param>
internal sealed record CreateManualCaseCommand(Guid ProjectId, JsonElement Body, string IdempotencyKey)
    : ICommand<Result<CreateManualCaseResponse>>, IIdempotentCommand;

/// <summary>Handler of <see cref="CreateManualCaseCommand"/>.</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, for the version row's authoring member.</param>
/// <param name="allocator">The per-project code allocator of §4.</param>
/// <param name="writer">The module's one write path (§5).</param>
/// <param name="steps">The validator of <c>contracts/schemas/authoring-steps.v1.json</c>.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.manual_case.create")]
[Permission(Permissions.Authoring.Write)]
internal sealed class CreateManualCaseCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    AuthoringCodeAllocator allocator,
    IAuthoringVersionWriter writer,
    ManualCaseStepsValidator steps,
    IAuditTrail audit) : ICommandHandler<CreateManualCaseCommand, Result<CreateManualCaseResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<CreateManualCaseResponse>> Handle(
        CreateManualCaseCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(CreateManualCaseCommand));

        ArgumentNullException.ThrowIfNull(command);

        // AuthoringRules.Project first: a project-scoped credential addressing a sibling project of the same tenant is
        // refused here, because RLS scopes rows per org and the permission check does not see the project.
        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<CreateManualCaseResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } missingProject)
        {
            return Result.Failure<CreateManualCaseResponse>(missingProject);
        }

        if (ManualCaseBody.RequiredId(command.Body, "checklist_item_id", out var itemId) is { } itemError)
        {
            return Result.Failure<CreateManualCaseResponse>(itemError);
        }

        if (ManualCaseBody.RequiredTitle(command.Body, out var title) is { } titleError)
        {
            return Result.Failure<CreateManualCaseResponse>(titleError);
        }

        if (ManualCaseBody.OptionalLongText(command.Body, "description", out var description, out _) is { } descriptionError)
        {
            return Result.Failure<CreateManualCaseResponse>(descriptionError);
        }

        if (ManualCaseBody.OptionalLongText(command.Body, "precondition", out var precondition, out _) is { } preconditionError)
        {
            return Result.Failure<CreateManualCaseResponse>(preconditionError);
        }

        if (ManualCaseBody.RequiredPriority(command.Body, out var priority) is { } priorityError)
        {
            return Result.Failure<CreateManualCaseResponse>(priorityError);
        }

        // AC4: the steps are validated against authoring-steps.v1.json before the allocator runs, so a create that the
        // schema refuses burns no number. `ManualCaseStepsValidator.ValidateSteps` wraps the array into the stored
        // document shape and rebases every detail onto `/steps/…`.
        var stepsValue = command.Body.TryGetProperty("steps", out var rawSteps) ? rawSteps : (JsonElement?)null;

        if (ManualCaseBody.StepsFailure(steps.ValidateSteps(stepsValue)) is { } stepsError)
        {
            return Result.Failure<CreateManualCaseResponse>(stepsError);
        }

        // AC4: the target item is probed before the allocation for the same reason the steps above are — a create the
        // writer would refuse (unknown or other-project item, deleted item, retired item) must burn no code. The
        // refusal is the writer's own (CheckCaseParentAsync, same code, reason and member path), and the writer still
        // decides it again under its locks, which is what makes this probe an optimisation rather than a second rule.
        if (await ManualCaseCommands.CaseParentRefusalAsync(
                db, orgId, command.ProjectId, itemId, cancellationToken).ConfigureAwait(false) is { } parentError)
        {
            return Result.Failure<CreateManualCaseResponse>(parentError);
        }

        var actor = await ManualCaseActor.MembershipIdAsync(db, principals, orgId, cancellationToken).ConfigureAwait(false);
        var allocated = await allocator.AllocateAsync(orgId, command.ProjectId, cancellationToken).ConfigureAwait(false);

        if (!allocated.IsSuccess)
        {
            // 409 quota_exceeded (counter exhausted) and 503 unavailable (lock timeout) already carry their status and
            // Retry-After; the route surfaces them unchanged.
            return Result.Failure<CreateManualCaseResponse>(allocated.Error!);
        }

        var caseId = Guid.CreateVersion7();
        var content = JsonSerializer.SerializeToElement(AuthoringContent.ManualTestCase(
            itemId,
            allocated.Value.Value,
            title,
            description,
            precondition,
            ManualCaseBody.StepsDocument(rawSteps),
            priority,
            ManualTestCaseStateMachine.NewCaseState));

        var written = await writer.AppendAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            [EntityChange.Create(AuthoringEntityKind.ManualTestCase, caseId, content)],
            null,
            cancellationToken).ConfigureAwait(false);

        if (!written.IsSuccess)
        {
            return Result.Failure<CreateManualCaseResponse>(ManualCaseRefusal.ToError(written.Refusal!));
        }

        audit.Describe(new AuditObject("manual_test_case", caseId.ToString("D")));

        return Result.Success(
            new CreateManualCaseResponse(caseId, allocated.Value.Value, written.Value!.Versions[0].VersionNo),
            StatusCodes.Status201Created);
    }
}
