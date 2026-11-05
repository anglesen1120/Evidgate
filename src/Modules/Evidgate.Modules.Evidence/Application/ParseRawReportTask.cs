using System.Globalization;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// Payload of the <c>raw_report_parse</c> task: the run the report belongs to, the stored artifact and the producer
/// kind its report declares. The tenant comes from the task row, never from the payload.
/// </summary>
/// <param name="RunId">The run the report was uploaded for; a report of another run yields no event.</param>
/// <param name="ArtifactId">The raw report artifact; also the synthetic producer id's suffix.</param>
/// <param name="ProducerKind">The report dialect, e.g. <c>pytest-junitxml</c>; resolved against the parser registry.</param>
internal sealed record ParseRawReportPayload(Guid RunId, Guid ArtifactId, string ProducerKind);

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
/// so the transaction that follows holds a connection only for its writes.
/// </para>
/// <para>
/// <b>Admission is the event batch route's, decided once under the run's lock.</b> A complete run, an incomplete run
/// past its late window, and a deleted or suspended project refuse the write as a terminal named outcome; a canceled
/// run accepts it and marks it <c>after_cancel</c>, an incomplete run inside its window accepts it and marks it
/// <c>late</c>. The rule itself is <see cref="RunAdmissionPolicy"/>, shared with the ingest path.
/// </para>
/// <para>
/// <b>One transaction writes the slot, the events, the attempts and the trigger.</b> The synthetic producer's slot is
/// created already closed and declared, because no producer will ever finalize a report, and only then can the
/// reconciler compare this shard instead of waiting on an open one. Every newly inserted finish also becomes a
/// <c>case_attempt</c> row through the existing writer, and one reconcile trigger is enqueued — the late-evidence one
/// for a run that already finished as incomplete, the ordinary one for every other run.
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
/// <b>A refused document writes nothing.</b> <see cref="JUnitParseRefusedException"/> is the mapper's only way to
/// report a refusal (DTD, truncation, cap), and it becomes a named <c>malformed</c> outcome with zero events — except a
/// wall-clock <c>timeout</c>, which is an infrastructure abandon rather than a verdict and is thrown so the queue
/// retries it. No other exception is read as a verdict: a resolver or storage fault must surface.
/// </para>
/// <para>
/// <b>A report whose artifact belongs to another run, or another tenant, yields no event.</b> The payload's run id is
/// compared with the artifact's inside the tenant scope, and the plan read is that run's, so a crafted payload can
/// neither attribute events to a foreign run nor read its plan.
/// </para>
/// </summary>
/// <param name="scopes">Opens the two short scopes this attempt runs in.</param>
/// <param name="contexts">The tenant write model the artifact and its kind policy are read through.</param>
/// <param name="db">The Dapper path for the registry, the plan, the slot and the event insert.</param>
/// <param name="storage">The object store the report's bytes are read from, capped by the kind policy.</param>
/// <param name="parsers">The parser registry read (producer kind → parser pair).</param>
/// <param name="mappers">The loaded JUnit mappers; the one whose pair the registry names does the mapping.</param>
/// <param name="scopeFactory">Reaches the scoped case-attempt writer: handlers are singletons, and the writer joins the ambient tenant transaction and never commits.</param>
/// <param name="outbox">The reconcile trigger's transactional outbox writer.</param>
/// <param name="logger">The one structured line each attempt writes.</param>
internal sealed partial class ParseRawReportHandler(
    ITenantScopeFactory scopes,
    ITenantDbContextFactory<EvidenceDbContext> contexts,
    ITenantDb db,
    IObjectStorage storage,
    IReportParserResolver parsers,
    IEnumerable<IJUnitMapper> mappers,
    IServiceScopeFactory scopeFactory,
    IOutboxWriter outbox,
    ILogger<ParseRawReportHandler> logger) : ITransactionlessTaskHandler<ParseRawReportPayload>
{
    /// <summary>The cap name a report over its byte budget carries in telemetry.</summary>
    private const string ReportBytesCap = "report_bytes";

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
        var (prepared, stopped) = await PrepareAsync(orgId, payload, cancellationToken).ConfigureAwait(false);
        if (prepared is null)
        {
            return stopped!;
        }

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
            return new ParseRawReportOutcome(ParseRawReportOutcome.Malformed, "too_large");
        }

        if (bytes is null)
        {
            // An absent object is an infrastructure fault (or a store that has not made the promotion visible yet):
            // retry instead of recording evidence nobody judged.
            throw new InvalidOperationException(
                "the raw report's stored object is not readable, so the report was not parsed; the task is retried");
        }

        JUnitMapResult mapped;
        using var input = new MemoryStream(bytes, writable: false);
        try
        {
            mapped = prepared.Mapper.Map(
                input,
                new JUnitMapContext(payload.ArtifactId, prepared.Plan.Resolve));
        }
        catch (JUnitParseRefusedException refused) when (string.Equals(
            refused.Reason, ParsingFailureReason.Timeout, StringComparison.Ordinal))
        {
            // A wall-clock abandon says nothing about the document: it may parse on a less loaded attempt, so the task
            // is retried rather than recorded as malformed evidence.
            throw new InvalidOperationException(
                "the raw report parse timed out before the document was judged, so the report was not parsed; the task is retried",
                refused);
        }
        catch (JUnitParseRefusedException refused)
        {
            LogRefused(logger, payload.ArtifactId, refused.Reason);
            return new ParseRawReportOutcome(ParseRawReportOutcome.Malformed, refused.Reason);
        }

        // Belt and braces over the mapper's contract: an event without a resolved key would attribute a result to no
        // planned execution, so it is never written.
        var cases = mapped.Cases
            .Where(item => !string.IsNullOrEmpty(item.ExecutionKey))
            .ToArray();

        var producerId = ProducerPrefix + payload.ArtifactId.ToString("D", CultureInfo.InvariantCulture);

        // The section that costs the most work (canonical hashing and serialization of every row) runs here, with no
        // scope open, and the write transaction only carries the rows.
        var events = SerializeEvents(payload.ArtifactId, producerId, prepared, cases);

        return await WriteAsync(orgId, payload, producerId, events, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one read-only scope: the artifact, its kind's cap, the parser pair and the run's plan. It closes before the
    /// caller fetches bytes, so nothing holds a connection while the object store answers.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The prepared attempt, or the outcome that ended it before any fetch.</returns>
    private async Task<(Prepared? Prepared, ParseRawReportOutcome? Stopped)> PrepareAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: true, cancellationToken)
            .ConfigureAwait(false);
        await using var context = contexts.Create();

        var artifact = await context.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == payload.ArtifactId, cancellationToken)
            .ConfigureAwait(false);

        if (artifact is null)
        {
            LogSkipped(logger, payload.ArtifactId, "absent");
            return (null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "absent"));
        }

        if (!string.Equals(artifact.State, ArtifactState.Ready, StringComparison.Ordinal)
            || !string.Equals(artifact.Kind, ArtifactKind.RawReport, StringComparison.Ordinal))
        {
            LogSkipped(logger, payload.ArtifactId, "not_a_ready_raw_report");
            return (null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "not_a_ready_raw_report"));
        }

        if (artifact.RunId != payload.RunId)
        {
            // The payload names a run the artifact does not belong to: no event may be attributed to that run.
            LogSkipped(logger, payload.ArtifactId, "foreign_run");
            return (null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "foreign_run"));
        }

        if (!ArtifactObjectKey.IsMintedFor(artifact))
        {
            // Defence in depth over the 0028 shape CHECK: the key a row holds must be the key this row mints, so a
            // parse can never read bytes another coordinate names. A row that fails this is corrupt or hand-written;
            // the attempt throws (redelivered, then dead-lettered) rather than reading an object it cannot vouch for.
            throw new InvalidOperationException(ArtifactObjectKey.MismatchMessage);
        }

        var resolution = await parsers.ResolveAsync(payload.ProducerKind, cancellationToken).ConfigureAwait(false);
        if (!resolution.IsResolved)
        {
            LogUnsupported(logger, payload.ArtifactId, resolution.UnsupportedReason!);
            return (null, new ParseRawReportOutcome(ParseRawReportOutcome.Unsupported, resolution.UnsupportedReason));
        }

        var mapper = mappers.FirstOrDefault(candidate =>
            string.Equals(candidate.ParserId, resolution.ParserId, StringComparison.Ordinal)
            && string.Equals(candidate.Version, resolution.Version, StringComparison.Ordinal));

        if (mapper is null)
        {
            // A registry row without a loaded mapper is a deployment mismatch, reported like an unknown kind so a
            // report of an unloaded parser is never parsed by a different one.
            LogUnsupported(logger, payload.ArtifactId, "parser_unavailable");
            return (null, new ParseRawReportOutcome(ParseRawReportOutcome.Unsupported, "parser_unavailable"));
        }

        // The kind policy caps one kind; the profile's hard ceiling caps every report whatever its kind says, so the
        // read is bounded by the tighter of the two and a widened kind policy cannot raise the platform limit.
        var kindMaxBytes = (await ArtifactCapturePolicy
                .ResolveAsync(context, artifact.ProjectId, artifact.Kind, cancellationToken)
                .ConfigureAwait(false))
            .MaxBytes;
        var maxBytes = Math.Min(kindMaxBytes, ParsingLimits.MaxReportBytes);

        var hints = await db
            .QueryAsync<PlannedExecutionHint>(
                PlannedExecutionIndex.SelectSql,
                new { org_id = orgId, run_id = payload.RunId },
                cancellationToken)
            .ConfigureAwait(false);

        return (
            new Prepared(artifact.ObjectKey, maxBytes, resolution.ParserId!, resolution.Version!, mapper, PlannedExecutionIndex.Build(hints)),
            null);
    }
}
