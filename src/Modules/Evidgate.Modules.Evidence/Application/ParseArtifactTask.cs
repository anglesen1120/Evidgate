using System.Text.Json;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// Payload of the <c>artifact_parse</c> task (06.3.6 §5, T1.4), as the completion's outbox row writes it: one
/// artifact id. The tenant comes from the task row (<c>task.org_id</c>), never from the payload, so a payload cannot
/// name another tenant's artifact (00 §5.4.1, T3).
/// </summary>
/// <param name="ArtifactId">The artifact whose stored report is parsed.</param>
internal sealed record ParseArtifactPayload(Guid ArtifactId);

/// <summary>
/// Runs the 06.3.1 profile over one artifact's bytes in the resource-limited child (06.3.6 §5/§7, T1.4): the task
/// handler that closes AC3 — a crash, OOM kill or timeout becomes a <c>rejected</c> artifact with the stable reason
/// <see cref="ParseRunRejection"/> maps it to, and never takes the Worker down.
/// <para>
/// <b>Nothing is written before the child finishes.</b> The handler reads the row, opens the object read with the
/// kind policy's cap, hands the stream to <see cref="IParseRunner"/> and only then decides. A successful run persists
/// nothing: no consumer of the parsed records exists yet (the in-process <c>IParsing</c> callers return the profile's
/// value to their own caller and store nothing), so writing a result here would be a second, unrouted representation.
/// </para>
/// <para>
/// <b>One guarded write per artifact.</b> A verdict is a single <c>UPDATE</c> whose predicate is the parseable state
/// (<c>uploaded</c>/<c>ready</c>), so a replay after the rejection — the queue is at-least-once — updates no row and
/// the artifact is parsed and decided once (06.3.6 AC8). An artifact that is already <c>rejected</c>, or not
/// <c>ready</c>/<c>uploaded</c> any more, is not parsed again.
/// </para>
/// <para>
/// <b>What is not a verdict.</b> A cancellation (lease loss, shutdown) rethrows: the runner has already tree-killed
/// its child, and the queue's redelivery is the retry. A <see cref="ParseRunFailureKind.SpawnFailed"/> run throws
/// too, because no child existed to judge the artifact — the row stays as it was and the queue retries with backoff
/// (06.3.6 §6). Neither path writes a state.
/// </para>
/// <para>
/// <b>Nothing secret, nothing tenant-visible beyond the code.</b> The reason is the only value a tenant sees; the
/// child's stderr may appear in one operator log line (already bounded and redacted by the runner) and never in the
/// failure code, the artifact row or a thrown exception's message (00 §5.7 S3, 06.3.6 §8).
/// </para>
/// <para>
/// The object read and the child run happen inside the tenant transaction the consumer opened around this attempt —
/// the same posture <see cref="ArtifactStagingDeleteTaskHandler"/> takes for its delete. The read takes no row lock,
/// so the exposure is one idle-in-transaction connection for the length of one parse, bounded by the wall clock the
/// runner enforces (06.3.6 §7) and by the queue's own per-queue concurrency.
/// </para>
/// </summary>
/// <param name="contexts">The tenant write model the artifact and its kind policy are read through.</param>
/// <param name="storage">The object store the artifact's bytes are read from, capped by the kind policy.</param>
/// <param name="runner">The out-of-process runner; the only way this module reaches a parser (06.3.6 AC1).</param>
/// <param name="logger">The one structured line a rejection or an invisible artifact writes.</param>
internal sealed partial class ParseArtifactTaskHandler(
    ITenantDbContextFactory<EvidenceDbContext> contexts,
    IObjectStorage storage,
    IParseRunner runner,
    ILogger<ParseArtifactTaskHandler> logger) : ITaskHandler<ParseArtifactPayload>
{
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
            // started, so a replay after the rejection costs no child process (06.3.6 AC8).
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
            // A report media type with no arm (text/html above all: the server never parses HTML, 06.3.1 §10 T1.2).
            // The in-process path has no parse for these bytes either, so a completion is the whole work.
            LogNothingToParse(logger, artifact.Id, artifact.Kind, artifact.MediaType);
            return;
        }

        var maxBytes = await MaxBytesAsync(context, artifact, cancellationToken).ConfigureAwait(false);

        var bytes = await storage.GetAsync(artifact.ObjectKey, maxBytes, cancellationToken).ConfigureAwait(false);

        if (bytes is null)
        {
            // The final object of a ready artifact is retained; an absent one is an infrastructure fault (or a store
            // that has not made the promotion visible yet), never a verdict: retry instead of rejecting evidence
            // nobody judged. The message names no key (00 §5.7 S3).
            throw new InvalidOperationException(
                "the artifact's stored object is not readable, so the report was not parsed; the task is retried");
        }

        await using var input = new MemoryStream(bytes, writable: false);

        var result = await runner
            .RunAsync(new ParseRunRequest(profile, maxBytes, artifact.MediaType, input), cancellationToken)
            .ConfigureAwait(false);

        if (result is not ParseRunFailed failed)
        {
            // Success: the record stream belongs to the caller that asked for a parse, and no such caller persists it
            // yet (see the type remarks). Nothing is written, which is exactly what the in-process path does.
            return;
        }

        if (!ParseRunRejection.TryMap(failed, out var reason))
        {
            // SpawnFailed: no child existed, so the artifact was never judged. The queue retries with backoff.
            throw new InvalidOperationException(
                "the parse child could not be started, so the artifact was not judged; the task is retried");
        }

        LogRejected(logger, artifact.Id, reason, failed.ChildExitCode, failed.StderrExcerpt);

        // The verdict is the row's own transition (MarkRejected: state rejected, the reason kept, bytes and scan
        // state untouched), written as ONE statement whose predicate is the version token the entity's concurrency
        // mapping appends (EvidenceDbContext: <c>version</c> IsConcurrencyToken). A replay — or a concurrent attempt
        // that already wrote — matches no row and throws instead of overwriting a later decision (06.3.6 AC8, §6
        // "transitions idempotent"); the 0028 trigger owns updated_at and the version increment. Only the columns the
        // transition changes are in the SET list, which is exactly what evidgate_app may update on this table.
        context.Entry(artifact).CurrentValues.SetValues(artifact.MarkRejected(reason));

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The stored outcome is already there (a replay, or another attempt of the same row): nothing to add.
            LogVerdictAlreadyWritten(logger, artifact.Id, reason);
        }
    }

    /// <summary>True when the artifact may still be parsed: <c>uploaded</c> or <c>ready</c> (00 §8.4, 06.3.6 §6).</summary>
    /// <param name="state">The row's <c>artifact.state</c>.</param>
    /// <returns><see langword="true"/> for a state the parse may still decide.</returns>
    private static bool IsParseable(string? state) =>
        string.Equals(state, ArtifactState.Uploaded, StringComparison.Ordinal)
        || string.Equals(state, ArtifactState.Ready, StringComparison.Ordinal);

    /// <summary>
    /// The effective byte cap the run is bounded by: the project's kind-policy row resolved against the static
    /// defaults by the same rule the mint and the completion used (06.1.2-T3.1), so the parse can never read more
    /// than the cap the artifact was admitted under (D-A9).
    /// </summary>
    /// <param name="context">This attempt's write model.</param>
    /// <param name="artifact">The row whose kind and project select the policy.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The effective cap in bytes.</returns>
    /// <exception cref="InvalidOperationException">
    /// The kind has neither a policy row nor a default, or its capture switch is off: an artifact of that kind cannot
    /// exist as <c>ready</c>, and a parse without a cap must never run.
    /// </exception>
    private static async Task<long> MaxBytesAsync(
        EvidenceDbContext context,
        Artifact artifact,
        CancellationToken cancellationToken)
    {
        var row = await context.KindPolicies
            .AsNoTracking()
            .SingleOrDefaultAsync(
                policy => policy.ProjectId == artifact.ProjectId && policy.Kind == artifact.Kind,
                cancellationToken)
            .ConfigureAwait(false);

        var resolved = ArtifactKindDefaults.Resolve(
            artifact.Kind,
            row is null
                ? null
                : new KindPolicyRow(row.MaxBytes, row.MediaTypes, row.CaptureEnabled, row.DefaultSensitivity));

        return resolved is { CaptureEnabled: true } policy
            ? policy.MaxBytes
            : throw new InvalidOperationException(
                "the artifact's kind has no effective capture policy, so no byte cap exists for a parse");
    }

    /// <summary>
    /// The profile arm a declared media type selects (06.3.6 §5), or <see langword="null"/> for a type the profile has
    /// no arm for — <c>text/html</c> above all, which the server never parses (06.3.1 §10 T1.2).
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
    /// runner's bounded, redacted stderr excerpt for the operator. Never a stored value and never a tenant-visible one
    /// (00 §5.7 S3, 06.3.6 §8).
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
    /// The guarded <c>UPDATE</c> matched no row: this attempt's verdict had already been written (a replay of the same
    /// task row, or a concurrent attempt that got there first), so exactly one write stands.
    /// </summary>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="artifactId">The artifact the payload named.</param>
    /// <param name="reason">The reason this attempt derived, which the stored row already carries.</param>
    [LoggerMessage(
        EventId = 2444,
        Level = LogLevel.Debug,
        Message = "parse verdict for artifact {ArtifactId} was already written as {Reason}: the replay changed nothing")]
    private static partial void LogVerdictAlreadyWritten(ILogger logger, Guid artifactId, string reason);

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
