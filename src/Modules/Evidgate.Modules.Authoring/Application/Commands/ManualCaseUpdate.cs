using System.Text.Json;
using System.Text.Json.Nodes;
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
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Commands;

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

        // AC10: the project's liveness before the immutability refusals and the row lock — a case of a soft-deleted
        // project is 404, not an editable row (its rows are still stored, so the case lookup alone would find it).
        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } missingProject)
        {
            return Result.Failure<AuthoringChangeResponse>(missingProject);
        }

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
            return Result.Failure<AuthoringChangeResponse>(AuthoringRequestScope.Stale("manual test case", row.Version));
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
