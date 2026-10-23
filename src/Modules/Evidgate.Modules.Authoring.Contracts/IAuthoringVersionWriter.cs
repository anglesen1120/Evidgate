namespace Evidgate.Modules.Authoring.Contracts;

/// <summary>The version context of one writer call: where the change came from and why (13.1.1 AC5).</summary>
public sealed record AuthoringAppendContext
{
    /// <summary>Who or what produced the change; defaults to <see cref="AuthoringSource.Manual"/>.</summary>
    public AuthoringSource Source { get; init; } = AuthoringSource.Manual;

    /// <summary>An optional 1..500 character reason recorded on every version row of the call.</summary>
    public string? Reason { get; init; }

    /// <summary>The <c>healing_llm_call.id</c> of the generation run; required exactly when <see cref="Source"/> is <see cref="AuthoringSource.Ai"/>.</summary>
    public Guid? AiRunId { get; init; }
}

/// <summary>One appended (or re-appended) version: the row the timeline and generation provenance read (13.1.1 AC9).</summary>
/// <param name="VersionId">The <c>authoring_version.id</c>.</param>
/// <param name="EntityKind">Which entity the version describes.</param>
/// <param name="EntityId">The entity.</param>
/// <param name="VersionNo">The version number, equal to the entity row's <c>version</c> after the change.</param>
/// <param name="ContentHash"><c>sha256:&lt;hex&gt;</c> over the RFC 8785 canonical form of the snapshot (00 §7).</param>
/// <param name="BatchId">The <c>batch_id</c> shared by every row of one command (§5: the rows <em>are</em> the version).</param>
public sealed record AuthoringVersionRef(
    Guid VersionId,
    AuthoringEntityKind EntityKind,
    Guid EntityId,
    int VersionNo,
    string ContentHash,
    Guid BatchId);

/// <summary>What one <see cref="IAuthoringVersionWriter.AppendAsync"/> call wrote.</summary>
/// <param name="Versions">One entry per changed entity, in the order the caller listed them.</param>
/// <param name="UnchangedEntityIds">The entities whose snapshot equalled their last one, so no version row was written (AC5's no-op).</param>
public sealed record AuthoringAppendOutcome(IReadOnlyList<AuthoringVersionRef> Versions, IReadOnlyList<Guid> UnchangedEntityIds);

/// <summary>What <see cref="IAuthoringVersionWriter.MoveManualTestCaseAsync"/> did (13.1.1 AC22).</summary>
/// <param name="EntityVersion">The case's <c>version</c> after the call — unchanged for a move onto the current item.</param>
/// <param name="Version">The one <c>update</c> row, or null when the case already sat under the target item.</param>
public sealed record AuthoringMoveOutcome(int EntityVersion, AuthoringVersionRef? Version)
{
    /// <summary>True when the case was re-parented and one version row was appended.</summary>
    public bool Moved => Version is not null;
}

/// <summary>One reason of a refusal, in the shape of the 00 §9.2 <c>details[]</c> entry.</summary>
/// <param name="Path">The offending member, e.g. <c>/changes/1/content/title</c>.</param>
/// <param name="Reason">The stable machine reason, e.g. <c>parent_kind_invalid</c>.</param>
public sealed record AuthoringRefusalDetail(string Path, string Reason);

/// <summary>
/// A refusal the caller maps to the 00 §9.2 envelope: the writer is module-internal, so the contract carries the
/// code, status and details the API layer needs instead of depending on the shared envelope type.
/// </summary>
/// <param name="Code">The stable machine code, e.g. <c>authoring_parent_invalid</c>.</param>
/// <param name="Message">A secret-free summary.</param>
/// <param name="Status">The HTTP status the envelope would carry.</param>
/// <param name="Details">Per-field reasons; never empty for a 422 the caller authored.</param>
public sealed record AuthoringRefusal(string Code, string Message, int Status, IReadOnlyList<AuthoringRefusalDetail> Details)
{
    /// <summary>Builds a refusal with its details.</summary>
    /// <param name="code">The stable machine code.</param>
    /// <param name="message">A secret-free summary.</param>
    /// <param name="status">The HTTP status.</param>
    /// <param name="details">Per-field reasons.</param>
    public static AuthoringRefusal Create(string code, string message, int status, params AuthoringRefusalDetail[] details) =>
        new(code, message, status, details);
}

/// <summary>The outcome of one writer call: exactly one of <see cref="Value"/> and <see cref="Refusal"/> is set.</summary>
/// <typeparam name="T">The success value.</typeparam>
/// <param name="Value">The success value, or <c>default</c> on a refusal.</param>
/// <param name="Refusal">The refusal, or null on success.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "The two factories are the only way to build the result, and they read like the SharedKernel's Result<T> factories the caller already uses.")]
public sealed record AuthoringWriteResult<T>(T? Value, AuthoringRefusal? Refusal)
{
    /// <summary>True when the call wrote what it was asked to write.</summary>
    public bool IsSuccess => Refusal is null;

