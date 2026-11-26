using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Application.Intake.Jobs;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>
/// Creates one requirement intake (route 1): the body is judged first, then the row is written <c>received</c> and the
/// kind decides what follows. Text is normalised and stored ready in the same call, an image is a stored reference with
/// nothing to extract, and a document enqueues its extraction task in this same transaction.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Body">The parsed body; its members are judged by the handler against the intake schema.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record CreateIntakeCommand(Guid ProjectId, JsonElement Body, string IdempotencyKey)
    : ICommand<Result<CreateIntakeResponse>>, IIdempotentCommand;

/// <summary>Handler of <see cref="CreateIntakeCommand"/>.</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, for the creating membership.</param>
/// <param name="tenant">The request's tenant context, for the project scope check.</param>
/// <param name="planner">The refusals that precede any write.</param>
/// <param name="caps">The caps in force.</param>
/// <param name="outbox">The outbox the extraction task is written to.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.intake.create")]
[Permission(Permissions.Authoring.Generate)]
internal sealed class CreateIntakeCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IntakePlanner planner,
    IntakeCapsResolver caps,
    IOutboxWriter outbox,
    IAuditTrail audit) : ICommandHandler<CreateIntakeCommand, Result<CreateIntakeResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<CreateIntakeResponse>> Handle(CreateIntakeCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(CreateIntakeCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<CreateIntakeResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } missing)
        {
            return Result.Failure<CreateIntakeResponse>(missing);
        }

        var limits = caps.Current;
        var (plan, refusal) = await planner.PlanAsync(orgId, command.ProjectId, command.Body, limits, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return Result.Failure<CreateIntakeResponse>(refusal!);
        }

        var id = Guid.CreateVersion7();
        var actor = await ManualCaseActor.MembershipIdAsync(db, principals, orgId, cancellationToken).ConfigureAwait(false);
        int version;
        try
        {
            version = await WriteAsync(orgId, command.ProjectId, id, plan, actor, limits, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "authoring_intake_artifact_key")
        {
            // Two requests for one artifact raced past the probe; the unique key decides and the loser is the duplicate.
            return Result.Failure<CreateIntakeResponse>(Error.Conflict(ApiErrorCode.IdempotencyConflict, "an intake already exists for this artifact"));
        }

        var state = plan.Kind is "docx" or "pdf" ? "received" : "ready";
        audit.Describe(new AuditObject(
            "authoring_intake",
            id.ToString("D"),
            Metadata: new Dictionary<string, string> { ["format"] = plan.Kind, ["status"] = state },
            ProjectId: command.ProjectId));
        IntakeJobMetrics.Record(plan.Kind, state);

        using var capsDocument = JsonDocument.Parse(IntakeCapsJson.Serialize(limits));
        return Result.Success(new CreateIntakeResponse(id, state, capsDocument.RootElement.Clone(), version), StatusCodes.Status201Created);
    }

    private async Task<int> WriteAsync(
        Guid orgId,
        Guid projectId,
        Guid id,
        IntakePlan plan,
        Guid? actor,
        IntakeCaps limits,
        CancellationToken cancellationToken)
    {
        var version = await db.ScalarAsync<int>(
            AuthoringIntakeSql.Insert,
            new
            {
                id,
                org_id = orgId,
                project_id = projectId,
                feature_node_id = plan.NodeId,
                kind = plan.Kind,
                artifact_id = plan.ArtifactId,
                input_text = plan.InputText,
                media_type = plan.MediaType,
                declared_bytes = plan.DeclaredBytes,
                caps = IntakeCapsJson.Serialize(limits),
                created_by = actor,
            },
            cancellationToken).ConfigureAwait(false);

        switch (plan.Kind)
        {
            case "text":
                return await db.ScalarAsync<int>(
                    AuthoringIntakeSql.CompleteText,
                    new { org_id = orgId, id, text = plan.InputText, hash = IntakeText.Hash(plan.InputText!) },
                    cancellationToken).ConfigureAwait(false);
            case "image":
                return await db.ScalarAsync<int>(AuthoringIntakeSql.CompleteImage, new { org_id = orgId, id }, cancellationToken).ConfigureAwait(false);
            default:
                await IntakeExtractionQueue.EnqueueAsync(outbox, id, cancellationToken).ConfigureAwait(false);
                return version;
        }
    }
}
