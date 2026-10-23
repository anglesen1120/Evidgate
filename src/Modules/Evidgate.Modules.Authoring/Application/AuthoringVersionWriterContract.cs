using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>
/// The published face of <see cref="AuthoringVersionWriter"/> (00 §5.5: modules talk through <c>.Contracts</c> only), so
/// 13.1.2's batched apply and 13.3.1's generation call the same writer the module's own commands do instead of reaching
/// into the module's internals.
/// </summary>
/// <remarks>
/// The translation is the only thing this type does: a <see cref="Result{T}"/> failure becomes the contract's
/// <see cref="AuthoringRefusal"/> with the same code, status and details, so a cross-module caller renders the 00 §9.2
/// envelope without a reference to the shared kernel.
/// </remarks>
internal sealed class AuthoringVersionWriterContract(AuthoringVersionWriter writer) : IAuthoringVersionWriter
{
    /// <inheritdoc />
    public async ValueTask<AuthoringWriteResult<AuthoringAppendOutcome>> AppendAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        IReadOnlyList<EntityChange> changes,
        AuthoringAppendContext? context = null,
        CancellationToken cancellationToken = default) =>
        Translate(await writer.AppendAsync(
            orgId,
            projectId,
            batchId,
            actorMembershipId,
            changes,
            context,
            cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async ValueTask<AuthoringWriteResult<AuthoringMoveOutcome>> MoveManualTestCaseAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        Guid caseId,
        Guid toItemId,
        string? reason = null,
        AuthoringAppendContext? context = null,
        CancellationToken cancellationToken = default) =>
        Translate(await writer.MoveManualTestCaseAsync(
            orgId,
            projectId,
            batchId,
            actorMembershipId,
            caseId,
            toItemId,
            reason,
            context,
            cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async ValueTask<AuthoringWriteResult<AuthoringVersionRef>> RestoreAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        AuthoringEntityKind entityKind,
        Guid entityId,
        string? reason = null,
        Guid? fromVersionId = null,
        AuthoringAppendContext? context = null,
        CancellationToken cancellationToken = default) =>
        Translate(await writer.RestoreAsync(
            orgId,
            projectId,
            batchId,
            actorMembershipId,
            entityKind,
            entityId,
            reason,
            fromVersionId,
            context,
            cancellationToken).ConfigureAwait(false));

    private static AuthoringWriteResult<T> Translate<T>(Result<T> result) => result.IsSuccess
        ? AuthoringWriteResult<T>.Success(result.Value!)
        : AuthoringWriteResult<T>.Failure(Refusal(result.Error!));

    private static AuthoringRefusal Refusal(Error error) => new(
        error.Code,
        error.Message,
        error.Status,
        error.Details is { Count: > 0 } details
            ? [.. details.Select(detail => new AuthoringRefusalDetail(detail.Path, detail.Reason))]
            : []);
}
