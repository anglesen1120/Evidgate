using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>
/// The one code path that mutates an authoring entity and appends an <c>authoring_version</c> row (13.1.1 §5, T1.4,
/// AC5 and AC7): <see cref="AppendAsync"/> over the three <see cref="EntityChange"/> kinds,
/// <see cref="MoveManualTestCaseAsync"/> for AC22 and <see cref="RestoreAsync"/> for AC8.
/// </summary>
/// <remarks>
/// <para>
/// <b>The writer owns both rows of a change.</b> §5 states that every mutation of the three entities goes through this
/// type in the same tenant transaction, so a create inserts the entity row, an update writes the new snapshot, a soft
/// delete sets <c>deleted_at</c> and a restore clears it — each next to the version row it describes. No caller edits an
/// entity row and then asks for a version.
/// </para>
/// <para>
/// <b>It never opens, commits or rolls back a transaction.</b> Every statement runs on the ambient tenant scope
/// (<see cref="ITenantDb"/>), so a refusal leaves the caller's transaction to abort: that is what AC5's and AC7's
/// "nothing changes" means for a batch that already wrote an earlier entity of the same call.
/// </para>
/// <para>
/// <b>Serialisation.</b> Every mutation locks its entity row <c>for update</c> first and derives
/// <c>version_no</c> from the <c>version</c> the trigger returns, so two writers of one entity serialise on the row
/// lock and the unique key <c>(org, project, entity_kind, entity_id, version_no)</c> never surfaces as a 23505. If the
/// stored history is out of step with the row it refuses with <c>409 invalid_transition</c> instead.
/// </para>
/// </remarks>
internal sealed class AuthoringVersionWriter(ITenantDb db, ITenantScopeAccessor scopes)
{
    private const int MaximumReasonLength = 500;

    /// <summary>The partial sibling index of AC2: the one unique violation that is a <c>position_taken</c>, not a 23505.</summary>
    private const string SiblingPositionIndex = "feature_node_sibling_position_uq";

    /// <summary>AC15's one-case-per-code key: a caller-supplied code that is already taken is a 422, not a 23505.</summary>
    private const string ManualCaseCodeIndex = "manual_test_case_code_key";

    private static readonly JsonSerializerOptions VersionRowJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // ------------------------------------------------------------------ AppendAsync

