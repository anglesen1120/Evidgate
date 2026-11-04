using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
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
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// Append one project AI settings version. The request reaches this command as the parsed body the edge
/// already member-checked, so the validation order and the JSON pointers are the handler's own.
/// </summary>
/// <param name="ProjectId">The project from the route; another tenant's or another project's id is 404.</param>
/// <param name="Settings">The request's <c>settings</c> member.</param>
/// <param name="TemplateCode">The template the draft came from, or null.</param>
/// <param name="TemplateVersion">That template's version, or null.</param>
/// <param name="Reason">The caller's reason, or null.</param>
/// <param name="IfMatchVersion">The head the caller read; 0 before the first save.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record SaveAiSettingsCommand(
    Guid ProjectId,
    JsonElement Settings,
    string? TemplateCode,
    int? TemplateVersion,
    string? Reason,
    int IfMatchVersion,
    string IdempotencyKey) : ICommand<Result<AiSettingsView>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="SaveAiSettingsCommand"/>: validate, take the project's writer lock, read the head,
/// compare the precondition, then answer a no-op or append exactly one version.
/// </summary>
/// <remarks>
/// The precondition is compared <b>before</b> the content hash, so a stale tag whose document happens to
/// equal an older version still answers 412 and writes nothing. The chain is append-only and every response
/// is built from stored values, never from the request.
/// </remarks>
/// <param name="db">The tenant-scoped data access of the open command transaction.</param>
/// <param name="scopes">The request's tenant scope.</param>
/// <param name="principals">The request principal.</param>
/// <param name="tenant">The request tenant context.</param>
/// <param name="audit">The audit buffer the pipeline appends just before commit.</param>
[AuditedAction("authoring.ai_settings.update")]
[Permission(Permissions.Project.Write)]
internal sealed class SaveAiSettingsCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit) : ICommandHandler<SaveAiSettingsCommand, Result<AiSettingsView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AiSettingsView>> Handle(
        SaveAiSettingsCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(SaveAiSettingsCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AiSettingsView>(scopeError);
        }

        var (document, reason, refusal) = AiSettingsValidation.Read(command);
        if (refusal is not null || document is null)
        {
            EvidgateTelemetry.RecordAiSettingsWrite("invalid");

            return Result.Failure<AiSettingsView>(refusal!);
        }

        var orgId = scopes.Require().OrgId;

        if (await db.ScalarAsync<int?>(
                AuthoringVersionReadSql.ProjectExists,
                new { org_id = orgId, project_id = command.ProjectId },
                cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<AiSettingsView>(AuthoringRules.ProjectNotFound);
        }

        if (command.TemplateCode is { } templateCode
            && await db.ScalarAsync<int?>(
                AiSettingsSql.TemplateExists,
                new { code = templateCode, version = command.TemplateVersion },
                cancellationToken).ConfigureAwait(false) is null)
        {
            EvidgateTelemetry.RecordAiSettingsWrite("invalid");

            return Result.Failure<AiSettingsView>(AiSettingsValidation.UnknownTemplate);
        }

        var author = await AuthoringRequestScope
            .ActorMembershipAsync(db, orgId, principals, cancellationToken)
            .ConfigureAwait(false);

        if (author is null)
        {
            return Result.Failure<AiSettingsView>(Error.Forbidden(
                "saving project AI settings needs an active membership in this organization"));
        }

        await db.ExecuteAsync(
            AiSettingsSql.LockProject,
            new { lock_key = string.Create(CultureInfo.InvariantCulture, $"authoring_ai_settings:{command.ProjectId:D}") },
            cancellationToken).ConfigureAwait(false);

        var head = await db.QuerySingleOrDefaultAsync<AiSettingsSql.HeadRow>(
            AiSettingsSql.Head,
            new { org_id = orgId, project_id = command.ProjectId },
            cancellationToken).ConfigureAwait(false);

        var headVersion = head?.VersionNo ?? 0;
        if (headVersion != command.IfMatchVersion)
        {
            EvidgateTelemetry.RecordAiSettingsWrite("stale");

            return Result.Failure<AiSettingsView>(AiSettingsValidation.Stale(headVersion));
        }

        var contentHash = document.ContentHash();

        if (head is not null && string.Equals(head.ContentHash, contentHash, StringComparison.Ordinal))
        {
            return NoOp(document, reason, head, audit);
        }

        return await AppendAsync(command, document, reason, contentHash, head, orgId, author.Value, audit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A save whose canonical document equals the head is a 200 with the head's identity and no new row. Its
    /// audit entry records the outcome, so a replay and a real change stay distinguishable.
    /// </summary>
    private static Result<AiSettingsView> NoOp(
        AiSettingsDocument document,
        string? reason,
        AiSettingsSql.HeadRow head,
        IAuditTrail audit)
    {
        EvidgateTelemetry.RecordAiSettingsWrite("noop");

        audit.Describe(AiSettingsValidation.AuditObject(
            head.Id,
            head,
            AiSettingsValidation.OutcomeNoOp,
            head.VersionNo,
            head.ContentHash,
            reason));

        return Result.Success(
            new AiSettingsView(
                head.VersionNo,
                AiSettingsSources.Saved,
                head.Id,
                head.ContentHash,
                document,
                AiSettingsModels.TemplateRef(head.TemplateCode, head.TemplateVersion),
                head.AuthorMembershipId,
                head.Reason,
                head.CreatedAt),
            200);
    }

    private async ValueTask<Result<AiSettingsView>> AppendAsync(
        SaveAiSettingsCommand command,
        AiSettingsDocument document,
        string? reason,
        string contentHash,
        AiSettingsSql.HeadRow? head,
        Guid orgId,
        Guid author,
        IAuditTrail audit,
        CancellationToken cancellationToken)
    {
        var versionNo = (head?.VersionNo ?? 0) + 1;
        var row = await db.QuerySingleOrDefaultAsync<InsertedRow>(
            AiSettingsSql.InsertVersion,
            new
            {
                id = Guid.CreateVersion7(),
                org_id = orgId,
                project_id = command.ProjectId,
                version_no = versionNo,
                description = document.Description,
                environment_url = document.EnvironmentUrl,
                languages = document.Languages.ToArray(),
                writing_style = document.WritingStyle,
                coverage_strategy = document.CoverageStrategy,
                critical = document.PriorityCriteria.Critical,
                high = document.PriorityCriteria.High,
                medium = document.PriorityCriteria.Medium,
                low = document.PriorityCriteria.Low,
                exclusion_rules = document.ExclusionRules,
                step_detail = document.StepDetail,
                template_code = command.TemplateCode,
                template_version = command.TemplateVersion,
                content_hash = contentHash,
                author_membership_id = author,
                reason = reason,
            },
            cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            // The unique version key refused the append: a writer this transaction did not serialise with
            // already took the number, so the caller's tag is stale and nothing of this request is stored.
            var current = await db.QuerySingleOrDefaultAsync<AiSettingsSql.HeadRow>(
                AiSettingsSql.Head,
                new { org_id = orgId, project_id = command.ProjectId },
                cancellationToken).ConfigureAwait(false);

            EvidgateTelemetry.RecordAiSettingsWrite("stale");

            return Result.Failure<AiSettingsView>(AiSettingsValidation.Stale(current?.VersionNo ?? 0));
        }

        EvidgateTelemetry.RecordAiSettingsWrite("created");

        audit.Describe(AiSettingsValidation.AuditObject(
            row.Id,
            head,
            AiSettingsValidation.OutcomeCreated,
            versionNo,
            contentHash,
            reason));

        return Result.Success(
            new AiSettingsView(
                versionNo,
                AiSettingsSources.Saved,
                row.Id,
                contentHash,
                document,
                AiSettingsModels.TemplateRef(command.TemplateCode, command.TemplateVersion),
                author,
                reason,
                row.CreatedAt),
            201);
    }

    /// <summary>The two columns the insert returns.</summary>
    private sealed class InsertedRow
    {
        /// <summary>The new row's id.</summary>
        public Guid Id { get; init; }

        /// <summary>The row's <c>created_at</c>.</summary>
        public DateTimeOffset CreatedAt { get; init; }
    }
}
