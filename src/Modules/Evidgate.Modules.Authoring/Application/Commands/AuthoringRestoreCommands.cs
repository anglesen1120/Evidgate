using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// The answer of a restore (13.1.1 §5 routes 6, 11, 18 and 24): the entity's <c>version</c> after the call, which is the
/// previous one plus one (AC8) and the tag the next write needs (AC19).
/// </summary>
/// <remarks>
/// Module-internal on purpose: the wire schema of these four operations is declared in <c>contracts/openapi.yaml</c> by
/// the step that maps the routes, and no other module reads a restore answer, so the type does not belong in
/// <c>.Contracts</c> yet (00 §5.5 publishes only the writer, which 13.1.2 and 13.3.1 call).
/// </remarks>
/// <param name="Version">The entity's version after the restore.</param>
internal sealed record AuthoringRestoreResponse(int Version);

/// <summary>
/// Restore a soft-deleted feature node (<c>POST /v1/projects/{p}/authoring/nodes/{n}/restore</c>, 13.1.1 §5 route 6):
/// a new <c>restore</c> version carrying the last live snapshot, <c>deleted_at</c> cleared and the position re-appended
/// at <c>max + 1</c> when a live sibling holds it (AC8). <paramref name="ExpectedVersion"/> is the <c>If-Match</c> the
/// soft-deleted row keeps (AC19).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="NodeId">The node from the route.</param>
/// <param name="ExpectedVersion">The version the caller last read (<c>If-Match</c>).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1, AC18).</param>
internal sealed record RestoreNodeCommand(
    Guid ProjectId,
    Guid NodeId,
    int ExpectedVersion,
    string IdempotencyKey)
    : ICommand<Result<AuthoringRestoreResponse>>, IIdempotentCommand;

/// <summary>
/// Restore a soft-deleted checklist item (<c>POST …/checklist-items/{i}/restore</c>, 13.1.1 §5 route 11): a new
/// <c>restore</c> version, <c>deleted_at</c> cleared and the item back in state <c>active</c> (§6, AC8). A parent node that
/// is still soft-deleted is refused with <c>422 authoring_parent_invalid</c>.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="ItemId">The checklist item from the route.</param>
/// <param name="ExpectedVersion">The version the caller last read (<c>If-Match</c>).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1, AC18).</param>
internal sealed record RestoreChecklistItemCommand(
    Guid ProjectId,
    Guid ItemId,
    int ExpectedVersion,
    string IdempotencyKey)
    : ICommand<Result<AuthoringRestoreResponse>>, IIdempotentCommand;

/// <summary>
/// Restore a soft-deleted manual test case (<c>POST …/manual-cases/{m}/restore</c>, 13.1.1 §5 route 18): a new
/// <c>restore</c> version with the same <c>code</c> and links, back in state <c>draft</c> (§6, AC8). A parent item that is
/// still soft-deleted is refused with <c>422 authoring_parent_invalid</c>.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="CaseId">The manual test case from the route.</param>
/// <param name="ExpectedVersion">The version the caller last read (<c>If-Match</c>).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1, AC18).</param>
internal sealed record RestoreManualCaseCommand(
    Guid ProjectId,
    Guid CaseId,
    int ExpectedVersion,
    string IdempotencyKey)
    : ICommand<Result<AuthoringRestoreResponse>>, IIdempotentCommand;

/// <summary>
/// Re-apply one stored version of the entity it describes (<c>POST …/versions/{v}/restore</c>, 13.1.1 §5 route 24): the
/// answer is the entity's new <c>version</c> (AC8). The route takes no <c>If-Match</c> — the version names the state to
/// re-apply, so there is no tag to be stale against — and the version is resolved inside the caller's org <b>and</b>
/// project, so another project's or another org's version is a plain <c>404</c> (AC10).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="VersionId">The <c>authoring_version</c> id from the route.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1, AC18).</param>
internal sealed record RestoreVersionCommand(
    Guid ProjectId,
    Guid VersionId,
    string IdempotencyKey)
    : ICommand<Result<AuthoringRestoreResponse>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="RestoreNodeCommand"/>: the project gate, the project's liveness (AC10: a soft-deleted project