    /// <summary>
    /// Appends one version per changed entity under one <paramref name="batchId"/> (AC5), running the AC7 non-empty
    /// guard inside a soft delete and refusing an empty <paramref name="batchId"/> or a repeated entity.
    /// </summary>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="batchId">The batch every row of the call shares.</param>
    /// <param name="actorMembershipId">The authoring member, or null.</param>
    /// <param name="changes">One change per entity, parents before their children.</param>
    /// <param name="context">Source, reason and AI run of the call.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async ValueTask<Result<AuthoringAppendOutcome>> AppendAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        IReadOnlyList<EntityChange> changes,
        AuthoringAppendContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            throw new ArgumentException("a batch must carry at least one change", nameof(changes));
        }

        EnsureScope(orgId);

        var settings = context ?? new AuthoringAppendContext();

        if (projectId == Guid.Empty)
        {
            return AuthoringErrors.Fail<AuthoringAppendOutcome>(AuthoringErrors.Invalid("/project_id", "required", "the project is required"));
        }

        if (batchId == Guid.Empty)
        {
            return AuthoringErrors.Fail<AuthoringAppendOutcome>(AuthoringErrors.Invalid("/batch_id", "required", "the batch id is required"));
        }

        if (CheckContext(settings) is { } contextFailure)
        {
            return AuthoringErrors.Fail<AuthoringAppendOutcome>(contextFailure);
        }

        // Everything that only reads a caller's own values is decided before the first statement, so a malformed batch
        // writes nothing at all even before the transaction aborts.
        var parsed = new List<(EntityChange Change, AuthoringSnapshot? Snapshot)>(changes.Count);
        var seen = new HashSet<(AuthoringEntityKind Kind, Guid Id)>();

        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index]
                ?? throw new ArgumentException($"change {index} is null", nameof(changes));
            var path = "/changes/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (!Enum.IsDefined(change.EntityKind))
            {
                return AuthoringErrors.Fail<AuthoringAppendOutcome>(AuthoringErrors.Invalid(path + "/entity_kind", "value_invalid", "unknown entity kind"));
            }

            if (!Enum.IsDefined(change.ChangeKind))
            {
                return AuthoringErrors.Fail<AuthoringAppendOutcome>(AuthoringErrors.Invalid(path + "/change_kind", "value_invalid", "unknown change kind"));
            }

            if (change.EntityId == Guid.Empty)
            {
                return AuthoringErrors.Fail<AuthoringAppendOutcome>(AuthoringErrors.Invalid(path + "/entity_id", "required", "the entity id is required"));
            }

            if (change.Reason is { } changeReason && !IsReasonLengthValid(changeReason))
            {
                return AuthoringErrors.Fail<AuthoringAppendOutcome>(AuthoringErrors.Invalid(path + "/reason", "length_invalid", "a reason is 1..500 characters"));
            }

            if (!seen.Add((change.EntityKind, change.EntityId)))
            {
                return AuthoringErrors.Fail<AuthoringAppendOutcome>(AuthoringErrors.Invalid(
                    path,
                    "duplicate_entity",
                    "an entity may appear once in one batch"));
            }

            if (change.ChangeKind == AuthoringChangeKind.SoftDelete)
            {
                parsed.Add((change, null));
                continue;
            }

            if (!AuthoringContent.Parse(change.EntityKind, change.Content, out var snapshot, out var details))
            {
                return AuthoringErrors.Fail<AuthoringAppendOutcome>(AuthoringErrors.InvalidMany(
                    "the change is not a valid snapshot of the entity",
                    [.. details.Select(detail => new ErrorDetail(path + detail.Path, detail.Reason))]));
            }

            parsed.Add((change, snapshot));
        }

        var appended = new List<PendingVersion>(parsed.Count);
        var unchanged = new List<Guid>();

        for (var index = 0; index < parsed.Count; index++)
        {
            var (change, snapshot) = parsed[index];
            var step = change.ChangeKind switch
            {
                AuthoringChangeKind.Create => await CreateAsync(orgId, projectId, index, change, snapshot!, settings, cancellationToken).ConfigureAwait(false),
                AuthoringChangeKind.Update => await UpdateAsync(orgId, projectId, index, change, snapshot!, settings, cancellationToken).ConfigureAwait(false),
                AuthoringChangeKind.SoftDelete => await SoftDeleteAsync(orgId, projectId, index, change, settings, cancellationToken).ConfigureAwait(false),
                _ => AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.Invalid(
                    "/changes/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "value_invalid",
                    "unknown change kind")),
            };

            if (!step.IsSuccess)
            {
                return AuthoringErrors.Fail<AuthoringAppendOutcome>(step.Error!);
            }

            switch (step.Value)
            {
                case { } pending:
                    appended.Add(pending);
                    break;
                case null when change.ChangeKind == AuthoringChangeKind.Update:
                    unchanged.Add(change.EntityId);
                    break;
                default:
                    break;
            }
        }

        if (appended.Count > 0)
        {
            var written = await AppendVersionsAsync(orgId, projectId, batchId, actorMembershipId, settings, appended, cancellationToken).ConfigureAwait(false);
            if (written is not null)
            {
                return AuthoringErrors.Fail<AuthoringAppendOutcome>(written);
            }
        }

        return Result.Success(new AuthoringAppendOutcome(
            [.. appended.Select(row => new AuthoringVersionRef(row.VersionId, row.EntityKind, row.EntityId, row.VersionNo, row.ContentHash, batchId))],
            unchanged));
    }

    // ------------------------------------------------------------------ MoveManualTestCaseAsync

    /// <summary>
    /// Re-parents one manual test case (AC22): the target item is locked first and the case second, exactly the order
    /// 13.1.2's apply already holds, so a nested call cannot invert it into a deadlock. The move keeps the code, bumps
    /// the case's version and appends one <c>update</c> row with the new <c>checklist_item_id</c>.
    /// </summary>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="batchId">The batch of the calling command.</param>
    /// <param name="actorMembershipId">The authoring member, or null.</param>
    /// <param name="caseId">The case to move.</param>
    /// <param name="toItemId">The target checklist item.</param>
    /// <param name="reason">An optional 1..500 character reason.</param>
    /// <param name="context">Source, reason and AI run of the call.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async ValueTask<Result<AuthoringMoveOutcome>> MoveManualTestCaseAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        Guid caseId,
        Guid toItemId,
        string? reason = null,
        AuthoringAppendContext? context = null,
        CancellationToken cancellationToken = default)
    {
        EnsureScope(orgId);

        var settings = context ?? new AuthoringAppendContext();

        if (projectId == Guid.Empty || caseId == Guid.Empty || toItemId == Guid.Empty)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.Invalid(
                "/checklist_item_id",
                "required",
                "the project, the case and the target item are required"));
        }

        if (batchId == Guid.Empty)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.Invalid("/batch_id", "required", "the batch id is required"));
        }

        if (CheckContext(settings) is { } contextFailure)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(contextFailure);
        }

        var effectiveReason = reason ?? settings.Reason;
        if (effectiveReason is not null && !IsReasonLengthValid(effectiveReason))
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.Invalid("/reason", "length_invalid", "a reason is 1..500 characters"));
        }

        // Lock order: the target item first, the case second (AC22, 13.1.2-AC13).
        var target = await LockAsync(orgId, projectId, toItemId, AuthoringEntityKind.ChecklistItem, cancellationToken).ConfigureAwait(false);
        if (target is not ItemRow item)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.ParentInvalid(
                PlacementReason.ParentNotFound,
                "/checklist_item_id"));
        }

        if (item.DeletedAt is not null)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.ParentInvalid(
                PlacementReason.ParentDeleted,
                "/checklist_item_id"));
        }

        if (string.Equals(item.State, ChecklistItemStateMachine.Retired, StringComparison.Ordinal))
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.InvalidTransition(
                "a retired checklist item takes no new content; restore it first"));
        }

        var stored = await LockAsync(orgId, projectId, caseId, AuthoringEntityKind.ManualTestCase, cancellationToken).ConfigureAwait(false);
        if (stored is not CaseRow existing || existing.DeletedAt is not null)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.EntityNotFound(AuthoringEntityKind.ManualTestCase));
        }

        if (string.Equals(existing.State, ManualTestCaseStateMachine.Retired, StringComparison.Ordinal))
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.InvalidTransition(
                "a retired manual test case takes no edit"));
        }

        if (existing.ChecklistItemId == toItemId)
        {
            return Result.Success(new AuthoringMoveOutcome(existing.Version, null));
        }

        var version = await db.QuerySingleOrDefaultAsync<VersionRow>(
            AuthoringSql.UpdateEntity(AuthoringEntityKind.ManualTestCase),
            new
            {
                id = caseId,
                org_id = orgId,
                project_id = projectId,
                checklist_item_id = toItemId,
                code = existing.Code,
                title = existing.Title,
                description = existing.Description,
                precondition = existing.Precondition,
                steps = existing.Steps,
                priority = existing.Priority,
                state = existing.State,
            },
            cancellationToken).ConfigureAwait(false);

        if (version is null)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.EntityNotFound(AuthoringEntityKind.ManualTestCase));
        }

        if (version.Version != existing.LastVersionNo + 1)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.InvalidTransition(
                "the version history of this manual test case is out of step"));
        }

        var snapshot = SnapshotOf(existing) with { ChecklistItemId = toItemId };
        var pending = Pending(AuthoringEntityKind.ManualTestCase, caseId, version.Version, AuthoringWireValues.UpdateChangeKind, snapshot, effectiveReason);

        if (await AppendVersionsAsync(orgId, projectId, batchId, actorMembershipId, settings, [pending], cancellationToken).ConfigureAwait(false) is { } failure)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(failure);
        }

        return Result.Success(new AuthoringMoveOutcome(
            version.Version,
            new AuthoringVersionRef(pending.VersionId, pending.EntityKind, pending.EntityId, pending.VersionNo, pending.ContentHash, batchId)));
    }

    // ------------------------------------------------------------------ RestoreAsync

    /// <summary>
    /// Re-appends a snapshot as a new version (AC8): clears <c>deleted_at</c>, writes the snapshot back, resolves a
    /// position clash at <c>max + 1</c>, refuses a parent that is still soft-deleted and sets a restored item to
    /// <c>active</c> and a restored case to <c>draft</c> (§6).
    /// </summary>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="batchId">The batch of the calling command.</param>
    /// <param name="actorMembershipId">The authoring member, or null.</param>
    /// <param name="entityKind">Which entity to restore.</param>
    /// <param name="entityId">The entity.</param>
    /// <param name="reason">An optional 1..500 character reason.</param>
    /// <param name="fromVersionId">A version of the entity whose snapshot is re-applied, or null for its own last state.</param>
    /// <param name="context">Source, reason and AI run of the call.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async ValueTask<Result<AuthoringVersionRef>> RestoreAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        AuthoringEntityKind entityKind,
        Guid entityId,
        string? reason = null,
        Guid? fromVersionId = null,
        AuthoringAppendContext? context = null,
        CancellationToken cancellationToken = default)
    {
        EnsureScope(orgId);

        var settings = context ?? new AuthoringAppendContext();

        if (entityId == Guid.Empty)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.Invalid("/id", "required", "the entity id is required"));
        }

        if (projectId == Guid.Empty)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.Invalid("/project_id", "required", "the project is required"));
        }

        if (batchId == Guid.Empty)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.Invalid("/batch_id", "required", "the batch id is required"));
        }

        if (!Enum.IsDefined(entityKind))
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.Invalid("/entity_kind", "value_invalid", "unknown entity kind"));
        }

        if (CheckContext(settings) is { } contextFailure)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(contextFailure);
        }

        var effectiveReason = reason ?? settings.Reason;
        if (effectiveReason is not null && !IsReasonLengthValid(effectiveReason))
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.Invalid("/reason", "length_invalid", "a reason is 1..500 characters"));
        }

        var stored = await LockAsync(orgId, projectId, entityId, entityKind, cancellationToken).ConfigureAwait(false);

        AuthoringSnapshot snapshot;
        int lastVersionNo;
        int previousVersion;

        if (stored is null)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.EntityNotFound(entityKind));
        }

        (snapshot, lastVersionNo, previousVersion) = stored switch
        {
            NodeRow node => ((AuthoringSnapshot)SnapshotOf(node), node.LastVersionNo, node.Version),
            ItemRow item => (SnapshotOf(item), item.LastVersionNo, item.Version),
            CaseRow manualCase => (SnapshotOf(manualCase), manualCase.LastVersionNo, manualCase.Version),
            _ => throw new InvalidOperationException("the locked row is not an authoring entity"),
        };

        if (IsDeleted(stored) && fromVersionId is null)
        {
            // The entity's own state is the snapshot to re-append.
        }
        else if (fromVersionId is { } versionId)
        {
            var version = await db.QuerySingleOrDefaultAsync<StoredVersion>(
                AuthoringSql.ReadVersion,
                new { org_id = orgId, project_id = projectId, id = versionId },
                cancellationToken).ConfigureAwait(false);

            if (version is null
                || !string.Equals(version.EntityKind, AuthoringWireValues.EntityKindName(entityKind), StringComparison.Ordinal)
                || version.EntityId != entityId)
            {
                return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.NotFound(
                    "no version with that id describes this entity in this project"));
            }

            using var document = JsonDocument.Parse(version.Content);
            if (!AuthoringContent.Parse(entityKind, document.RootElement, out var parsed, out var details))
            {
                throw new InvalidOperationException(
                    "the stored snapshot of authoring version " + versionId.ToString("D") + " is not a valid "
                    + AuthoringWireValues.EntityKindName(entityKind) + " content: "
                    + string.Join(", ", details.Select(detail => detail.Path + " " + detail.Reason)));
            }

            snapshot = parsed!;
        }
        else
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.InvalidTransition(
                "the entity is not soft-deleted; restore names the version it re-applies"));
        }

        var parentFailure = await CheckRestoreParentAsync(orgId, projectId, snapshot, cancellationToken).ConfigureAwait(false);
        if (parentFailure is not null)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(parentFailure);
        }

        snapshot = await ResolveRestorePositionAsync(orgId, projectId, entityId, snapshot, cancellationToken).ConfigureAwait(false);

        VersionRow? version2;
        try
        {
            version2 = await db.QuerySingleOrDefaultAsync<VersionRow>(
                AuthoringSql.RestoreEntity(entityKind),
                RestoreParameters(orgId, projectId, entityId, snapshot),
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(exception.ConstraintName, SiblingPositionIndex, StringComparison.Ordinal))
        {
            // The resolved max + 1 was taken by a concurrent sibling between the read and this write. The transaction is
            // already aborted, so the retry belongs to the caller: answer the retryable 503 of §5 rather than a 500.
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.Unavailable());
        }

        if (version2 is null)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.EntityNotFound(entityKind));
        }

        if (version2.Version != previousVersion + 1 || version2.Version != lastVersionNo + 1)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.InvalidTransition(
                "the version history of this entity is out of step"));
        }

        var pending = Pending(entityKind, entityId, version2.Version, AuthoringWireValues.RestoreChangeKind, snapshot, effectiveReason);

        if (await AppendVersionsAsync(orgId, projectId, batchId, actorMembershipId, settings, [pending], cancellationToken).ConfigureAwait(false) is { } failure)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(failure);
        }

        return Result.Success(new AuthoringVersionRef(pending.VersionId, entityKind, entityId, pending.VersionNo, pending.ContentHash, batchId));
    }

    // ------------------------------------------------------------------ the three change kinds

    private async ValueTask<Result<PendingVersion?>> CreateAsync(
        Guid orgId,
        Guid projectId,
        int index,
        EntityChange change,
        AuthoringSnapshot snapshot,
        AuthoringAppendContext settings,
        CancellationToken cancellationToken)
    {
        var path = "/changes/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/content/";

        if (await LockAsync(orgId, projectId, change.EntityId, change.EntityKind, cancellationToken).ConfigureAwait(false) is not null)
        {
            return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.Invalid(
                path.TrimEnd('/') + "/entity_id",
                "entity_exists",
                "an entity with that id already exists in this project"));
        }

        switch (snapshot)
        {
            case FeatureNodeSnapshot node:
                {
                    var placement = await CheckNodePlacementAsync(orgId, projectId, node, cancellationToken).ConfigureAwait(false);
                    if (placement is not null)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.ParentInvalid(placement, path + "parent_id"));
                    }

                    if (await CheckNodePositionAsync(orgId, projectId, change.EntityId, node.ParentId, node.Position, path + "position", cancellationToken).ConfigureAwait(false) is { } taken)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(taken);
                    }

                    break;
                }

            case ChecklistItemSnapshot item:
                {
                    if (settings.Source != AuthoringSource.Import
                        && !string.Equals(item.State, ChecklistItemStateMachine.NewItemState(settings.Source), StringComparison.Ordinal))
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.Invalid(
                            path + "state",
                            "value_invalid",
                            "a new checklist item is active unless the AI generated it as a draft"));
                    }

                    if (await CheckItemParentAsync(orgId, projectId, item.FeatureNodeId, path + "feature_node_id", cancellationToken).ConfigureAwait(false) is { } itemParent)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(itemParent);
                    }

                    break;
                }

            case ManualTestCaseSnapshot manualCase:
                {
                    if (settings.Source != AuthoringSource.Import
                        && !string.Equals(manualCase.State, ManualTestCaseStateMachine.NewCaseState, StringComparison.Ordinal))
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.Invalid(
                            path + "state",
                            "value_invalid",
                            "a new manual test case is a draft until it is reviewed"));
                    }

                    if (await CheckCaseParentAsync(orgId, projectId, manualCase.ChecklistItemId, path + "checklist_item_id", cancellationToken).ConfigureAwait(false) is { } caseParent)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(caseParent);
                    }

                    break;
                }

            default:
                return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.Invalid(path.TrimEnd('/'), "value_invalid", "unknown snapshot"));
        }

        VersionRow? created;
        try
        {
            created = await db.QuerySingleOrDefaultAsync<VersionRow>(
                AuthoringSql.InsertEntity(change.EntityKind),
                InsertParameters(orgId, projectId, change.EntityId, snapshot, settings.Source),
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Two races reach this catch, and they answer differently: a concurrent create of the same id (the key) and
            // a concurrent sibling taking the same position (the AC2 partial index).
            return AuthoringErrors.Fail<PendingVersion?>(exception.ConstraintName switch
            {
                SiblingPositionIndex => AuthoringErrors.Invalid(
                    path + "position",
                    "position_taken",
                    "a live sibling already holds that position"),
                ManualCaseCodeIndex => AuthoringErrors.Invalid(
                    path + "code",
                    "taken",
                    "another manual test case of this project already carries that code"),
                _ => AuthoringErrors.Invalid(
                    path.TrimEnd('/') + "/entity_id",
                    "entity_exists",
                    "an entity with that id already exists in this project"),
            });
        }

        if (created is null)
        {
            throw new InvalidOperationException(
                "the insert of " + AuthoringWireValues.EntityKindName(change.EntityKind) + " " + change.EntityId.ToString("D") + " returned no row");
        }

        return Result.Success<PendingVersion?>(Pending(
            change.EntityKind,
            change.EntityId,
            created.Version,
            AuthoringWireValues.CreateChangeKind,
            snapshot,
            change.Reason ?? settings.Reason));
    }

    private async ValueTask<Result<PendingVersion?>> UpdateAsync(
        Guid orgId,
        Guid projectId,
        int index,
        EntityChange change,
        AuthoringSnapshot snapshot,
        AuthoringAppendContext settings,
        CancellationToken cancellationToken)
    {
        var path = "/changes/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/content/";
        var stored = await LockAsync(orgId, projectId, change.EntityId, change.EntityKind, cancellationToken).ConfigureAwait(false);

        if (stored is null)
        {
            return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.EntityNotFound(change.EntityKind));
        }

        var reason = change.Reason ?? settings.Reason;
        int lastVersionNo;
        int previousVersion;

        switch (stored, snapshot)
        {
            case (NodeRow node, FeatureNodeSnapshot edited):
                {
                    if (node.DeletedAt is not null)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.InvalidTransition(
                            "a soft-deleted feature node takes no edit; restore it first"));
                    }

                    if (!string.Equals(node.Kind, edited.Kind, StringComparison.Ordinal))
                    {
                        // Route 3 of §5 has no kind member: a node's kind is fixed once it exists.
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.Invalid(
                            path + "kind",
                            "immutable",
                            "a feature node's kind never changes"));
                    }

                    if (node.ParentId != edited.ParentId)
                    {
                        if (await CheckNodePlacementAsync(orgId, projectId, edited, cancellationToken).ConfigureAwait(false) is { } placement)
                        {
                            return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.ParentInvalid(placement, path + "parent_id"));
                        }

                        // Cycle-freedom is structural (plan 13.1.1 §4 [DECISION]): the kind ladder makes an ancestor chain
                        // strictly decreasing in kind, so the only reachable cycle is a node naming itself, and the schema
                        // keeps `parent_id <> id` as its own CHECK. No ancestor query runs on a write.
                        var reparent = FeatureNodeRules.CheckReparent(node.Id, edited.ParentId, []);
                        if (!reparent.IsAccepted)
                        {
                            return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.ParentInvalid(reparent.Reason!, path + "parent_id"));
                        }
                    }

                    if (node.Position != edited.Position
                        && await CheckNodePositionAsync(orgId, projectId, node.Id, edited.ParentId, edited.Position, path + "position", cancellationToken).ConfigureAwait(false) is { } taken)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(taken);
                    }

                    lastVersionNo = node.LastVersionNo;
                    previousVersion = node.Version;
                    break;
                }

            case (ItemRow item, ChecklistItemSnapshot edited):
                {
                    if (item.DeletedAt is not null)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.InvalidTransition(
                            "a soft-deleted checklist item takes no edit; restore it first"));
                    }

                    if (!ChecklistItemStateMachine.CheckEditable(item.State).IsAllowed)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.InvalidTransition(
                            "a retired checklist item takes no edit"));
                    }

                    if (item.FeatureNodeId != edited.FeatureNodeId)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.Invalid(
                            path + "feature_node_id",
                            "immutable",
                            "a checklist item does not move between nodes"));
                    }

                    var transition = AuthoringStateFailure(
                        ChecklistItemStateMachine.Decide(item.State, edited.State, reason),
                        path + "state");
                    if (transition is not null)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(transition);
                    }

                    lastVersionNo = item.LastVersionNo;
                    previousVersion = item.Version;
                    break;
                }

            case (CaseRow existing, ManualTestCaseSnapshot edited):
                {
                    if (existing.DeletedAt is not null)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.InvalidTransition(
                            "a soft-deleted manual test case takes no edit; restore it first"));
                    }

                    if (!ManualTestCaseStateMachine.CheckEditable(existing.State).IsAllowed)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.InvalidTransition(
                            "a retired manual test case takes no edit"));
                    }

                    if (!string.Equals(existing.Code, edited.Code, StringComparison.Ordinal))
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.Invalid(
                            path + "code",
                            "immutable",
                            "a manual test case code never changes"));
                    }

                    if (existing.ChecklistItemId != edited.ChecklistItemId)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.Invalid(
                            path + "checklist_item_id",
                            "move_required",
                            "moving a manual test case between items has its own route (AC22)"));
                    }

                    var transition = AuthoringStateFailure(
                        ManualTestCaseStateMachine.Decide(existing.State, edited.State, reason),
                        path + "state");
                    if (transition is not null)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(transition);
                    }

                    lastVersionNo = existing.LastVersionNo;
                    previousVersion = existing.Version;
                    break;
                }

            default:
                return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.Invalid(path.TrimEnd('/'), "value_invalid", "unknown snapshot"));
        }

        var storedHash = stored switch
        {
            NodeRow node => node.LastContentHash,
            ItemRow item => item.LastContentHash,
            CaseRow manualCase => manualCase.LastContentHash,
            _ => string.Empty,
        };

        if (string.Equals(AuthoringHashes.ContentHash(snapshot), storedHash, StringComparison.Ordinal))
        {
            // AC5's no-op rule: an identical snapshot writes no version row and leaves the entity untouched.
            return Result.Success<PendingVersion?>(null);
        }

        VersionRow? updated;
        try
        {
            updated = await db.QuerySingleOrDefaultAsync<VersionRow>(
                AuthoringSql.UpdateEntity(change.EntityKind),
                UpdateParameters(orgId, projectId, change.EntityId, snapshot),
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(exception.ConstraintName, SiblingPositionIndex, StringComparison.Ordinal))
        {
            // AC2 again, from the other side: a sibling took the position between the check above and this write.
            return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.Invalid(
                path + "position",
                "position_taken",
                "a live sibling already holds that position"));
        }

        if (updated is null)
        {
            return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.EntityNotFound(change.EntityKind));
        }

        if (updated.Version != previousVersion + 1 || updated.Version != lastVersionNo + 1)
        {
            return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.InvalidTransition(
                "the version history of this entity is out of step"));
        }

        return Result.Success<PendingVersion?>(Pending(
            change.EntityKind,
            change.EntityId,
            updated.Version,
            AuthoringWireValues.UpdateChangeKind,
            snapshot,
            reason));
    }

    private async ValueTask<Result<PendingVersion?>> SoftDeleteAsync(
        Guid orgId,
        Guid projectId,
        int index,
        EntityChange change,
        AuthoringAppendContext settings,
        CancellationToken cancellationToken)
    {
        var path = "/changes/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var stored = await LockAsync(orgId, projectId, change.EntityId, change.EntityKind, cancellationToken).ConfigureAwait(false);
        if (stored is null || IsDeleted(stored))
        {
            return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.EntityNotFound(change.EntityKind));
        }

        // AC7's non-empty guard lives here, inside the writer, so 13.1.2's delete and merge compose from SoftDelete
        // without restating it.
        switch (stored)
        {
            case NodeRow:
                {
                    var contents = await db.QuerySingleOrDefaultAsync<NodeContentsRow>(
                        AuthoringSql.NodeContents,
                        new { org_id = orgId, project_id = projectId, id = change.EntityId },
                        cancellationToken).ConfigureAwait(false);

                    if (contents is null)
                    {
                        throw new InvalidOperationException("the non-empty probe of a feature node returned no row");
                    }

                    if (contents.HasChildren)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.NotEmpty("node_has_children", path));
                    }

                    if (contents.HasItems)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.NotEmpty("node_has_items", path));
                    }

                    break;
                }

            case ItemRow:
                {
                    var contents = await db.QuerySingleOrDefaultAsync<ItemContentsRow>(
                        AuthoringSql.ItemContents,
                        new { org_id = orgId, project_id = projectId, id = change.EntityId },
                        cancellationToken).ConfigureAwait(false);

                    if (contents is null)
                    {
                        throw new InvalidOperationException("the non-empty probe of a checklist item returned no row");
                    }

                    if (contents.HasCases)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.NotEmpty("item_has_cases", path));
                    }

                    break;
                }

            case CaseRow:
                break;

            default:
                throw new InvalidOperationException("the locked row is not an authoring entity");
        }

        var deleted = await db.QuerySingleOrDefaultAsync<VersionRow>(
            AuthoringSql.SoftDeleteEntity(change.EntityKind),
            new { org_id = orgId, project_id = projectId, id = change.EntityId },
            cancellationToken).ConfigureAwait(false);

        if (deleted is null)
        {
            return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.EntityNotFound(change.EntityKind));
        }

        var lastVersionNo = stored switch
        {
            NodeRow node => node.LastVersionNo,
            ItemRow item => item.LastVersionNo,
            CaseRow manualCase => manualCase.LastVersionNo,
            _ => 0,
        };

        if (deleted.Version != lastVersionNo + 1)
        {
            return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.InvalidTransition(
                "the version history of this entity is out of step"));
        }

        AuthoringSnapshot snapshot = stored switch
        {
            NodeRow node => SnapshotOf(node),
            ItemRow item => SnapshotOf(item),
            CaseRow manualCase => SnapshotOf(manualCase),
            _ => throw new InvalidOperationException("the locked row is not an authoring entity"),
        };

        return Result.Success<PendingVersion?>(Pending(
            change.EntityKind,
            change.EntityId,
            deleted.Version,
            AuthoringWireValues.DeleteChangeKind,
            snapshot,
            change.Reason ?? settings.Reason));
    }

    // ------------------------------------------------------------------ shared pieces

    private async ValueTask<object?> LockAsync(
        Guid orgId,
        Guid projectId,
        Guid entityId,
        AuthoringEntityKind kind,
        CancellationToken cancellationToken)
    {
        var parameters = new { org_id = orgId, project_id = projectId, id = entityId };

        return kind switch
        {
            AuthoringEntityKind.FeatureNode => await db.QuerySingleOrDefaultAsync<NodeRow>(
                AuthoringSql.LockEntity(kind), parameters, cancellationToken).ConfigureAwait(false),
            AuthoringEntityKind.ChecklistItem => await db.QuerySingleOrDefaultAsync<ItemRow>(
                AuthoringSql.LockEntity(kind), parameters, cancellationToken).ConfigureAwait(false),
            AuthoringEntityKind.ManualTestCase => await db.QuerySingleOrDefaultAsync<CaseRow>(
                AuthoringSql.LockEntity(kind), parameters, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
        };
    }

    /// <summary>Writes the batch with one statement and reports a short write instead of surfacing a 23505.</summary>
    private async ValueTask<Error?> AppendVersionsAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        AuthoringAppendContext settings,
        List<PendingVersion> rows,
        CancellationToken cancellationToken)
    {
        var source = AuthoringWireValues.SourceName(settings.Source);
        var payload = JsonSerializer.Serialize(
            rows.Select(row => new VersionRowJson(
                row.VersionId,
                AuthoringWireValues.EntityKindName(row.EntityKind),
                row.EntityId,
                row.VersionNo,
                row.ChangeKind,
                row.Content,
                row.ContentHash,
                source,
                row.Reason,
                settings.Source == AuthoringSource.Ai ? settings.AiRunId : null)),
            VersionRowJsonOptions);

        var written = await db.ExecuteAsync(
            AuthoringSql.AppendVersions,
            new
            {
                org_id = orgId,
                project_id = projectId,
                batch_id = batchId,
                author_membership_id = actorMembershipId,
                rows = payload,
            },
            cancellationToken).ConfigureAwait(false);

        return written == rows.Count
            ? null
            : AuthoringErrors.InvalidTransition("the version history of this batch is out of step");
    }

    private async ValueTask<string?> CheckNodePlacementAsync(
        Guid orgId,
        Guid projectId,
        FeatureNodeSnapshot node,
        CancellationToken cancellationToken)
    {
        FeatureNodeParent? parent = null;
        if (node.ParentId is { } parentId)
        {
            // The parent is locked for update: a concurrent soft delete of it cannot commit between this check and the
            // write, so a child never lands under a deleted parent.
            parent = await LockAsync(orgId, projectId, parentId, AuthoringEntityKind.FeatureNode, cancellationToken).ConfigureAwait(false) is NodeRow row
                ? new FeatureNodeParent(row.Id, row.Kind, row.DeletedAt is not null, SameProject: true)
                : null;
        }

        var placement = FeatureNodeRules.CheckPlacement(node.Kind, parent);

        return placement.IsAccepted ? null : placement.Reason;
    }

    /// <summary>AC2's <c>position_taken</c>: a node never lands on a position a live sibling holds.</summary>
    private async ValueTask<Error?> CheckNodePositionAsync(
        Guid orgId,
        Guid projectId,
        Guid entityId,
        Guid? parentId,
        int position,
        string path,
        CancellationToken cancellationToken)
    {
        var siblings = await db.QuerySingleOrDefaultAsync<SiblingPositionRow>(
            AuthoringSql.NodeSiblingPosition,
            new { org_id = orgId, project_id = projectId, id = entityId, parent_id = parentId, position },
            cancellationToken).ConfigureAwait(false);

        return siblings is { Taken: true }
            ? AuthoringErrors.Invalid(path, "position_taken", "a live sibling already holds that position")
            : null;
    }

    private async ValueTask<Error?> CheckItemParentAsync(
        Guid orgId,
        Guid projectId,
        Guid featureNodeId,
        string path,
        CancellationToken cancellationToken)
    {
        var node = await LockAsync(orgId, projectId, featureNodeId, AuthoringEntityKind.FeatureNode, cancellationToken).ConfigureAwait(false) as NodeRow;

        if (node is null)
        {
            return AuthoringErrors.ParentInvalid(PlacementReason.ParentNotFound, path);
        }

        if (node.DeletedAt is not null)
        {
            return AuthoringErrors.ParentInvalid(PlacementReason.ParentDeleted, path);
        }

        return FeatureNodeKinds.ItemParentKinds.Contains(node.Kind, StringComparer.Ordinal)
            ? null
            : AuthoringErrors.ParentInvalid(PlacementReason.ParentKindInvalid, path);
    }

    private async ValueTask<Error?> CheckCaseParentAsync(
        Guid orgId,
        Guid projectId,
        Guid checklistItemId,
        string path,
        CancellationToken cancellationToken)
    {
        var item = await LockAsync(orgId, projectId, checklistItemId, AuthoringEntityKind.ChecklistItem, cancellationToken).ConfigureAwait(false) as ItemRow;

        if (item is null)
        {
            return AuthoringErrors.ParentInvalid(PlacementReason.ParentNotFound, path);
        }

        if (item.DeletedAt is not null)
        {
            return AuthoringErrors.ParentInvalid(PlacementReason.ParentDeleted, path);
        }

        return string.Equals(item.State, ChecklistItemStateMachine.Retired, StringComparison.Ordinal)
            ? AuthoringErrors.InvalidTransition("a retired checklist item takes no new content; restore it first")
            : null;
    }

    private async ValueTask<Error?> CheckRestoreParentAsync(
        Guid orgId,
        Guid projectId,
        AuthoringSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        return snapshot switch
        {
            FeatureNodeSnapshot node when node.ParentId is { } parentId => await CheckNodePlacementAsync(orgId, projectId, node, cancellationToken).ConfigureAwait(false) is { } reason
                ? AuthoringErrors.ParentInvalid(reason, "/content/parent_id")
                : null,
            ChecklistItemSnapshot item => await CheckItemParentAsync(orgId, projectId, item.FeatureNodeId, "/content/feature_node_id", cancellationToken).ConfigureAwait(false),
            ManualTestCaseSnapshot manualCase => await CheckCaseParentAsync(orgId, projectId, manualCase.ChecklistItemId, "/content/checklist_item_id", cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    /// <summary>AC8's position rule: an occupied old position re-appends at <c>max + 1</c> instead of failing.</summary>
    private async ValueTask<AuthoringSnapshot> ResolveRestorePositionAsync(
        Guid orgId,
        Guid projectId,
        Guid entityId,
        AuthoringSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        switch (snapshot)
        {
            case FeatureNodeSnapshot node:
                {
                    var siblings = await db.QuerySingleOrDefaultAsync<SiblingPositionRow>(
                        AuthoringSql.NodeSiblingPosition,
                        new { org_id = orgId, project_id = projectId, id = entityId, parent_id = node.ParentId, position = node.Position },
                        cancellationToken).ConfigureAwait(false);

                    return siblings is { Taken: true }
                        ? node with { Position = siblings.MaxPosition + 1 }
                        : node;
                }

            case ChecklistItemSnapshot item:
                {
                    var siblings = await db.QuerySingleOrDefaultAsync<SiblingPositionRow>(
                        AuthoringSql.ItemSiblingPosition,
                        new { org_id = orgId, project_id = projectId, id = entityId, feature_node_id = item.FeatureNodeId, position = item.Position },
                        cancellationToken).ConfigureAwait(false);

                    return siblings is { Taken: true }
                        ? item with { Position = siblings.MaxPosition + 1 }
                        : item;
                }

            case ManualTestCaseSnapshot manualCase:
                return manualCase with { State = ManualTestCaseStateMachine.RestoreState };

            default:
                throw new InvalidOperationException("unknown snapshot");
        }
    }

    private static object RestoreParameters(Guid orgId, Guid projectId, Guid entityId, AuthoringSnapshot snapshot) => snapshot switch
    {
        FeatureNodeSnapshot node => new
        {
            id = entityId,
            org_id = orgId,
            project_id = projectId,
            parent_id = node.ParentId,
            kind = node.Kind,
            title = node.Title,
            position = node.Position,
        },
        ChecklistItemSnapshot item => new
        {
            id = entityId,
            org_id = orgId,
            project_id = projectId,
            feature_node_id = item.FeatureNodeId,
            title = item.Title,
            description = item.Description,
            priority = item.Priority,
            state = item.State,
            position = item.Position,
        },
        ManualTestCaseSnapshot manualCase => new
        {
            id = entityId,
            org_id = orgId,
            project_id = projectId,
            checklist_item_id = manualCase.ChecklistItemId,
            code = manualCase.Code,
            title = manualCase.Title,
            description = manualCase.Description,
            precondition = manualCase.Precondition,
            steps = manualCase.Steps,
            priority = manualCase.Priority,
            state = manualCase.State,
        },
        _ => throw new InvalidOperationException("unknown snapshot"),
    };

    private static object InsertParameters(
        Guid orgId,
        Guid projectId,
        Guid entityId,
        AuthoringSnapshot snapshot,
        AuthoringSource source) => new
        {
            id = entityId,
            org_id = orgId,
            project_id = projectId,
            source = AuthoringWireValues.SourceName(source),
            parent_id = (snapshot as FeatureNodeSnapshot)?.ParentId,
            kind = (snapshot as FeatureNodeSnapshot)?.Kind,
            title = TitleOf(snapshot),
            position = PositionOf(snapshot),
            feature_node_id = (snapshot as ChecklistItemSnapshot)?.FeatureNodeId,
            description = (snapshot as ChecklistItemSnapshot)?.Description ?? (snapshot as ManualTestCaseSnapshot)?.Description,
            priority = (snapshot as ChecklistItemSnapshot)?.Priority ?? (snapshot as ManualTestCaseSnapshot)?.Priority,
            state = (snapshot as ChecklistItemSnapshot)?.State ?? (snapshot as ManualTestCaseSnapshot)?.State,
            checklist_item_id = (snapshot as ManualTestCaseSnapshot)?.ChecklistItemId,
            code = (snapshot as ManualTestCaseSnapshot)?.Code,
            precondition = (snapshot as ManualTestCaseSnapshot)?.Precondition,
            steps = (snapshot as ManualTestCaseSnapshot)?.Steps,
        };

    private static object UpdateParameters(Guid orgId, Guid projectId, Guid entityId, AuthoringSnapshot snapshot) => new
    {
        id = entityId,
        org_id = orgId,
        project_id = projectId,
        parent_id = (snapshot as FeatureNodeSnapshot)?.ParentId,
        kind = (snapshot as FeatureNodeSnapshot)?.Kind,
        title = TitleOf(snapshot),
        position = PositionOf(snapshot),
        feature_node_id = (snapshot as ChecklistItemSnapshot)?.FeatureNodeId,
        description = (snapshot as ChecklistItemSnapshot)?.Description ?? (snapshot as ManualTestCaseSnapshot)?.Description,
        priority = (snapshot as ChecklistItemSnapshot)?.Priority ?? (snapshot as ManualTestCaseSnapshot)?.Priority,
        state = (snapshot as ChecklistItemSnapshot)?.State ?? (snapshot as ManualTestCaseSnapshot)?.State,
        checklist_item_id = (snapshot as ManualTestCaseSnapshot)?.ChecklistItemId,
        code = (snapshot as ManualTestCaseSnapshot)?.Code,
        precondition = (snapshot as ManualTestCaseSnapshot)?.Precondition,
        steps = (snapshot as ManualTestCaseSnapshot)?.Steps,
    };

    private static string TitleOf(AuthoringSnapshot snapshot) => snapshot switch
    {
        FeatureNodeSnapshot node => node.Title,
        ChecklistItemSnapshot item => item.Title,
        ManualTestCaseSnapshot manualCase => manualCase.Title,
        _ => throw new InvalidOperationException("unknown snapshot"),
    };

    private static int PositionOf(AuthoringSnapshot snapshot) => snapshot switch
    {
        FeatureNodeSnapshot node => node.Position,
        ChecklistItemSnapshot item => item.Position,
        _ => 0,
    };

    private static bool IsDeleted(object row) => row switch
    {
        NodeRow node => node.DeletedAt is not null,
        ItemRow item => item.DeletedAt is not null,
        CaseRow manualCase => manualCase.DeletedAt is not null,
        _ => throw new InvalidOperationException("the row is not an authoring entity"),
    };

    private static FeatureNodeSnapshot SnapshotOf(NodeRow row) => new(row.Kind, row.Title, row.Position, row.ParentId);

    private static ChecklistItemSnapshot SnapshotOf(ItemRow row) =>
        new(row.FeatureNodeId, row.Title, row.Description, row.Priority, row.State, row.Position);

    private static ManualTestCaseSnapshot SnapshotOf(CaseRow row) =>
        new(row.ChecklistItemId, row.Code, row.Title, row.Description, row.Precondition, row.Steps, row.Priority, row.State);

    private static PendingVersion Pending(
        AuthoringEntityKind kind,
        Guid entityId,
        int versionNo,
        string changeKind,
        AuthoringSnapshot snapshot,
        string? reason) => new(
            Guid.CreateVersion7(),
            kind,
            entityId,
            versionNo,
            changeKind,
            AuthoringContent.Serialize(snapshot),
            AuthoringHashes.ContentHash(snapshot),
            reason);

    /// <summary>Maps a refused state move to its 00 §9.2 answer (§6), or null when it may go ahead.</summary>
    private static Error? AuthoringStateFailure(AuthoringTransition transition, string path) => transition.Outcome switch
    {
        AuthoringTransitionOutcome.Allowed or AuthoringTransitionOutcome.Unchanged => null,
        AuthoringTransitionOutcome.ReasonRequired => AuthoringErrors.Invalid(
            "/reason",
            "required",
            "a move into retired needs a 1..500 character reason"),
        _ => AuthoringErrors.InvalidTransition("the entity's state does not allow that move"),
    };

    private static Error? CheckContext(AuthoringAppendContext settings)
    {
        if (settings.Source == AuthoringSource.Ai && settings.AiRunId is null)
        {
            return AuthoringErrors.Invalid("/ai_run_id", "required", "an AI change names the run that produced it");
        }

        if (settings.Source != AuthoringSource.Ai && settings.AiRunId is not null)
        {
            return AuthoringErrors.Invalid("/ai_run_id", "unexpected", "only an AI change carries a run id");
        }

        if (settings.Reason is { } reason && !IsReasonLengthValid(reason))
        {
            return AuthoringErrors.Invalid("/reason", "length_invalid", "a reason is 1..500 characters");
        }

        return null;
    }

    private static bool IsReasonLengthValid(string reason) => reason.Length is > 0 and <= MaximumReasonLength;

    private void EnsureScope(Guid orgId)
    {
        if (scopes.Require().OrgId != orgId)
        {
            throw new ArgumentException("the open tenant scope belongs to another organization", nameof(orgId));
        }
    }

    // ------------------------------------------------------------------ rows

    /// <summary>The row of <c>public.feature_node</c> as the writer locks it.</summary>
    internal sealed record NodeRow(
        Guid Id,
        Guid? ParentId,
        string Kind,
        string Title,
        int Position,
        DateTimeOffset? DeletedAt,
        int Version,
        int LastVersionNo,
        string LastContentHash);

    /// <summary>The row of <c>public.checklist_item</c> as the writer locks it.</summary>
    internal sealed record ItemRow(
        Guid Id,
        Guid FeatureNodeId,
        string Title,
        string? Description,
        string Priority,
        string State,
        string Source,
        int Position,
        DateTimeOffset? DeletedAt,
        int Version,
        int LastVersionNo,
        string LastContentHash);

    /// <summary>The row of <c>public.manual_test_case</c> as the writer locks it.</summary>
    internal sealed record CaseRow(
        Guid Id,
        Guid ChecklistItemId,
        string Code,
        string Title,
        string? Description,
        string? Precondition,
        string Steps,
        string Priority,
        string State,
        string Source,
        DateTimeOffset? DeletedAt,
        int Version,
        int LastVersionNo,
        string LastContentHash);

    /// <summary>The <c>version</c> a write returned.</summary>
    internal sealed record VersionRow(int Version);

    /// <summary>The two answers of the AC7 guard.</summary>
    internal sealed record NodeContentsRow(bool HasChildren, bool HasItems);

    /// <summary>The AC7 guard of a checklist item.</summary>
    internal sealed record ItemContentsRow(bool HasCases);

    /// <summary>AC8's position answers.</summary>
    internal sealed record SiblingPositionRow(bool Taken, int MaxPosition);

    /// <summary>One stored version (the <c>fromVersionId</c> restore).</summary>
    internal sealed record StoredVersion(Guid Id, string EntityKind, Guid EntityId, int VersionNo, string Content, string ContentHash);

    /// <summary>A version row still to be inserted.</summary>
    internal sealed record PendingVersion(
        Guid VersionId,
        AuthoringEntityKind EntityKind,
        Guid EntityId,
        int VersionNo,
        string ChangeKind,
        string Content,
        string ContentHash,
        string? Reason);

    /// <summary>One row of the batched <c>authoring_version</c> insert.</summary>
    private sealed record VersionRowJson(
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
