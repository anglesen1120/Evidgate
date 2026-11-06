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
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The task types 06.1.2's <c>complete</c> hands to the background through the outbox (00 §5.3 C7, T19/T86): the
/// async masking scan of a <c>ready</c> artifact, the delete of the <c>pending/</c> staging object and — when
/// out-of-process parsing is composed — the parse of a raw report (06.3.6-T1.4). Every name is the wire contract's
/// (<c>task.task_type</c>, 06.1.2 §5); the consumer only claims the types a registered handler declares, so an
/// undeployed handler leaves its rows <c>queued</c> instead of dead-lettering them.
/// </summary>
internal static class EvidenceTaskTypes
{
    /// <summary>Masking scan of a <c>ready</c> artifact; its handler is 06.2.3-T1.</summary>
    public const string SecretScan = "artifact_secret_scan";

    /// <summary>Deletes the staging object after the completion committed; handled by <see cref="ArtifactStagingDeleteTaskHandler"/>.</summary>
    public const string StagingDelete = "artifact_staging_delete";

    /// <summary>
    /// Parses one <c>raw_report</c> artifact in the resource-limited child (06.3.6 §5, T1.4); handled by
    /// <c>ParseArtifactTaskHandler</c>, which only the Worker composes and only with <c>EVIDGATE__PARSE__OUTOFPROCESS</c>
    /// on.
    /// </summary>
    public const string Parse = "artifact_parse";

    /// <summary>
    /// Maps one stored <c>raw_report</c> artifact to <c>test.finished</c> events under the parser its producer kind
    /// registers; handled by <see cref="ParseRawReportHandler"/>, which writes events rather than a verdict.
    /// </summary>
    public const string ReportParse = "raw_report_parse";
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
/// transaction: guard the state, write <c>ready</c> with the verified size, <c>scan_state=pending</c> and the outbox
/// rows, or <c>rejected</c> with its reason code and no outbox rows. <c>org_id</c> and <c>project_id</c> are
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
/// opened, so it is never trusted for the state), re-reads the project's status so a project that flipped to
/// <c>deleted</c> since the verifier cannot commit, writes the outcome and — for a verified object — the outbox
/// rows (the scan and the staging delete, plus the parse rows of a raw report when out-of-process parsing is on),
/// in one commit (C7). A row that already holds <c>ready</c>/<c>rejected</c> is returned unchanged: a fresh key on
/// a completed artifact is a no-op, never a second scan, a second staging delete or a second parse. The
/// staging object is deleted only by the <see cref="ArtifactStagingDeleteTaskHandler"/>,
/// after this transaction commits, and the parse runs only in the Worker (<c>ParseArtifactTaskHandler</c> and
/// <see cref="ParseRawReportHandler"/>).
/// </summary>
/// <remarks>
/// The version column is the concurrency token (migration 0028's trigger owns the increment) and the artifact
/// update is written FIRST, so a completion that raced another one blocks on the winner's row lock and then
/// updates no row: it re-reads the committed outcome and answers it — the same stored-state no-op a replay
/// answers (AC5) — instead of overwriting the winner, and the outbox inserts below can never produce a second
/// scan or a 23505. No storage call happens here: <c>HEAD</c> and the copy ran before the transaction opened
/// (00 §5.7 P5).
/// </remarks>
/// <param name="contexts">The tenant write model the row is read and written through.</param>
/// <param name="outbox">The outbox the background rows are written on the same connection and transaction.</param>
/// <param name="audit">The trail the completed command is recorded on.</param>
/// <param name="db">The Dapper path, used only for the project-status guard.</param>
/// <param name="parseOptions">
/// The parse runner's options, read for the one switch that decides whether a <c>raw_report</c> schedules its parse
/// rows (<c>EVIDGATE__PARSE__OUTOFPROCESS</c>, 06.3.6 §11). Both hosts bind them, because the Api commits the rows
/// even though only the Worker runs the tasks.
/// </param>
/// <param name="producerKinds">
/// The run's manifest-declared producer kind, read in this transaction's tenant scope: the report's dialect is server
/// truth, never a value the upload declared.
/// </param>
[AuditedAction("artifact.complete")]
[Permission(Permissions.Artifact.Write)]
internal sealed class CompleteArtifactUploadHandler(
    ITenantDbContextFactory<EvidenceDbContext> contexts,
    IOutboxWriter outbox,
    IAuditTrail audit,
    ITenantDb db,
    IOptions<ParseRunnerOptions> parseOptions,
    IRawReportProducerKindReader producerKinds)
    : ICommandHandler<CompleteArtifactUpload, Result<CompleteResponse>>
{
    /// <summary>The outbox aggregate the rows belong to (00 §5.3).</summary>
    private const string AggregateType = "artifact";

    /// <summary>The <c>project.status</c> that refuses a completion (migration 0005, 06.4.4 AC2).</summary>
    private const string ProjectDeleted = "deleted";

    /// <summary>
    /// The project's status for one artifact's <c>project_id</c>, read on this command's tenant scope (RLS applies,
    /// C3). The verifier checked the project before its storage calls and before this transaction opened, so the
    /// writer re-reads it here — the same guard, at commit time — so a project that flips to <c>deleted</c> in
    /// between cannot commit <c>ready</c> bytes and outbox rows (06.1.2 §5, 06.4.4 AC2).
    /// </summary>
    private const string ProjectStatusSql = "select status from public.project where id = @project_id";

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
            // A replay (or a fresh key on a completed artifact) changes nothing, but the authorization stage still
            // records an `artifact.complete` entry for the success (RunAuditedAsync): naming the artifact keeps
            // that row actionable instead of leaving its subject null (review C-F2). Suppressing it would hide the
            // attempt, which the trail is meant to keep.
            audit.Describe(new AuditObject(AggregateType, row.Id.ToString("D")));

            return Result.Success(Respond(row));
        }

