using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Import;

/// <summary>
/// Route 25's import half (13.1.1 AC13/AC15, §5): one file of rows is validated as a whole, resolved against the project,
/// then applied <b>atomically</b> — one transaction, one <c>batch_id</c>, <c>source = import</c> — or refused as a whole.
/// </summary>
/// <remarks>
/// <para>
/// <b>The five phases, in this order.</b> (1) <see cref="ImportValidator"/>: everything the file alone decides — the key
/// graph, the kind ladder, the §4 value rules, the steps schema — with no database read. (2) <see cref="ImportPlanner"/>:
/// three reads of the stored tree and one of the stored hashes resolve every row to a create, update, restore or
/// unchanged outcome. (3) A dry run stops here: the same report, no allocation, no write. (4) The two counter calls:
/// <b>one</b> <c>ReserveAtLeastAsync</c> past the highest code the file brought — before the block is handed out, so a
/// fresh code can never land on an explicit one — and <b>one</b> <c>AllocateManyAsync</c> for every code-less case row.
/// (5) The apply: a fixed number of set-based statements (see <see cref="ImportApplySql"/>), so 50 rows and 5 000 rows
/// cost the same round trips (plan P2).
/// </para>
/// <para>
/// <b>Nothing is written until everything is known.</b> The whole file is validated and resolved before the first
/// statement of the apply runs, so "one bad row at the end" refuses 199 good ones (AC13). The allocation that phase 4
/// performs is burned when the apply then fails — deliberate, §4's [DECISION] on non-gap-free numbering, exactly as a
/// create that rolls back burns one number.
/// </para>
/// <para>
/// <b>Refusals never echo a cell.</b> Every <c>details[]</c> entry is <c>(path, reason, index)</c> — a fixed column
/// pointer, one of the AC13 reasons, and the row's file position (S3). A key, a title, a step text or a code the caller
/// sent can therefore never reach a log or another tenant through an error.
/// </para>
/// <para>
/// <b>It runs in the caller's scope.</b> Every statement runs on the ambient tenant transaction of the Mediator handler
/// (<see cref="ITenantDb"/>), which owns <c>commit</c>/<c>rollback</c>; the two counter calls deliberately open their own
/// short-lived transaction (the allocator's own design, §4).
/// </para>
/// </remarks>
internal sealed class AuthoringImporter(
    ITenantDb db,
    AuthoringCodeAllocator allocator,
    ManualCaseStepsValidator steps)
{
    /// <summary>The JSON shape of the payloads the set-based statements consume (snake_case, nulls kept).</summary>
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly ImportValidator validator = new(steps);

    /// <summary>Imports one file's rows (route 25), or reports what it would do.</summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="projectId">The project to import into.</param>
    /// <param name="actorMembershipId">The authoring member, or null.</param>
    /// <param name="rows">The rows a reader produced, in file order.</param>
    /// <param name="dryRun">True to return the report without allocating or writing anything (AC13).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report, or the refusal of the whole file.</returns>
    internal async ValueTask<Result<ImportReport>> ImportAsync(
        Guid orgId,
        Guid projectId,
        Guid? actorMembershipId,
        IReadOnlyList<ImportRow> rows,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ImportCoreAsync(orgId, projectId, actorMembershipId, rows, dryRun, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (AuthoringLockGuard.IsContention(exception))
        {
            // The writer-wide 5 s lock_timeout (55P03) and a deadlock (40P01) answer the retryable 503 of §5.
            return AuthoringErrors.Fail<ImportReport>(AuthoringErrors.Unavailable());
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation)
        {
            // Only the constraints a concurrent writer can take — a sibling position, a case code, a project counter or a
            // parent row — are retryable: the plan was right when it read, and a second attempt sees the new state. Any
            // other unique or foreign-key violation is this file's own content, which no retry can fix, so it answers
            // deterministically instead of inviting a client to spin (finding H).
            return AuthoringErrors.Fail<ImportReport>(
                IsContentionConstraint(exception.ConstraintName)
                    ? AuthoringErrors.Unavailable()
                    : AuthoringErrors.Invalid(
                        "/rows",
                        "value_invalid",
                        "the file's rows collide with the project's stored rows"));
        }
    }

    /// <summary>
    /// The constraints a concurrent writer can take between the resolution read and the apply: the ones this import
    /// answers with the retryable <c>503</c>. Everything else is the file's own content (finding H).
    /// </summary>
    private static readonly HashSet<string> ContentionConstraints = new(StringComparer.Ordinal)
    {
        "project_org_case_prefix_uq",
        "feature_node_sibling_position_uq",
        "manual_test_case_code_key",
        "feature_node_project_fk",
        "feature_node_parent_fk",
        "checklist_item_feature_node_fk",
        "manual_test_case_checklist_item_fk",
        "authoring_code_counter_project_fk",
    };

    /// <summary>True when a unique or foreign-key violation is one a concurrent writer can cause.</summary>
    /// <param name="constraint">The violated constraint.</param>
    private static bool IsContentionConstraint(string? constraint) =>
        constraint is not null && ContentionConstraints.Contains(constraint);

    /// <summary>The five phases of one import.</summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="actorMembershipId">The authoring member, or null.</param>
    /// <param name="rows">The rows a reader produced, in file order.</param>
    /// <param name="dryRun">True for a report only.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Result<ImportReport>> ImportCoreAsync(
        Guid orgId,
        Guid projectId,
        Guid? actorMembershipId,
        IReadOnlyList<ImportRow> rows,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (projectId == Guid.Empty)
        {
            return AuthoringErrors.Fail<ImportReport>(AuthoringErrors.Invalid(
                "/project_id",
                "required",
                "the project is required"));
        }

        if (rows.Count == 0)
        {
            return AuthoringErrors.Fail<ImportReport>(AuthoringErrors.Invalid(
                "/rows",
                "required",
                "an import carries at least one data row"));
        }

        if (rows.Count > ImportLimits.MaxDataRows)
        {
            // A reader refuses an over-long file with 413 already; this guards a caller that bypassed it, so the apply's
            // statements stay bounded whatever the caller does.
            return AuthoringErrors.Fail<ImportReport>(TooLarge());
        }

        var casePrefix = await db.ScalarAsync<string>(
            AuthoringSql.ReadCasePrefix,
            new { org_id = orgId, project_id = projectId },
            cancellationToken).ConfigureAwait(false);

        if (casePrefix is null)
        {
            return AuthoringErrors.Fail<ImportReport>(AuthoringErrors.NotFound(
                "no project with that id is visible in this tenant"));
        }

        var validation = validator.Validate(rows, casePrefix);

        if (!validation.IsAccepted)
        {
            return Refusal(validation.Issues);
        }

        var plan = await new ImportPlanner(db)
            .PlanAsync(orgId, projectId, validation.Rows, cancellationToken)
            .ConfigureAwait(false);

        if (!plan.IsAccepted)
        {
            return Refusal(plan.Issues);
        }

        if (dryRun)
        {
            return Result.Success(Report(null, plan.Rows));
        }

        var coded = await CodesAsync(orgId, projectId, plan, cancellationToken).ConfigureAwait(false);

        if (!coded.IsSuccess)
        {
            return AuthoringErrors.Fail<ImportReport>(coded.Error!);
        }

        var final = coded.Value!;

        if (final.All(row => row.Outcome == ImportOutcome.Unchanged))
        {
            // AC15: a file that describes exactly what is stored appends nothing, so there is no batch to name.
            return Result.Success(Report(null, final));
        }

        var batchId = Guid.CreateVersion7();
        var applied = await ApplyAsync(
            orgId,
            projectId,
            batchId,
            actorMembershipId,
            final,
            plan.Groups,
            cancellationToken).ConfigureAwait(false);

        return applied.IsSuccess
            ? Result.Success(Report(batchId, final))
            : AuthoringErrors.Fail<ImportReport>(applied.Error!);
    }

    /// <summary>Phase 4: the reservation past the file's highest code, then the one block of fresh codes.</summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="plan">The accepted plan.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Result<IReadOnlyList<ImportPlannedRow>>> CodesAsync(
        Guid orgId,
        Guid projectId,
        ImportPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.ExplicitCodeFloor is { } floor)
        {
            // Before the block: the counter must already be past every code the file brought, or a fresh number could
            // land on one of them and the whole transaction would die on manual_test_case_code_key.
            var reserved = await allocator.ReserveAtLeastAsync(orgId, projectId, floor, cancellationToken)
                .ConfigureAwait(false);

            if (!reserved.IsSuccess)
            {
                return AuthoringErrors.Fail<IReadOnlyList<ImportPlannedRow>>(reserved.Error!);
            }
        }

        if (plan.Allocations == 0)
        {
            return Result.Success(plan.Rows);
        }

        var allocated = await allocator.AllocateManyAsync(orgId, projectId, plan.Allocations, cancellationToken)
            .ConfigureAwait(false);

        if (!allocated.IsSuccess)
        {
            return AuthoringErrors.Fail<IReadOnlyList<ImportPlannedRow>>(allocated.Error!);
        }

        var codes = allocated.Value!;
        var next = 0;
        var filled = new ImportPlannedRow[plan.Rows.Count];

        for (var index = 0; index < plan.Rows.Count; index++)
        {
            var row = plan.Rows[index];

            if (row.Outcome != ImportOutcome.Create || row.Row.Kind != ImportRowKind.Case || row.Code is not null)
            {
                filled[index] = row;
                continue;
            }

            if (row.Content is not ManualTestCaseSnapshot manualCase)
            {
                throw new InvalidOperationException("a case create carries a manual test case snapshot");
            }

            var code = codes[next++].Value;
            filled[index] = row with
            {
                Code = code,
                Content = manualCase with { Code = code },
            };
        }

        if (next != codes.Count)
        {
            throw new InvalidOperationException("the allocation block does not match the planned case creates");
        }

        if (filled.Any(row => row.Outcome == ImportOutcome.Create
            && row.Row.Kind == ImportRowKind.Case
            && row.Code is null))
        {
            throw new InvalidOperationException("a case create left phase 4 without a code");
        }

        return Result.Success<IReadOnlyList<ImportPlannedRow>>(filled);
    }

    /// <summary>
    /// Phase 5: the set-based apply, in the writer-wide lock order — sibling-group advisory lock, then row locks by rank
    /// (<c>feature_node</c> → <c>checklist_item</c> → <c>manual_test_case</c>) ascending by id — then the writes.
    /// </summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="batchId">The one batch id of every version row.</param>
    /// <param name="actorMembershipId">The authoring member, or null.</param>
    /// <param name="rows">The decided rows, in file order.</param>
    /// <param name="groups">The sibling groups the plan assigned positions in (finding F).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Result<bool>> ApplyAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        IReadOnlyList<ImportPlannedRow> rows,
        IReadOnlyList<ImportGroupExpectation> groups,
        CancellationToken cancellationToken)
    {
        // The writer-wide bound (§5): a holder that never commits answers the retryable 503 instead of parking the import.
        await db.ExecuteAsync(AuthoringSql.SetLockTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        var createdNodes = rows
            .Where(row => row.Outcome == ImportOutcome.Create && row.Row.Kind == ImportRowKind.Node)
            .Select(row =>
            {
                var snapshot = (FeatureNodeSnapshot)row.Content!;
                return new NodeInsertRow(row.EntityId, snapshot.ParentId, snapshot.Kind, snapshot.Title, snapshot.Position);
            })
            .ToArray();

        var createdItems = rows
            .Where(row => row.Outcome == ImportOutcome.Create && row.Row.Kind == ImportRowKind.Item)
            .Select(row =>
            {
                var snapshot = (ChecklistItemSnapshot)row.Content!;
                return new ItemInsertRow(
                    row.EntityId,
                    snapshot.FeatureNodeId,
                    snapshot.Title,
                    snapshot.Description,
                    snapshot.Priority,
                    snapshot.State,
                    snapshot.Position);
            })
            .ToArray();

        var createdCases = rows
            .Where(row => row.Outcome == ImportOutcome.Create && row.Row.Kind == ImportRowKind.Case)
            .Select(row =>
            {
                var snapshot = (ManualTestCaseSnapshot)row.Content!;
                return new CaseInsertRow(
                    row.EntityId,
                    snapshot.ChecklistItemId,
                    snapshot.Code,
                    snapshot.Title,
                    snapshot.Description,
                    snapshot.Precondition,
                    snapshot.Steps,
                    snapshot.Priority,
                    snapshot.State);
            })
            .ToArray();

        // The advisory lock of every group this file adds to, before any row lock (the create, move and reorder paths take
        // the same key), so an import cannot meet a parked position of a concurrent renumber.
        var groupKeys = createdNodes
            .Select(node => AuthoringLockGuard.SiblingGroupKey(projectId, node.ParentId, AuthoringLockGuard.SiblingGroup.FeatureNode))
            .Concat(createdItems.Select(item => AuthoringLockGuard.SiblingGroupKey(
                projectId,
                item.FeatureNodeId,
                AuthoringLockGuard.SiblingGroup.ChecklistItem)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (groupKeys.Length != 0)
        {
            await db.ExecuteAsync(
                ImportApplySql.LockSiblingGroups,
                new { group_keys = new ImportSql.TextArrayParameter(groupKeys) },
                cancellationToken).ConfigureAwait(false);
        }

        // Under the locks, every group this file adds to must still hold what the plan read: a count or a highest position
        // that moved since the resolution read means another writer renumbered or added to the same sibling set, so the
        // positions this file assigned are no longer append positions. Retryable, and nothing has been written yet
        // (finding F).
        if (!await GroupsUnchangedAsync(orgId, projectId, groups, cancellationToken).ConfigureAwait(false))
        {
            return AuthoringErrors.Fail<bool>(AuthoringErrors.Unavailable());
        }

        var createdIds = rows
            .Where(row => row.Outcome == ImportOutcome.Create)
            .Select(row => row.EntityId)
            .ToHashSet();

        // Rank order, ascending id, one statement per rank. A parent this file creates is skipped: it does not exist yet
        // and the same transaction inserts it before its child.
        var nodeIds = LockSet(
            rows,
            ImportRowKind.Node,
            createdIds,
            createdNodes.Select(node => node.ParentId),
            createdItems.Select(item => (Guid?)item.FeatureNodeId));
        // Every parent item this file's cases hang under, created cases and update/restore cases alike: a case may only be
        // written under a live, non-retired item, so the lock set has to carry the parent itself and not just the item row
        // the file edits (N1/N3).
        var caseParents = createdCases
            .Select(item => (Guid?)item.ChecklistItemId)
            .Concat(rows
                .Where(row => row.Row.Kind == ImportRowKind.Case
                    && row.Outcome is ImportOutcome.Update or ImportOutcome.Restore)
                .Select(row => (Guid?)((ManualTestCaseSnapshot)row.Content!).ChecklistItemId));

        var itemIds = LockSet(
            rows,
            ImportRowKind.Item,
            createdIds,
            caseParents,
            []);
        var caseIds = LockSet(rows, ImportRowKind.Case, createdIds, [], []);

        var locked = await LockRanksAsync(orgId, projectId, rows, nodeIds, itemIds, caseIds, cancellationToken)
            .ConfigureAwait(false);

        if (!locked.IsSuccess)
        {
            return locked;
        }

        var outcome = await InsertAsync(orgId, projectId, ImportApplySql.InsertEntities(AuthoringEntityKind.FeatureNode), null, createdNodes, cancellationToken)
            .ConfigureAwait(false)
            ?? await InsertAsync(orgId, projectId, ImportApplySql.InsertEntities(AuthoringEntityKind.ChecklistItem), AuthoringWireValues.ImportSource, createdItems, cancellationToken)
                .ConfigureAwait(false)
            ?? await InsertAsync(orgId, projectId, ImportApplySql.InsertEntities(AuthoringEntityKind.ManualTestCase), AuthoringWireValues.ImportSource, createdCases, cancellationToken)
                .ConfigureAwait(false);

        if (outcome is not null)
        {
            return AuthoringErrors.Fail<bool>(outcome);
        }

        // A restore writes the file's content as well as clearing deleted_at, in the same one statement, so the row that
        // comes back is exactly the row the file describes (AC8 records it, AC15 converges on it).
        var restores = rows
            .Where(row => row.Outcome == ImportOutcome.Restore && row.Row.Kind == ImportRowKind.Case)
            .Select(CaseUpdateRowOf)
            .ToArray();

        outcome = await UpdateAsync(orgId, projectId, ImportApplySql.RestoreCases, restores, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is not null)
        {
            return AuthoringErrors.Fail<bool>(outcome);
        }

        var updatedItems = rows
            .Where(row => row.Outcome == ImportOutcome.Update && row.Row.Kind == ImportRowKind.Item)
            .Select(row =>
            {
                var snapshot = (ChecklistItemSnapshot)row.Content!;
                return new ItemUpdateRow(
                    row.EntityId,
                    snapshot.Title,
                    snapshot.Description,
                    snapshot.Priority,
                    snapshot.State,
                    snapshot.Position);
            })
            .ToArray();

        outcome = await UpdateAsync(
            orgId,
            projectId,
            ImportApplySql.UpdateEntities(AuthoringEntityKind.ChecklistItem),
            updatedItems,
            cancellationToken).ConfigureAwait(false);

        if (outcome is not null)
        {
            return AuthoringErrors.Fail<bool>(outcome);
        }

        var updatedCases = rows
            .Where(row => row.Outcome == ImportOutcome.Update && row.Row.Kind == ImportRowKind.Case)
            .Select(CaseUpdateRowOf)
            .ToArray();

        outcome = await UpdateAsync(
            orgId,
            projectId,
            ImportApplySql.UpdateEntities(AuthoringEntityKind.ManualTestCase),
            updatedCases,
            cancellationToken).ConfigureAwait(false);

        if (outcome is not null)
        {
            return AuthoringErrors.Fail<bool>(outcome);
        }

        return await AppendVersionsAsync(orgId, projectId, batchId, actorMembershipId, rows, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The ids one rank's lock statement covers: every row of that kind the apply mutates, plus the parents of the rows it
    /// creates that already exist. A parent this file creates is left out — the same transaction inserts it first.
    /// </summary>
    /// <param name="rows">The decided rows.</param>
    /// <param name="kind">The rank's kind.</param>
    /// <param name="createdIds">The ids this file creates.</param>
    /// <param name="first">The first parent set the rank covers.</param>
    /// <param name="second">The second parent set the rank covers.</param>
    private static Guid[] LockSet(
        IReadOnlyList<ImportPlannedRow> rows,
        ImportRowKind kind,
        HashSet<Guid> createdIds,
        IEnumerable<Guid?> first,
        IEnumerable<Guid?> second) =>
        rows
            .Where(row => row.Row.Kind == kind
                && row.Outcome is ImportOutcome.Update or ImportOutcome.Restore)
            .Select(row => row.EntityId)
            .Concat(first.OfType<Guid>())
            .Concat(second.OfType<Guid>())
            .Where(id => !createdIds.Contains(id))
            .Distinct()
            .Order()
            .ToArray();

    /// <summary>Locks every rank in order and proves each planned row is still at the version the plan resolved.</summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="rows">The decided rows.</param>
    /// <param name="nodeIds">The rank-0 lock set.</param>
    /// <param name="itemIds">The rank-1 lock set.</param>
    /// <param name="caseIds">The rank-2 lock set.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Result<bool>> LockRanksAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyList<ImportPlannedRow> rows,
        Guid[] nodeIds,
        Guid[] itemIds,
        Guid[] caseIds,
        CancellationToken cancellationToken)
    {
        var ranks = new[]
        {
            (Kind: AuthoringEntityKind.FeatureNode, Ids: nodeIds),
            (Kind: AuthoringEntityKind.ChecklistItem, Ids: itemIds),
            (Kind: AuthoringEntityKind.ManualTestCase, Ids: caseIds),
        };

        foreach (var (kind, ids) in ranks)
        {
            if (ids.Length == 0)
            {
                continue;
            }

            var locked = await db.QueryAsync<ImportApplySql.LockedRow>(
                ImportApplySql.LockEntities(kind),
                new { org_id = orgId, project_id = projectId, ids },
                cancellationToken).ConfigureAwait(false);

            if (locked.Count != ids.Length)
            {
                // A parent of a created row, or a row to update, is gone: a concurrent delete of something this file
                // depends on. Retryable, and nothing has been written yet.
                return AuthoringErrors.Fail<bool>(AuthoringErrors.Unavailable());
            }

            var stored = locked.ToDictionary(row => row.Id);
            var rowKind = kind switch
            {
                AuthoringEntityKind.FeatureNode => ImportRowKind.Node,
                AuthoringEntityKind.ChecklistItem => ImportRowKind.Item,
                _ => ImportRowKind.Case,
            };

            if (rowKind != ImportRowKind.Case
                && locked.Any(row => row.DeletedAt is not null))
            {
                // Only a *parent* can be locked and deleted: a soft-deleted node is not in the live tree the plan read, so
                // it is never an update target, and the same holds for an item. A row created or restored under it would
                // hang from a parent the project no longer lists: the plan is stale, and a retry after the delete settles
                // answers truthfully (finding D).
                return AuthoringErrors.Fail<bool>(AuthoringErrors.Unavailable());
            }

            if (rowKind == ImportRowKind.Item
                && locked.Any(row => string.Equals(
                    row.State,
                    ChecklistItemStateMachine.Retired,
                    StringComparison.Ordinal)))
            {
                // The writer's rule — "a retired checklist item takes no new content; restore it first"
                // (AuthoringVersionWriter.CheckCaseParentAsync) — re-checked under the lock: the plan saw a live item, and a
                // concurrent retire would otherwise let this file create, restore or update a case under a terminal item.
                // An unchanged row is never in a lock set, so a round trip of a retired item still imports (N1/N3).
                return AuthoringErrors.Fail<bool>(AuthoringErrors.Unavailable());
            }

            foreach (var row in rows.Where(row => row.Row.Kind == rowKind
                && row.Outcome is ImportOutcome.Update or ImportOutcome.Restore))
            {
                if (!stored.TryGetValue(row.EntityId, out var current) || current.Version != row.StoredVersion)
                {
                    return AuthoringErrors.Fail<bool>(AuthoringErrors.Unavailable());
                }

                if (row.RestoreSnapshot is not null && current.DeletedAt is null)
                {
                    return AuthoringErrors.Fail<bool>(AuthoringErrors.Unavailable());
                }
            }
        }

        return Result.Success(true);
    }

    /// <summary>
    /// Re-reads every sibling group this file adds to, under its advisory lock, and answers false when any of them moved
    /// since the plan assigned positions from it (finding F). One statement for every group of both kinds.
    /// </summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="groups">The groups and the state the plan read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<bool> GroupsUnchangedAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyList<ImportGroupExpectation> groups,
        CancellationToken cancellationToken)
    {
        if (groups.Count == 0)
        {
            return true;
        }

        var nodeGroups = groups
            .Where(group => group.Kind == ImportRowKind.Node)
            .Select(group => group.ParentId)
            .Distinct()
            .ToArray();
        var itemGroups = groups
            .Where(group => group.Kind == ImportRowKind.Item)
            .Select(group => group.ParentId)
            .Distinct()
            .ToArray();

        var observed = await db.QueryAsync<ImportApplySql.GroupStateRow>(
            ImportApplySql.GroupState,
            new
            {
                org_id = orgId,
                project_id = projectId,
                root = Guid.Empty,
                node_groups = nodeGroups,
                item_groups = itemGroups,
            },
            cancellationToken).ConfigureAwait(false);

        var state = observed.ToDictionary(
            row => (Kind: row.Kind, row.ParentId),
            row => (row.LiveCount, row.Maximum));

        return ExpectationsHold(groups, state);
    }

    /// <summary>
    /// The comparison itself, kept pure so its three refusal directions — a live count that moved, a highest position that
    /// moved, a group that vanished entirely — are provable without standing up a concurrent writer (N2's stale-group
    /// branch).
    /// </summary>
    /// <param name="groups">What the plan read, per sibling group it adds to.</param>
    /// <param name="observed">What the live rows hold now, keyed by the table name and the group's parent id.</param>
    internal static bool ExpectationsHold(
        IReadOnlyList<ImportGroupExpectation> groups,
        IReadOnlyDictionary<(string Kind, Guid ParentId), (int LiveCount, int Maximum)> observed) =>
        groups.All(group => observed.TryGetValue(
                (Kind: group.Kind == ImportRowKind.Node ? "feature_node" : "checklist_item", group.ParentId),
                out var seen)
            && seen.LiveCount == group.LiveCount
            && seen.Maximum == group.Maximum);

    /// <summary>One set-based insert; returns the refusal when the row count does not match the plan.</summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="statement">The insert statement of the kind.</param>
    /// <param name="source">The entity rows' <c>source</c>, or null for a kind without one (a feature node).</param>
    /// <param name="rows">The rows to write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Error?> InsertAsync<T>(
        Guid orgId,
        Guid projectId,
        string statement,
        string? source,
        IReadOnlyList<T> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        // The payload object carries `source` only for the two kinds whose table has one (a feature node has none), so no
        // statement is ever sent a parameter it does not reference.
        var payload = (object)JsonSerializer.Serialize(rows, PayloadJsonOptions);
        var written = source is null
            ? await db.ExecuteAsync(
                statement,
                new { org_id = orgId, project_id = projectId, rows = payload },
                cancellationToken).ConfigureAwait(false)
            : await db.ExecuteAsync(
                statement,
                new { org_id = orgId, project_id = projectId, source, rows = payload },
                cancellationToken).ConfigureAwait(false);

        return written == rows.Count ? null : OutOfStep();
    }

    /// <summary>One set-based update or restore; returns the refusal when the row count does not match the plan.</summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="statement">The update statement.</param>
    /// <param name="rows">The rows to write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Error?> UpdateAsync<T>(
        Guid orgId,
        Guid projectId,
        string statement,
        IReadOnlyList<T> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var written = await db.ExecuteAsync(
            statement,
            new
            {
                org_id = orgId,
                project_id = projectId,
                rows = JsonSerializer.Serialize(rows, PayloadJsonOptions),
            },
            cancellationToken).ConfigureAwait(false);

        return written == rows.Count ? null : OutOfStep();
    }

    /// <summary>
    /// Appends every version row of the import in <b>one</b> statement — the writer's own
    /// <see cref="AuthoringSql.AppendVersions"/> — with <c>source = import</c> and the shared <c>batch_id</c> (AC13).
    /// </summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="batchId">The batch id of the import.</param>
    /// <param name="actorMembershipId">The authoring member, or null.</param>
    /// <param name="rows">The decided rows.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Result<bool>> AppendVersionsAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        IReadOnlyList<ImportPlannedRow> rows,
        CancellationToken cancellationToken)
    {
        var source = AuthoringWireValues.ImportSource;
        var versions = new List<VersionRow>(rows.Count);

        foreach (var row in rows)
        {
            if (row.Outcome == ImportOutcome.Unchanged)
            {
                continue;
            }

            var kind = AuthoringWireValues.EntityKindName(KindOf(row.Row.Kind));

            if (row.Outcome == ImportOutcome.Restore)
            {
                // One row per entity per import: `version` is trigger-owned and bumps once, so a restore and an update of
                // the same row could not both be numbered against it. The restore row therefore records the file's
                // content, which is also the content the restore statement wrote.
                versions.Add(Version(row.EntityId, kind, row.StoredVersion + 1, AuthoringWireValues.RestoreChangeKind, row.Content!, source));
                continue;
            }

            versions.Add(row.Outcome == ImportOutcome.Create
                ? Version(row.EntityId, kind, 1, AuthoringWireValues.CreateChangeKind, row.Content!, source)
                : Version(row.EntityId, kind, row.StoredVersion + 1, AuthoringWireValues.UpdateChangeKind, row.Content!, source));
        }

        if (versions.Count == 0)
        {
            return Result.Success(true);
        }

        var written = await db.ExecuteAsync(
            AuthoringSql.AppendVersions,
            new
            {
                org_id = orgId,
                project_id = projectId,
                batch_id = batchId,
                author_membership_id = actorMembershipId,
                rows = JsonSerializer.Serialize(versions, PayloadJsonOptions),
            },
            cancellationToken).ConfigureAwait(false);

        return written == versions.Count
            ? Result.Success(true)
            : AuthoringErrors.Fail<bool>(OutOfStep());
    }

    /// <summary>One version row of the import's batch.</summary>
    /// <param name="entityId">The entity.</param>
    /// <param name="entityKind">The stored entity kind.</param>
    /// <param name="versionNo">The entity's <c>version</c> after this row.</param>
    /// <param name="changeKind">create, update or restore.</param>
    /// <param name="snapshot">The snapshot the row records.</param>
    /// <param name="source">Always <c>import</c>.</param>
    private static VersionRow Version(
        Guid entityId,
        string entityKind,
        int versionNo,
        string changeKind,
        AuthoringSnapshot snapshot,
        string source) => new(
            Guid.CreateVersion7(),
            entityKind,
            entityId,
            versionNo,
            changeKind,
            AuthoringContent.Serialize(snapshot),
            AuthoringHashes.ContentHash(snapshot),
            source,
            null,
            null);

    /// <summary>Builds the report of one import (AC13): one row per input row, in input order, and the totals.</summary>
    /// <param name="batchId">The batch id, or null on a dry run and for a file that changes nothing.</param>
    /// <param name="rows">The decided rows, in file order.</param>
    private static ImportReport Report(Guid? batchId, IReadOnlyList<ImportPlannedRow> rows) => new(
        batchId,
        new ImportCounts(
            rows.Count(row => row.Outcome == ImportOutcome.Create),
            rows.Count(row => row.Outcome == ImportOutcome.Update),
            rows.Count(row => row.Outcome == ImportOutcome.Restore),
            rows.Count(row => row.Outcome == ImportOutcome.Unchanged)),
        [.. rows.Select(row => new ImportRowReport(
            row.Row.Source.Index,
            row.Row.Key,
            row.Row.Kind,
            OutcomeName(row.Outcome),
            row.Code))]);

    /// <summary>The wire name of an outcome (13.1.1 §5 route 25 <c>rows[].outcome</c>).</summary>
    /// <param name="outcome">The outcome.</param>
    private static string OutcomeName(ImportOutcome outcome) => outcome switch
    {
        ImportOutcome.Create => "create",
        ImportOutcome.Update => "update",
        ImportOutcome.Restore => "restore",
        ImportOutcome.Unchanged => "unchanged",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "unknown import outcome"),
    };

    /// <summary>The refusal of a whole file: one <c>details[]</c> row per issue, never a cell value (AC13, S3).</summary>
    /// <param name="issues">The issues that refuse it.</param>
    internal static Result<ImportReport> Refusal(IReadOnlyList<ImportIssue> issues) =>
        AuthoringErrors.Fail<ImportReport>(Error.Invalid(
            "the import file is not valid; nothing was imported",
            [.. issues.Select(issue => new ErrorDetail(issue.Path, issue.Reason, issue.Index))]));

    /// <summary>The 413 of a file that carries more rows than one import may (AC13).</summary>
    private static Error TooLarge() => new(
        ApiErrorCode.PayloadTooLarge,
        "the import carries more than "
            + ImportLimits.MaxDataRows.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " data rows",
        413);

    /// <summary>The 409 of a batch whose stored state no longer matches the plan it was resolved from.</summary>
    private static Error OutOfStep() => AuthoringErrors.InvalidTransition(
        "the project changed while the import was resolved; nothing was imported");

    /// <summary>The entity kind of a row kind.</summary>
    /// <param name="kind">The row kind.</param>
    private static AuthoringEntityKind KindOf(ImportRowKind kind) => kind switch
    {
        ImportRowKind.Node => AuthoringEntityKind.FeatureNode,
        ImportRowKind.Item => AuthoringEntityKind.ChecklistItem,
        ImportRowKind.Case => AuthoringEntityKind.ManualTestCase,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown import row kind"),
    };

    /// <summary>The case-row parameters of a set-based case write (its update and its restore share them).</summary>
    /// <param name="row">An update or restore row.</param>
    private static CaseUpdateRow CaseUpdateRowOf(ImportPlannedRow row)
    {
        var snapshot = (ManualTestCaseSnapshot)row.Content!;

        return new CaseUpdateRow(
            row.EntityId,
            snapshot.ChecklistItemId,
            snapshot.Title,
            snapshot.Description,
            snapshot.Precondition,
            snapshot.Steps,
            snapshot.Priority,
            snapshot.State);
    }

    /// <summary>One row of the set-based feature node insert.</summary>
    /// <param name="Id">The node's id.</param>
    /// <param name="ParentId">The parent node, or null for a root module.</param>
    /// <param name="Kind">The node's kind.</param>
    /// <param name="Title">The title, in NFC.</param>
    /// <param name="Position">The resolved sibling position.</param>
    private sealed record NodeInsertRow(Guid Id, Guid? ParentId, string Kind, string Title, int Position);

    /// <summary>One row of the set-based checklist item insert.</summary>
    /// <param name="Id">The item's id.</param>
    /// <param name="FeatureNodeId">The owning node.</param>
    /// <param name="Title">The title, in NFC.</param>
    /// <param name="Description">The description, in NFC.</param>
    /// <param name="Priority">The priority.</param>
    /// <param name="State">The state.</param>
    /// <param name="Position">The resolved sibling position.</param>
    private sealed record ItemInsertRow(
        Guid Id,
        Guid FeatureNodeId,
        string Title,
        string? Description,
        string Priority,
        string State,
        int Position);

    /// <summary>One row of the set-based checklist item update.</summary>
    /// <param name="Id">The item's id.</param>
    /// <param name="Title">The title, in NFC.</param>
    /// <param name="Description">The description, in NFC.</param>
    /// <param name="Priority">The priority.</param>
    /// <param name="State">The state.</param>
    /// <param name="Position">The stored sibling position (an import never reorders).</param>
    private sealed record ItemUpdateRow(
        Guid Id,
        string Title,
        string? Description,
        string Priority,
        string State,
        int Position);

    /// <summary>One row of the set-based manual case insert.</summary>
    /// <param name="Id">The case's id.</param>
    /// <param name="ChecklistItemId">The owning item.</param>
    /// <param name="Code">The case code.</param>
    /// <param name="Title">The title, in NFC.</param>
    /// <param name="Description">The description, in NFC.</param>
    /// <param name="Precondition">The precondition, in NFC.</param>
    /// <param name="Steps">The steps document as JSON text.</param>
    /// <param name="Priority">The priority.</param>
    /// <param name="State">The state.</param>
    private sealed record CaseInsertRow(
        Guid Id,
        Guid ChecklistItemId,
        string Code,
        string Title,
        string? Description,
        string? Precondition,
        string Steps,
        string Priority,
        string State);

    /// <summary>One row of the set-based manual case update.</summary>
    /// <param name="Id">The case's id.</param>
    /// <param name="ChecklistItemId">The owning item (an import never moves a case).</param>
    /// <param name="Title">The title, in NFC.</param>
    /// <param name="Description">The description, in NFC.</param>
    /// <param name="Precondition">The precondition, in NFC.</param>
    /// <param name="Steps">The steps document as JSON text.</param>
    /// <param name="Priority">The priority.</param>
    /// <param name="State">The state.</param>
    private sealed record CaseUpdateRow(
        Guid Id,
        Guid ChecklistItemId,
        string Title,
        string? Description,
        string? Precondition,
        string Steps,
        string Priority,
        string State);

    /// <summary>One row of the batched <c>authoring_version</c> insert.</summary>
    /// <param name="Id">The version row's id.</param>
    /// <param name="EntityKind">The stored entity kind.</param>
    /// <param name="EntityId">The entity.</param>
    /// <param name="VersionNo">The entity's <c>version</c> after the change.</param>
    /// <param name="ChangeKind">create, update or restore.</param>
    /// <param name="Content">The snapshot as JSON text.</param>
    /// <param name="ContentHash">Its RFC 8785 digest.</param>
    /// <param name="Source">Always <c>import</c>.</param>
    /// <param name="Reason">Always null (the import route carries no reason).</param>
    /// <param name="AiRunId">Always null (an import is not an AI run).</param>
    private sealed record VersionRow(
        Guid Id,
        string EntityKind,
        Guid EntityId,
        int VersionNo,
        string ChangeKind,
        string Content,
        string ContentHash,
        string Source,
        string? Reason,
        Guid? AiRunId);
}
