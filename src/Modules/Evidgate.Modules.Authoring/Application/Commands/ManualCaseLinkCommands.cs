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
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Evidgate.Modules.Catalog.Contracts;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// Links one manual test case to one catalog test case (route 20, AC11): the manual case must be live in this project
/// and the catalog case live (not <c>archived</c>) in the same project. The pair is unique, so a repeat is a 200
/// <c>changed:false</c> rather than an error, and nothing is written or audited then.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="CaseId">The manual test case from the route.</param>
/// <param name="Body">The parsed request body (<c>{test_case_id}</c>).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record LinkManualCaseCommand(Guid ProjectId, Guid CaseId, JsonElement Body, string IdempotencyKey)
    : ICommand<Result<LinkManualCaseResponse>>, IIdempotentCommand;

/// <summary>Handler of <see cref="LinkManualCaseCommand"/>.</summary>
/// <remarks>
/// The catalog is read through <see cref="ICatalogContracts"/>, never through its tables (N4): the answer distinguishes
/// an unknown case from an archived one so the caller gets the documented reason, and both are worded without echoing any
/// catalog content. The insert itself is the arbiter of the pair — <c>manual_case_link_uq</c> plus
/// <c>on conflict do nothing</c> — so two concurrent links of one pair produce one row and one <c>changed:false</c>.
/// </remarks>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, for the version row's authoring member.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="catalog">The catalog surface, for the case's liveness.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.manual_case.link")]
[Permission(Permissions.Authoring.Write)]
internal sealed class LinkManualCaseCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    ICatalogContracts catalog,
    IAuditTrail audit) : ICommandHandler<LinkManualCaseCommand, Result<LinkManualCaseResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<LinkManualCaseResponse>> Handle(
        LinkManualCaseCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(LinkManualCaseCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<LinkManualCaseResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } missingProject)
        {
            return Result.Failure<LinkManualCaseResponse>(missingProject);
        }

        if (ManualCaseBody.RequiredId(command.Body, "test_case_id", out var testCaseId) is { } bodyError)
        {
            return Result.Failure<LinkManualCaseResponse>(bodyError);
        }

        // The manual case must be live: locking it answers the 404 for an unknown or soft-deleted case and keeps a
        // concurrent delete from removing it between this read and the insert.
        var locked = await ManualCaseCommands.LockAsync(db, orgId, command.ProjectId, command.CaseId, cancellationToken)
            .ConfigureAwait(false);

        if (locked.Row is null)
        {
            return Result.Failure<LinkManualCaseResponse>(locked.Error!);
        }

        var statuses = await catalog.GetCaseStatusesAsync(orgId, command.ProjectId, [testCaseId], cancellationToken)
            .ConfigureAwait(false);

        if (!statuses.TryGetValue(testCaseId, out var status))
        {
            return Result.Failure<LinkManualCaseResponse>(AuthoringErrors.Invalid(
                "/test_case_id",
                "case_not_found",
                "no test case with that id is visible in this project"));
        }

        if (string.Equals(status, "archived", StringComparison.Ordinal))
        {
            return Result.Failure<LinkManualCaseResponse>(AuthoringErrors.Invalid(
                "/test_case_id",
                "case_archived",
                "an archived test case cannot be linked; a live case supersedes it"));
        }

        var actor = await ManualCaseActor.MembershipIdAsync(db, principals, orgId, cancellationToken).ConfigureAwait(false);
        var linkId = Guid.CreateVersion7();

        var inserted = await db.ScalarAsync<Guid?>(
            AuthoringManualCaseLinkSql.InsertLink,
            new
            {
                id = linkId,
                org_id = orgId,
                project_id = command.ProjectId,
                manual_test_case_id = command.CaseId,
                test_case_id = testCaseId,
                membership_id = actor,
            },
            cancellationToken).ConfigureAwait(false);

        if (inserted is null)
        {
            // The pair was already linked (or a concurrent request linked it first): nothing was written, so there is
            // nothing to audit and the answer is the documented no-op.
            audit.Suppress();

            return Result.Success(new LinkManualCaseResponse(null, Changed: false));
        }

        audit.Describe(new AuditObject("manual_case_link", inserted.Value.ToString("D")));

        return Result.Success(new LinkManualCaseResponse(inserted.Value, Changed: true), StatusCodes.Status201Created);
    }
}

/// <summary>
/// Removes one manual test case's link to one catalog test case (route 21). The link is addressed by the manual case and
/// the catalog case, so a caller never needs the link row's own id; a pair that is not linked is a 404, not a silent 204.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="CaseId">The manual test case from the route.</param>
/// <param name="TestCaseId">The catalog case from the route.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record UnlinkManualCaseCommand(Guid ProjectId, Guid CaseId, Guid TestCaseId, string IdempotencyKey)
    : ICommand<Result>, IIdempotentCommand;

/// <summary>Handler of <see cref="UnlinkManualCaseCommand"/>.</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.manual_case.unlink")]
[Permission(Permissions.Authoring.Write)]
internal sealed class UnlinkManualCaseCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit) : ICommandHandler<UnlinkManualCaseCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(UnlinkManualCaseCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(UnlinkManualCaseCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } missingProject)
        {
            return Result.Failure(missingProject);
        }

        // A soft-deleted case keeps its links (a restore brings its coverage back), so the case itself is probed
        // without the deleted_at predicate here; what this route refuses is a link on a case of another project or
        // tenant, which the statement's own project filter answers as a missing row.
        var removed = await db.ScalarAsync<Guid?>(
            AuthoringManualCaseLinkSql.DeleteLink,
            new
            {
                org_id = orgId,
                project_id = command.ProjectId,
                manual_test_case_id = command.CaseId,
                test_case_id = command.TestCaseId,
            },
            cancellationToken).ConfigureAwait(false);

        if (removed is null)
        {
            return Result.Failure(AuthoringErrors.NotFound(
                "this manual test case has no link to that test case in this project"));
        }

        audit.Describe(new AuditObject("manual_case_link", removed.Value.ToString("D")));

        return Result.Success(StatusCodes.Status204NoContent);
    }
}
