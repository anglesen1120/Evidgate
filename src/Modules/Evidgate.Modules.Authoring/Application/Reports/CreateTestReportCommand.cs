using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Application.Reports;

/// <summary>Asks for a report snapshot (<c>POST …/authoring/reports</c>); the build runs as a queue job.</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Body">The strict JSON object the route read; the handler reads it member by member.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record CreateTestReportCommand(Guid ProjectId, JsonElement Body, string IdempotencyKey)
    : ICommand<Result<TestReportJobAcceptedDto>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="CreateTestReportCommand"/>: it refuses every request the build could not serve (a scope with
/// no source, a section no builder serves, an id that is not a live item of the project) before it queues anything.
/// </summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, whose credential may be narrowed to one project.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="reader">The source read, used here only to count the selected ids.</param>
/// <param name="registry">The registered section builders.</param>
/// <param name="outbox">The outbox the build task is written to, in this request's transaction.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.report.create")]
[Permission(Permissions.Authoring.Write)]
internal sealed class CreateTestReportCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    TestReportSourceReader reader,
    ReportSectionRegistry registry,
    IOutboxWriter outbox,
    IAuditTrail audit) : ICommandHandler<CreateTestReportCommand, Result<TestReportJobAcceptedDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<TestReportJobAcceptedDto>> Handle(CreateTestReportCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(CreateTestReportCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (await RequirementGate.RefusalAsync(db, scopes, principals, tenant, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Result.Failure<TestReportJobAcceptedDto>(refusal);
        }

        var read = TestReportBody.Read(command.Body);
        if (!read.IsSuccess)
        {
            return Result.Failure<TestReportJobAcceptedDto>(read.Error!);
        }

        var request = read.Value;
        if (!ReportScopeKinds.IsSupported(request.ScopeKind))
        {
            return Result.Failure<TestReportJobAcceptedDto>(AuthoringErrors.Invalid(
                "/scope_kind", "unsupported_scope", "this report scope has no source yet"));
        }

        if (registry.FirstUnsupported(request.Sections) is { } unsupported)
        {
            return Result.Failure<TestReportJobAcceptedDto>(AuthoringErrors.Invalid(
                $"/sections/{unsupported}", "unsupported_section", "no builder serves this section"));
        }

        var orgId = scopes.Require().OrgId;
        var live = await reader.CountLiveAsync(orgId, command.ProjectId, request, cancellationToken).ConfigureAwait(false);
        if (live != request.SelectedIds.Count)
        {
            // One answer for a missing, deleted and foreign id: the refusal is no oracle for what other projects hold.
            return Result.Failure<TestReportJobAcceptedDto>(AuthoringErrors.Invalid(
                "/selected_ids", "unknown_selection", "a selected id is not a live item of this project"));
        }

        var author = await AuthoringRequestScope.ActorMembershipAsync(db, orgId, principals, cancellationToken).ConfigureAwait(false);
        var jobId = Guid.CreateVersion7();
        await TestReportTasks.EnqueueAsync(
            outbox,
            new ReportJobPayload(
                orgId,
                command.ProjectId,
                jobId,
                author,
                request.ScopeKind,
                request.SelectedIds,
                request.Sections,
                request.Language,
                request.Strategy,
                request.Environment),
            cancellationToken).ConfigureAwait(false);

        // Ids, counts and enumerated names only: the strategy and the environment are customer text.
        audit.Describe(new AuditObject(
            "test_report",
            jobId.ToString("D"),
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = request.ScopeKind,
                ["count"] = request.SelectedIds.Count.ToString(CultureInfo.InvariantCulture),
                ["fields"] = string.Join(',', request.Sections),
            },
            ProjectId: command.ProjectId));

        return Result.Success(new TestReportJobAcceptedDto(jobId), StatusCodes.Status202Accepted);
    }
}
