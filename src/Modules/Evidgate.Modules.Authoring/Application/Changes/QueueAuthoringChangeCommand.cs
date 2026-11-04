using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
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
using Evidgate.Modules.Authoring.Application.Import;
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
/// <param name="Body">
/// The request body as the edge read it. It travels whole because the handler validates the document against
/// <c>authoring-change.v1</c> before projecting any member: a projection that reads what it knows would drop an
/// unknown root member, and an absent <c>payload</c> would become a default element that cannot be serialised.
/// </param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record QueueAuthoringChangeCommand(
    Guid ProjectId,
    JsonElement Body,
    string IdempotencyKey) : ICommand<Result<QueuedChange>>, IIdempotentCommand;

/// <summary>
/// The validated queue request the database half works from: the root members the schema and the shape rules accepted,
/// and the reader's payload with its server-derived affected set.
/// </summary>
/// <param name="TargetKind">The one target kind, <c>checklist_item</c>.</param>
/// <param name="TargetId">The target item, or the merge's survivor.</param>
/// <param name="Payload">The validated payload and the affected set it implies.</param>
/// <param name="Reason">The caller's optional 1..500 character reason.</param>
internal sealed record QueueRequest(string TargetKind, Guid TargetId, AuthoringChangePayload Payload, string? Reason);

