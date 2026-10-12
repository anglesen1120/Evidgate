using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Endpoints;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The task types 06.1.2's <c>complete</c> hands to the background through the outbox (00 §5.3 C7, T19/T86): the
/// async masking scan of a <c>ready</c> artifact and the delete of the <c>pending/</c> staging object. Both names
/// are the wire contract's (<c>task.task_type</c>, 06.1.2 §5); the consumer only claims the types a registered
/// handler declares, so an undeployed handler leaves its rows <c>queued</c> instead of dead-lettering them.
/// </summary>
internal static class EvidenceTaskTypes
{
    /// <summary>Masking scan of a <c>ready</c> artifact; its handler is 06.2.3-T1.</summary>
    public const string SecretScan = "artifact_secret_scan";

    /// <summary>Deletes the staging object after the completion committed; handled by <see cref="ArtifactStagingDeleteTaskHandler"/>.</summary>
    public const string StagingDelete = "artifact_staging_delete";
}

/// <summary>
/// The verdict of the storage verification T86 steps 2-4 describes (06.1.2 §5): what the store reported about the
/// object, derived by <see cref="ArtifactCompletionVerifier"/> from server-side facts only — the route has no body,
/// so nothing here is request input. <see cref="Verified"/> decides which member carries the outcome: a verified
/// verdict the length the store reported, a rejected one the reason code the writer persists.
/// </summary>
/// <param name="Verified">True when the object matched the declared size and checksum and was promoted to the final key.</param>
/// <param name="VerifiedBytes">The stored length the <c>HEAD</c> reported; <c>null</c> on a rejection.</param>
/// <param name="FailureCode">One of the <see cref="ArtifactFailureCode"/> reasons; <c>null</c> when verified.</param>
internal sealed record ArtifactCompletionVerdict(bool Verified, long? VerifiedBytes, string? FailureCode)
{
    /// <summary>The object matched and now sits at the final key (or already did, on the T86 retry path).</summary>
    /// <param name="verifiedBytes">The stored length the store reported for the object it verified.</param>
    public static ArtifactCompletionVerdict Accept(long? verifiedBytes) => new(true, verifiedBytes, null);

    /// <summary>
    /// The object did not match the declared facts, or is absent. <paramref name="failureCode"/> is <c>null</c>
    /// only when the verdict mirrors an already-<c>rejected</c> row that carries no code (the row is returned
    /// unchanged, so nothing is written).
    /// </summary>
    /// <param name="failureCode">One of the <see cref="ArtifactFailureCode"/> reasons.</param>
    public static ArtifactCompletionVerdict Reject(string? failureCode) => new(false, null, failureCode);
}

/// <summary>
/// Complete one presigned upload (06.1.2 §5, <c>POST /v1/artifacts/{a}/complete</c>): the writer half of T86. The
/// endpoint's <see cref="ArtifactCompletionVerifier"/> already read the row, <c>HEAD</c>ed the object and promoted
/// it — all with no transaction open (00 §5.7 P5) — so this command only commits the verdict inside the C2 tenant
/// transaction: guard the state, write <c>ready</c> with the verified size, <c>scan_state=pending</c> and the two
/// outbox rows, or <c>rejected</c> with its reason code and no outbox rows. <c>org_id</c> and <c>project_id</c> are
/// never command members — they come from the tenant transaction (00 §5.4.1, T3).
/// <para>
/// The verdict is part of the idempotency hash (the store canonicalises the serialized command), so a replay must
/// derive the identical one: a row already <c>ready</c> or <c>rejected</c> yields the stored outcome, which is why
/// the same <c>Idempotency-Key</c> replays instead of conflicting. <paramref name="CredentialId"/> is the caller's
/// token identity, carried so the hash binds the key to it: another principal replaying the key is 409
/// <c>idempotency_conflict</c>, never the first caller's response (T1 decision 11, security review F3).
/// </para>
/// </summary>
/// <param name="ArtifactId">The artifact the route addressed.</param>
/// <param name="Verdict">The server-derived storage verdict.</param>
/// <param name="CredentialId">The caller's token id (<c>jti</c>), or <c>null</c> when it has none.</param>
/// <param name="IdempotencyKey">The required <c>Idempotency-Key</c> header (T92).</param>
internal sealed record CompleteArtifactUpload(
    Guid ArtifactId,
    ArtifactCompletionVerdict Verdict,
    string? CredentialId,
    string IdempotencyKey) : ICommand<Result<CompleteResponse>>, IIdempotentCommand;

/// <summary>
/// Shape validation of <see cref="CompleteArtifactUpload"/> (C2 stage 2, before the tenant transaction): the route
/// carries no body and every other member is server-derived (the verdict) or a header the idempotency stage
/// validates (the <c>Idempotency-Key</c>), so the addressed artifact id is the one shape this stage owns. The
/// route constraint already refuses a non-UUID path segment; an all-zero id would be a client error.
/// </summary>
internal sealed class CompleteArtifactUploadValidator : AbstractValidator<CompleteArtifactUpload>
{
    /// <summary>Builds the rules.</summary>
    public CompleteArtifactUploadValidator()
    {
        RuleFor(command => command.ArtifactId)
            .NotEmpty()
            .WithErrorCode("required");
    }
}

