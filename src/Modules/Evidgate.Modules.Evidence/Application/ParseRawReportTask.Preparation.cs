using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The prepare half of <see cref="ParseRawReportHandler"/>: one short read-only scope resolves the artifact, its kind's
/// byte cap, the parser pair and the run's plan, then closes before any bytes are read. Kept apart from the fetch-and-map
/// half so each stage stays one file to read.
/// </summary>
internal sealed partial class ParseRawReportHandler
{
    /// <summary>
    /// What the one read scope answered: the attempt the fetch needs, or the outcome that ended it, plus the parser pair
    /// the registry named (null when it named none) so a refusal row can carry it.
    /// </summary>
    /// <param name="Prepared">The fetch inputs, or null when the attempt stopped in the read scope.</param>
    /// <param name="Stopped">The outcome that ended the attempt, or null when it continues.</param>
    /// <param name="ParserId">The registry's <c>parser_id</c>, when one was resolved.</param>
    /// <param name="ParserVersion">The registry's <c>version</c>, when one was resolved.</param>
    private sealed record Preparation(
        Prepared? Prepared,
        ParseRawReportOutcome? Stopped,
        string? ParserId = null,
        string? ParserVersion = null);

    /// <summary>
    /// The one read-only scope: the artifact, its kind's cap, the parser pair and the run's plan. It closes before the
    /// caller fetches bytes, so nothing holds a connection while the object store answers.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The prepared attempt, or the outcome that ended it (with the parser pair a refusal row carries).</returns>
    private async Task<Preparation> PrepareAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: true, cancellationToken)
            .ConfigureAwait(false);
        await using var context = contexts.Create();

        var (artifact, stopped) = await ReadArtifactAsync(context, payload, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            return stopped!;
        }

        // The attempt's guard has already refused a report without one kind, so the registry is only ever asked about a
        // kind the manifest really declared. It is asked before an artifact verdict is turned into an outcome, because
        // the refusal row's key is the parser pair and only an `unsupported` row may carry none.
        var resolution = await parsers.ResolveAsync(payload.ProducerKind!, cancellationToken).ConfigureAwait(false);

        if (stopped?.Stopped is { } verdict)
        {
            // The report's own `artifact_parse` judged the bytes unparseable and left the artifact rejected: the report
            // task records that verdict as its `malformed` row without reading a byte, because the parse that would
            // have judged it already ran.
            return ParseVerdictRefusal(payload, verdict, resolution);
        }

        if (!resolution.IsResolved)
        {
            // A kind no registry row names has no pair, so the refusal row's key is the kind the report declared.
            return Unsupported(payload, resolution);
        }

        if (MapperFor(resolution) is not { } mapper)
        {
            return ParserUnavailable(payload);
        }

        return new Preparation(
            await ResolveFetchAsync(context, orgId, artifact, payload, resolution, mapper, cancellationToken).ConfigureAwait(false),
            null);
    }

    /// <summary>
    /// The outcome an artifact verdict the read scope already reached ends the attempt with: the report's own parse
    /// judged it, so its code is this report's refusal and the row carries the parser pair the registry names. A kind
    /// no registry row names cannot carry a pair, so the verdict becomes the <c>unsupported</c> row it would have been.
    /// </summary>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="verdict">The <c>malformed</c> outcome the artifact's own parse left behind.</param>
    /// <param name="resolution">What the registry answered for the declared kind.</param>
    /// <returns>The preparation that ends the attempt.</returns>
    private Preparation ParseVerdictRefusal(
        ParseRawReportPayload payload,
        ParseRawReportOutcome verdict,
        ReportParserResolution resolution) =>
        resolution.IsResolved
            ? new Preparation(null, verdict, resolution.ParserId, resolution.Version)
            : Unsupported(payload, resolution);

    /// <summary>
    /// The refusal a kind the registry does not name ends the attempt with: no pair exists, so the row's key is the
    /// kind the report declared and a Worker that later loads the parser can still store its own row for the release.
    /// </summary>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="resolution">What the registry answered for the declared kind.</param>
    /// <returns>The preparation that ends the attempt.</returns>
    private Preparation Unsupported(ParseRawReportPayload payload, ReportParserResolution resolution)
    {
        LogUnsupported(logger, payload.ArtifactId, resolution.UnsupportedReason!);
        return new Preparation(null, new ParseRawReportOutcome(ParseRawReportOutcome.Unsupported, resolution.UnsupportedReason));
    }

    /// <summary>
    /// The loaded mapper whose pair the registry named: null is a deployment mismatch, which the caller reports like
    /// an unknown kind so a report of an unloaded parser is never parsed by a different one.
    /// </summary>
    /// <param name="resolution">What the registry answered for the declared kind.</param>
    /// <returns>The mapper that pair names, or null when this deployment loaded none.</returns>
    private IJUnitMapper? MapperFor(ReportParserResolution resolution) =>
        mappers.FirstOrDefault(candidate =>
            string.Equals(candidate.ParserId, resolution.ParserId, StringComparison.Ordinal)
            && string.Equals(candidate.Version, resolution.Version, StringComparison.Ordinal));

    /// <summary>
    /// The refusal a registry row without its loaded mapper ends the attempt with: the row carries no parser pair, so
    /// a Worker that does load the mapper later can still store its own row for the same release.
    /// </summary>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <returns>The preparation that ends the attempt.</returns>
    private Preparation ParserUnavailable(ParseRawReportPayload payload)
    {
        LogUnsupported(logger, payload.ArtifactId, "parser_unavailable");
        return new Preparation(
            null,
            new ParseRawReportOutcome(ParseRawReportOutcome.Unsupported, "parser_unavailable"),
            null,
            null);
    }

    /// <summary>
    /// The inputs the fetch needs: the byte cap and the run's planned executions. The kind policy caps one kind and the
    /// profile's hard ceiling caps every report, so the read is bounded by the tighter of the two and a widened kind
    /// policy cannot raise the platform limit.
    /// </summary>
    /// <param name="context">The tenant write model the cap is resolved through.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="artifact">The artifact the read scope resolved.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="resolution">The parser pair the registry named, which the fetch records.</param>
    /// <param name="mapper">The loaded mapper that pair names.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The bytes to fetch and the plan the mapping is built against.</returns>
    private async Task<Prepared> ResolveFetchAsync(
        EvidenceDbContext context,
        Guid orgId,
        Artifact artifact,
        ParseRawReportPayload payload,
        ReportParserResolution resolution,
        IJUnitMapper mapper,
        CancellationToken cancellationToken)
    {
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

        return new Prepared(
            artifact.ObjectKey,
            Math.Min(maxBytes, ParsingLimits.MaxReportBytes),
            resolution.ParserId!,
            resolution.Version!,
            mapper,
            PlannedExecutionIndex.Build(hints));
    }

    /// <summary>
    /// The artifact checks of the read scope: this tenant's, of the raw-report kind and this run's. A row whose object
    /// key is not the key it mints is corrupt, so the attempt throws rather than reading an object it cannot vouch for.
    /// A <c>ready</c> row continues the attempt; a row its own <c>artifact_parse</c> verdict rejected (see
    /// <see cref="IsParseRejected"/>) stops it with the <c>malformed</c> outcome of that verdict and is returned as
    /// well, so the caller can resolve the parser pair the refusal row carries; every other state is a skip.
    /// </summary>
    /// <param name="context">The tenant write model the artifact is read through.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The artifact when the attempt continues or stops on its verdict, or the skipped outcome that ended it.</returns>
    private async Task<(Artifact? Artifact, Preparation? Stopped)> ReadArtifactAsync(
        EvidenceDbContext context,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        var artifact = await context.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == payload.ArtifactId, cancellationToken)
            .ConfigureAwait(false);

        if (artifact is null)
        {
            LogSkipped(logger, payload.ArtifactId, "absent");
            return (null, new Preparation(null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "absent")));
        }

        if (!string.Equals(artifact.Kind, ArtifactKind.RawReport, StringComparison.Ordinal))
        {
            LogSkipped(logger, payload.ArtifactId, "not_a_ready_raw_report");
            return (null, new Preparation(null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "not_a_ready_raw_report")));
        }

        if (artifact.RunId != payload.RunId)
        {
            // The payload names a run the artifact does not belong to: no event may be attributed to that run.
            LogSkipped(logger, payload.ArtifactId, "foreign_run");
            return (null, new Preparation(null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "foreign_run")));
        }

        if (IsParseRejected(artifact))
        {
            // The bytes were already judged: `artifact_parse` read this report first (the completion schedules both in
            // one transaction) and rejected it, so the report task records the same verdict as its own refusal instead
            // of stopping at a state that is no longer `ready` and leaving no report row at all.
            LogRefused(logger, payload.ArtifactId, artifact.FailureCode!);
            return (artifact, new Preparation(null, new ParseRawReportOutcome(ParseRawReportOutcome.Malformed, artifact.FailureCode)));
        }

        if (!string.Equals(artifact.State, ArtifactState.Ready, StringComparison.Ordinal))
        {
            LogSkipped(logger, payload.ArtifactId, "not_a_ready_raw_report");
            return (null, new Preparation(null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "not_a_ready_raw_report")));
        }

        if (!ArtifactObjectKey.IsMintedFor(artifact))
        {
            throw new InvalidOperationException(ArtifactObjectKey.MismatchMessage);
        }

        return (artifact, null);
    }

    /// <summary>
    /// The codes <c>artifact_parse</c> writes when it refuses bytes: its own verdicts and the profile words a child's
    /// rejection record carries. The column also holds the completion's upload refusals, so this set, not the state
    /// alone, tells a judged report from one that never became evidence — except <c>too_large</c>, which both write.
    /// </summary>
    private static readonly HashSet<string> ParseVerdictCodes = new(StringComparer.Ordinal)
    {
        ArtifactFailureCode.TooLarge,
        ArtifactFailureCode.CapExceeded,
        ArtifactFailureCode.ParseOom,
        ArtifactFailureCode.ParseTimeout,
        ArtifactFailureCode.ParseCrashed,
        ParsingFailureReason.DtdProhibited,
        ParsingFailureReason.DepthExceeded,
        ParsingFailureReason.SizeExceeded,
        ParsingFailureReason.Timeout,
        ParsingFailureReason.Malformed,
        ParsingFailureReason.UnsafeEntryName,
        ParsingFailureReason.DuplicateEntry,
        ParsingFailureReason.EntryCountExceeded,
        ParsingFailureReason.CompressionRatioExceeded,
        ParsingFailureReason.NestingExceeded,
    };

    /// <summary>
    /// True when the artifact is a report its own parse already judged unparseable — the state and code
    /// <c>artifact_parse</c> leaves behind. <c>too_large</c> is the one code both writers produce, and the row holds no
    /// value that separates the two writers: <c>bytes</c> is the mint's declared length and is overwritten only by the
    /// ready transition, <c>scan_state</c> starts at its <c>pending</c> default for every row, and <c>version</c> is the
    /// concurrency trigger's counter, not a statement about which transition ran. Such a report is therefore read here
    /// as judged. That is unreachable through the pipeline rather than merely unlikely: a rejected completion schedules
    /// no parse row (the row is written by the ready path only), so no report task ever reads a completion refusal.
    /// Every other code in the set is the parse's alone.
    /// </summary>
    /// <param name="artifact">The artifact row this attempt read.</param>
    /// <returns>True when the rejection came from the parse of the report's own bytes.</returns>
    private static bool IsParseRejected(Artifact artifact) =>
        string.Equals(artifact.State, ArtifactState.Rejected, StringComparison.Ordinal)
        && artifact.FailureCode is { } code
        && ParseVerdictCodes.Contains(code);
}
