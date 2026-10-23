using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Contracts.Validation;
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
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
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
/// burns none. Every refusal below therefore runs ahead of <see cref="AuthoringCodeAllocator.AllocateAsync"/>.
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

        if (await ManualCaseCommands.ProjectExistsAsync(db, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } missing)
        {
            return Result.Failure<CreateManualCaseResponse>(missing);
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

/// <summary>
/// Changes one manual test case (route 15, 13.1.1 §5): the <c>If-Match</c> version is compared under the row lock, an
/// absent member is unchanged, and <c>code</c>/<c>checklist_item_id</c> are refused because a move is route 19.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="CaseId">The case from the route.</param>
/// <param name="ExpectedVersion">The <c>If-Match</c> version the caller read (AC19).</param>
/// <param name="Body">The parsed request body.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record UpdateManualCaseCommand(Guid ProjectId, Guid CaseId, int ExpectedVersion, JsonElement Body, string IdempotencyKey)
    : ICommand<Result<AuthoringChangeResponse>>, IIdempotentCommand;

/// <summary>Handler of <see cref="UpdateManualCaseCommand"/>.</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, for the version row's authoring member.</param>
/// <param name="writer">The module's one write path (§5).</param>
/// <param name="steps">The steps schema validator.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.manual_case.update")]
[Permission(Permissions.Authoring.Write)]
internal sealed class UpdateManualCaseCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuthoringVersionWriter writer,
    ManualCaseStepsValidator steps,
    IAuditTrail audit) : ICommandHandler<UpdateManualCaseCommand, Result<AuthoringChangeResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AuthoringChangeResponse>> Handle(
        UpdateManualCaseCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(UpdateManualCaseCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AuthoringChangeResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        // The two members that would move or re-key the case: route 19 moves it (AC22) and the code is immutable (AC4),
        // so each is its own 422 with the member it names.
        foreach (var member in new[] { "code", "checklist_item_id" })
        {
            if (command.Body.TryGetProperty(member, out _))
            {
                return Result.Failure<AuthoringChangeResponse>(Error.Invalid(
                    $"a manual test case's {member} never changes: a move is POST …/manual-cases/{{m}}/item (13.1.1 §5)",
                    [new ErrorDetail("/" + member, "immutable_field")]));
            }
        }

        var locked = await ManualCaseCommands.LockAsync(db, orgId, command.ProjectId, command.CaseId, cancellationToken)
            .ConfigureAwait(false);

        if (locked.Row is not { } row)
        {
            return Result.Failure<AuthoringChangeResponse>(locked.Error!);
        }

        if (row.Version != command.ExpectedVersion)
        {
            return Result.Failure<AuthoringChangeResponse>(ManualCaseCommands.Stale(row.Version));
        }

        if (ManualCaseBody.OptionalTitle(command.Body, out var title, out var titleSet) is { } titleError)
        {
            return Result.Failure<AuthoringChangeResponse>(titleError);
        }

        if (ManualCaseBody.OptionalLongText(command.Body, "description", out var description, out var descriptionSet) is { } descriptionError)
        {
            return Result.Failure<AuthoringChangeResponse>(descriptionError);
        }

        if (ManualCaseBody.OptionalLongText(command.Body, "precondition", out var precondition, out var preconditionSet) is { } preconditionError)
        {
            return Result.Failure<AuthoringChangeResponse>(preconditionError);
        }

        if (ManualCaseBody.OptionalPriority(command.Body, out var priority, out var prioritySet) is { } priorityError)
        {
            return Result.Failure<AuthoringChangeResponse>(priorityError);
        }

        JsonNode? stepsDocument = null;

        if (command.Body.TryGetProperty("steps", out var rawSteps))
        {
            if (ManualCaseBody.StepsFailure(steps.ValidateSteps(rawSteps)) is { } stepsError)
            {
                return Result.Failure<AuthoringChangeResponse>(stepsError);
            }

            stepsDocument = ManualCaseBody.StepsDocument(rawSteps);
        }

        var content = JsonSerializer.SerializeToElement(AuthoringContent.ManualTestCase(
            row.ChecklistItemId,
            row.Code,
            titleSet ? title! : row.Title,
            descriptionSet ? description : row.Description,
            preconditionSet ? precondition : row.Precondition,
            stepsDocument ?? ManualCaseBody.ParseDocument(row.Steps),
            prioritySet ? priority! : row.Priority,
            row.State));

        var actor = await ManualCaseActor.MembershipIdAsync(db, principals, orgId, cancellationToken).ConfigureAwait(false);
        var written = await writer.AppendAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            [EntityChange.Update(AuthoringEntityKind.ManualTestCase, command.CaseId, content)],
            null,
            cancellationToken).ConfigureAwait(false);

        if (!written.IsSuccess)
        {
            return Result.Failure<AuthoringChangeResponse>(ManualCaseRefusal.ToError(written.Refusal!));
        }

        // AC5: a snapshot equal to the last one writes nothing and answers the version the row already had.
        var unchanged = written.Value!.UnchangedEntityIds.Contains(command.CaseId);

        if (unchanged)
        {
            audit.Suppress();
        }
        else
        {
            audit.Describe(new AuditObject("manual_test_case", command.CaseId.ToString("D")));
        }

        return Result.Success(new AuthoringChangeResponse(
            unchanged ? row.Version : written.Value.Versions[0].VersionNo,
            !unchanged));
    }
}

