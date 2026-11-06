using System.Globalization;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Evidgate.Modules.Ingestion.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// Payload of the <c>raw_report_parse</c> task: the run the report belongs to, the stored artifact and the producer
/// kind its run's manifest declares. The tenant comes from the task row, never from the payload.
/// </summary>
/// <param name="RunId">The run the report was uploaded for; a report of another run yields no event.</param>
/// <param name="ArtifactId">The raw report artifact; also the synthetic producer id's suffix.</param>
/// <param name="ProducerKind">
/// The report dialect, e.g. <c>pytest-junitxml</c>; resolved against the parser registry. Null when the manifest named
/// no single producer kind, which <paramref name="ProducerIssue"/> then explains.
/// </param>
/// <param name="ProducerIssue">
/// The code word of a manifest whose shards do not name one producer kind (<see cref="ProducerUnknown"/> or
/// <see cref="ProducerAmbiguous"/>). Such a report is refused without being read, because no parser can be chosen.
/// </param>
internal sealed record ParseRawReportPayload(
    Guid RunId,
    Guid ArtifactId,
    string? ProducerKind,
    string? ProducerIssue = null)
{
    /// <summary>The issue of a manifest whose shards declare no producer hint at all.</summary>
    public const string ProducerUnknown = "producer_unknown";

    /// <summary>The issue of a manifest whose shards declare more than one distinct producer hint.</summary>
    public const string ProducerAmbiguous = "producer_ambiguous";

    /// <summary>
    /// The producer kind an issue row stores, because <c>raw_report.producer_kind</c> is not null and admits only
    /// <c>^[a-z][a-z0-9-]*$</c>. No registry kind is spelled this way, and one issue row per artifact is all the
    /// table's (org, artifact, producer kind) key allows when the parser pair is absent.
    /// </summary>
    public const string UnresolvedProducerKind = "producer-unresolved";

    /// <summary>The producer kind the report's own row stores: the declared one, or the reserved word of an issue row.</summary>
    public string StoredProducerKind => ProducerKind is { Length: > 0 } kind ? kind : UnresolvedProducerKind;
}

/// <summary>
/// What one parse attempt produced, named so a caller (and a test) can tell a stored event set from a refusal.
/// </summary>
/// <param name="Status">One of <c>parsed</c>, <c>malformed</c>, <c>unsupported</c> or <c>skipped</c>.</param>
/// <param name="Reason">The code word behind a non-parsed status, or <see langword="null"/>.</param>
/// <param name="Events">How many event rows this attempt inserted; 0 on a replay and on every refusal.</param>
internal sealed record ParseRawReportOutcome(string Status, string? Reason = null, int Events = 0)
{
    /// <summary>The mapped event set was stored (or already was: a replay reports 0 events).</summary>
    public const string Parsed = "parsed";

    /// <summary>The document was refused by the parser profile; zero events.</summary>
    public const string Malformed = "malformed";

    /// <summary>No parser is registered for the producer kind, or none is loaded; zero events.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>
    /// There was nothing this attempt could store: the artifact, the run or the producer slot was absent or taken, the
    /// run no longer admits evidence, or a replay disagreed with the stored rows. Zero events; terminal.
    /// </summary>
    public const string Skipped = "skipped";

    /// <summary>True when the attempt left no event row because the document was refused.</summary>
    public bool IsRefusal => Status is Malformed or Unsupported;
}

