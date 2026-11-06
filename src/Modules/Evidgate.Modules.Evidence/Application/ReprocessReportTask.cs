using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Ingestion.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// Payload of the <c>ingest.reprocess_report</c> task: the immutable reprocess request the command committed, whose own
/// id is also the id of the <c>raw_report</c> row this attempt will write. The tenant, the run, the artifact and the
/// parser release all come from that row, never from the payload.
/// </summary>
/// <param name="ParsingId">The <c>raw_report_reprocess</c> row, and the id of the report row this task writes.</param>
internal sealed record ReprocessReportPayload(Guid ParsingId)
{
    /// <summary>The task type the command enqueues and this handler claims.</summary>
    public const string TaskType = "ingest.reprocess_report";
}

/// <summary>
/// Re-parses one stored raw report at the parser release its request pinned, then writes the derived evidence under a
/// producer identity of its own.
/// <para>
/// <b>Nothing about the run moves.</b> This handler is not the ingest route: it does not drive <c>queued → running</c>,
/// writes no <c>event_batch</c> receipt, emits no usage row, calls no quota gate, and never rewrites the source report,
/// the earlier revision or the events behind it. What it adds is a second reading of the same bytes, and 05.3.2's
/// reconciler turns that into the next <c>evidence_revision</c>.
/// </para>
/// <para>
/// <b>Synthetic producer, never a CI producer.</b> The events carry <c>reprocess-&lt;parsing id&gt;</c> and sequences
/// from 1, so they cannot collide with the shard that delivered the original report. The slot that identity needs is
/// written closed and declared in the same commit and carries no CI run reference, so it registers no producer and can
/// never hold the reconciler on a shard that no producer will finalize.
/// </para>
/// <para>
/// <b>The bytes are read, the rows are hashed, and only then does a write transaction open.</b> One short read-only
/// scope resolves the request, the artifact, the pinned release and the plan; the object is fetched and the document
/// mapped with no scope open (00 §5.7 P5). A store that cannot answer throws, so the queue retries and eventually
/// dead-letters the attempt: the request row then still holds no report row, and the evidence of the run is untouched.
/// </para>
/// </summary>
/// <param name="scopes">Opens the short scopes this attempt runs in: the read, the write of a parsed report and, for a refusal, the write of its row.</param>
/// <param name="contexts">The tenant write model the artifact and its kind policy are read through.</param>
/// <param name="db">The Dapper path for the request, the registry, the plan, the slot and the report row.</param>
/// <param name="storage">The object store the report's bytes are read from, capped by the kind policy.</param>
/// <param name="runner">The resource-limited child the untrusted report is parsed in; this process never parses it.</param>
/// <param name="limits">The host's ingest caps; the report entry budget is handed to the child.</param>
/// <param name="mappers">The loaded JUnit mappers; the one whose pair the request pinned does the mapping.</param>
/// <param name="scopeFactory">Reaches the scoped case-attempt writer: handlers are singletons, and the writer joins the ambient tenant transaction and never commits.</param>
/// <param name="outbox">The reconcile trigger's transactional outbox writer.</param>
/// <param name="logger">The one structured line each attempt writes.</param>
internal sealed partial class ReprocessReportHandler(
    ITenantScopeFactory scopes,
    ITenantDbContextFactory<EvidenceDbContext> contexts,
    ITenantDb db,
    IObjectStorage storage,
    IParseRunner runner,
    IOptions<IngestionLimits> limits,
    IEnumerable<IJUnitMapper> mappers,
    IServiceScopeFactory scopeFactory,
    IOutboxWriter outbox,
    ILogger<ReprocessReportHandler> logger) : ITransactionlessTaskHandler<ReprocessReportPayload>
{
    /// <summary>The cap name a report over its byte budget carries in telemetry.</summary>
    private const string ReportBytesCap = "report_bytes";

    /// <summary>The cap name a report over its entry budget carries in telemetry.</summary>
    private const string ReportCasesCap = "report_cases";

    /// <summary>The media type the child is told the report carries.</summary>
    private const string XmlMediaType = "application/xml";

    /// <inheritdoc />
    public string TaskType => ReprocessReportPayload.TaskType;

    /// <summary>Runs one reprocess attempt and discards the named outcome (the task queue has no caller to return it to).</summary>
    /// <param name="payload">The reprocess request.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="cancellationToken">Cancelled on shutdown and on lease loss.</param>
    public async ValueTask HandleAsync(
        ReprocessReportPayload payload,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await ExecuteAsync(payload, orgId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One reprocess attempt: prepare, fetch, run the child, map, serialize, write. The named outcome is what a test
    /// asserts and what the task line logs.
    /// </summary>
    /// <param name="payload">The reprocess request.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="cancellationToken">Cancelled on shutdown and on lease loss.</param>
    /// <returns>The named outcome of the attempt.</returns>
    internal async Task<ParseRawReportOutcome> ExecuteAsync(
        ReprocessReportPayload payload,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var preparation = await PrepareAsync(orgId, payload, cancellationToken).ConfigureAwait(false);
        if (preparation.Prepared is not { } prepared)
        {
            return preparation.Stopped!;
        }

        var (bytes, readStopped) = await ReadBytesAsync(prepared, orgId, cancellationToken).ConfigureAwait(false);
        if (bytes is not { } content)
        {
            return readStopped!;
        }

        var (succeeded, childStopped) = await RunChildAsync(prepared, content, orgId, cancellationToken).ConfigureAwait(false);
        if (succeeded is not { } run)
        {
            return childStopped!;
        }

        var (mapped, mapStopped) = await MapAsync(run, prepared, orgId, cancellationToken).ConfigureAwait(false);
        if (mapped is not { } resolution)
        {
            return mapStopped!;
        }

        return await WriteMappedAsync(prepared, resolution, orgId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The fetch: the object is read with no scope open, capped by the kind policy and the profile's ceiling. A store
    /// that refuses to truncate is a refusal; an absent object is a retryable fault, because the request, the artifact
    /// and the previous evidence stay coherent while the queue retries.
    /// </summary>
    /// <param name="prepared">The fetch inputs the read scope resolved.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The object's bytes, or the refused outcome that ended the attempt.</returns>
    private async Task<(byte[]? Bytes, ParseRawReportOutcome? Stopped)> ReadBytesAsync(
        Prepared prepared,
        Guid orgId,
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
            LogOversize(logger, prepared.ParsingId, prepared.MaxBytes);
            EvidgateTelemetry.RecordIngestCapExceeded(ReportBytesCap);
            return (null, await RefuseMalformedAsync(prepared, orgId, "too_large", cancellationToken).ConfigureAwait(false));
        }

        if (bytes is null)
        {
            throw new InvalidOperationException(
                "the reprocessed report's stored object is not readable, so the report was not parsed; the task is retried");
        }

        return (bytes, null);
    }

    /// <summary>
    /// The child: the untrusted bytes go to the resource-limited child and nowhere else. A child that never started
    /// throws, because nothing judged the report; every other failure and the child's own rejection record are refusals.
    /// </summary>
    /// <param name="prepared">The cap and the pinned release the refusal row is stamped with.</param>
    /// <param name="bytes">The report's bytes, owned by this attempt.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="cancellationToken">Cancels the child.</param>
    /// <returns>The child's record stream, or the refused outcome that ended the attempt.</returns>
    private async Task<(ParseRunSucceeded? Succeeded, ParseRawReportOutcome? Stopped)> RunChildAsync(
        Prepared prepared,
        byte[] bytes,
        Guid orgId,
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
                // SpawnFailed: no child existed, so the document was never judged; the queue retries with backoff.
                throw new InvalidOperationException(
                    "the parse child could not be started, so the reprocessed report was not judged; the task is retried");
            }

            LogRefused(logger, prepared.ParsingId, failure);
            return (null, await RefuseMalformedAsync(prepared, orgId, failure, cancellationToken).ConfigureAwait(false));
        }

        var succeeded = (ParseRunSucceeded)run;
        if (ChildRejectionRecord.ReasonIn(succeeded) is { } reported)
        {
            if (string.Equals(reported, ArtifactFailureCode.CapExceeded, StringComparison.Ordinal))
            {
                // The only child-side cap_exceeded is the report entry budget, so the counter names it.
                EvidgateTelemetry.RecordIngestCapExceeded(ReportCasesCap);
            }

            LogRefused(logger, prepared.ParsingId, reported);
            return (null, await RefuseMalformedAsync(prepared, orgId, reported, cancellationToken).ConfigureAwait(false));
        }

        return (succeeded, null);
    }

    /// <summary>The mapping: the child's records become cases through the release the request pinned.</summary>
    /// <param name="succeeded">The child's records, its marker excluded and no rejection among them.</param>
    /// <param name="prepared">The mapper and the plan the cases resolve against.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="cancellationToken">Cancels the refusal write.</param>
    /// <returns>The mapping, or the refused outcome that ended the attempt.</returns>
    private async Task<(JUnitMapResult? Mapped, ParseRawReportOutcome? Stopped)> MapAsync(
        ParseRunSucceeded succeeded,
        Prepared prepared,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        try
        {
            return (
                prepared.Mapper.Map(
                    succeeded.Records,
                    new JUnitMapContext(prepared.ArtifactId, prepared.Plan.Resolve)),
                null);
        }
        catch (JUnitParseRefusedException refused)
        {
            LogRefused(logger, prepared.ParsingId, refused.Reason);
            return (null, await RefuseMalformedAsync(prepared, orgId, refused.Reason, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// The last stage: the resolved cases become events under this request's own producer identity, and the event set
    /// and the report's own row are written in one transaction. The serialization runs with no scope open, so the
    /// transaction only carries parameters.
    /// </summary>
    /// <param name="prepared">The pinned release the events and the row are stamped with.</param>
    /// <param name="mapped">What the mapper resolved.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The named outcome of the attempt.</returns>
    private async Task<ParseRawReportOutcome> WriteMappedAsync(
        Prepared prepared,
        JUnitMapResult mapped,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        // Belt and braces over the mapper's contract: an event without a resolved key attributes a result to no planned
        // execution, so it is never written.
        var cases = mapped.Cases
            .Where(item => !string.IsNullOrEmpty(item.ExecutionKey))
            .ToArray();

        // Sequences are this producer's own 1..n, not the document positions: the events must not collide with the
        // sequences the original report's producer already holds for this run.
        var events = SerializeEvents(prepared, cases);

        return await WriteAsync(orgId, prepared, events, ParsedReport(prepared, mapped, cases), cancellationToken)
            .ConfigureAwait(false);
    }
}