    /// <summary>A successful outcome.</summary>
    /// <param name="value">The success value.</param>
    public static AuthoringWriteResult<T> Success(T value) => new(value, null);

    /// <summary>A refused outcome; nothing is committed because the caller's transaction aborts (13.1.1 AC5).</summary>
    /// <param name="refusal">The refusal.</param>
    public static AuthoringWriteResult<T> Failure(AuthoringRefusal refusal) => new(default, refusal);
}

/// <summary>
/// The published write contract of the authoring module (00 §5.5: modules talk through <c>.Contracts</c> only),
/// implemented by 13.1.1-T1.4 and consumed by 13.1.2's batched apply (H2/H2b) and by 13.3.1's generation.
/// Every member MUST run inside the caller's tenant transaction: the writer never opens one, never commits and never
/// rolls back, so a refusal leaves the caller's transaction to abort and the AC5 "nothing is written" answer intact.
/// </summary>
public interface IAuthoringVersionWriter
{
    /// <summary>
    /// Appends one <c>authoring_version</c> row per changed entity under one <paramref name="batchId"/>, and — because
    /// every mutation goes through this member (§5) — performs the entity-row write too: the create inserts the row, the
    /// update writes the new snapshot, the soft delete sets <c>deleted_at</c> after its non-empty guard.
    /// </summary>
    /// <param name="orgId">The tenant; MUST equal the open scope's organization.</param>
    /// <param name="projectId">The project whose tree the entities belong to.</param>
    /// <param name="batchId">The batch; <see cref="Guid.Empty"/> is refused (13.1.1 AC5).</param>
    /// <param name="actorMembershipId">The authoring member, or null for a system action.</param>
    /// <param name="changes">One change per entity; an entity may appear once.</param>
    /// <param name="context">Source, reason and AI run of the whole call.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<AuthoringWriteResult<AuthoringAppendOutcome>> AppendAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        IReadOnlyList<EntityChange> changes,
        AuthoringAppendContext? context = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-parents one live manual test case inside the caller's transaction (13.1.1 AC22, OI-13-127): it locks the
    /// target item <em>first</em> and the case second, keeps the case's <c>code</c>, bumps its <c>version</c> and
    /// appends exactly one <c>change_kind = update</c> row carrying the new <c>checklist_item_id</c> and the reason.
    /// A move onto the item the case already sits under writes nothing.
    /// </summary>
    /// <param name="orgId">The tenant; MUST equal the open scope's organization.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="batchId">The batch of the calling command.</param>
    /// <param name="actorMembershipId">The authoring member, or null for a system action.</param>
    /// <param name="caseId">The case to move.</param>
    /// <param name="toItemId">The target checklist item of the same project.</param>
    /// <param name="reason">An optional 1..500 character reason recorded on the version row.</param>
    /// <param name="context">Source, reason and AI run of the whole call; <see cref="AuthoringAppendContext.Reason"/> is used when <paramref name="reason"/> is null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<AuthoringWriteResult<AuthoringMoveOutcome>> MoveManualTestCaseAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        Guid caseId,
        Guid toItemId,
        string? reason = null,
        AuthoringAppendContext? context = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a soft-deleted (or, from an explicit version, any) entity by re-appending a snapshot as a new version
    /// (13.1.1 AC8): it clears <c>deleted_at</c>, writes the snapshot back, re-appends it at <c>max + 1</c> when a live
    /// sibling holds its old position, and sets a restored checklist item to <c>active</c> and a restored manual test
    /// case to <c>draft</c> (§6).
    /// </summary>
    /// <param name="orgId">The tenant; MUST equal the open scope's organization.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="batchId">The batch of the calling command.</param>
    /// <param name="actorMembershipId">The authoring member, or null for a system action.</param>
    /// <param name="entityKind">Which entity to restore.</param>
    /// <param name="entityId">The entity.</param>
    /// <param name="reason">An optional 1..500 character reason recorded on the version row.</param>
    /// <param name="fromVersionId">A version of that entity whose snapshot is re-applied, or null for the entity's own last state.</param>
    /// <param name="context">Source, reason and AI run of the whole call; <see cref="AuthoringAppendContext.Reason"/> is used when <paramref name="reason"/> is null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<AuthoringWriteResult<AuthoringVersionRef>> RestoreAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        AuthoringEntityKind entityKind,
        Guid entityId,
        string? reason = null,
        Guid? fromVersionId = null,
        AuthoringAppendContext? context = null,
        CancellationToken cancellationToken = default);
}
