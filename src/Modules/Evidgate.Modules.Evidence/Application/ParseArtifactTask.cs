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
/// <b>The child's own rejection is still a verdict.</b> A run that ends 0 with its marker may hold a <c>rejected</c>
/// record — the child's decision on content it refuses (a DTD, an unsafe archive entry, an input over the cap) — and
/// that record is the verdict: the artifact is rejected with the record's <c>reason</c> when that value is shaped like
/// a code word (<c>^[a-z0-9_]{1,64}$</c>) and with <see cref="ArtifactFailureCode.ParseCrashed"/> otherwise, so no text
/// the untrusted document influenced can reach a tenant-visible column (D-S125-Q4, 00 §5.7 S3).
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
/// once (06.3.6 AC8). An artifact that is already <c>rejected</c>, or not <c>ready</c>/<c>uploaded</c> any more, is not
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
/// too, because no child existed to judge the artifact — the row stays as it was and the queue retries with backoff
/// (06.3.6 §6). Neither path writes a state.
/// </para>
/// <para>
/// <b>Nothing secret, nothing tenant-visible beyond the code.</b> The reason is the only value a tenant sees; the
/// child's stderr may appear in one operator log line (already bounded and redacted by the runner) and never in the
/// failure code, the artifact row or a thrown exception's message (00 §5.7 S3, 06.3.6 §8).
/// </para>
/// <para>
/// <b>P5 exception, recorded (06.3.6-T2).</b> The object read and the child run happen inside the tenant
/// transaction the consumer opened around this attempt, because a task handler receives that transaction and cannot
/// release it — the same posture <see cref="ArtifactStagingDeleteTaskHandler"/> takes for its delete. This is a
/// deliberate exception to the 00 §5.7 P5 rule that storage calls run outside a database transaction, and it is
/// temporary: <see cref="IObjectStorage.GetAsync"/>'s own remarks name it as the one recorded exception and forbid a
/// new caller from copying it.
/// <list type="bullet">
/// <item><b>Receiver:</b> 06.3.6-T2 (its own parse queue and concurrency), which schedules the parse outside the
/// consumer's transaction or runs it on a scope opened after the read.</item>
/// <item><b>Exposure now:</b> one idle-in-transaction connection for the length of one parse and one object read,
/// bounded by the wall clock the runner enforces (06.3.6 §7) and by the queue's own per-queue concurrency
/// (originally one, this queue's default).</item>
/// <item><b>Why it is not exploitable:</b> the read takes no row lock, so it blocks no writer; the transaction
/// commits only the guarded verdict <c>UPDATE</c> at the end.</item>
/// </list>
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

    /// <summary>The record kind a rejection carries, as the child's protocol spells it (<c>NdjsonWriter.RejectedKind</c>).</summary>
    private const string RejectedRecordKind = "rejected";

    /// <summary>The property a record's kind travels in (<c>NdjsonWriter.KindProperty</c>).</summary>
    private const string RecordKindProperty = "t";

    /// <summary>The property a rejection record's reason word travels in (<c>NdjsonWriter.ReasonProperty</c>).</summary>
    private const string RecordReasonProperty = "reason";

    /// <summary>
    /// The bytes the JSON value <c>rejected</c> always occupies in a record — quotes included, because that value has no
    /// escapable character — so a record can be dismissed as "not a rejection" without being parsed.
    /// </summary>
    private static ReadOnlySpan<byte> RejectedRecordKindBytes => "\"rejected\""u8;

    /// <summary>
    /// The fixed first line of the exception a row raises whose stored <c>object_key</c> is not the key its own
    /// coordinates mint. It names no key and no coordinate (00 §5.7 S3).
    /// </summary>
    private const string ObjectKeyMismatchMessage =
        "the artifact's stored object key is not the key its row mints, so the report was not read; an operator must look at this artifact";

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

        if (!ObjectKeyIsMintedFor(artifact))
        {
            // Defence in depth over the 0028 shape CHECK: the key a row holds must be the key this row mints, so a
            // parse can never read bytes another coordinate names. A row that fails this is corrupt or hand-written;
            // the attempt throws (redelivered, then dead-lettered) rather than reading an object it cannot vouch for.
            throw new InvalidOperationException(ObjectKeyMismatchMessage);
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
            await RejectAsync(context, artifact, ArtifactFailureCode.TooLarge, cancellationToken).ConfigureAwait(false);
            return;
        }

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
            // yet (see the type remarks). Nothing is written, which is exactly what the in-process path does — unless
            // the stream carries the child's own rejection record, which is this run's verdict (D-S125-Q4).
            if (result is ParseRunSucceeded succeeded && ChildRejectionReasonIn(succeeded) is { } reported)
            {
                LogChildRejected(logger, artifact.Id, reported, succeeded.ChildExitCode);
                await RejectAsync(context, artifact, reported, cancellationToken).ConfigureAwait(false);
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
        await RejectAsync(context, artifact, reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the verdict as the row's own guarded transition — state <c>rejected</c> and one reason, bytes, checksum,
    /// key and scan state untouched — and decides what a lost race means.
    /// </summary>
    /// <param name="context">This attempt's write model, holding the tracked row.</param>
    /// <param name="artifact">The row this attempt read.</param>
    /// <param name="reason">The failure code the run derived.</param>
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
            // parse. The message carries neither stderr nor a key (00 §5.7 S3).
            throw new InvalidOperationException(
                "the parse verdict could not be written because the artifact changed while it was parsed, so the task is retried");
        }
    }

    /// <summary>
    /// True when the row's stored <c>object_key</c> is exactly the final key the row's own coordinates mint
    /// (<see cref="ArtifactKeyMinter.Mint"/>). The 0028 shape CHECK enforces this in the database; this is the same
    /// statement in code, so the read never names an object the row does not own.
    /// </summary>
    /// <param name="artifact">The row about to be read.</param>
    /// <returns><see langword="true"/> when the key is the row's minted final key.</returns>
    private static bool ObjectKeyIsMintedFor(Artifact artifact)
    {
        string minted;

        try
        {
            minted = ArtifactKeyMinter.Mint(
                artifact.OrgId,
                artifact.ProjectId,
                artifact.RunId,
                artifact.ExecutionKey,
                artifact.AttemptNo,
                artifact.Id);
        }
        catch (ArgumentException)
        {
            // Coordinates the CHECK constraints should have refused (a malformed execution key, attempt_no below 1):
            // no key is minted for them, so the row is a mismatch rather than a read.
            return false;
        }

        return string.Equals(artifact.ObjectKey, minted, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure code a successful stream's own <c>rejected</c> record carries, or <see langword="null"/> when the
    /// child reported no rejection (the stream is then the parsed records alone, and nothing is written).
    /// <para>
    /// The child writes one <c>rejected</c> record and still exits 0 with its marker when it refuses the content
    /// itself — a DTD, an unsafe archive entry name, an input over the cap — because the run succeeded and the
    /// document is what failed (D-S125-Q4). Only that record's <c>reason</c> is read, and only a value shaped like
    /// a code word (<c>^[a-z0-9_]{1,64}$</c>) is kept: anything else becomes
    /// <see cref="ArtifactFailureCode.ParseCrashed"/>, so no text the untrusted document influenced can reach the
    /// tenant-visible column (00 §5.7 S3).
    /// </para>
    /// </summary>
    /// <param name="succeeded">The run's records, in stream order.</param>
    /// <returns>The code to write, or <see langword="null"/> for a stream without a rejection record.</returns>
    private static string? ChildRejectionReasonIn(ParseRunSucceeded succeeded)
    {
        foreach (var record in succeeded.Records)
        {
            // A record that does not carry the literal `"rejected"` anywhere cannot be a rejection — no encoding of
            // that value escapes it — so the overwhelming majority of a stream's records are dismissed by one
            // vectorised search over bytes, without a reader, a string or a copy.
            if (record.Utf8Json.Span.IndexOf(RejectedRecordKindBytes) < 0)
            {
                continue;
            }

            if (TryReadRejectedReason(record.Utf8Json, out var reason))
            {
                return IsCodeWord(reason) ? reason : ArtifactFailureCode.ParseCrashed;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads one record's top level only — its kind and, for a rejection, its reason — with
    /// <see cref="Utf8JsonReader"/> over the UTF-8 the child wrote. Nothing else of the record is decoded, kept or
    /// logged, so a hostile record costs no string for its content.
    /// </summary>
    /// <param name="utf8">The record's bytes, without the terminating newline.</param>
    /// <param name="reason">The rejection's <c>reason</c> string, or <see langword="null"/> when it is absent or not a string.</param>
    /// <returns><see langword="true"/> when the record is object-kind <c>rejected</c>.</returns>
    private static bool TryReadRejectedReason(ReadOnlyMemory<byte> utf8, out string? reason)
    {
        reason = null;

        if (utf8.IsEmpty)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(utf8.Span);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            string? kind = null;
            var kindRead = false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return false;
                }

                if (reader.ValueTextEquals(RecordKindProperty))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        return false;
                    }

                    kind = reader.GetString();
                    kindRead = true;
                }
                else if (reader.ValueTextEquals(RecordReasonProperty))
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    reason = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        // A reason that is not a string is never a code word; the value is skipped, not walked, so a
                        // hostile record cannot make the handler traverse it.
                        reader.Skip();
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            if (kindRead && string.Equals(kind, RejectedRecordKind, StringComparison.Ordinal))
            {
                return true;
            }
        }
        catch (JsonException)
        {
            // A line the runner framed but that is not one JSON object is not a rejection this handler can read; the
            // framing check already reports such a stream as malformed output, and no verdict is invented from it.
        }

        reason = null;
        return false;
    }

    /// <summary>True when <paramref name="value"/> matches <c>^[a-z0-9_]{1,64}$</c> — the shape of a reason word.</summary>
    /// <param name="value">The child-supplied reason.</param>
    /// <returns><see langword="true"/> when the value may become a tenant-visible failure code.</returns>
    private static bool IsCodeWord(string? value)
    {
        if (value is null || value.Length is 0 or > 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        return true;
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
    /// text (00 §5.7 S3).
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
