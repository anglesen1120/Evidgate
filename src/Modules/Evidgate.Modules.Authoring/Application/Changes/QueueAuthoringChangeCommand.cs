using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Domain.Changes;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Changes;

/// <summary>
/// Queue one pending authoring change (<c>POST /v1/projects/{p}/authoring/changes</c>): a comment, a delete, a merge
/// or a split that waits for a batched apply instead of touching the item now.
/// </summary>
/// <param name="ProjectId">The project from the route; another tenant's or another project's id is 404.</param>
/// <param name="TargetKind">The request's <c>target_kind</c>; only <c>checklist_item</c> exists.</param>
/// <param name="TargetId">The target item, or the merge's survivor.</param>
/// <param name="ChangeType">One of the four change types.</param>
/// <param name="Payload">The change's own document, validated by <see cref="AuthoringChangePayloadReader"/>.</param>
/// <param name="Reason">The caller's optional 1..500 character reason.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record QueueAuthoringChangeCommand(
    Guid ProjectId,
    string? TargetKind,
    Guid TargetId,
    string? ChangeType,
    JsonElement Payload,
    string? Reason,
    string IdempotencyKey) : ICommand<Result<QueuedChange>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="QueueAuthoringChangeCommand"/>: validate the payload, read the whole affected set under RLS,