        // The project guard the verifier applied before its storage calls, re-applied here at commit time: a
        // project that flipped to `deleted` between the verifier and this transaction must not commit `ready`
        // bytes and outbox rows (06.1.2 §5, 06.4.4 AC2). A stored outcome returned above is never re-guarded, so
        // only a still-pending artifact is refused — the same 409 the verifier answers (review S-F3).
        var projectStatus = await db
            .ScalarAsync<string>(ProjectStatusSql, new { project_id = row.ProjectId }, cancellationToken)
            .ConfigureAwait(false);

        if (projectStatus is null || string.Equals(projectStatus, ProjectDeleted, StringComparison.Ordinal))
        {
            return Result.Failure<CompleteResponse>(EvidenceErrors.ProjectDeleting);
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

        // The artifact UPDATE goes FIRST, before the outbox inserts: it takes the row lock the version token
        // guards, so a concurrent completion of the same artifact blocks here and — once this transaction
        // commits — its own update matches no row instead of racing the outbox inserts below into a 23505
        // (T19/T86: one scan and one staging-delete row per artifact, never two).
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another completion committed between the read above and this update; the version token is the
            // guard, and that completion is the winner. This request is the loser and answers the stored outcome
            // (the idempotent no-op of AC5) instead of overwriting it — never a 500.
            var loser = await LoserAsync(context, command.ArtifactId, cancellationToken).ConfigureAwait(false);

            if (loser.IsSuccess)
            {
                // The loser's stored-outcome answer is still an `artifact.complete` the authorization stage
                // records, so it names the artifact exactly like the stored-state no-op above (review C-F2).
                audit.Describe(new AuditObject(AggregateType, command.ArtifactId.ToString("D")));
            }

            return loser;
        }