/// <summary>
/// Soft-deletes one manual test case (route 16, 13.1.1 §5): <c>If-Match</c> on the row, an optional bounded
/// <c>reason</c>, and the writer's own non-empty guard behind it (AC7).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="CaseId">The case from the route.</param>
/// <param name="ExpectedVersion">The <c>If-Match</c> version the caller read.</param>
/// <param name="Reason">The optional reason from the body, already bounded by the route's strict shape.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record DeleteManualCaseCommand(Guid ProjectId, Guid CaseId, int ExpectedVersion, string? Reason, string IdempotencyKey)
    : ICommand<Result>, IIdempotentCommand;

/// <summary>Handler of <see cref="DeleteManualCaseCommand"/>.</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, for the version row's authoring member.</param>
/// <param name="writer">The module's one write path (§5).</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.manual_case.delete")]
[Permission(Permissions.Authoring.Write)]
internal sealed class DeleteManualCaseCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuthoringVersionWriter writer,
    IAuditTrail audit) : ICommandHandler<DeleteManualCaseCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(DeleteManualCaseCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(DeleteManualCaseCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure(scopeError);
        }

        var orgId = scopes.Require().OrgId;
        var locked = await ManualCaseCommands.LockAsync(db, orgId, command.ProjectId, command.CaseId, cancellationToken)
            .ConfigureAwait(false);

        if (locked.Row is not { } row)
        {
            return Result.Failure(locked.Error!);
        }

        if (row.Version != command.ExpectedVersion)
        {
            return Result.Failure(ManualCaseCommands.Stale(row.Version));
        }

        var actor = await ManualCaseActor.MembershipIdAsync(db, principals, orgId, cancellationToken).ConfigureAwait(false);
        var written = await writer.AppendAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            [EntityChange.SoftDelete(AuthoringEntityKind.ManualTestCase, command.CaseId, command.Reason)],
            command.Reason is { } reason ? new AuthoringAppendContext { Reason = reason } : null,
            cancellationToken).ConfigureAwait(false);

        if (!written.IsSuccess)
        {
            return Result.Failure(ManualCaseRefusal.ToError(written.Refusal!));
        }

        audit.Describe(new AuditObject("manual_test_case", command.CaseId.ToString("D")));

        return Result.Success(StatusCodes.Status204NoContent);
    }
}

/// <summary>
/// Moves one manual test case through §6's state matrix (route 17, 13.1.1 §5, AC21): <c>draft → approved</c> with an
/// optional reason, <c>{draft, approved} → retired</c> with a required 1..500 character reason, <c>retired</c> terminal.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="CaseId">The case from the route.</param>
/// <param name="ExpectedVersion">The <c>If-Match</c> version the caller read.</param>
/// <param name="Body">The parsed request body (<c>{state, reason?}</c>).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record SetManualCaseStateCommand(Guid ProjectId, Guid CaseId, int ExpectedVersion, JsonElement Body, string IdempotencyKey)
    : ICommand<Result<SetManualCaseStateResponse>>, IIdempotentCommand;

