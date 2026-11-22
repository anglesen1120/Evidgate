using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application;

internal sealed partial class AuthoringVersionWriter
{
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

                    // Every caller's steps document is validated after the NFC normalisation AuthoringContent applies,
                    // so no path can store a document outside authoring-steps.v1 (AC13/AC14).
                    if (CheckSteps(manualCase, path) is { } stepsFailure)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(stepsFailure);
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

                    // Same rule as create: the edited steps document must be an authoring-steps.v1 document.
                    if (CheckSteps(edited, path) is { } stepsFailure)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(stepsFailure);
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
}