/// apply the queue-time guards (a deleted item, a delete with live cases, the pending budget, a pending structural
/// neighbour) and insert one <c>pending</c> row whose <c>affected_versions</c> is the server's own map.
/// </summary>
/// <remarks>
/// The handler never writes an item: queueing changes nothing a reader of the tree or the version stream can see.
/// The pending-overlap pre-check gives the caller a clean 409 without waiting on a lock; the table's guard trigger
/// takes a per-project advisory lock and raises <c>23514</c> for an insert this transaction could not see, which is
/// mapped to the same body so a lost race is never a 500.
/// </remarks>
/// <param name="db">The tenant-scoped data access of the open command transaction.</param>
/// <param name="scopes">The request's tenant scope.</param>
/// <param name="principals">The request principal.</param>
/// <param name="tenant">The request tenant context.</param>
/// <param name="audit">The audit buffer the pipeline appends just before commit.</param>
[AuditedAction("authoring.change.queue")]
[Permission(Permissions.Authoring.Write)]
internal sealed class QueueAuthoringChangeCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit) : ICommandHandler<QueueAuthoringChangeCommand, Result<QueuedChange>>
{
    /// <inheritdoc />
    public async ValueTask<Result<QueuedChange>> Handle(
        QueueAuthoringChangeCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(QueueAuthoringChangeCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<QueuedChange>(scopeError);
        }

        if (Shape(command) is { } shapeError)
        {
            return Result.Failure<QueuedChange>(shapeError);
        }

        var orgId = scopes.Require().OrgId;

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false)
            is { } missingProject)
        {
            return Result.Failure<QueuedChange>(missingProject);
        }

        var author = await AuthoringRequestScope
            .ActorMembershipAsync(db, orgId, principals, cancellationToken)
            .ConfigureAwait(false);

        if (author is null)
        {
            // author_membership_id is NOT NULL and every change names its author, so a service credential without a
            // membership cannot queue one; the refusal is the same 403 the save route answers.
            return Result.Failure<QueuedChange>(Error.Forbidden(
                "queueing a change needs an active membership in this organization"));
        }

        var (payload, refusal) = AuthoringChangePayloadReader.Read(command.ChangeType, command.TargetId, command.Payload);
        if (refusal is not null)
        {
            return Result.Failure<QueuedChange>(refusal);
        }

        if (payload is null)
        {
            throw new InvalidOperationException("the payload reader refused without a refusal");
        }

        var (versions, affectedRefusal) = await AffectedVersionsAsync(
                db, orgId, command.ProjectId, command.TargetId, payload, cancellationToken)
            .ConfigureAwait(false);

        if (affectedRefusal is not null)
        {
            return Result.Failure<QueuedChange>(affectedRefusal);
        }

        if (versions is null)
        {
            throw new InvalidOperationException("the affected-set read refused without a refusal");
        }

        if (await RefuseAsync(db, orgId, command.ProjectId, command.TargetId, payload, cancellationToken).ConfigureAwait(false)
            is { } reasoned)
        {
            return Result.Failure<QueuedChange>(reasoned);
        }

        return await InsertAsync(
                db,
                orgId,
                command,
                payload,
                versions,
                author.Value,
                audit,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The shape rules that need no database: the one target kind, a validated change type, a non-empty target and a
    /// reason inside the table's 1..500 bound.
    /// </summary>
    private static Error? Shape(QueueAuthoringChangeCommand command)
    {
        if (!AuthoringChangeTargetKind.IsValid(command.TargetKind))
        {
            return AuthoringChangeErrors.PayloadInvalid("/target_kind");
        }

        if (!AuthoringChangeTypes.IsValid(command.ChangeType))
        {
            return AuthoringChangeErrors.PayloadInvalid("/change_type");
        }

        if (command.TargetId == Guid.Empty)
        {
            return AuthoringChangeErrors.PayloadInvalid("/target_id");
        }

        if (command.Reason is { } reason
            && (string.IsNullOrWhiteSpace(reason) || ReasonLength(reason) > AuthoringChangeLimits.ReasonMax))
        {
            return AuthoringErrors.Invalid("/reason", "invalid", "the reason must be 1..500 characters");
        }

        return null;
    }

    /// <summary>The reason's length in code points, the unit PostgreSQL's <c>char_length</c> counts.</summary>
    private static int ReasonLength(string reason) => reason.EnumerateRunes().Count();

    /// <summary>
    /// Reads every affected item with its version. An id this project and tenant cannot see is <c>404</c> — absent,
    /// foreign and other-project are one answer — while a visible soft-deleted item is the <c>422 target_deleted</c>
    /// the caller can act on.
    /// </summary>
    private static async ValueTask<(List<(Guid ItemId, int Version)>? Versions, Error? Refusal)> AffectedVersionsAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        Guid targetId,
        AuthoringChangePayload payload,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<AuthoringChangeSql.ItemVersion>(
            AuthoringChangeSql.AffectedItems,
            new { org_id = orgId, project_id = projectId, item_ids = payload.AffectedItemIds.ToArray() },
            cancellationToken).ConfigureAwait(false);

        var found = rows.ToDictionary(row => row.Id, row => row);
        var versions = new List<(Guid ItemId, int Version)>(payload.AffectedItemIds.Count);

        for (var index = 0; index < payload.AffectedItemIds.Count; index++)
        {
            var itemId = payload.AffectedItemIds[index];

            if (!found.TryGetValue(itemId, out var row))
            {
                return (null, AuthoringErrors.NotFound("no checklist item with that id is visible in this project"));
            }

            if (row.DeletedAt is not null)
            {
                var path = itemId == targetId ? "/target_id" : $"/payload/source_item_ids/{index}";

                return (null, AuthoringChangeErrors.TargetDeleted(path));
            }

            versions.Add((itemId, row.Version));
        }

        return (versions, null);
    }

    /// <summary>
    /// The guards that read other rows: 13.1.1's non-empty rule for a delete, the project's pending budget, and the
    /// pending-structural overlap. All three run before the insert and none of them writes.
    /// </summary>
    private static async ValueTask<Error?> RefuseAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        Guid targetId,
        AuthoringChangePayload payload,
        CancellationToken cancellationToken)
    {
        if (payload.ChangeType == AuthoringChangeTypes.Delete)
        {
            var cases = await db.ScalarAsync<int>(
                AuthoringChangeSql.LiveCaseCount,
                new { org_id = orgId, project_id = projectId, item_id = targetId },
                cancellationToken).ConfigureAwait(false);

            if (cases > 0)
            {
                return AuthoringErrors.NotEmpty("item_has_cases", "/target_id");
            }
        }

        // The cap is a count-then-insert, so it is serialised per project by the same advisory lock the guard trigger
        // takes; without the lock two requests at 499 both insert. Every change type takes it, structural or not.
        await db.ExecuteAsync(
            AuthoringChangeSql.LockProject,
            new { org_id = orgId, project_id = projectId },
            cancellationToken).ConfigureAwait(false);

        var pending = await db.ScalarAsync<int>(
            AuthoringChangeSql.PendingCount,
            new { org_id = orgId, project_id = projectId },
            cancellationToken).ConfigureAwait(false);

        if (pending >= AuthoringChangeLimits.PendingMax)
        {
            return AuthoringChangeErrors.PendingLimitReached();
        }

        if (!payload.IsStructural)
        {
            return null;
        }

        var keys = payload.AffectedItemIds.Select(itemId => itemId.ToString("D")).ToArray();
        var overlap = await db.ScalarAsync<int?>(
            AuthoringChangeSql.PendingStructuralOverlap,
            new { org_id = orgId, project_id = projectId, keys },
            cancellationToken).ConfigureAwait(false);

        return overlap is null ? null : AuthoringChangeErrors.ChangeAlreadyPending();
    }

    /// <summary>
    /// One insert, then the 201 body. A guard trigger's <c>23514</c>, the same-target unique index, or a target that
    /// disappeared between the read and the insert are mapped to the refusals the caller can act on; anything else is
    /// a real fault and surfaces.
    /// </summary>
    private static async ValueTask<Result<QueuedChange>> InsertAsync(
        ITenantDb db,
        Guid orgId,
        QueueAuthoringChangeCommand command,
        AuthoringChangePayload payload,
        List<(Guid ItemId, int Version)> versions,
        Guid author,
        IAuditTrail audit,
        CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();

        try
        {
            var inserted = await db.QuerySingleOrDefaultAsync<AuthoringChangeSql.InsertedChange>(
                AuthoringChangeSql.Insert,
                new
                {
                    id,
                    org_id = orgId,
                    project_id = command.ProjectId,
                    target_kind = AuthoringChangeTargetKind.ChecklistItem,
                    target_id = command.TargetId,
                    change_type = payload.ChangeType,
                    payload = payload.RawJson,
                    base_version = TargetVersion(versions, command.TargetId),
                    affected_versions = AuthoringChangeJson.Versions(versions),
                    author_membership_id = author,
                    reason = command.Reason,
                },
                cancellationToken).ConfigureAwait(false);

            if (inserted is null)
            {
                throw new InvalidOperationException("the change insert returned no row");
            }

            audit.Describe(new AuditObject("authoring_change", id.ToString("D")));
            EvidgateTelemetry.RecordAuthoringChangeQueued(payload.ChangeType);

            var baseVersion = TargetVersion(versions, command.TargetId);

            return Result.Success(
                new QueuedChange(id, "pending", baseVersion, inserted.CreatedAt),
                201);
        }
        catch (PostgresException exception) when (IsPendingOverlap(exception))
        {
            return Result.Failure<QueuedChange>(AuthoringChangeErrors.ChangeAlreadyPending());
        }
        catch (PostgresException exception) when (IsMissingTarget(exception))
        {
            return Result.Failure<QueuedChange>(AuthoringChangeErrors.TargetDeleted("/target_id"));
        }
    }

    /// <summary>
    /// The target's own stamped version: for a merge the survivor is one entry of the source list, not necessarily
    /// the first, so the base version is looked up by id rather than by position.
    /// </summary>
    private static int TargetVersion(List<(Guid ItemId, int Version)> versions, Guid targetId) =>
        versions.First(pair => pair.ItemId == targetId).Version;

    /// <summary>
    /// True for the guard trigger's own raise and for the same-target partial unique index: both mean another pending
    /// structural change covers an item of this one, which is the AC14 refusal, not a fault.
    /// </summary>
    private static bool IsPendingOverlap(PostgresException exception) =>
        (exception.SqlState == PostgresErrorCodes.CheckViolation
            && exception.MessageText.StartsWith("authoring_change_affected", StringComparison.Ordinal))
        || (exception.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(exception.ConstraintName, "authoring_change_pending_structural_uq", StringComparison.Ordinal));

    /// <summary>True when the composite target foreign key lost its item between the read and the insert.</summary>
    private static bool IsMissingTarget(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.ForeignKeyViolation
        && string.Equals(exception.ConstraintName, "authoring_change_target_fk", StringComparison.Ordinal);
}