/// <summary>Handler of <see cref="SetManualCaseStateCommand"/> (§6's manual test case matrix, AC21).</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, for the version row's authoring member.</param>
/// <param name="writer">The module's one write path (§5).</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.manual_case.state")]
[Permission(Permissions.Authoring.Write)]
internal sealed class SetManualCaseStateCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuthoringVersionWriter writer,
    IAuditTrail audit) : ICommandHandler<SetManualCaseStateCommand, Result<SetManualCaseStateResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<SetManualCaseStateResponse>> Handle(
        SetManualCaseStateCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(SetManualCaseStateCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<SetManualCaseStateResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (ManualCaseBody.RequiredState(command.Body, out var state) is { } stateError)
        {
            return Result.Failure<SetManualCaseStateResponse>(stateError);
        }

        if (ManualCaseBody.OptionalReason(command.Body, out var reason) is { } reasonError)
        {
            return Result.Failure<SetManualCaseStateResponse>(reasonError);
        }

        var locked = await ManualCaseCommands.LockAsync(db, orgId, command.ProjectId, command.CaseId, cancellationToken)
            .ConfigureAwait(false);

        if (locked.Row is not { } row)
        {
            return Result.Failure<SetManualCaseStateResponse>(locked.Error!);
        }

        if (row.Version != command.ExpectedVersion)
        {
            return Result.Failure<SetManualCaseStateResponse>(ManualCaseCommands.Stale(row.Version));
        }

        var content = JsonSerializer.SerializeToElement(AuthoringContent.ManualTestCase(
            row.ChecklistItemId,
            row.Code,
            row.Title,
            row.Description,
            row.Precondition,
            ManualCaseBody.ParseDocument(row.Steps),
            row.Priority,
            state));

        var actor = await ManualCaseActor.MembershipIdAsync(db, principals, orgId, cancellationToken).ConfigureAwait(false);
        var written = await writer.AppendAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            [EntityChange.Update(AuthoringEntityKind.ManualTestCase, command.CaseId, content)],
            reason is null ? null : new AuthoringAppendContext { Reason = reason },
            cancellationToken).ConfigureAwait(false);

        if (!written.IsSuccess)
        {
            // The writer owns §6's matrix: a refused edge is 409 invalid_transition and a retire without a reason is
            // 422 with details[].path = /reason (AC21).
            return Result.Failure<SetManualCaseStateResponse>(ManualCaseRefusal.ToError(written.Refusal!));
        }

        var unchanged = written.Value!.UnchangedEntityIds.Contains(command.CaseId);

        if (unchanged)
        {
            audit.Suppress();
        }
        else
        {
            audit.Describe(new AuditObject("manual_test_case", command.CaseId.ToString("D")));
        }

        return Result.Success(new SetManualCaseStateResponse(
            state,
            unchanged ? row.Version : written.Value.Versions[0].VersionNo));
    }
}

/// <summary>
/// Re-parents one manual test case (route 19, AC22): the target item is locked first, the case's <c>If-Match</c> is
/// compared second, and the move itself runs through <see cref="IAuthoringVersionWriter.MoveManualTestCaseAsync"/>,
/// which owns every guard (missing/deleted/other-project target, retired case or target).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="CaseId">The case from the route.</param>
/// <param name="ExpectedVersion">The <c>If-Match</c> version the caller read.</param>
/// <param name="Body">The parsed request body (<c>{checklist_item_id, reason?}</c>).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record MoveManualTestCaseCommand(Guid ProjectId, Guid CaseId, int ExpectedVersion, JsonElement Body, string IdempotencyKey)
    : ICommand<Result<MoveManualCaseResponse>>, IIdempotentCommand;

