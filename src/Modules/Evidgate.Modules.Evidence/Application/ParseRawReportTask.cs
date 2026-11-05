using System.Globalization;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Orchestrator.Contracts;
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

    /// <summary>There was nothing to parse (absent, not ready, another run's report, no free producer slot).</summary>
    public const string Skipped = "skipped";

    /// <summary>True when the attempt left no event row because the document was refused.</summary>
    public bool IsRefusal => Status is Malformed or Unsupported;
}

/// <summary>
/// Maps one stored raw report to <c>test.finished</c> events under the parser its producer kind registers
/// (04.4.1-T1.4): the registry decides the parser pair, the mapper decides the cases, and this handler decides what may
/// be written and in which transaction.
/// <para>
/// <b>The bytes are read and parsed before any write transaction opens.</b> One short read-only scope resolves what
/// the fetch needs (the artifact, the kind's byte cap, the parser pair, the run's plan), then closes; the object is
/// fetched and the document mapped with no scope open (00 §5.7 P5). Only after the mapping succeeded does a second
/// scope open, and that one holds the run's lock, the synthetic producer slot, every event in one statement, and the
/// reconcile trigger — all committed together or not at all.
/// </para>
/// <para>
/// <b>A refused document writes nothing.</b> <see cref="JUnitParseRefusedException"/> is the mapper's only way to
/// report a refusal (DTD, truncation, cap, timeout), and it becomes a named <c>malformed</c> outcome with zero events.
/// No other exception is read as a verdict: a resolver or storage fault must surface so the queue retries instead of
/// recording a document as broken.
/// </para>
/// <para>
/// <b>A replay is a no-op.</b> The producer id is synthetic and per report (<c>junit-&lt;artifact id&gt;</c>), and the
/// sequence is the testcase's document position, so <c>event_run_producer_sequence_key</c> makes the second insert of
/// the same event set match no row: the slot's high-water mark and the reconcile trigger are written only when at
/// least one event was new.
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
/// <param name="outbox">The reconcile trigger's transactional outbox writer.</param>
/// <param name="logger">The one structured line each attempt writes.</param>
internal sealed partial class ParseRawReportHandler(
    ITenantScopeFactory scopes,
    ITenantDbContextFactory<EvidenceDbContext> contexts,
    ITenantDb db,
    IObjectStorage storage,
    IReportParserResolver parsers,
    IEnumerable<IJUnitMapper> mappers,
    IOutboxWriter outbox,
    ILogger<ParseRawReportHandler> logger) : ITransactionlessTaskHandler<ParseRawReportPayload>
{
    /// <summary>The cap name a report over its byte budget carries in telemetry.</summary>
    private const string ReportBytesCap = "report_bytes";

    /// <summary>The one event type a mapped testcase becomes.</summary>
    private const string TestFinished = "test.finished";

    /// <summary>The prefix of the synthetic producer id; the artifact id makes it unique per report.</summary>
    private const string ProducerPrefix = "junit-";

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
    /// One parse attempt: prepare, fetch, map, write. The named outcome is the same value the task line logs.
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

        return await WriteAsync(orgId, payload, prepared, cases, cancellationToken).ConfigureAwait(false);
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

        var maxBytes = (await ArtifactCapturePolicy
                .ResolveAsync(context, artifact.ProjectId, artifact.Kind, cancellationToken)
                .ConfigureAwait(false))
            .MaxBytes;

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

    /// <summary>
    /// The one write transaction: the run's lock, the synthetic producer slot, every event in one statement, and the
    /// reconcile trigger when the run is in its late window — committed together or not at all.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="prepared">The parser pair the attempt resolved.</param>
    /// <param name="cases">The mapped cases with a resolved execution key.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The named outcome of the write.</returns>
    private async Task<ParseRawReportOutcome> WriteAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        Prepared prepared,
        MappedCase[] cases,
        CancellationToken cancellationToken)
    {
        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);
        await using var context = contexts.Create();

        // Read again inside the write scope rather than trusting the preparation: a concurrent attempt may have
        // decided the artifact while the bytes were mapped, and a report that left `ready` is out of evidence.
        var artifact = await context.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == payload.ArtifactId, cancellationToken)
            .ConfigureAwait(false);

        if (artifact is null
            || !string.Equals(artifact.State, ArtifactState.Ready, StringComparison.Ordinal)
            || !string.Equals(artifact.Kind, ArtifactKind.RawReport, StringComparison.Ordinal)
            || artifact.RunId != payload.RunId)
        {
            LogSkipped(logger, payload.ArtifactId, "left_ready");
            return new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "left_ready");
        }

        // The run lock serialises two reports of one run, so the slot's next shard index cannot be taken twice, and it
        // also tells this attempt whether the run is in its late window (the same rule the ingest path applies).
        var run = await db
            .QuerySingleOrDefaultAsync<RunGate>(RunGateSql, new { org_id = orgId, run_id = payload.RunId }, cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            LogSkipped(logger, payload.ArtifactId, "run_absent");
            return new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "run_absent");
        }

        if (cases.Length == 0)
        {
            // Nothing mapped (every testcase unmapped, or a document with no testcase): no slot, no event, no trigger.
            LogMapped(logger, payload.ArtifactId, 0, 0);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ParseRawReportOutcome(ParseRawReportOutcome.Parsed, Events: 0);
        }

        var producerId = ProducerPrefix + payload.ArtifactId.ToString("D", CultureInfo.InvariantCulture);
        var shardIndex = await db
            .ScalarAsync<int?>(NextShardIndexSql, new { org_id = orgId, run_id = payload.RunId }, cancellationToken)
            .ConfigureAwait(false);

        if (shardIndex is null or > MaxShardIndex)
        {
            // Every manifest shard index is taken, so no slot can name this synthetic producer. Retrying cannot free
            // one; the attempt is reported for an operator instead of silently dropping the report.
            LogSkipped(logger, payload.ArtifactId, "no_producer_slot");
            return new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "no_producer_slot");
        }

        await db.ExecuteAsync(
                InsertSlotSql,
                new
                {
                    id = Guid.CreateVersion7(),
                    org_id = orgId,
                    run_id = payload.RunId,
                    shard_index = shardIndex.Value,
                    producer_id = producerId,
                },
                cancellationToken)
            .ConfigureAwait(false);

        var maxSequence = cases.Max(item => item.Sequence);
        var inserted = await db.ExecuteAsync(
                InsertEventsSql,
                new
                {
                    org_id = orgId,
                    run_id = payload.RunId,
                    after_cancel = run.AfterCancel,
                    late = run.Late,
                    events = SerializeEvents(payload.ArtifactId, producerId, prepared, cases),
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (inserted > 0)
        {
            await db.ExecuteAsync(
                    UpdateSlotSql,
                    new { org_id = orgId, run_id = payload.RunId, producer_id = producerId, last_sequence = maxSequence },
                    cancellationToken)
                .ConfigureAwait(false);

            if (run.Late)
            {
                // The run already finished as incomplete, so no finalize will compare these events: one trigger per
                // report, keyed by the report's producer and its high-water mark, is what appends the next revision.
                await outbox.EnqueueAsync(
                        RunReconcileContract.OutboxTaskType,
                        RunReconcileContract.LatePayloadJson(payload.RunId, producerId),
                        RunReconcileContract.AggregateType,
                        payload.RunId,
                        RunReconcileContract.LateIdempotencyKey(payload.RunId, producerId, maxSequence),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        LogMapped(logger, payload.ArtifactId, cases.Length, inserted);

        return new ParseRawReportOutcome(ParseRawReportOutcome.Parsed, Events: inserted);
    }
}
