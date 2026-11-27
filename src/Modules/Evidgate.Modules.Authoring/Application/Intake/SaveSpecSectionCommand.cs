using System.Text.Json;
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
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>
/// Saves a selection of one ready intake's extracted text as a <c>spec_section</c> requirement
/// (<c>POST …/authoring/intakes/{i}/spec-section</c>).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="IntakeId">The intake from the route.</param>
/// <param name="Body">The strict JSON object the route read; the handler reads it member by member.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record SaveSpecSectionCommand(Guid ProjectId, Guid IntakeId, JsonElement Body, string IdempotencyKey)
    : ICommand<Result<RequirementWriteResponse>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="SaveSpecSectionCommand"/>. The text comes from the stored intake and the requirement from
/// <see cref="RequirementWriter"/>, so the body cap, the storable-text rule and the version history are the ones every
/// other requirement write has; nothing here calls a model.
/// </summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, whose credential may be narrowed to one project.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="writer">The module's requirement write path.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.requirement.create")]
[Permission(Permissions.Authoring.Write)]
internal sealed class SaveSpecSectionCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    RequirementWriter writer,
    IAuditTrail audit) : ICommandHandler<SaveSpecSectionCommand, Result<RequirementWriteResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RequirementWriteResponse>> Handle(
        SaveSpecSectionCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(SaveSpecSectionCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (await RequirementGate.RefusalAsync(db, scopes, principals, tenant, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Result.Failure<RequirementWriteResponse>(refusal);
        }

        var read = SpecSectionBody.Read(command.Body);
        if (!read.IsSuccess)
        {
            return Result.Failure<RequirementWriteResponse>(read.Error!);
        }

        var fields = read.Value;
        var orgId = scopes.Require().OrgId;
        var selected = await SelectAsync(command, fields, orgId, cancellationToken).ConfigureAwait(false);
        if (!selected.IsSuccess)
        {
            return Result.Failure<RequirementWriteResponse>(selected.Error!);
        }

        var author = await AuthoringRequestScope.ActorMembershipAsync(db, orgId, principals, cancellationToken).ConfigureAwait(false);
        var created = await writer.CreateAsync(
            orgId,
            new RequirementCreate(
                command.ProjectId,
                "spec_section",
                fields.Title,
                fields.ExternalRef,
                fields.FeatureNodeId,
                fields.ParentRequirementId,
                selected.Value,
                "import",
                author,
                fields.Reason),
            cancellationToken).ConfigureAwait(false);

        if (!created.IsSuccess)
        {
            return Result.Failure<RequirementWriteResponse>(created.Error!);
        }

        audit.Describe(RequirementAudit.Created(command.ProjectId, created.Value));

        return Result.Success(RequirementResponses.Write(created.Value), StatusCodes.Status201Created);
    }

    /// <summary>The body to store: the intake's selection, judged against its stored text.</summary>
    private async Task<Result<string>> SelectAsync(
        SaveSpecSectionCommand command,
        SpecSectionFields fields,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var row = await db.QuerySingleOrDefaultAsync<IntakeSelectionRow>(
            AuthoringIntakeSql.Selection,
            new
            {
                org_id = orgId,
                project_id = command.ProjectId,
                id = command.IntakeId,
                start_at = (long)(fields.Start ?? 0),
                end_at = (long?)fields.End,
            },
            cancellationToken).ConfigureAwait(false);

        // A foreign or unknown intake reads exactly like the status route's miss, so ids stay unprobeable.
        if (row is null)
        {
            return Result.Failure<string>(AuthoringErrors.NotFound("no intake with that id is visible in this project"));
        }

        if (row.State != "ready" || row.CharCount is not { } characters || row.Selection is null)
        {
            return Result.Failure<string>(AuthoringErrors.InvalidTransition("the intake has no extracted text to save yet"));
        }

        return SpecSectionBody.Select(fields, characters, row.Selection);
    }
}

/// <summary>The members of a spec-section save; <paramref name="Start"/> and <paramref name="End"/> are code-point offsets.</summary>
/// <param name="Title">The requirement title.</param>
/// <param name="Start">The first selected code point, or null for the start of the text.</param>
/// <param name="End">One past the last selected code point, or null for the end of the text.</param>
/// <param name="Reason">The optional reason recorded on the first version.</param>
/// <param name="ExternalRef">The optional catalog label.</param>
/// <param name="FeatureNodeId">The optional feature node of the same project.</param>
/// <param name="ParentRequirementId">The optional parent requirement of the same project.</param>
internal sealed record SpecSectionFields(
    string Title,
    int? Start,
    int? End,
    string? Reason,
    string? ExternalRef,
    Guid? FeatureNodeId,
    Guid? ParentRequirementId);

/// <summary>The row of <see cref="AuthoringIntakeSql.Selection"/>.</summary>
internal sealed class IntakeSelectionRow
{
    /// <summary>The intake state.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Code points of the whole extracted text, null without one.</summary>
    public int? CharCount { get; set; }

    /// <summary>The selected code points, clamped to the text.</summary>
    public string? Selection { get; set; }
}

/// <summary>Reads the body of a spec-section save and judges the selected range against the stored text.</summary>
internal static class SpecSectionBody
{
    /// <summary>The members of <c>POST …/intakes/{i}/spec-section</c>.</summary>
    internal static readonly string[] Members =
        ["title", "start", "end", "reason", "external_ref", "feature_node_id", "parent_requirement_id"];

    /// <summary>Reads the members one by one, so each type mistake has its own 422 path.</summary>
    /// <param name="body">The strict JSON object the route read.</param>
    internal static Result<SpecSectionFields> Read(JsonElement body)
    {
        if (RequirementBody.Text(body, "title", false, out var title) is { } titleError)
        {
            return Result.Failure<SpecSectionFields>(titleError);
        }

        if (title is null)
        {
            return Result.Failure<SpecSectionFields>(RequirementBody.Required("title"));
        }

        if (Offset(body, "start", out var start) is { } startError)
        {
            return Result.Failure<SpecSectionFields>(startError);
        }

        if (Offset(body, "end", out var end) is { } endError)
        {
            return Result.Failure<SpecSectionFields>(endError);
        }

        if (RequirementBody.Text(body, "reason", true, out var reason) is { } reasonError)
        {
            return Result.Failure<SpecSectionFields>(reasonError);
        }

        if (RequirementBody.Text(body, "external_ref", true, out var externalRef) is { } refError)
        {
            return Result.Failure<SpecSectionFields>(refError);
        }

        if (RequirementBody.Id(body, "feature_node_id", out var node) is { } nodeError)
        {
            return Result.Failure<SpecSectionFields>(nodeError);
        }

        if (RequirementBody.Id(body, "parent_requirement_id", out var parent) is { } parentError)
        {
            return Result.Failure<SpecSectionFields>(parentError);
        }

        return Result.Success(new SpecSectionFields(title, start, end, reason, externalRef, node, parent));
    }

    /// <summary>
    /// The body to store: the range must lie inside the text and select something, and the selection after NFC must fit
    /// the requirement body cap. Offsets count code points, the unit the intake status reports its length in.
    /// </summary>
    /// <param name="fields">The members the caller sent.</param>
    /// <param name="characters">Code points of the whole extracted text.</param>
    /// <param name="selection">The code points the SQL clamped to the range.</param>
    internal static Result<string> Select(SpecSectionFields fields, int characters, string selection)
    {
        var start = fields.Start ?? 0;
        var end = fields.End ?? characters;
        if (start < 0 || start >= characters)
        {
            return Failure("/start", "range_invalid", "start must be inside the extracted text");
        }

        if (end < 0 || end > characters || end <= start)
        {
            return Failure("/end", "range_invalid", "end must be after start and inside the extracted text");
        }

        if (!RequirementRules.TryNormalizeBody(selection, out var normalized))
        {
            return Failure("/end", "length", "the selection is over the 20 000 code-point body cap");
        }

        return string.IsNullOrWhiteSpace(normalized)
            ? Failure("/start", "empty_selection", "the selection holds only whitespace")
            : Result.Success(normalized);
    }

    private static Error? Offset(JsonElement body, string member, out int? value)
    {
        value = null;
        if (!body.TryGetProperty(member, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number))
        {
            return Error.Invalid($"{member} must be an integer", [new ErrorDetail("/" + member, "invalid_type")]);
        }

        value = number;

        return null;
    }

    private static Result<string> Failure(string path, string reason, string message) =>
        Result.Failure<string>(AuthoringErrors.Invalid(path, reason, message));
}