/// <summary>Handler of <see cref="MoveManualTestCaseCommand"/> (AC22).</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, for the version row's authoring member.</param>
/// <param name="writer">The module's one write path, whose move owns the guards of AC22.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.manual_case.move")]
[Permission(Permissions.Authoring.Write)]
internal sealed class MoveManualTestCaseCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuthoringVersionWriter writer,
    IAuditTrail audit) : ICommandHandler<MoveManualTestCaseCommand, Result<MoveManualCaseResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<MoveManualCaseResponse>> Handle(
        MoveManualTestCaseCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(MoveManualTestCaseCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<MoveManualCaseResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (ManualCaseBody.RequiredId(command.Body, "checklist_item_id", out var toItemId) is { } itemError)
        {
            return Result.Failure<MoveManualCaseResponse>(itemError);
        }

        if (ManualCaseBody.OptionalReason(command.Body, out var reason) is { } reasonError)
        {
            return Result.Failure<MoveManualCaseResponse>(reasonError);
        }

        // Lock order of the writer's own move: the target item first, the case second (OI-13-127). Taking them here in
        // that order is what makes two concurrent moves deadlock-free; the version comparison under the case lock turns
        // the loser into a 412 rather than a second version row (AC19).
        await db.ScalarAsync<Guid?>(
            AuthoringManualCaseReadSql.LockItem,
            new { org_id = orgId, project_id = command.ProjectId, item_id = toItemId },
            cancellationToken).ConfigureAwait(false);

        var locked = await ManualCaseCommands.LockAsync(db, orgId, command.ProjectId, command.CaseId, cancellationToken)
            .ConfigureAwait(false);

        if (locked.Row is not { } row)
        {
            return Result.Failure<MoveManualCaseResponse>(locked.Error!);
        }

        if (row.Version != command.ExpectedVersion)
        {
            return Result.Failure<MoveManualCaseResponse>(ManualCaseCommands.Stale(row.Version));
        }

        var actor = await ManualCaseActor.MembershipIdAsync(db, principals, orgId, cancellationToken).ConfigureAwait(false);
        var moved = await writer.MoveManualTestCaseAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            command.CaseId,
            toItemId,
            reason,
            null,
            cancellationToken).ConfigureAwait(false);

        if (!moved.IsSuccess)
        {
            return Result.Failure<MoveManualCaseResponse>(ManualCaseRefusal.ToError(moved.Refusal!));
        }

        if (moved.Value!.Moved)
        {
            audit.Describe(new AuditObject("manual_test_case", command.CaseId.ToString("D")));
        }
        else
        {
            // A move onto the item the case already sits under writes nothing (AC5/AC22), so there is nothing to audit.
            audit.Suppress();
        }

        return Result.Success(new MoveManualCaseResponse(moved.Value.EntityVersion, toItemId));
    }
}

/// <summary>The shared lookups of the five case commands: the project probe, the case's locked row and the stale answer.</summary>
internal static class ManualCaseCommands
{
    /// <summary>The longest reason a version row records (13.1.2's bounded reasons).</summary>
    internal const int MaximumReasonLength = 500;

    /// <summary>404 when the project of the URL is not visible to the tenant (AC10).</summary>
    /// <param name="db">The tenant-scoped Dapper seam.</param>
    /// <param name="projectId">The project from the route.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async Task<Error?> ProjectExistsAsync(ITenantDb db, Guid projectId, CancellationToken cancellationToken)
    {
        var found = await db.ScalarAsync<int?>(
            AuthoringManualCaseReadSql.ProjectExists,
            new { project_id = projectId },
            cancellationToken).ConfigureAwait(false);

        return found is null ? AuthoringRules.ProjectNotFound : null;
    }

    /// <summary>
    /// The case row locked <c>for update</c> with everything a command needs: the row on success, or the 404 to return.
    /// </summary>
    /// <param name="db">The tenant-scoped Dapper seam.</param>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="projectId">The project from the route.</param>
    /// <param name="caseId">The case from the route.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async Task<(ManualCaseRow? Row, Error? Error)> LockAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        Guid caseId,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<ManualCaseRow>(
            AuthoringManualCaseReadSql.LockCase,
            new { org_id = orgId, project_id = projectId, case_id = caseId },
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0
            ? (null, Error.NotFound("no manual test case with that id is visible to this tenant"))
            : (rows[0], null);
    }

    /// <summary>
    /// 412 <c>precondition_failed</c> for a stale <c>If-Match</c> (AC19): nothing has been written at this point and the
    /// current version travels in <c>details</c>, so a client can retry against it without a second read.
    /// </summary>
    /// <param name="currentVersion">The version the row holds now.</param>
    internal static Error Stale(int currentVersion) => new(
        ApiErrorCode.PreconditionFailed,
        "the manual test case changed since you read it; re-read it and retry",
        StatusCodes.Status412PreconditionFailed,
        Retryable: false,
        Details:
        [
            new ErrorDetail("/If-Match", "stale"),
            new ErrorDetail("/version", currentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ]);
}

/// <summary>The body readers of the case routes: one precise 422 per member instead of a single opaque refusal.</summary>
internal static class ManualCaseBody
{
    /// <summary>The members <c>POST …/manual-cases</c> accepts.</summary>
    internal static readonly string[] CreateMembers =
        ["checklist_item_id", "title", "description", "precondition", "steps", "priority"];

