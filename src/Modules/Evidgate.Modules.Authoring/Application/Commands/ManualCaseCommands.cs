using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>The shared lookups of the five case commands: the create's parent probe, the case's locked row and the stale answer.</summary>
internal static class ManualCaseCommands
{
    /// <summary>The longest reason a version row records (13.1.2's bounded reasons).</summary>
    internal const int MaximumReasonLength = 500;

    /// <summary>
    /// The refusal of a create whose <c>checklist_item_id</c> the writer would reject, worded exactly as the writer words
    /// it (<c>AuthoringVersionWriter.CheckCaseParentAsync</c>; the same message guards the AC22 move, under a different
    /// code). The edge answers it before the code allocation, so both call sites must read the same.
    /// </summary>
    internal const string RetiredItemMessage = "a retired checklist item takes no new content; restore it first";

    /// <summary>
    /// The create's placement probe (AC4): the target item's liveness and state, answered with the writer's own refusal
    /// so a create that could never be written spends no code.
    /// </summary>
    /// <remarks>
    /// <see cref="AuthoringCodeAllocator"/> commits its number on its own connection, so a create refused afterwards
    /// burns it (§4's accepted price for a lock-free counter). This read repeats the writer's decision early; the
    /// writer decides again under its locks, which stays authoritative.
    /// </remarks>
    /// <param name="db">The tenant-scoped Dapper seam.</param>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="projectId">The project from the route.</param>
    /// <param name="itemId">The <c>checklist_item_id</c> of the request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Null when the item can take the case, else the writer's refusal.</returns>
    internal static async Task<Error?> CaseParentRefusalAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var placement = await db.ScalarAsync<string>(
            AuthoringManualCaseReadSql.CaseParent,
            new { org_id = orgId, project_id = projectId, item_id = itemId },
            cancellationToken).ConfigureAwait(false);

        return placement switch
        {
            // Another project's item and a missing one are the same answer (AC10): the statement carries the project.
            null => AuthoringErrors.ParentInvalid(PlacementReason.ParentNotFound, "/checklist_item_id"),
            "deleted" => AuthoringErrors.ParentInvalid(PlacementReason.ParentDeleted, "/checklist_item_id"),
            "retired" => AuthoringErrors.InvalidTransition(RetiredItemMessage),
            _ => null,
        };
    }

    /// <summary>
    /// The case row locked <c>for update</c> with everything a command needs: the row on success, or the 404 to return.
    /// </summary>
    /// <remarks>
    /// The 5 s <c>lock_timeout</c> is set here, before the statement that takes the lock, so a case another request holds
    /// answers the writer's own retryable <c>503 unavailable</c> instead of waiting for that holder's transaction to end.
    /// </remarks>
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
        IReadOnlyList<ManualCaseRow> rows;

        try
        {
            await AuthoringLockGuard.SetLockTimeoutAsync(db, cancellationToken).ConfigureAwait(false);

            rows = await db.QueryAsync<ManualCaseRow>(
                AuthoringManualCaseReadSql.LockCase,
                new { org_id = orgId, project_id = projectId, case_id = caseId },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (AuthoringLockGuard.IsContention(exception))
        {
            // The writer maps 55P03/40P01 to this same refusal; a lock this command takes first must not become a 500.
            return (null, AuthoringErrors.Unavailable());
        }

        return rows.Count == 0
            ? (null, Error.NotFound("no manual test case with that id is visible to this tenant"))
            : (rows[0], null);
    }
}