        if (command.Verdict.Verified)
        {
            try
            {
                await EnqueueAsync(row, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Defensive (T19): the outbox idempotency keys are per artifact (`outbox_org_idem_uq`, 0006), so
                // a completion that slipped past the row lock cannot enqueue a second scan or staging delete. The
                // 23505 has aborted this transaction, so the stored outcome cannot be re-read here: the request is
                // the loser and answers the same 409 the version guard produces, never a 500.
                return Result.Failure<CompleteResponse>(EvidenceErrors.ArtifactStateConflict);
            }
        }

        audit.Describe(new AuditObject(AggregateType, row.Id.ToString("D")));

        return Result.Success(Respond(next));
    }

    /// <summary>
    /// The loser of a concurrent completion (T19/T86): the version token made this transaction's artifact update
    /// touch no row, so the winner's outcome is committed. It is re-read in this same scope — the tenant scope is
    /// READ COMMITTED, so the winner's commit is visible — and answered verbatim, the same stored-state no-op a
    /// replay answers (AC5). Only a row that is somehow no outcome (vanished, or still neither <c>ready</c> nor
    /// <c>rejected</c>) falls back to the 409 the contract names.
    /// </summary>
    /// <param name="context">This command's write model, whose transaction is still open and uncommitted.</param>
    /// <param name="artifactId">The artifact the route addressed.</param>
    /// <param name="cancellationToken">Cancels the re-read.</param>
    private static async Task<Result<CompleteResponse>> LoserAsync(
        EvidenceDbContext context,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        var stored = await context.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(artifact => artifact.Id == artifactId, cancellationToken)
            .ConfigureAwait(false);

        return stored is not null && IsStored(stored.State)
            ? Result.Success(Respond(stored))
            : Result.Failure<CompleteResponse>(EvidenceErrors.ArtifactStateConflict);
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
    /// The outbox rows of 06.1.2 §5 (AC4) and 06.3.6-T1.4, written on this scope's connection and transaction so they
    /// commit with the state change or not at all (C7). The idempotency keys are per artifact, so a second commit
    /// cannot enqueue a second scan, a second delete or a second parse; the staging key is derived, never read from
    /// the row (T1 decision 7). The parse row is written only when out-of-process parsing is composed and only for
    /// <c>raw_report</c>, the one kind with a parse arm: with the flag off nothing extra is scheduled, which is the
    /// in-process rollback 06.3.6 §11 names, and the other seven kinds are never parsed.
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

        if (parseOptions.Value.OutOfProcess
            && string.Equals(artifact.Kind, ArtifactKind.RawReport, StringComparison.Ordinal))
        {
            await outbox.EnqueueAsync(
                EvidenceTaskTypes.Parse,
                JsonSerializer.Serialize(new { artifact_id = artifact.Id }),
                AggregateType,
                artifact.Id,
                $"evidence:artifact:{artifact.Id:D}:parse",
                cancellationToken).ConfigureAwait(false);

            await EnqueueReportParseAsync(artifact, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The mapping row of a raw report: always written for the one kind that has one, because a report whose dialect
    /// cannot be resolved still leaves a report row the comparison read can show. The lookup answers the kind or the
    /// closed issue code that stood in its place, and the task carries whichever it got; the payload's names are the
    /// task's own wire shape (<c>snake_case</c>), the same text the handler binds.
    /// </summary>
    /// <param name="artifact">The row that just became <c>ready</c>; its run is the manifest read's key.</param>
    /// <param name="cancellationToken">Cancels the read and the statement.</param>
    private async Task EnqueueReportParseAsync(Artifact artifact, CancellationToken cancellationToken)
    {
        var producer = await producerKinds.ReadAsync(artifact.RunId, cancellationToken).ConfigureAwait(false);

        await outbox.EnqueueAsync(
            EvidenceTaskTypes.ReportParse,
            JsonSerializer.Serialize(new
            {
                run_id = artifact.RunId,
                artifact_id = artifact.Id,
                producer_kind = producer.Kind,
                producer_issue = producer.Issue,
            }),
            AggregateType,
            artifact.Id,
            $"evidence:artifact:{artifact.Id:D}:raw_report_parse",
            cancellationToken).ConfigureAwait(false);

        EvidgateTelemetry.RecordRawReportEnqueued(producer.Issue);
    }
}
