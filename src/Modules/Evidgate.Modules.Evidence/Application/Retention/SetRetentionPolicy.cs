using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Endpoints;
using Mediator;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Evidence.Application.Retention;

/// <summary>
/// Set a project's retention window (<c>PUT /v1/projects/{p}/retention</c>). A whole-object write: both windows are
/// always supplied, so the row is replaced rather than patched and the verb is naturally idempotent — no
/// <c>Idempotency-Key</c> and no conditional write.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="ArtifactDays">The requested artifact window, in days.</param>
/// <param name="MetadataDays">The requested metadata window, in days.</param>
internal sealed record SetRetentionPolicyCommand(Guid ProjectId, int ArtifactDays, int MetadataDays)
    : ICommand<Result<RetentionPolicy>>;

/// <summary>
/// Handler of <see cref="SetRetentionPolicyCommand"/>: the scope rule, then the configured range, then the live
/// project, then one upsert in the caller's tenant transaction.
/// <para>
/// The range rule is a <see cref="Result"/> refusal rather than a FluentValidation rule on purpose: the pipeline's
/// validation stage answers every failure with <c>request_invalid</c>, while this route's contract is
/// <c>retention_out_of_range</c> with the bound in <c>details</c>. The gate runs before any statement, so a refused
/// request never borrows a connection and never writes.
/// </para>
/// </summary>
[AuditedAction("project.retention.set")]
[Permission(Permissions.Project.Write)]
internal sealed class SetRetentionPolicyCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IOptionsMonitor<RetentionLimits> limits,
    IAuditTrail audit)
    : ICommandHandler<SetRetentionPolicyCommand, Result<RetentionPolicy>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RetentionPolicy>> Handle(
        SetRetentionPolicyCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Evidence, nameof(SetRetentionPolicyCommand));

        if (EvidenceProjectScope.Project(command.ProjectId, principals.Current?.ProjectScope, tenant.Current?.ProjectId) is { } mismatch)
        {
            return Result.Failure<RetentionPolicy>(mismatch);
        }

        if (RetentionPolicyGate.Check(command.ArtifactDays, command.MetadataDays, limits.CurrentValue) is { } refused)
        {
            return Result.Failure<RetentionPolicy>(refused);
        }

        var orgId = scopes.Require().OrgId;

        // The project row is locked for the length of the upsert, so a concurrent soft delete cannot retire the
        // project between the check and the row that points at it.
        var live = await db.ScalarAsync<int?>(
            RetentionPolicySql.ProjectExistsForShare,
            new { project_id = command.ProjectId },
            cancellationToken).ConfigureAwait(false);

        if (live is null)
        {
            return Result.Failure<RetentionPolicy>(RetentionPolicyErrors.ProjectNotFound);
        }

        var row = await db.QuerySingleOrDefaultAsync<RetentionPolicyUpsertedRow>(
            RetentionPolicySql.Upsert,
            new
            {
                id = Guid.CreateVersion7(),
                org_id = orgId,
                project_id = command.ProjectId,
                artifact_days = command.ArtifactDays,
                metadata_days = command.MetadataDays,
            },
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("the retention policy upsert returned no row");

        // Only allowlisted keys: the two values travel together under `range_days`, because the stored pair is what
        // the change means and neither value alone describes it.
        audit.Describe(new AuditObject(
            "retention_policy",
            row.Id.ToString("D"),
            Metadata: new Dictionary<string, string>
            {
                ["fields"] = "artifact_days,metadata_days",
                ["range_days"] = string.Create(
                    CultureInfo.InvariantCulture,
                    $"artifact_days={row.ArtifactDays},metadata_days={row.MetadataDays}"),
            }));

        return Result.Success(new RetentionPolicy(row.ArtifactDays, row.MetadataDays));
    }
}