/// hides its nodes), then <b>one</b> <see cref="AuthoringVersionWriter.RestoreAsync"/> call, which is the only append path
/// of the module. Every refusal of AC8 is the writer's own — a node whose parent is still soft-deleted
/// (<c>422 parent_deleted</c>), an unknown id (<c>404</c>), a stale <c>If-Match</c> (<c>412</c>, compared under the row
/// lock) and a node that is not soft-deleted (<c>409 invalid_transition</c>) — and writes nothing.
/// </summary>
/// <remarks>
/// An authoring write is audit-once (AC18): the pipeline appends the row described here, and an idempotency replay
/// suppresses it because the original request already wrote its own.
/// </remarks>
[AuditedAction("authoring.node.restore")]
[Permission(Permissions.Authoring.Write)]
internal sealed class RestoreNodeCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    AuthoringVersionWriter writer,
    IAuditTrail audit)
    : ICommandHandler<RestoreNodeCommand, Result<AuthoringRestoreResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AuthoringRestoreResponse>> Handle(
        RestoreNodeCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(RestoreNodeCommand));

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AuthoringRestoreResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false)
            is { } missingProject)
        {
            return Result.Failure<AuthoringRestoreResponse>(missingProject);
        }

        var actor = await AuthoringRequestScope
            .ActorMembershipAsync(db, orgId, principals, cancellationToken)
            .ConfigureAwait(false);

        var restored = await writer.RestoreAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            AuthoringEntityKind.FeatureNode,
            command.NodeId,
            expectedVersion: command.ExpectedVersion,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!restored.IsSuccess)
        {
            return Result.Failure<AuthoringRestoreResponse>(restored.Error!);
        }

        audit.Describe(new AuditObject(RestoreAudit.NodeObjectType, command.NodeId.ToString("D")));

        return Result.Success(new AuthoringRestoreResponse(restored.Value!.VersionNo));
    }
}

/// <summary>
/// Handler of <see cref="RestoreChecklistItemCommand"/> (route 11): the same shape as the node restore — project gate,
/// project liveness, one <see cref="AuthoringVersionWriter.RestoreAsync"/> call — with AC8's item rules applied by the
/// writer (a deleted parent node is <c>422 parent_deleted</c>, and the restored item is <c>active</c>).
/// </summary>
[AuditedAction("authoring.checklist_item.restore")]
[Permission(Permissions.Authoring.Write)]
internal sealed class RestoreChecklistItemCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    AuthoringVersionWriter writer,
    IAuditTrail audit)
    : ICommandHandler<RestoreChecklistItemCommand, Result<AuthoringRestoreResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AuthoringRestoreResponse>> Handle(
        RestoreChecklistItemCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(RestoreChecklistItemCommand));

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AuthoringRestoreResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false)
            is { } missingProject)
        {
            return Result.Failure<AuthoringRestoreResponse>(missingProject);
        }

        var actor = await AuthoringRequestScope
            .ActorMembershipAsync(db, orgId, principals, cancellationToken)
            .ConfigureAwait(false);

        var restored = await writer.RestoreAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            AuthoringEntityKind.ChecklistItem,
            command.ItemId,
            expectedVersion: command.ExpectedVersion,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!restored.IsSuccess)
        {
            return Result.Failure<AuthoringRestoreResponse>(restored.Error!);
        }

        audit.Describe(new AuditObject(RestoreAudit.ItemObjectType, command.ItemId.ToString("D")));

        return Result.Success(new AuthoringRestoreResponse(restored.Value!.VersionNo));
    }
}

/// <summary>
/// Handler of <see cref="RestoreManualCaseCommand"/> (route 18): the same shape again, with AC8's case rules applied by
/// the writer (a deleted parent item is <c>422 parent_deleted</c>, and the restored case is <c>draft</c> with its
/// <c>code</c> unchanged).
/// </summary>
[AuditedAction("authoring.manual_case.restore")]
[Permission(Permissions.Authoring.Write)]
internal sealed class RestoreManualCaseCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    AuthoringVersionWriter writer,
    IAuditTrail audit)
    : ICommandHandler<RestoreManualCaseCommand, Result<AuthoringRestoreResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AuthoringRestoreResponse>> Handle(
        RestoreManualCaseCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(RestoreManualCaseCommand));

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AuthoringRestoreResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false)
            is { } missingProject)
        {
            return Result.Failure<AuthoringRestoreResponse>(missingProject);
        }

        var actor = await AuthoringRequestScope
            .ActorMembershipAsync(db, orgId, principals, cancellationToken)
            .ConfigureAwait(false);

        var restored = await writer.RestoreAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            AuthoringEntityKind.ManualTestCase,
            command.CaseId,
            expectedVersion: command.ExpectedVersion,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!restored.IsSuccess)
        {
            return Result.Failure<AuthoringRestoreResponse>(restored.Error!);
        }

        audit.Describe(new AuditObject(RestoreAudit.CaseObjectType, command.CaseId.ToString("D")));

        return Result.Success(new AuthoringRestoreResponse(restored.Value!.VersionNo));
    }
}