/// <summary>
/// Handler of <see cref="CompleteArtifactUpload"/> (06.1.2 §5, T86 step 5): inside the tenant transaction it reads
/// the row once, guards its state (the endpoint's verdict is derived from facts read before this transaction
/// opened, so it is never trusted for the state), writes the outcome and — for a verified object — the two outbox
/// rows, in one commit (C7). A row that already holds <c>ready</c>/<c>rejected</c> is returned unchanged: a fresh
/// key on a completed artifact is a no-op, never a second scan or a second staging delete (T19/T86). The staging
/// object is deleted only by the <see cref="ArtifactStagingDeleteTaskHandler"/>, after this transaction commits.
/// </summary>
/// <remarks>
/// The version column is the concurrency token (migration 0028's trigger owns the increment), so a completion that
/// raced another one updates no row and is answered 409 <c>artifact_state_conflict</c> instead of overwriting the
/// winner. No storage call happens here: <c>HEAD</c> and the copy ran before the transaction opened (00 §5.7 P5).
/// </remarks>
[AuditedAction("artifact.complete")]
[Permission(Permissions.Artifact.Write)]
internal sealed class CompleteArtifactUploadHandler(
    ITenantDbContextFactory<EvidenceDbContext> contexts,
    IOutboxWriter outbox,
    IAuditTrail audit)
    : ICommandHandler<CompleteArtifactUpload, Result<CompleteResponse>>
{
    /// <summary>The outbox aggregate the two rows belong to (00 §5.3).</summary>
    private const string AggregateType = "artifact";

    /// <inheritdoc />
    public async ValueTask<Result<CompleteResponse>> Handle(CompleteArtifactUpload command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Evidence, nameof(CompleteArtifactUpload));

        await using var context = contexts.Create();

        // One row read in the command's tenant scope (P3); RLS and the endpoint's project check make another
        // tenant's row and another project's row unreachable here.
        var row = await context.Artifacts
            .SingleOrDefaultAsync(artifact => artifact.Id == command.ArtifactId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return Result.Failure<CompleteResponse>(EvidenceErrors.ArtifactNotFound);
        }

        if (IsStored(row.State))
        {
            return Result.Success(Respond(row));
        }

        if (!IsCompletable(row.State))
        {
            return Result.Failure<CompleteResponse>(EvidenceErrors.ArtifactStateConflict);
        }

        // A completable row is only ever rejected with a reason: the verifier derives the code from the object it
        // checked. The null-code shape of a verdict belongs to an already-rejected row, which returns above.
        var next = command.Verdict.Verified
            ? row.MarkReady(command.Verdict.VerifiedBytes)
            : row.MarkRejected(command.Verdict.FailureCode!);

        context.Entry(row).CurrentValues.SetValues(next);

        if (command.Verdict.Verified)
        {
            await EnqueueAsync(row, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another completion committed between the read above and this update; the version token is the guard.
            return Result.Failure<CompleteResponse>(EvidenceErrors.ArtifactStateConflict);
        }

        audit.Describe(new AuditObject(AggregateType, row.Id.ToString("D")));

        return Result.Success(Respond(next));
    }

    /// <summary>True when the row already holds an outcome a later <c>complete</c> returns unchanged (AC5).</summary>
    private static bool IsStored(string? state) =>
        string.Equals(state, ArtifactState.Ready, StringComparison.Ordinal)
        || string.Equals(state, ArtifactState.Rejected, StringComparison.Ordinal);

    /// <summary>True when the row may still be completed (06.1.2 §6: <c>pending_upload</c> or <c>uploaded</c>).</summary>
    private static bool IsCompletable(string? state) =>
        string.Equals(state, ArtifactState.PendingUpload, StringComparison.Ordinal)
        || string.Equals(state, ArtifactState.Uploaded, StringComparison.Ordinal);

    /// <summary>
    /// The 200 body of the stored outcome: a <c>ready</c> artifact reports the size it was verified with and the
    /// pending scan, a rejected one its reason and no size — nothing was verified — and neither carries a key.
    /// </summary>
    private static CompleteResponse Respond(Artifact artifact) =>
        string.Equals(artifact.State, ArtifactState.Ready, StringComparison.Ordinal)
            ? new CompleteResponse(artifact.Id, ArtifactState.Ready, artifact.Bytes, artifact.ScanState, null)
            : new CompleteResponse(artifact.Id, ArtifactState.Rejected, null, null, artifact.FailureCode);

    /// <summary>
    /// The two outbox rows of 06.1.2 §5 (AC4), written on this scope's connection and transaction so they commit
    /// with the state change or not at all (C7). The idempotency keys are per artifact, so a second commit cannot
    /// enqueue a second scan or a second delete; the staging key is derived, never read from the row (T1 decision 7).
    /// </summary>
    /// <param name="artifact">The row that just became <c>ready</c>.</param>
    /// <param name="cancellationToken">Cancels the statements.</param>
    private async Task EnqueueAsync(Artifact artifact, CancellationToken cancellationToken)
    {
        var stagingKey = ArtifactKeyMinter.StagingKey(artifact.OrgId, artifact.Id);

        await outbox.EnqueueAsync(
            EvidenceTaskTypes.SecretScan,
            JsonSerializer.Serialize(new { artifact_id = artifact.Id }),
            AggregateType,
            artifact.Id,
            $"evidence:artifact:{artifact.Id:D}:secret_scan",
            cancellationToken).ConfigureAwait(false);

        await outbox.EnqueueAsync(
            EvidenceTaskTypes.StagingDelete,
            JsonSerializer.Serialize(new { artifact_id = artifact.Id, staging_key = stagingKey }),
            AggregateType,
            artifact.Id,
            $"evidence:artifact:{artifact.Id:D}:staging_delete",
            cancellationToken).ConfigureAwait(false);
    }
}