/// <summary>
/// Handler of <see cref="QueueAuthoringChangeCommand"/>: validate the whole document and its payload, read the affected
/// set under RLS, apply the queue-time guards (a deleted item, a delete with live cases, a case assignment outside the
/// split target, the pending budget, a pending structural neighbour) and insert one <c>pending</c> row whose
/// <c>affected_versions</c> is the server's own map.
/// </summary>
/// <remarks>
/// <para>
/// The handler never writes an item: queueing changes nothing a reader of the tree or the version stream can see.
/// The pending-overlap pre-check gives the caller a clean 409 without waiting on a lock; the table's guard trigger
/// takes a per-project advisory lock and raises <c>23514</c> for an insert this transaction could not see, which is
/// mapped to the same body so a lost race is never a 500.
/// </para>
/// <para>
/// The project's queue lock is taken by this handler too, so the pending budget is a real cap rather than a hint, and
/// its wait carries the same 5 s bound the writers use: a project whose lock is held past it answers the retryable
/// 503 that apply and discard answer, never a request parked forever.
/// </para>
/// </remarks>
/// <param name="db">The tenant-scoped data access of the open command transaction.</param>
/// <param name="scopes">The request's tenant scope.</param>
/// <param name="principals">The request principal.</param>
/// <param name="tenant">The request tenant context.</param>
/// <param name="schemas">The compiled contract set the queue body is validated against, resolved on first use.</param>
/// <param name="audit">The audit buffer the pipeline appends just before commit.</param>
[AuditedAction("authoring.change.queue")]
[Permission(Permissions.Authoring.Write)]
internal sealed class QueueAuthoringChangeCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    Lazy<ContractSchemaSet> schemas,
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

        var (request, refusal) = Prepare(command);
        if (refusal is not null)
        {
            return Result.Failure<QueuedChange>(refusal);
        }

        if (request is null)
        {
            throw new InvalidOperationException("the queue preparation refused without a refusal");
        }

        try
        {
            return await QueueAsync(command.ProjectId, request, audit, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (AuthoringLockGuard.IsContention(exception))
        {
            return Result.Failure<QueuedChange>(AuthoringErrors.Unavailable());
        }
    }

    /// <summary>
    /// The checks that need no database, in the order their reasons are documented: the root members' shape, then the
    /// payload's own semantics, then the whole document against <c>authoring-change.v1</c>.
    /// </summary>
    /// <remarks>
    /// The schema runs last on purpose. The reader answers a merge of 51 sources with <c>merge_sources_max</c>, the
    /// reason the review panel renders, which a schema-first order would replace with its own <c>max_items</c>; reading
    /// the document afterwards is what refuses the members a projection would silently drop — an absent
    /// <c>payload</c>, an unknown root member, a mistyped <c>reason</c>.
    /// </remarks>
    /// <param name="command">The command whose raw body is validated and projected.</param>
    private (QueueRequest? Request, Error? Refusal) Prepare(QueueAuthoringChangeCommand command)
    {
        var body = command.Body;

        if (Text(body, "target_kind") is not { } targetKind || !AuthoringChangeTargetKind.IsValid(targetKind))
        {
            return (null, AuthoringChangeErrors.PayloadInvalid("/target_kind"));
        }

        if (Text(body, "change_type") is not { } changeType || !AuthoringChangeTypes.IsValid(changeType))
        {
            return (null, AuthoringChangeErrors.PayloadInvalid("/change_type"));
        }

        var targetId = Id(body, "target_id");
        if (targetId == Guid.Empty)
        {
            return (null, AuthoringChangeErrors.PayloadInvalid("/target_id"));
        }

        var reason = Text(body, "reason");
        if (reason is { } note
            && (string.IsNullOrWhiteSpace(note) || ReasonLength(note) > AuthoringChangeLimits.ReasonMax))
        {
            return (null, AuthoringErrors.Invalid("/reason", "invalid", "the reason must be 1..500 characters"));
        }

        var (payload, refusal) = AuthoringChangePayloadReader.Read(changeType, targetId, Payload(body));
        if (refusal is not null)
        {
            return (null, refusal);
        }

        if (payload is null)
        {
            throw new InvalidOperationException("the payload reader refused without a refusal");
        }

        if (AuthoringChangeDocument.Validate(body, schemas.Value) is { } documentError)
        {
            return (null, documentError);
        }

        return (new QueueRequest(targetKind, targetId, payload, reason), null);
    }

    /// <summary>The database half: the probes, the affected set, the guards and the insert, in one bounded transaction.</summary>
    /// <param name="projectId">The project from the route.</param>
    /// <param name="request">The validated request.</param>
    /// <param name="audit">The audit buffer the pipeline appends just before commit.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Result<QueuedChange>> QueueAsync(
        Guid projectId,
        QueueRequest request,
        IAuditTrail audit,
        CancellationToken cancellationToken)
    {
        var orgId = scopes.Require().OrgId;

        await AuthoringLockGuard.SetLockTimeoutAsync(db, cancellationToken).ConfigureAwait(false);

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, projectId, cancellationToken).ConfigureAwait(false)
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

        var (versions, affectedRefusal) = await AffectedVersionsAsync(
                db, orgId, projectId, request.TargetId, request.Payload, cancellationToken)
            .ConfigureAwait(false);

        if (affectedRefusal is not null)
        {
            return Result.Failure<QueuedChange>(affectedRefusal);
        }

        if (versions is null)
        {
            throw new InvalidOperationException("the affected-set read refused without a refusal");
        }

        if (await UnassignedCaseAsync(db, orgId, projectId, request, cancellationToken).ConfigureAwait(false)
            is { } assignmentError)
        {
            return Result.Failure<QueuedChange>(assignmentError);
        }

        if (await RefuseAsync(db, orgId, projectId, request.TargetId, request.Payload, cancellationToken).ConfigureAwait(false)
            is { } reasoned)
        {
            return Result.Failure<QueuedChange>(reasoned);
        }

        return await InsertAsync(db, orgId, projectId, request, versions, author.Value, audit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a split that assigns a case which is not a live child of the target: an assignment says where an existing
    /// case of the item goes, so a case of another item or a deleted one is a caller error the apply could never honour.
    /// </summary>
    /// <param name="db">The tenant-scoped data access of the open command transaction.</param>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="projectId">The project from the route.</param>
    /// <param name="request">The validated request: its payload carries the assignments with their payload indexes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private static async ValueTask<Error?> UnassignedCaseAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        QueueRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Payload.CaseAssignments.Count == 0)
        {
            return null;
        }

        var caseIds = request.Payload.CaseAssignments.Select(assignment => assignment.CaseId).ToArray();
        var live = await db.QueryAsync<Guid>(
            AuthoringChangeSql.LiveCaseIds,
            new { org_id = orgId, project_id = projectId, item_id = request.TargetId, case_ids = caseIds },
            cancellationToken).ConfigureAwait(false);

        var known = live.ToHashSet();

        foreach (var (index, caseId) in request.Payload.CaseAssignments)
        {
            if (!known.Contains(caseId))
            {
                return AuthoringChangeErrors.PayloadInvalid(
                    string.Create(CultureInfo.InvariantCulture, $"/payload/case_assignments/{index}/case_id"));
            }
        }

        return null;
    }

    /// <summary>A string member, or <c>null</c> when it is absent or of another kind.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="name">The member's name.</param>
    private static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A uuid member, or <see cref="Guid.Empty"/> so the shape rule can name <c>/target_id</c>.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="name">The member's name.</param>
    private static Guid Id(JsonElement body, string name) =>
        Text(body, name) is { } raw && Guid.TryParse(raw, out var id) ? id : Guid.Empty;

    /// <summary>The payload member as sent; an absent one is <see cref="JsonValueKind.Undefined"/> for the reader.</summary>
    /// <param name="body">The request body.</param>
    private static JsonElement Payload(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("payload", out var payload) ? payload : default;

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

        // The column is jsonb and `?|` takes a text[]; a plain string[] would reach Npgsql's global Dapper type handler,
        // whose SetValue throws for exactly this shape, so the keys travel as the module's own array parameter.
        var keys = payload.AffectedItemIds.Select(itemId => itemId.ToString("D")).ToArray();
        var overlap = await db.ScalarAsync<int?>(
            AuthoringChangeSql.PendingStructuralOverlap,
            new { org_id = orgId, project_id = projectId, keys = new ImportSql.TextArrayParameter(keys) },
            cancellationToken).ConfigureAwait(false);

        return overlap is null ? null : AuthoringChangeErrors.ChangeAlreadyPending();
    }

    /// <summary>
    /// One insert, then the 201 body. A guard trigger's <c>23514</c>, the same-target unique index, the payload column's
    /// own size bound, or a target that disappeared between the read and the insert are mapped to the refusals the caller
    /// can act on; anything else is a real fault and surfaces.
    /// </summary>
    private static async ValueTask<Result<QueuedChange>> InsertAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        QueueRequest request,
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
                    project_id = projectId,
                    target_kind = AuthoringChangeTargetKind.ChecklistItem,
                    target_id = request.TargetId,
                    change_type = request.Payload.ChangeType,
                    payload = request.Payload.RawJson,
                    base_version = TargetVersion(versions, request.TargetId),
                    affected_versions = AuthoringChangeJson.Versions(versions),
                    author_membership_id = author,
                    reason = request.Reason,
                },
                cancellationToken).ConfigureAwait(false);

            if (inserted is null)
            {
                throw new InvalidOperationException("the change insert returned no row");
            }

            audit.Describe(new AuditObject("authoring_change", id.ToString("D")));
            EvidgateTelemetry.RecordAuthoringChangeQueued(request.Payload.ChangeType);

            var baseVersion = TargetVersion(versions, request.TargetId);

            return Result.Success(
                new QueuedChange(id, "pending", baseVersion, inserted.CreatedAt),
                201);
        }
        catch (PostgresException exception) when (IsPendingOverlap(exception))
        {
            return Result.Failure<QueuedChange>(AuthoringChangeErrors.ChangeAlreadyPending());
        }
        catch (PostgresException exception) when (IsPayloadTooLarge(exception))
        {
            return Result.Failure<QueuedChange>(AuthoringChangeErrors.PayloadInvalid("/payload"));
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

    /// <summary>
    /// True for the payload column's own octet bound: jsonb's normalised text can exceed the bytes the caller sent, so
    /// this CHECK can still fire after the reader's cap and must not surface as a 500.
    /// </summary>
    private static bool IsPayloadTooLarge(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.CheckViolation
        && string.Equals(exception.ConstraintName, "authoring_change_payload_size_check", StringComparison.Ordinal);
}