/// <summary>
/// Maps one stored raw report to <c>test.finished</c> events under the parser its producer kind registers: the registry
/// decides the parser pair, the mapper decides the cases, and this handler decides what may be written and in which
/// transaction.
/// <para>
/// <b>The bytes are read, the event rows are serialized and hashed, and only then does a write transaction open.</b>
/// One short read-only scope resolves what the fetch needs (the artifact, the kind's byte cap, the parser pair, the
/// run's plan), then closes; the object is fetched and the document mapped with no scope open (00 §5.7 P5). The
/// serialized rows — including every payload hash and every case-attempt input — are computed with no scope open too,
/// so the transaction that follows holds a connection only for its writes. A refusal opens one more, equally short,
/// write scope for its single row and nothing else.
/// </para>
/// <para>
/// <b>Admission is the event batch route's, decided once under the run's lock.</b> A complete run, an incomplete run
/// past its late window, and a deleted or suspended project refuse the write as a terminal named outcome; a canceled
/// run accepts it and marks it <c>after_cancel</c>, an incomplete run inside its window accepts it and marks it
/// <c>late</c>. The rule itself is <see cref="RunAdmissionPolicy"/>, shared with the ingest path.
/// </para>
/// <para>
/// <b>One transaction writes the slot, the events, the attempts, the trigger and the report's own row.</b> The
/// synthetic producer's slot is created already closed and declared, because no producer will ever finalize a report,
/// and only then can the reconciler compare this shard instead of waiting on an open one. Every newly inserted finish
/// also becomes a <c>case_attempt</c> row through the existing writer, and one reconcile trigger is enqueued — the
/// late-evidence one for a run that already finished as incomplete, the ordinary one for every other run. The
/// <c>raw_report</c> row is the last write before the commit, so a refusal anywhere in the transaction leaves none.
/// </para>
/// <para>
/// <b>The run's first accepted report moves it on, and what this attempt really inserted is metered.</b> A
/// <c>queued</c> run is driven <c>queued → running</c> through 05's transition <em>before</em> the admission read takes
/// the same row's lock, so this writer and the event batch route share one lock order. The events this attempt inserted —
/// never the replayed ones — become one <c>event_batch</c> receipt and one <c>usage.batch_accepted</c> outbox row in the
/// evidence's own transaction, so a report cannot be metered for a commit that rolled back.
/// </para>
/// <para>
/// <b>A replay is a no-op only when it is the same evidence.</b> The producer id is synthetic and per report
/// (<c>junit-&lt;artifact id&gt;</c>) and the sequence is the testcase's document position, so
/// <c>event_run_producer_sequence_key</c> matches the stored rows of a second parse. Each matched row's stored payload
/// hash is compared with the one this parse computed over the execution key, the statuses, the duration and the
/// <c>ext</c> document: identical hashes make the attempt a no-op, and a different hash refuses the whole attempt
/// transactionally instead of overwriting immutable evidence.
/// </para>
/// <para>
/// <b>A refused document writes no event, and exactly one <c>raw_report</c> row.</b>
/// <see cref="JUnitParseRefusedException"/> is the mapper's only way to report a refusal of the record stream, and the
/// child's own rejection record (DTD, truncation, cap) or a failed run (<c>parse_crashed</c>, <c>parse_oom</c>,
/// <c>parse_timeout</c>) becomes a named <c>malformed</c> outcome with zero events, exactly as for an artifact parse;
/// what the parser decided is stored in one short write of its own, so a report nobody could parse still leaves
/// evidence of who judged it and why. A producer kind with no loaded parser is stored the same way as
/// <c>unsupported</c>. Only a child that never started is retried, and it writes nothing. No other exception is read as
/// a verdict: a resolver or storage fault must surface.
/// </para>
/// <para>
/// <b>A report whose artifact belongs to another run, or another tenant, yields no event.</b> The payload's run id is
/// compared with the artifact's inside the tenant scope, and the plan read is that run's, so a crafted payload can
/// neither attribute events to a foreign run nor read its plan.
/// </para>
/// </summary>
/// <param name="scopes">Opens the short scopes this attempt runs in: the read, the write of a parsed report and, for a refusal, the one-row write.</param>
/// <param name="contexts">The tenant write model the artifact and its kind policy are read through.</param>
/// <param name="db">The Dapper path for the registry, the plan, the slot, the event insert and the report row.</param>
/// <param name="storage">The object store the report's bytes are read from, capped by the kind policy.</param>
/// <param name="parsers">The parser registry read (producer kind → parser pair).</param>
/// <param name="runner">The resource-limited child the untrusted report is parsed in; this process never parses it.</param>
/// <param name="limits">The host's ingest caps; the report entry budget is handed to the child.</param>
/// <param name="mappers">The loaded JUnit mappers; the one whose pair the registry names does the mapping.</param>
/// <param name="scopeFactory">Reaches the scoped case-attempt writer: handlers are singletons, and the writer joins the ambient tenant transaction and never commits.</param>
/// <param name="outbox">The reconcile trigger's transactional outbox writer.</param>
/// <param name="audit">The attempt's audit buffer, which the run transition inside the write transaction records into.</param>
/// <param name="logger">The one structured line each attempt writes.</param>
internal sealed partial class ParseRawReportHandler(
    ITenantScopeFactory scopes,
    ITenantDbContextFactory<EvidenceDbContext> contexts,
    ITenantDb db,
    IObjectStorage storage,
    IReportParserResolver parsers,
    IParseRunner runner,
    IOptions<IngestionLimits> limits,
    IEnumerable<IJUnitMapper> mappers,
    IServiceScopeFactory scopeFactory,
    IOutboxWriter outbox,
    IAuditTrail audit,
    ILogger<ParseRawReportHandler> logger) : ITransactionlessTaskHandler<ParseRawReportPayload>
{
    /// <summary>The cap name a report over its byte budget carries in telemetry.</summary>
    private const string ReportBytesCap = "report_bytes";

    /// <summary>The cap name a report over its entry budget carries in telemetry.</summary>
    private const string ReportCasesCap = "report_cases";

    /// <summary>The media type the child is told the report carries.</summary>
    private const string XmlMediaType = "application/xml";

    /// <summary>The prefix of the synthetic producer id; the artifact id makes it unique per report.</summary>
    private const string ProducerPrefix = "junit-";

    /// <summary>
    /// The binding the synthetic slot carries: no credential writes this producer, so the slot is bound to a value no
    /// token can hold and a producer with the same id is refused rather than silently inheriting the report's shard.
    /// </summary>
    private const string SyntheticBinding = "synthetic:junit-report";

    /// <summary>The most shards a run's manifest may declare (contracts/manifest shard_plan).</summary>
    private const int MaxShardIndex = 256;

    /// <inheritdoc />
    public string TaskType => EvidenceTaskTypes.ReportParse;

    /// <summary>Runs one parse attempt and discards the named outcome (the task queue has no caller to return it to).</summary>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="cancellationToken">Cancelled on shutdown and on lease loss.</param>
    public async ValueTask HandleAsync(
        ParseRawReportPayload payload,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await ExecuteAsync(payload, orgId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One parse attempt: prepare, fetch, map, serialize, write. The named outcome is the same value the task line logs.
    /// </summary>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="cancellationToken">Cancelled on shutdown and on lease loss.</param>
    /// <returns>The named outcome of the attempt.</returns>
    internal async Task<ParseRawReportOutcome> ExecuteAsync(
        ParseRawReportPayload payload,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        if (payload.ProducerKind is not { Length: > 0 } || payload.ProducerIssue is not null)
        {
            // No single producer kind to resolve, so no parser can judge the bytes: the report is refused before they
            // are read and leaves its one row. A null kind without an issue is the same refusal, so a hand-written
            // payload can never reach the registry with a null.
            var issue = string.IsNullOrEmpty(payload.ProducerIssue)
                ? ParseRawReportPayload.ProducerUnknown
                : payload.ProducerIssue;
            LogUnsupported(logger, payload.ArtifactId, issue);
            return await RefuseAsync(
                    orgId,
                    payload,
                    RawReportOutcomes.Unsupported,
                    issue,
                    parserId: null,
                    parserVersion: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var preparation = await PrepareAsync(orgId, payload, cancellationToken).ConfigureAwait(false);
        if (preparation.Prepared is not { } prepared)
        {
            // A refusal is the one stop that still leaves evidence: an unsupported producer kind writes its row, a skip
            // writes nothing.
            return await RefuseUnsupportedAsync(preparation, preparation.Stopped!, orgId, payload, cancellationToken)
                .ConfigureAwait(false);
        }

        var (bytes, readStopped) = await ReadBytesAsync(prepared, orgId, payload, cancellationToken).ConfigureAwait(false);
        if (bytes is not { } content)
        {
            return readStopped!;
        }

        var (succeeded, childStopped) = await RunChildAsync(prepared, content, orgId, payload, cancellationToken).ConfigureAwait(false);
        if (succeeded is not { } run)
        {
            return childStopped!;
        }

        var (mapped, mapStopped) = await MapAsync(run, prepared, orgId, payload, cancellationToken).ConfigureAwait(false);
        if (mapped is not { } resolution)
        {
            return mapStopped!;
        }

        return await WriteMappedAsync(prepared, resolution, orgId, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The fetch: the object is read with no scope open, capped by the kind policy and the profile's ceiling. A store
    /// that refuses to truncate is a refusal; an absent object is a retryable fault.
    /// </summary>
    /// <param name="prepared">The fetch inputs the read scope resolved.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The object's bytes, or the refused outcome that ended the attempt.</returns>
    private async Task<(byte[]? Bytes, ParseRawReportOutcome? Stopped)> ReadBytesAsync(
        Prepared prepared,
        Guid orgId,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        byte[]? bytes;
        try
        {
            // No scope is open here (P5): the read scope closed before this call.
            bytes = await storage.GetAsync(prepared.ObjectKey, prepared.MaxBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
        {
            // The store refuses to truncate an object above the caller's cap, and retrying cannot shrink it. A report
            // the cap no longer admits is refused, never parsed as a truncated document.
            LogOversize(logger, payload.ArtifactId, prepared.MaxBytes);
            EvidgateTelemetry.RecordIngestCapExceeded(ReportBytesCap);
            return (null, await RefuseMalformedAsync(prepared, orgId, payload, "too_large", cancellationToken).ConfigureAwait(false));
        }

        if (bytes is null)
        {
            throw new InvalidOperationException(
                "the raw report's stored object is not readable, so the report was not parsed; the task is retried");
        }

        return (bytes, null);
    }

    /// <summary>
    /// The child: the untrusted bytes go to the resource-limited child and nowhere else. A child that never started
    /// throws, because nothing judged the report; every other failure and the child's own rejection record are refusals.
    /// </summary>
    /// <param name="prepared">The cap and the parser pair the child and the refusal rows are stamped with.</param>
    /// <param name="bytes">The report's bytes, owned by this attempt.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="cancellationToken">Cancels the child.</param>
    /// <returns>The child's record stream, or the refused outcome that ended the attempt.</returns>
    private async Task<(ParseRunSucceeded? Succeeded, ParseRawReportOutcome? Stopped)> RunChildAsync(
        Prepared prepared,
        byte[] bytes,
        Guid orgId,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        await using var input = new MemoryStream(bytes, writable: false);
        var run = await runner
            .RunAsync(
                new ParseRunRequest(
                    ParseRunnerProfile.Xml,
                    prepared.MaxBytes,
                    XmlMediaType,
                    input,
                    limits.Value.MaxReportCases),
                cancellationToken)
            .ConfigureAwait(false);

        if (run is ParseRunFailed failed)
        {
            if (!ParseRunRejection.TryMap(failed, out var failure))
            {
                // SpawnFailed: no child existed, so the report was never judged; the queue retries with backoff.
                throw new InvalidOperationException(
                    "the parse child could not be started, so the raw report was not judged; the task is retried");
            }

            LogRefused(logger, payload.ArtifactId, failure);
            return (null, await RefuseMalformedAsync(prepared, orgId, payload, failure, cancellationToken).ConfigureAwait(false));
        }

        var succeeded = (ParseRunSucceeded)run;
        if (ChildRejectionRecord.ReasonIn(succeeded) is { } reported)
        {
            if (string.Equals(reported, ArtifactFailureCode.CapExceeded, StringComparison.Ordinal))
            {
                // The only child-side cap_exceeded is the report entry budget, so the counter names it.
                EvidgateTelemetry.RecordIngestCapExceeded(ReportCasesCap);
            }

            LogRefused(logger, payload.ArtifactId, reported);
            return (null, await RefuseMalformedAsync(prepared, orgId, payload, reported, cancellationToken).ConfigureAwait(false));
        }

        return (succeeded, null);
    }

    /// <summary>The mapping: the child's records become cases through the parser release the registry named.</summary>
    /// <param name="succeeded">The child's records, its marker excluded and no rejection among them.</param>
    /// <param name="prepared">The mapper and the plan the cases resolve against.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="cancellationToken">Cancels the refusal write.</param>
    /// <returns>The mapping, or the refused outcome that ended the attempt.</returns>
    private async Task<(JUnitMapResult? Mapped, ParseRawReportOutcome? Stopped)> MapAsync(
        ParseRunSucceeded succeeded,
        Prepared prepared,
        Guid orgId,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            return (
                prepared.Mapper.Map(
                    succeeded.Records,
                    new JUnitMapContext(payload.ArtifactId, prepared.Plan.Resolve)),
                null);
        }
        catch (JUnitParseRefusedException refused)
        {
            LogRefused(logger, payload.ArtifactId, refused.Reason);
            return (null, await RefuseMalformedAsync(prepared, orgId, payload, refused.Reason, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// The last stage: the resolved cases become events, and the event set and the report's own row are written in one
    /// transaction. The serialization runs with no scope open, so the transaction only carries the rows.
    /// </summary>
    /// <param name="prepared">The parser release the events and the row are stamped with.</param>
    /// <param name="mapped">What the mapper resolved.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The named outcome of the attempt.</returns>
    private async Task<ParseRawReportOutcome> WriteMappedAsync(
        Prepared prepared,
        JUnitMapResult mapped,
        Guid orgId,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        // Belt and braces over the mapper's contract: an event without a resolved key attributes a result to no planned
        // execution, so it is never written.
        var cases = mapped.Cases
            .Where(item => !string.IsNullOrEmpty(item.ExecutionKey))
            .ToArray();

        var producerId = ProducerPrefix + payload.ArtifactId.ToString("D", CultureInfo.InvariantCulture);
        var events = SerializeEvents(payload.ArtifactId, producerId, prepared, cases);

        return await WriteAsync(
                orgId,
                payload,
                producerId,
                events,
                ParsedReport(prepared, mapped, cases),
                cancellationToken)
            .ConfigureAwait(false);
    }

}
