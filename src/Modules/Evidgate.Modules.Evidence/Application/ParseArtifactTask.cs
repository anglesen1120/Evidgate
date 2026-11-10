using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Ingestion.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// Payload of the <c>artifact_parse</c> task, as the completion's outbox row writes it: one artifact id. The tenant
/// comes from the task row (<c>task.org_id</c>), never from the payload, so a payload cannot name another tenant's
/// artifact.
/// </summary>
/// <param name="ArtifactId">The artifact whose stored report is parsed.</param>
internal sealed record ParseArtifactPayload(Guid ArtifactId);

/// <summary>
/// Runs the parsing profile over one artifact's bytes in the resource-limited child: a crash, OOM kill or timeout
/// becomes a <c>rejected</c> artifact with the stable reason <see cref="ParseRunRejection"/> maps it to, and never takes
/// the Worker down.
/// <para>
/// <b>Nothing is written before the child finishes.</b> The handler reads the row, opens the object read with the
/// kind policy's cap, hands the stream to <see cref="IParseRunner"/> and only then decides. A successful run persists
/// nothing: no consumer of the parsed records exists yet (the in-process <c>IParsing</c> callers return the profile's
/// value to their own caller and store nothing), so writing a result here would be a second, unrouted representation.
/// </para>
/// <para>
/// <b>The absence of a verdict is the freeze's own signal.</b> Evidence counts a report only when its
/// <c>artifact_parse</c> row is <c>done</c> and the completion's parse outbox row has been dispatched, and the
/// consumer writes <c>done</c> only after this handler's transaction committed, so a rejection can never be read as
/// frozen evidence; a parse that never finishes (a dead-lettered row) keeps the report out of every revision for as
/// long as the task sweep retains that row, and an operator replay after it is gone is what puts the report back in
/// front of a freeze.
/// </para>
/// <para>
/// <b>The child's own rejection is still a verdict.</b> A run that ends 0 with its marker may hold a <c>rejected</c>
/// record — the child's decision on content it refuses (a DTD, an unsafe archive entry, an input over the cap) — and
/// that record is the verdict: the artifact is rejected with the record's <c>reason</c> when that value is shaped like
/// a code word (<c>^[a-z0-9_]{1,64}$</c>) and with <see cref="ArtifactFailureCode.ParseCrashed"/> otherwise, so no text
/// the untrusted document influenced can reach a tenant-visible column.
/// </para>
/// <para>
/// <b>An object the cap no longer admits is a verdict too.</b> <see cref="IObjectStorage.GetAsync"/> refuses to
/// truncate an object above the kind's cap (a truncated report is a different document), and that refusal is
/// <see cref="ArtifactFailureCode.TooLarge"/>: retrying cannot shrink the stored object, so the artifact is rejected
/// instead of being retried to a dead letter.
/// </para>
/// <para>
/// <b>One guarded write per artifact.</b> A verdict is a single <c>UPDATE</c> whose predicate is the row's version
/// token (the entity's concurrency mapping), issued only after this attempt read the row in a parseable state — so a
/// replay after the rejection, or a concurrent attempt that already wrote, matches no row and the artifact is decided
/// once. An artifact that is already <c>rejected</c>, or not <c>ready</c>/<c>uploaded</c> any more, is not
/// parsed again at all.
/// </para>
/// <para>
/// <b>A lost race is not a verdict.</b> When the guarded <c>UPDATE</c> matches no row the handler re-reads the stored
/// state before concluding anything: a stored <c>rejected</c> means the artifact was decided, and a row that left the
/// parseable set (a delete in flight) is the same no-op the top of the handler makes. A row that is still
/// <c>ready</c>/<c>uploaded</c> was changed by some other writer (a freeze resolving <c>attempt_id</c>, an operator
/// update), so this attempt throws and the queue's redelivery parses again — dropping it would leave the artifact
/// undecided forever.
/// </para>
/// <para>
/// <b>What is not a verdict.</b> A cancellation (lease loss, shutdown) rethrows: the runner has already tree-killed
/// its child, and the queue's redelivery is the retry. A <see cref="ParseRunFailureKind.SpawnFailed"/> run throws
/// too, because no child existed to judge the artifact — the row stays as it was and the queue retries with
/// backoff. Neither path writes a state.
/// </para>
/// <para>
/// <b>Nothing secret, nothing tenant-visible beyond the code.</b> The reason is the only value a tenant sees; the
/// child's stderr may appear in one operator log line (already bounded and redacted by the runner) and never in the
/// failure code, the artifact row or a thrown exception's message.
/// </para>
/// <para>
/// <b>Transaction exception, recorded.</b> The object read and the child run happen inside the tenant
/// transaction the consumer opened around this attempt, because a task handler receives that transaction and cannot
/// release it — the same posture <see cref="ArtifactStagingDeleteTaskHandler"/> takes for its delete. This is a
/// deliberate exception to the rule that storage calls run outside a database transaction, and it is
/// temporary: <see cref="IObjectStorage.GetAsync"/>'s own remarks name it as the one recorded exception and forbid a
/// new caller from copying it.
/// <list type="bullet">
/// <item><b>Receiver:</b> the parse queue's own concurrency work, which schedules the parse outside the
/// consumer's transaction or runs it on a scope opened after the read.</item>
/// <item><b>Exposure now:</b> one idle-in-transaction connection for the length of one parse and one object read,
/// bounded by the wall clock the runner enforces and by the queue's own per-queue concurrency
/// (originally one, this queue's default).</item>
/// <item><b>Why it is not exploitable:</b> the read takes no row lock, so it blocks no writer; the transaction
/// commits only the guarded verdict <c>UPDATE</c> at the end.</item>
/// </list>
/// </para>
/// </summary>
/// <param name="contexts">The tenant write model the artifact and its kind policy are read through.</param>
/// <param name="storage">The object store the artifact's bytes are read from, capped by the kind policy.</param>
/// <param name="runner">The out-of-process runner; the only way this module reaches a parser.</param>
/// <param name="logger">The one structured line a rejection or an invisible artifact writes.</param>
internal sealed partial class ParseArtifactTaskHandler(
    ITenantDbContextFactory<EvidenceDbContext> contexts,
    IObjectStorage storage,
    IParseRunner runner,
    IOptions<IngestionLimits> limits,
    ILogger<ParseArtifactTaskHandler> logger) : ITaskHandler<ParseArtifactPayload>
{
    /// <summary>The cap name a raw report over its byte budget carries in telemetry.</summary>
    private const string ReportBytesCap = "report_bytes";

    /// <summary>The cap name a raw report over its entry budget carries in telemetry.</summary>
    private const string ReportCasesCap = "report_cases";

    /// <summary>The <c>application/xml</c> type of the XML family.</summary>
    private const string XmlType = "application/xml";

    /// <summary>The <c>text/xml</c> type of the XML family.</summary>
    private const string TextXmlType = "text/xml";

    /// <summary>The <c>application/json</c> type of the JSON family.</summary>
    private const string JsonType = "application/json";

    /// <summary>The one zip type the archive arm accepts.</summary>
    private const string ZipType = "application/zip";

    /// <summary>Every media type of this family carries this suffix (<c>application/*+xml</c>).</summary>
    private const string XmlSuffix = "+xml";

    /// <summary>Every media type of this family carries this suffix (<c>application/*+json</c>).</summary>
    private const string JsonSuffix = "+json";

    /// <summary>The prefix the suffixed families require.</summary>
    private const string ApplicationPrefix = "application/";

    /// <summary>The parameter separator a declared media type may carry (<c>application/xml; charset=utf-8</c>).</summary>
    private const char ParameterSeparator = ';';

    /// <inheritdoc />
    public string TaskType => EvidenceTaskTypes.Parse;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        ParseArtifactPayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (scope is null)
        {
            throw new InvalidOperationException(
                "artifact_parse is tenant-bound: a claimed row without an org_id is dead-lettered as missing_tenant, never run unscoped");
        }

        await using var context = contexts.Create();

        // One row read on this attempt's tenant scope (RLS applies, C3): another tenant's artifact is not a row at
        // all, so a crafted payload can never reach its object or its state.
        // Tracked: the verdict below is the row's own transition, applied through the domain and committed by the
        // version token (see the write at the end of this method), exactly as CompleteArtifactUpload applies its
        // ready/rejected verdict.
        var artifact = await context.Artifacts
            .SingleOrDefaultAsync(row => row.Id == payload.ArtifactId, cancellationToken)
            .ConfigureAwait(false);

        if (artifact is null)
        {
            // A payload naming an artifact this tenant cannot see is worth an operator's attention but is not an
            // error: the attempt compared nothing and changed nothing (the ReconcileRunTaskHandler's posture).
            LogArtifactNotFound(logger, payload.ArtifactId);
            return;
        }

        if (!IsParseable(artifact.State))
        {
            // Already rejected (parsed and decided once), or not yet ready: nothing to do, and the runner is never
            // started, so a replay after the rejection costs no child process.
            LogAlreadyDecided(logger, artifact.Id, artifact.State);
            return;
        }

        if (!string.Equals(artifact.Kind, ArtifactKind.RawReport, StringComparison.Ordinal))
        {
            // Only raw_report has a parse arm; the row was scheduled by a producer that asked for another kind, or
            // the kind changed under the task. Nothing is parsed and nothing is written.
            LogNothingToParse(logger, artifact.Id, artifact.Kind, artifact.MediaType);
            return;
        }

        if (ParseRunnerProfileFor(artifact.MediaType) is not { } profile)
        {
            // A report media type with no arm (text/html above all: the server never parses HTML).
            // The in-process path has no parse for these bytes either, so a completion is the whole work.
            LogNothingToParse(logger, artifact.Id, artifact.Kind, artifact.MediaType);
            return;
        }

        if (!ArtifactObjectKey.IsMintedFor(artifact))
        {
            // Defence in depth over the 0028 shape CHECK: the key a row holds must be the key this row mints, so a
            // parse can never read bytes another coordinate names. A row that fails this is corrupt or hand-written;
            // the attempt throws (redelivered, then dead-lettered) rather than reading an object it cannot vouch for.
            throw new InvalidOperationException(ArtifactObjectKey.MismatchMessage);
        }

        var maxBytes = await MaxBytesAsync(context, artifact, cancellationToken).ConfigureAwait(false);

        byte[]? bytes;

        try
        {
            bytes = await storage.GetAsync(artifact.ObjectKey, maxBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
        {
            // The store refuses to truncate an object above the caller's cap (IObjectStorage.GetAsync: the only
            // InvalidOperationException that read raises), so the bytes are larger than the kind policy admits now.
            // Retrying cannot shrink them: the artifact is rejected, not retried towards a dead letter. A disposed
            // store is excluded by the filter: that is the host's fault (shutdown), never a property of the artifact.
            LogOversizeRejected(logger, artifact.Id, maxBytes);
            EvidgateTelemetry.RecordIngestCapExceeded(ReportBytesCap);
            await RejectAsync(context, artifact, ArtifactFailureCode.TooLarge, scope, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (bytes is null)
        {
            // The final object of a ready artifact is retained; an absent one is an infrastructure fault (or a store
            // that has not made the promotion visible yet), never a verdict: retry instead of rejecting evidence
            // nobody judged. The message names no key.
            throw new InvalidOperationException(
                "the artifact's stored object is not readable, so the report was not parsed; the task is retried");
        }

        await using var input = new MemoryStream(bytes, writable: false);

        var result = await runner
            .RunAsync(
                new ParseRunRequest(profile, maxBytes, artifact.MediaType, input, limits.Value.MaxReportCases),
                cancellationToken)
            .ConfigureAwait(false);

        if (result is not ParseRunFailed failed)
        {
            // Success: the record stream belongs to the caller that asked for a parse, and no such caller persists it
            // yet (see the type remarks). Nothing is written, which is exactly what the in-process path does — unless
            // the stream carries the child's own rejection record, which is this run's verdict.
            if (result is ParseRunSucceeded succeeded && ChildRejectionRecord.ReasonIn(succeeded) is { } reported)
            {
                if (string.Equals(reported, ArtifactFailureCode.CapExceeded, StringComparison.Ordinal))
                {
                    // The only child-side cap_exceeded is the report entry budget, so the counter names it.
                    EvidgateTelemetry.RecordIngestCapExceeded(ReportCasesCap);
                }

                LogChildRejected(logger, artifact.Id, reported, succeeded.ChildExitCode);
                await RejectAsync(context, artifact, reported, scope, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (!ParseRunRejection.TryMap(failed, out var reason))
        {
            // SpawnFailed: no child existed, so the artifact was never judged. The queue retries with backoff.
            throw new InvalidOperationException(
                "the parse child could not be started, so the artifact was not judged; the task is retried");
        }

        LogRejected(logger, artifact.Id, reason, failed.ChildExitCode, failed.StderrExcerpt);
        await RejectAsync(context, artifact, reason, scope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the verdict as the row's own guarded transition — state <c>rejected</c> and one reason, bytes, checksum,
    /// key and scan state untouched — and decides what a lost race means.
    /// </summary>
    /// <param name="context">This attempt's write model, holding the tracked row.</param>
    /// <param name="artifact">The row this attempt read.</param>
    /// <param name="reason">The failure code the run derived.</param>
    /// <param name="scope">This attempt's tenant scope, whose commit the failure measurement waits for.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// The <c>UPDATE</c>'s predicate is the version token the entity's concurrency mapping appends (EvidenceDbContext:
    /// <c>version</c> IsConcurrencyToken), so a concurrent writer makes it match no row. That alone proves nothing
    /// about the stored outcome: the handler re-reads the state <see langword="as no tracking"/> and decides on what
    /// it reads — a stored <c>rejected</c> is decided once (nothing added), a row that left the parseable set has no
    /// evidence left to judge (the same no-op the top of the handler makes), and a row still
    /// <c>ready</c>/<c>uploaded</c> was changed by some other writer (a freeze resolving <c>attempt_id</c>, an operator
    /// update) and is thrown out for the queue's redelivery, because returning would leave the artifact without a
    /// verdict forever. Only the columns the transition changes are in the <c>SET</c> list, which is exactly what the
    /// app role may update on this table; the 0028 trigger owns <c>updated_at</c> and the version increment.
    /// </remarks>
    private async Task RejectAsync(
        EvidenceDbContext context,
        Artifact artifact,
        string reason,
        ITenantScope scope,
        CancellationToken cancellationToken)
    {
        context.Entry(artifact).CurrentValues.SetValues(artifact.MarkRejected(reason));

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            var stored = await context.Artifacts
                .AsNoTracking()
                .Where(row => row.Id == artifact.Id)
                .Select(row => row.State)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(stored, ArtifactState.Rejected, StringComparison.Ordinal))
            {
                // A verdict is stored — this attempt's own, a replay's, or a concurrent attempt's — so the artifact is
                // decided once and this attempt adds nothing.
                LogVerdictAlreadyWritten(logger, artifact.Id, reason);
                return;
            }

            if (!IsParseable(stored))
            {
                // The row left the parseable set while it was parsed (a delete in flight, a missing row): there is no
                // evidence left to judge, so this is the same no-op the top of the handler makes for such a state.
                LogAlreadyDecided(logger, artifact.Id, stored);
                return;
            }

            // Still parseable, so no verdict is stored: the attempt lost a race to another write and must not drop the
            // parse. The message carries neither stderr nor a key.
            throw new InvalidOperationException(
                "the parse verdict could not be written because the artifact changed while it was parsed, so the task is retried");
        }

        // Only the attempt whose rejection committed counts it: a stored verdict and a row that left the
        // parseable set both returned above, and their writer owns the one sample for this artifact. The measurement
        // waits for the commit this attempt does not own, so a rolled-back verdict records nothing.
        scope.AfterCommit(() => EvidgateTelemetry.RecordArtifactFailure("parse", reason));
    }

    /// <summary>True when the artifact may still be parsed: <c>uploaded</c> or <c>ready</c>.</summary>
    /// <param name="state">The row's <c>artifact.state</c>.</param>
    /// <returns><see langword="true"/> for a state the parse may still decide.</returns>
    private static bool IsParseable(string? state) =>
        string.Equals(state, ArtifactState.Uploaded, StringComparison.Ordinal)
        || string.Equals(state, ArtifactState.Ready, StringComparison.Ordinal);

    /// <summary>
    /// The effective byte cap the run is bounded by: the project's kind-policy row resolved against the static
    /// defaults by the same rule the mint and the completion used, so the parse can never read more
    /// than the cap the artifact was admitted under (D-A9).
    /// </summary>
    /// <param name="context">This attempt's write model.</param>
    /// <param name="artifact">The row whose kind and run's project select the policy.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The effective cap in bytes.</returns>
    /// <exception cref="InvalidOperationException">
    /// The kind has neither a policy row nor a default, or its capture switch is off: an artifact of that kind cannot
    /// exist as <c>ready</c>, and a parse without a cap must never run.
    /// </exception>
    private static async Task<long> MaxBytesAsync(
        EvidenceDbContext context,
        Artifact artifact,
        CancellationToken cancellationToken) =>
        (await ArtifactCapturePolicy
            .ResolveForRunAsync(context, artifact, cancellationToken)
            .ConfigureAwait(false))
        .MaxBytes;

    /// <summary>
    /// The profile arm a declared media type selects, or <see langword="null"/> for a type the profile has no arm
    /// for — <c>text/html</c> above all, which the server never parses.
    /// <para>
    /// The families are the ones <see cref="MediaSniffer"/> verifies, and this only chooses the arm: the artifact
    /// reached <c>ready</c> through that sniffer, so the bytes already agree with the declared type. Keep the two
    /// tables in step — a family added there and not here would silently skip a report.
    /// </para>
    /// </summary>
    /// <param name="mediaType">The artifact's declared media type; parameters after <c>;</c> are ignored.</param>
    /// <returns>The arm, or <see langword="null"/> when there is nothing to parse.</returns>
    private static ParseRunnerProfile? ParseRunnerProfileFor(string mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        var separator = mediaType.IndexOf(ParameterSeparator);
        var bare = (separator < 0 ? mediaType : mediaType[..separator]).Trim();

        if (bare.Equals(XmlType, StringComparison.OrdinalIgnoreCase)
            || bare.Equals(TextXmlType, StringComparison.OrdinalIgnoreCase)
            || (bare.StartsWith(ApplicationPrefix, StringComparison.OrdinalIgnoreCase)
                && bare.EndsWith(XmlSuffix, StringComparison.OrdinalIgnoreCase)))
        {
            return ParseRunnerProfile.Xml;
        }

        if (bare.Equals(JsonType, StringComparison.OrdinalIgnoreCase)
            || (bare.StartsWith(ApplicationPrefix, StringComparison.OrdinalIgnoreCase)
                && bare.EndsWith(JsonSuffix, StringComparison.OrdinalIgnoreCase)))
        {
            return ParseRunnerProfile.Json;
        }

        return bare.Equals(ZipType, StringComparison.OrdinalIgnoreCase) ? ParseRunnerProfile.Archive : null;
    }

    /// <summary>
    /// The one line a rejection writes: the artifact and the reason it is now carrying, the child's exit code, and the
    /// runner's bounded, redacted stderr excerpt for the operator. Never a stored value and never a tenant-visible one.
    /// </summary>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="artifactId">The artifact that was rejected.</param>
    /// <param name="reason">The <c>parse_*</c> code the row now carries.</param>
    /// <param name="exitCode">The child's exit code, or <see langword="null"/> when it never started.</param>
    /// <param name="stderrExcerpt">The runner's redacted excerpt, or <see langword="null"/> when the child wrote nothing.</param>
    [LoggerMessage(
        EventId = 2440,
        Level = LogLevel.Warning,
        Message = "parse rejected artifact {ArtifactId} as {Reason} (child exit {ExitCode}); stderr: {StderrExcerpt}")]
    private static partial void LogRejected(
        ILogger logger,
        Guid artifactId,
        string reason,
        int? exitCode,
        string? stderrExcerpt);

    /// <summary>
    /// A payload naming an artifact this tenant cannot see: the attempt changed nothing, but a misfiled or crafted
    /// payload is worth a Warning rather than a silent completion.
    /// </summary>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="artifactId">The artifact the payload named.</param>
    [LoggerMessage(
        EventId = 2441,
        Level = LogLevel.Warning,
        Message = "parse found no artifact {ArtifactId} in this tenant: nothing parsed")]
    private static partial void LogArtifactNotFound(ILogger logger, Guid artifactId);

    /// <summary>
    /// The routine no-op: the artifact already holds an outcome (or is not parsable yet), so the child was never
    /// started and no statement was issued.
    /// </summary>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="artifactId">The artifact the payload named.</param>
    /// <param name="state">The state it holds, or <see langword="null"/> while the column default applies.</param>
    [LoggerMessage(
        EventId = 2442,
        Level = LogLevel.Debug,
        Message = "parse skipped artifact {ArtifactId} in state {State}: nothing parsed")]
    private static partial void LogAlreadyDecided(ILogger logger, Guid artifactId, string? state);

    /// <summary>
    /// The guarded <c>UPDATE</c> matched no row and the re-read found a stored rejection: the artifact was decided
    /// once — by this attempt's earlier write, a replay, or a concurrent attempt — so nothing was added.
    /// </summary>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="artifactId">The artifact the payload named.</param>
    /// <param name="reason">The reason this attempt derived; the stored row carries the verdict that stands.</param>
    [LoggerMessage(
        EventId = 2444,
        Level = LogLevel.Debug,
        Message = "parse verdict for artifact {ArtifactId} was already written as {Reason}: this attempt changed nothing")]
    private static partial void LogVerdictAlreadyWritten(ILogger logger, Guid artifactId, string reason);

    /// <summary>
    /// The verdict the child reported itself, on a run that exited 0: an operator wants to know that the rejected
    /// document was content, not a crash, so this line names the reason and the exit code and never a record's other
    /// text.
    /// </summary>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="artifactId">The artifact whose stream held the rejection.</param>
    /// <param name="reason">The failure code the record's reason became.</param>
    /// <param name="exitCode">The child's exit code, which is 0 on this shape.</param>
    [LoggerMessage(
        EventId = 2445,
        Level = LogLevel.Warning,
        Message = "parse rejected artifact {ArtifactId} as {Reason}: the child reported the rejection itself (exit {ExitCode})")]
    private static partial void LogChildRejected(
        ILogger logger,
        Guid artifactId,
        string reason,
        int? exitCode);

    /// <summary>
    /// The stored object is larger than the kind's cap, so it was never read and no child ran: the one rejection that
    /// happens before the runner.
    /// </summary>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="artifactId">The artifact whose object is over the cap.</param>
    /// <param name="maxBytes">The cap the kind policy resolved to for this artifact.</param>
    [LoggerMessage(
        EventId = 2446,
        Level = LogLevel.Warning,
        Message = "parse rejected artifact {ArtifactId} as too_large: the stored object is larger than the kind's {MaxBytes}-byte cap, so it was not read and no child started")]
    private static partial void LogOversizeRejected(ILogger logger, Guid artifactId, long maxBytes);

    /// <summary>
    /// A <c>raw_report</c> artifact whose media type has no profile arm: nothing is parsed, exactly as the in-process
    /// path treats the same bytes.
    /// </summary>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="artifactId">The artifact the payload named.</param>
    /// <param name="kind">Its kind.</param>
    /// <param name="mediaType">Its declared media type.</param>
    [LoggerMessage(
        EventId = 2443,
        Level = LogLevel.Debug,
        Message = "parse skipped artifact {ArtifactId}: kind {Kind} and media type {MediaType} name no parse arm")]
    private static partial void LogNothingToParse(ILogger logger, Guid artifactId, string kind, string mediaType);
}