    /// <summary>
    /// The members <c>PATCH …/manual-cases/{m}</c> accepts — <c>code</c> and <c>checklist_item_id</c> are listed so the
    /// handler answers them with their own 422 paths (AC22/AC4) rather than as an unknown field.
    /// </summary>
    internal static readonly string[] UpdateMembers =
        ["title", "description", "precondition", "steps", "priority", "code", "checklist_item_id"];

    /// <summary>The members <c>POST …/manual-cases/{m}/state</c> accepts.</summary>
    internal static readonly string[] StateMembers = ["state", "reason"];

    /// <summary>The members <c>POST …/manual-cases/{m}/item</c> accepts.</summary>
    internal static readonly string[] MoveMembers = ["checklist_item_id", "reason"];

    /// <summary>A required uuid member.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="member">The member name.</param>
    /// <param name="id">The id on success.</param>
    internal static Error? RequiredId(JsonElement body, string member, out Guid id)
    {
        id = Guid.Empty;

        if (!body.TryGetProperty(member, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return Error.Invalid($"{member} is required", [new ErrorDetail("/" + member, "required")]);
        }

        if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out id) || id == Guid.Empty)
        {
            return Error.Invalid($"{member} must be a uuid", [new ErrorDetail("/" + member, "value_invalid")]);
        }

