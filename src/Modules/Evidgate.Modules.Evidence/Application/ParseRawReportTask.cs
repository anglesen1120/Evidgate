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
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Evidgate.Modules.Ingestion.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Evidgate.Modules.Evidence.Application.RawReportWorkerConstants;

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
/// The code word of a manifest whose shards do not name one storable producer kind (<see cref="ProducerUnknown"/>,
/// <see cref="ProducerInvalid"/> or <see cref="ProducerAmbiguous"/>). Such a report is refused without being read,
/// because no parser can be chosen.
/// </param>
internal sealed record ParseRawReportPayload(
    Guid RunId,
    Guid ArtifactId,
    string? ProducerKind,
    string? ProducerIssue = null)
{
    /// <summary>The issue of a manifest whose shards declare no producer hint at all.</summary>
    public const string ProducerUnknown = "producer_unknown";

    /// <summary>
    /// The issue of a manifest whose shards agree on one hint that the column's shape cannot store
    /// (<c>^[a-z][a-z0-9-]*$</c> in one to 64 characters): a dialect word outside the registry's vocabulary, such as a
    /// mixed-case word with an underscore. The hint is still only a lookup key, so it never reaches the payload as a
    /// kind; the refusal row then carries this issue rather than disappearing.
    /// </summary>
    public const string ProducerInvalid = "producer_invalid";

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
/// <param name="Status">
/// One of <c>parsed</c>, <c>malformed</c>, <c>unsupported</c>, <c>skipped</c> or <c>quota_blocked</c>.
/// </param>
/// <param name="Reason">The code word behind a non-parsed status, or <see langword="null"/>.</param>
/// <param name="Events">How many event rows this attempt inserted; 0 on a replay and on every refusal.</param>
internal sealed record ParseRawReportOutcome(string Status, string? Reason = null, int Events = 0)
{
    /// <summary>
    /// The project's billing period had no room for the events this attempt inserted: nothing was committed and the
    /// task is rescheduled for <see cref="RetryAfter"/> instead of spending an attempt on a refusal only a rollover or
    /// an operator raise can lift.
    /// </summary>
    public const string QuotaBlocked = "quota_blocked";

    /// <summary>The gate's wait on <see cref="QuotaBlocked"/>, null otherwise: how long until that batch may be retried.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// The question the gate blocked, carried out of the transaction so the caller can stamp the counter in a scope
    /// of its own; null unless the outcome is <see cref="QuotaBlocked"/>.
    /// </summary>
    internal QuotaCheckRequest? QuotaRequest { get; init; }

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
/// write scope for its single row and the trigger beside it.
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
/// <b>The first accepted report moves its run on, and only new events reserve quota and are metered.</b>
/// The gate runs after insertion so replays reserve nothing, but before metering and commit so a block rolls back
/// the evidence and the queued-to-running transition together with its audit buffer.
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
/// <b>A refused document writes no event, and one <c>raw_report</c> row per verdict.</b>
/// <see cref="JUnitParseRefusedException"/> is the mapper's only way to report a refusal of the record stream, and the
/// child's own rejection record (DTD, truncation, cap) or a failed run (<c>parse_crashed</c>, <c>parse_oom</c>,
/// <c>parse_timeout</c>) becomes a named <c>malformed</c> outcome with zero events, exactly as for an artifact parse;
/// what the parser decided is stored in one short write of its own, together with the reconcile trigger that names that
/// row and lets the freeze path read the report as missing evidence, so a report nobody could parse still leaves
/// evidence of who judged it and why. One artifact may carry more than one verdict — an unsupported row, then a
/// malformed one under a parser — and each leaves its own row and trigger. A producer kind with no loaded parser is
/// stored the same way as
/// <c>unsupported</c>. A report whose bytes its own <c>artifact_parse</c> already judged — the artifact is
/// <c>rejected</c> with that verdict, because the completion schedules both tasks in one transaction — stores the same
/// verdict as its <c>malformed</c> row without reading a byte and without an event; a rejected artifact of any other
/// reason is a skip, because no report was ever validly uploaded. Only a child that never started is retried, and it
/// writes nothing. No other exception is read as a verdict: a resolver or storage fault must surface.
/// </para>
/// <para>
/// <b>A report whose artifact belongs to another run, or another tenant, yields no event.</b> The payload's run id is
/// compared with the artifact's inside the tenant scope, and the plan read is that run's, so a crafted payload can
/// neither attribute events to a foreign run nor read its plan.
/// </para>
/// </summary>
/// <param name="scopes">Opens the short scopes this attempt runs in: the read, the write of a parsed report and, for a refusal, the write of its row and trigger.</param>
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
/// <param name="time">The attempt's clock: it bounds the retry a quota block defers for, never the gate's billing month, which the transaction supplies.</param>
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
    TimeProvider time,
    ILogger<ParseRawReportHandler> logger) : ITransactionlessTaskHandler<ParseRawReportPayload>
{
    /// <inheritdoc />
    public string TaskType => EvidenceTaskTypes.ReportParse;

    /// <summary>Runs one parse attempt; quota refusals defer the same task without spending an attempt.</summary>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="cancellationToken">Cancelled on shutdown and on lease loss.</param>
    public async ValueTask HandleAsync(
        ParseRawReportPayload payload,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var outcome = await ExecuteAsync(payload, orgId, cancellationToken).ConfigureAwait(false);
        if (outcome.Status == ParseRawReportOutcome.QuotaBlocked)
        {
            // The real sample is drawn here only: the delay itself stays a pure function a test can pin.
            throw new TaskDeferredException(
                QuotaRetryDelay(outcome.RetryAfter, time.GetUtcNow(), Random.Shared.NextDouble()));
        }
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
        if (await RefuseUnknownProducerAsync(payload, orgId, cancellationToken).ConfigureAwait(false)
            is { } unknownProducer)
        {
            return unknownProducer;
        }

        var preparation = await PrepareAsync(orgId, payload, cancellationToken).ConfigureAwait(false);
        if (preparation.Blocked is { } blocked)
        {
            // A report the queue already deferred and the counter still has no room for: nothing is fetched, parsed or
            // written, and the deferral repeats. The stamp is written again because the raise that cleared the first
            // one can still have left less than one event of room; the statement touches only counters that are
            // unstamped and still full, so a second pre-check stamps nothing. A pre-check fault carries no request and
            // writes nothing here.
            return await StampQuotaBlockAsync(orgId, blocked, cancellationToken).ConfigureAwait(false);
        }

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

        // The mapper is opened before the child runs: it is the sink the child's records stream into, so no record list
        // is ever built on this side, and its deferred faults surface only after the runner's own verdict.
        var recordMapper = prepared.Mapper.OpenRecordMapper(
            new JUnitMapContext(payload.ArtifactId, prepared.Plan.Resolve));

        if (await RunChildAsync(prepared, content, orgId, payload, recordMapper, cancellationToken)
                .ConfigureAwait(false) is { } childStopped)
        {
            return childStopped;
        }

        var (mapped, mapStopped) = await MapAsync(recordMapper, prepared, orgId, payload, cancellationToken).ConfigureAwait(false);
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
    /// <param name="recordMapper">The mapper the child's records reach as they are read; it also carries the rejection.</param>
    /// <param name="cancellationToken">Cancels the child.</param>
    /// <returns>The refused outcome that ended the attempt, or null when the child succeeded.</returns>
    private async Task<ParseRawReportOutcome?> RunChildAsync(
        Prepared prepared,
        byte[] bytes,
        Guid orgId,
        ParseRawReportPayload payload,
        IJUnitRecordMapper recordMapper,
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
                    limits.Value.MaxReportCases,
                    recordMapper),
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
            return await RefuseMalformedAsync(prepared, orgId, payload, failure, cancellationToken).ConfigureAwait(false);
        }

        if (recordMapper.RejectedReason is { } reported)
        {
            if (string.Equals(reported, ArtifactFailureCode.CapExceeded, StringComparison.Ordinal))
            {
                // The only child-side cap_exceeded is the report entry budget, so the counter names it.
                EvidgateTelemetry.RecordIngestCapExceeded(ReportCasesCap);
            }

            LogRefused(logger, payload.ArtifactId, reported);
            return await RefuseMalformedAsync(prepared, orgId, payload, reported, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>The mapping: the records the child fed become cases through the parser release the registry named.</summary>
    /// <param name="recordMapper">The mapper that received the child's records, its marker excluded and no rejection among them.</param>
    /// <param name="prepared">The mapper and the plan the cases resolve against.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="cancellationToken">Cancels the refusal write.</param>
    /// <returns>The mapping, or the refused outcome that ended the attempt.</returns>
    private async Task<(JUnitMapResult? Mapped, ParseRawReportOutcome? Stopped)> MapAsync(
        IJUnitRecordMapper recordMapper,
        Prepared prepared,
        Guid orgId,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            return (recordMapper.Finish(), null);
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

        var producerId = RawReportProducer.IdFor(payload.ArtifactId);
        var events = SerializeEvents(payload.ArtifactId, producerId, prepared, cases);

        var outcome = await WriteTransactionAsync(
                orgId,
                payload,
                producerId,
                events,
                ParsedReport(prepared, mapped, cases),
                cancellationToken)
            .ConfigureAwait(false);

        // The transaction is closed (committed or rolled back) by now, so the refusal stamp is the only write that can
        // still survive a block: the gate's own stamp rolled back with the evidence it refused.
        return await StampQuotaBlockAsync(orgId, outcome, cancellationToken).ConfigureAwait(false);
    }
}
