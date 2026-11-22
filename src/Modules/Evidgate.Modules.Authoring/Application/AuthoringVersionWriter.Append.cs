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
        try
        {
            return await AppendCoreAsync(orgId, projectId, batchId, actorMembershipId, changes, context, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (IsContention(exception))
        {
            return AuthoringErrors.Fail<AuthoringAppendOutcome>(AuthoringErrors.Unavailable());
        }
    }

    private async ValueTask<Result<AuthoringAppendOutcome>> AppendCoreAsync(
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

        // One bound for every lock this call takes (the allocator's 5 s design, SuiteCommands.cs:161): a holder that
        // never commits answers the retryable 503 instead of piling writers up.
        await db.ExecuteAsync(AuthoringSql.SetLockTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

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

        // A batch of checklist-item soft deletes runs as one set of statements for the whole selection: the per-entity
        // loop would issue four statements per change, which a 200-delete apply cannot afford. Every other batch, and a
        // mixed one, keeps the per-entity path and its lock order.
        if (parsed.TrueForAll(entry => entry.Change.EntityKind == AuthoringEntityKind.ChecklistItem
                && entry.Change.ChangeKind == AuthoringChangeKind.SoftDelete))
        {
            return await AppendChecklistItemDeletesAsync(
                db, orgId, projectId, batchId, actorMembershipId, parsed, settings, cancellationToken).ConfigureAwait(false);
        }

        // Every lock this batch can need is taken here, in one global order (rank, then id): the rows it mutates for
        // update and the parents its content names for share. A probe inside CreateAsync/UpdateAsync then re-locks a row
        // this transaction already holds, so no two batches can acquire two rows in opposite orders.
        await LockBatchAsync(orgId, projectId, parsed, cancellationToken).ConfigureAwait(false);

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
}
