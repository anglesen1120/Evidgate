using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// Payload of the <c>artifact_secret_scan</c> task, as the completion's outbox row writes it: one artifact id. The
/// tenant comes from the task row (<c>task.org_id</c>), never from the payload, so a payload cannot name another
/// tenant's artifact.
/// </summary>
/// <param name="ArtifactId">The artifact whose stored bytes are scanned.</param>
internal sealed record RunArtifactSecretScanPayload(Guid ArtifactId);

/// <summary>
/// Runs the masking scan over one <c>ready</c> artifact (06.2.3 §4): every pattern class the bytes match becomes one
/// <c>masking_event</c> row, the artifact's <c>scan_state</c> leaves <c>pending</c>, a flagged artifact is raised to
/// <c>sensitive</c>, and a project whose kind policy says <c>reject_on_secret</c> moves the artifact to
/// <c>pending_delete</c> so the object is deleted instead of served.
/// <para>
/// <b>The read runs with no transaction open.</b> The handler is registered transactionless because an object-store
/// read must not happen inside a database transaction (00 §5.7 P5): a first short read-only scope resolves what the
/// read needs — the final key, the kind's byte cap and the project's rejection switch — the scope closes, the object
/// is fetched and scanned, and only then does a second scope write. Nothing holds a connection while the store is
/// answering.
/// </para>
/// <para>
/// <b>One final decision per pattern class.</b> The events are inserted before the artifact's own transition, keyed by
/// <c>(org_id, artifact_id, pattern_class)</c> with <c>on conflict do nothing</c>: a replayed attempt therefore adds no
/// second row for a class, so a class keeps the action its first scan decided and flags and rejects can never be two
/// rows of one class. The <c>action</c> each row carries is the scan's decision for that class — <c>rejected</c> when
/// the project rejects, <c>flagged</c> otherwise — so the row and the artifact's state always agree.
/// </para>
/// <para>
/// <b>A replay writes nothing.</b> The artifact is read again inside the write scope and the scan is only applied when
/// its <c>scan_state</c> is still <c>pending</c>; a row another attempt already decided is a no-op. The transition is
/// additionally guarded by the row's concurrency token, so an interleaved writer that changed the row while the bytes
/// were scanned makes this attempt re-read and either no-op (the scan is decided) or throw (the row is still pending,
/// so the queue's redelivery scans again). A crash between the events and the transition rolls both back, because the
/// events are written in the same transaction as the transition.
/// </para>
/// <para>
/// <b>An archive is scanned through the parser profile, and its container too.</b> A trace or report bundle is a
/// deflated zip, and its entries' secrets do not appear in the compressed bytes at all, so scanning the object in
/// place alone would always answer clean. When the artifact's declared type is <c>application/zip</c> — the type the
/// completion sniffed the stored bytes against — every entry is decompressed by <see cref="IParsing.ReadArchiveEntries"/>
/// and scanned, and the counts are summed per class. The container's own bytes are scanned as well, because an entry
/// name and the archive comment live in the headers and a stored entry is readable there. The expanded budget is the
/// same cap the object was admitted under, so one artifact can never expand past the size its kind allows. An archive
/// the profile refuses is thrown, never read as clean: the queue retries and finally dead-letters while
/// <c>scan_state</c> stays <c>pending</c>.
/// </para>
/// <para>
/// <b>Nothing here fails into a verdict.</b> A missing or unreadable object throws, so the queue retries and finally
/// dead-letters the row while <c>scan_state</c> stays <c>pending</c> — a store hiccup must never look like a clean
/// artifact. An object above the kind's cap throws for the same reason: the cap the artifact was admitted under is
/// the cap the scan applies (D-A9), and a truncated scan is not a scan of the stored bytes.
/// </para>
/// <para>
/// <b>No matched value leaves the scanner.</b> <see cref="SecretPatternScanner"/> returns class names and counts only;
/// this handler writes those, and its log line names the artifact, the classes and the action — never a match, a key,
/// a URL or a byte of the object (00 §5.7 S3).
/// </para>
/// </summary>
/// <param name="scopes">Opens the two short tenant scopes this attempt runs in.</param>
/// <param name="contexts">The tenant write model the artifact and its kind policy are read through.</param>
/// <param name="db">The Dapper path that writes the events inside the write scope's transaction.</param>
/// <param name="storage">The object store the artifact's bytes are read from, capped by the kind policy.</param>
/// <param name="parsing">The untrusted-parser profile, the only arm that decompresses an archive for the scan.</param>
/// <param name="logger">The one structured line each decided or skipped attempt writes.</param>
internal sealed partial class RunArtifactSecretScanHandler(
    ITenantScopeFactory scopes,
    ITenantDbContextFactory<EvidenceDbContext> contexts,
    ITenantDb db,
    IObjectStorage storage,
    IParsing parsing,
    ILogger<RunArtifactSecretScanHandler> logger) : ITransactionlessTaskHandler<RunArtifactSecretScanPayload>
{
    /// <inheritdoc />
    public string TaskType => EvidenceTaskTypes.SecretScan;

    /// <summary>Scans one artifact: read, fetch, scan, then write the verdict.</summary>
    /// <param name="payload">The artifact to scan.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="cancellationToken">Cancelled on shutdown and on lease loss.</param>
    public async ValueTask HandleAsync(
        RunArtifactSecretScanPayload payload,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var prepared = await ReadAsync(orgId, payload.ArtifactId, cancellationToken).ConfigureAwait(false);
        if (prepared is null)
        {
            return;
        }

        var (artifact, settings) = prepared.Value;

        // The read is outside every transaction, and it is bounded by the same cap the artifact was admitted under:
        // a store that answers a longer object fails here instead of being scanned as a truncated document.
        var content = await storage
            .GetAsync(artifact.ObjectKey, settings.MaxBytes, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the stored object of artifact {artifact.Id} is absent, so no scan verdict exists");

        var hits = ArtifactSecretScan.Scan(parsing, artifact.MediaType, content, settings.MaxBytes, cancellationToken);

        await WriteAsync(orgId, artifact.Id, hits, settings.RejectOnSecret, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves what the object read needs in one short read-only scope: the artifact, and the effective capture
    /// policy of its kind. An artifact that is absent, already scanned, or no longer <c>ready</c> is not this task's
    /// work at all, so the attempt ends without a call and without a write.
    /// </summary>
    /// <param name="orgId">The tenant of the claimed task row.</param>
    /// <param name="artifactId">The artifact the payload names.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The artifact and its capture policy, or null when there is nothing to scan.</returns>
    private async Task<(Artifact Artifact, ArtifactCaptureSettings Settings)?> ReadAsync(
        Guid orgId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: true, cancellationToken)
            .ConfigureAwait(false);
        await using var context = contexts.Create();

        var artifact = await context.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == artifactId, cancellationToken)
            .ConfigureAwait(false);

        if (artifact is null)
        {
            // Not a failure the tenant should be told about, and not a row this attempt may invent a verdict for: the
            // artifact is gone, so the scan has nothing to classify. Logged because a payload naming a row that never
            // existed is a producer bug an operator should see.
            LogSkipped(logger, artifactId, "absent");
            return null;
        }

        if (!string.Equals(artifact.ScanState, ArtifactScanState.Pending, StringComparison.Ordinal))
        {
            LogSkipped(logger, artifactId, "already_scanned");
            return null;
        }

        if (!string.Equals(artifact.State, ArtifactState.Ready, StringComparison.Ordinal))
        {
            // Only a ready artifact holds its final object: an upload still in flight has nothing to scan yet, and a
            // row that left ready (a rejection, a scheduled delete) is out of evidence, so scanning it would spend the
            // read on bytes that are about to be removed.
            LogSkipped(logger, artifactId, "not_ready");
            return null;
        }

        var settings = await ArtifactCapturePolicy
            .ResolveAsync(context, artifact.ProjectId, artifact.Kind, cancellationToken)
            .ConfigureAwait(false);

        return (artifact, settings);
    }

    /// <summary>
    /// Writes the verdict in one tenant transaction: the event of every matched class, then the artifact's own
    /// transition. Both are in the same transaction, so a failure after the events leaves neither behind, and a
    /// replayed attempt that finds the row decided writes nothing at all.
    /// </summary>
    /// <param name="orgId">The tenant of the claimed task row.</param>
    /// <param name="artifactId">The artifact the payload names.</param>
    /// <param name="hits">The classes the scan matched, with their counts; empty for a clean artifact.</param>
    /// <param name="rejectOnSecret">The project's switch: true moves a flagged artifact out of evidence.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task WriteAsync(
        Guid orgId,
        Guid artifactId,
        IReadOnlyList<SecretPatternHit> hits,
        bool rejectOnSecret,
        CancellationToken cancellationToken)
    {
        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);
        await using var context = contexts.Create();

        var artifact = await context.Artifacts
            .SingleOrDefaultAsync(row => row.Id == artifactId, cancellationToken)
            .ConfigureAwait(false);

        if (artifact is null
            || !string.Equals(artifact.ScanState, ArtifactScanState.Pending, StringComparison.Ordinal)
            || !string.Equals(artifact.State, ArtifactState.Ready, StringComparison.Ordinal))
        {
            // Read again rather than trusting the first scope: a concurrent attempt may have decided the row while the
            // bytes were being scanned, and a decided artifact is never scanned or rewritten twice.
            LogSkipped(logger, artifactId, artifact is null ? "absent" : "left_scan_pending");
            return;
        }

        var reject = rejectOnSecret && hits.Count > 0;
        var inserted = 0;

        if (hits.Count > 0)
        {
            inserted = await db.ExecuteAsync(
                    MaskingEventSql.InsertEvents,
                    new
                    {
                        events = SerializeEvents(orgId, artifactId, hits, reject),
                        org_id = orgId,
                        artifact_id = artifactId,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var next = artifact.MarkScanned(
            hits.Count > 0 ? ArtifactScanState.Flagged : ArtifactScanState.Clean);
        if (reject)
        {
            next = next.MarkPendingDelete(ArtifactFailureCode.SecretRejected);
        }

        context.Entry(artifact).CurrentValues.SetValues(next);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            await HandleLostRaceAsync(context, artifactId, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Checked only after the transition has written: a shortfall now means the statement dropped a class this
        // attempt counted (a surviving row of the same class from a scan whose transition never committed, or a
        // parameter that reached the statement incomplete). The artifact would then be flagged with an event set that
        // does not explain it, so the transaction is refused and the violation rolls back with it.
        if (inserted != hits.Count)
        {
            throw new InvalidOperationException(
                $"the masking events of artifact {artifactId} could not be written: {hits.Count} class(es) matched but {inserted} event row(s) were inserted");
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        LogScanned(logger, artifactId, hits.Count, reject);
    }

    /// <summary>
    /// Decides a lost concurrency race: a stored scan state other than <c>pending</c> means another attempt (or an
    /// earlier one) already wrote the verdict, so this attempt commits nothing and the events it inserted roll back
    /// with the scope. A row still pending was changed by another writer (a freeze resolving <c>attempt_id</c>, an
    /// operator update), so throwing lets the queue redeliver instead of leaving the artifact undecided forever.
    /// </summary>
    /// <param name="context">The write model of the attempt that lost the race.</param>
    /// <param name="artifactId">The artifact the payload names.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    private async Task HandleLostRaceAsync(
        EvidenceDbContext context,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        var stored = await context.Artifacts
            .AsNoTracking()
            .Where(row => row.Id == artifactId)
            .Select(row => row.ScanState)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (stored is null || string.Equals(stored, ArtifactScanState.Pending, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"the scan verdict of artifact {artifactId} could not be written: the row changed while its bytes were scanned");
        }

        LogSkipped(logger, artifactId, "already_scanned");
    }

    /// <summary>
    /// The one <c>jsonb</c> parameter of the event insert, in the recordset's column names. Every row carries the
    /// attempt's own org and artifact, so the statement's predicate can fail closed on a value this method got wrong.
    /// </summary>
    /// <param name="orgId">The tenant the event belongs to.</param>
    /// <param name="artifactId">The artifact the event belongs to.</param>
    /// <param name="hits">The matched classes and counts.</param>
    /// <param name="reject">True when the project rejects an artifact with a match.</param>
    /// <returns>The rows as JSON text.</returns>
    private static string SerializeEvents(
        Guid orgId,
        Guid artifactId,
        IReadOnlyList<SecretPatternHit> hits,
        bool reject)
    {
        var action = reject ? MaskingEventAction.Rejected : MaskingEventAction.Flagged;
        var rows = new MaskingEventRow[hits.Count];
        for (var index = 0; index < hits.Count; index++)
        {
            rows[index] = new MaskingEventRow(Guid.CreateVersion7(), orgId, artifactId, hits[index].PatternClass, hits[index].HitCount, action);
        }

        return JsonSerializer.Serialize(rows);
    }

    [LoggerMessage(
        EventId = 2450,
        Level = LogLevel.Warning,
        Message = "artifact secret scan of {ArtifactId} wrote no verdict: {Reason}")]
    private static partial void LogSkipped(ILogger logger, Guid artifactId, string reason);

    [LoggerMessage(
        EventId = 2451,
        Level = LogLevel.Information,
        Message = "artifact secret scan of {ArtifactId} decided {PatternClasses} pattern class(es); rejected {Rejected}")]
    private static partial void LogScanned(ILogger logger, Guid artifactId, int patternClasses, bool rejected);

    /// <summary>One event as <c>jsonb_to_recordset</c> reads it (the wire names are the column names).</summary>
    /// <param name="Id">The event's id.</param>
    /// <param name="OrgId">The tenant the event belongs to.</param>
    /// <param name="ArtifactId">The artifact whose bytes matched.</param>
    /// <param name="PatternClass">The matched class, one of <see cref="SecretPatternClass.All"/>.</param>
    /// <param name="HitCount">How many matches of the class the scan counted.</param>
    /// <param name="Action">The scan's decision for the class.</param>
    private sealed record MaskingEventRow(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("org_id")] Guid OrgId,
        [property: JsonPropertyName("artifact_id")] Guid ArtifactId,
        [property: JsonPropertyName("pattern_class")] string PatternClass,
        [property: JsonPropertyName("hit_count")] int HitCount,
        [property: JsonPropertyName("action")] string Action);
}