/// <summary>
/// Handler of <see cref="RestoreVersionCommand"/> (route 24): reads the named version inside the caller's org and project
/// to learn which entity it describes, then calls <see cref="AuthoringVersionWriter.RestoreAsync"/> with that version as
/// the snapshot to re-apply. An unknown version, another project's version and another org's version are the same
/// <c>404</c> (AC10); a version whose entity row is gone is the writer's own <c>404</c> (AC8).
/// </summary>
/// <remarks>
/// The version read is a plain read of <c>authoring_version</c> under the tenant scope (RLS scopes it by org, the
/// statement by project), so the handler adds no write path: the only append is the writer's.
/// </remarks>
[AuditedAction("authoring.version.restore")]
[Permission(Permissions.Authoring.Write)]
internal sealed class RestoreVersionCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    AuthoringVersionWriter writer,
    IAuditTrail audit)
    : ICommandHandler<RestoreVersionCommand, Result<AuthoringRestoreResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AuthoringRestoreResponse>> Handle(
        RestoreVersionCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(RestoreVersionCommand));

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AuthoringRestoreResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false)
            is { } missingProject)
        {
            return Result.Failure<AuthoringRestoreResponse>(missingProject);
        }

        var version = await db.QuerySingleOrDefaultAsync<AuthoringVersionWriter.StoredVersion>(
            AuthoringSql.ReadVersion,
            new { org_id = orgId, project_id = command.ProjectId, id = command.VersionId },
            cancellationToken).ConfigureAwait(false);

        if (version is null || !RestoreAudit.TryKindOf(version.EntityKind, out var kind))
        {
            return Result.Failure<AuthoringRestoreResponse>(
                AuthoringErrors.NotFound("no version with that id is visible in this project"));
        }

        var actor = await AuthoringRequestScope
            .ActorMembershipAsync(db, orgId, principals, cancellationToken)
            .ConfigureAwait(false);

        var restored = await writer.RestoreAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            kind,
            version.EntityId,
            fromVersionId: command.VersionId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!restored.IsSuccess)
        {
            return Result.Failure<AuthoringRestoreResponse>(restored.Error!);
        }

        audit.Describe(new AuditObject(RestoreAudit.ObjectTypeOf(kind), version.EntityId.ToString("D")));

        return Result.Success(new AuthoringRestoreResponse(restored.Value!.VersionNo));
    }
}

/// <summary>
/// The two spellings the four restore handlers share: the audit object type of each entity kind (the strings the other
/// authoring handlers audit under) and the reverse of <see cref="AuthoringWireValues.EntityKindName"/> route 24 needs.
/// </summary>
internal static class RestoreAudit
{
    /// <summary>The audit object type of a feature node, as routes 2-6 record it.</summary>
    internal const string NodeObjectType = "feature_node";

    /// <summary>The audit object type of a checklist item, as routes 8-11 record it.</summary>
    internal const string ItemObjectType = "checklist_item";

    /// <summary>The audit object type of a manual test case, as routes 13-19 record it.</summary>
    internal const string CaseObjectType = "manual_test_case";

    /// <summary>The audit object type of an entity kind.</summary>
    /// <param name="kind">The entity kind.</param>
    internal static string ObjectTypeOf(AuthoringEntityKind kind) => kind switch
    {
        AuthoringEntityKind.FeatureNode => NodeObjectType,
        AuthoringEntityKind.ChecklistItem => ItemObjectType,
        AuthoringEntityKind.ManualTestCase => CaseObjectType,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
    };

    /// <summary>
    /// The entity kind a stored <c>authoring_version.entity_kind</c> names, through the one naming function
    /// (<see cref="AuthoringWireValues.EntityKindName"/>) so the two cannot drift. False for a value this build does not
    /// know, which route 24 answers as the same <c>404</c> an unknown version gets.
    /// </summary>
    /// <param name="entityKind">The stored text.</param>
    /// <param name="kind">The resolved kind.</param>
    internal static bool TryKindOf(string entityKind, out AuthoringEntityKind kind)
    {
        if (string.Equals(entityKind, AuthoringWireValues.EntityKindName(AuthoringEntityKind.FeatureNode), StringComparison.Ordinal))
        {
            kind = AuthoringEntityKind.FeatureNode;
            return true;
        }

        if (string.Equals(entityKind, AuthoringWireValues.EntityKindName(AuthoringEntityKind.ChecklistItem), StringComparison.Ordinal))
        {
            kind = AuthoringEntityKind.ChecklistItem;
            return true;
        }

        if (string.Equals(entityKind, AuthoringWireValues.EntityKindName(AuthoringEntityKind.ManualTestCase), StringComparison.Ordinal))
        {
            kind = AuthoringEntityKind.ManualTestCase;
            return true;
        }

        kind = default;
        return false;
    }
}