        return null;
    }

    /// <summary>A required 1..<paramref name="max"/> code-point title.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="title">The title on success.</param>
    internal static Error? RequiredTitle(JsonElement body, out string title)
    {
        title = string.Empty;

        if (!body.TryGetProperty("title", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return Error.Invalid("title is required", [new ErrorDetail("/title", "required")]);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return Error.Invalid("title must be a string", [new ErrorDetail("/title", "invalid_type")]);
        }

        title = value.GetString()!;

        if (AuthoringText.CodePointLength(title) == 0)
        {
            return Error.Invalid("title is required", [new ErrorDetail("/title", "required")]);
        }

        return AuthoringText.Fits(title, AuthoringContent.MaximumTitleLength)
            ? null
            : Error.Invalid("title is too long", [new ErrorDetail("/title", "too_long")]);
    }

    /// <summary>An optional title: absent is unchanged, an explicit null or an empty string is refused.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="title">The title when it was sent.</param>
    /// <param name="present">True when the member was sent.</param>
    internal static Error? OptionalTitle(JsonElement body, out string? title, out bool present)
    {
        title = null;
        present = body.TryGetProperty("title", out _);

        if (!present)
        {
            return null;
        }

        if (RequiredTitle(body, out var required) is { } error)
        {
            return error;
        }

        title = required;

        return null;
    }

    /// <summary>An optional <c>description</c>/<c>precondition</c>: absent is unchanged, null clears, a string replaces.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="member">The member name.</param>
    /// <param name="text">The value when the member was sent (null when it clears the field).</param>
    /// <param name="present">True when the member was sent.</param>
    internal static Error? OptionalLongText(JsonElement body, string member, out string? text, out bool present)
    {
        text = null;
        present = body.TryGetProperty(member, out var value);

        if (!present || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return Error.Invalid($"{member} must be a string or null", [new ErrorDetail("/" + member, "invalid_type")]);
        }

        text = value.GetString();

        return AuthoringText.Fits(text, AuthoringContent.MaximumLongTextLength)
            ? null
            : Error.Invalid($"{member} is too long", [new ErrorDetail("/" + member, "too_long")]);
    }

    /// <summary>A required priority of §3.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="priority">The priority on success.</param>
    internal static Error? RequiredPriority(JsonElement body, out string priority)
    {
        priority = string.Empty;

        if (!body.TryGetProperty("priority", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return Error.Invalid("priority is required", [new ErrorDetail("/priority", "required")]);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return Error.Invalid("priority must be a string", [new ErrorDetail("/priority", "invalid_type")]);
        }

        priority = value.GetString()!;

        return AuthoringPriorities.IsValid(priority)
            ? null
            : Error.Invalid(
                "priority is one of critical, high, medium or low (13.1.1 §3)",
                [new ErrorDetail("/priority", "value_invalid")]);
    }

    /// <summary>An optional priority: absent is unchanged.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="priority">The priority when it was sent.</param>
    /// <param name="present">True when the member was sent.</param>
    internal static Error? OptionalPriority(JsonElement body, out string? priority, out bool present)
    {
        priority = null;
        present = body.TryGetProperty("priority", out _);

        if (!present)
        {
            return null;
        }

        if (RequiredPriority(body, out var required) is { } error)
        {
            return error;
        }

        priority = required;

        return null;
    }

    /// <summary>A required <c>state</c> of §6's manual test case matrix.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="state">The state on success.</param>
    internal static Error? RequiredState(JsonElement body, out string state)
    {
        state = string.Empty;

        if (!body.TryGetProperty("state", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return Error.Invalid("state is required", [new ErrorDetail("/state", "required")]);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return Error.Invalid("state must be a string", [new ErrorDetail("/state", "invalid_type")]);
        }

        state = value.GetString()!;

        return ManualTestCaseStateMachine.States.Contains(state, StringComparer.Ordinal)
            ? null
            : Error.Invalid(
                "state is one of draft, approved or retired (13.1.1 §6)",
                [new ErrorDetail("/state", "value_invalid")]);
    }

    /// <summary>An optional 1..500 character <c>reason</c>.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="reason">The reason when it was sent and legal.</param>
    internal static Error? OptionalReason(JsonElement body, out string? reason)
    {
        reason = null;

        if (!body.TryGetProperty("reason", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return Error.Invalid("reason must be a string", [new ErrorDetail("/reason", "invalid_type")]);
        }

        reason = value.GetString();

        return AuthoringText.CodePointLength(reason) is > 0 and <= ManualCaseCommands.MaximumReasonLength
            ? null
            : Error.Invalid(
                "a reason is 1..500 characters (13.1.2)",
                [new ErrorDetail("/reason", "length_invalid")]);
    }

    /// <summary>
    /// The 422 of a steps verdict: <c>request_invalid</c> with the schema's own <c>/steps/…</c> details, never a path
    /// into the stored document and never any step text (S3).
    /// </summary>
    /// <param name="verdict">The validator's verdict.</param>
    internal static Error? StepsFailure(ContractValidationResult verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return verdict.Errors.Count == 0
            ? null
            : Error.Invalid(
                "steps do not match contracts/schemas/authoring-steps.v1.json (13.1.1 §5)",
                [.. verdict.Errors.Select(error => new ErrorDetail(error.Path, error.Reason))]);
    }

    /// <summary>The stored document of a request's <c>steps</c> array: <c>{"steps":[…]}</c>, the shape the column's CHECK requires.</summary>
    /// <param name="steps">The validated array.</param>
    internal static JsonNode? StepsDocument(JsonElement steps) => new JsonObject
    {
        ["steps"] = JsonNode.Parse(steps.GetRawText()),
    };

    /// <summary>The document a stored row already holds, parsed back for a rebuild.</summary>
    /// <param name="document">The row's <c>steps::text</c>.</param>
    internal static JsonNode? ParseDocument(string document) => string.IsNullOrEmpty(document)
        ? null
        : JsonNode.Parse(document);
}

/// <summary>The authoring member behind the caller, for the <c>actor_membership_id</c> of a version row.</summary>
internal static class ManualCaseActor
{
    /// <summary>
    /// The caller's membership of the tenant, or null when the caller is not a member (a service principal or an
    /// operator writes a system version, exactly like the writer's <c>null</c> actor).
    /// </summary>
    /// <param name="db">The tenant-scoped Dapper seam.</param>
    /// <param name="principals">The caller.</param>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async Task<Guid?> MembershipIdAsync(
        ITenantDb db,
        IRequestPrincipalAccessor principals,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        if (principals.Current is not { Kind: PrincipalKind.Member, UserId: { } userId })
        {
            return null;
        }

        return await db.ScalarAsync<Guid?>(
            AuthoringManualCaseReadSql.ActorMembership,
            new { org_id = orgId, user_id = userId },
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The writer's refusal as the 00 §9.2 error the pipeline renders, in the module's application layer.</summary>
internal static class ManualCaseRefusal
{
    /// <summary>Maps a writer refusal to its <see cref="Error"/>.</summary>
    /// <param name="refusal">The refusal.</param>
    internal static Error ToError(AuthoringRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        return new Error(
            refusal.Code,
            refusal.Message,
            refusal.Status,
            Retryable: refusal.Status == StatusCodes.Status503ServiceUnavailable,
            Details: refusal.Details is { Count: > 0 } details
                ? [.. details.Select(detail => new ErrorDetail(detail.Path, detail.Reason))]
                : null);
    }
}
