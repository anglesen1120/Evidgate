using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The read half of one reprocess attempt: the request row, the artifact it names, the parser release it pinned and the
/// run's plan, resolved in one short read-only scope that closes before a single byte is fetched.
/// </summary>
internal sealed partial class ReprocessReportHandler
{
    /// <summary>Why an attempt found nothing to do: the request or its artifact is not there, or the artifact stopped being ready.</summary>
    private const string RequestAbsent = "request_absent";

    /// <summary>The artifact the request names no longer exists in this tenant.</summary>
    private const string ArtifactAbsent = "artifact_absent";

    /// <summary>The artifact exists but is no longer the ready raw report of this run.</summary>
    private const string ArtifactLeftReady = "artifact_left_ready";

    /// <summary>
    /// The request row joined to its source report: the parser pair and the run come from the request, the producer kind
    /// from the report, and nothing else about either is trusted.
    /// </summary>
    private const string RequestSql = """
        select r.run_id as "RunId", r.artifact_id as "ArtifactId", r.source_raw_report_id as "SourceRawReportId",
               r.parser_id as "ParserId", r.parser_version as "ParserVersion", s.producer_kind as "ProducerKind"
          from public.raw_report_reprocess r
          join public.raw_report s on s.org_id = r.org_id and s.id = r.source_raw_report_id
         where r.org_id = @org_id
           and r.id = @parsing_id
        """;

    /// <summary>
    /// Whether the pinned release is in the registry this tenant can see (the built-in releases are org-null). The
    /// request was written from a verdict on this row, so a miss here is a deployment fault, not a tenant's bad input.
    /// </summary>
    private const string RegistrySql = """
        select 1
          from public.report_parser
         where parser_id = @parser_id
           and version = @parser_version
        """;

    /// <summary>Everything an attempt needs, resolved before the object is fetched.</summary>
    /// <param name="ParsingId">The request row, and the id of the report row a mapped attempt writes.</param>
    /// <param name="RunId">The run whose plan and admission decide what may be stored.</param>
    /// <param name="ArtifactId">The artifact whose bytes are re-read; also the mapper's report identity.</param>
    /// <param name="ProducerKind">The dialect the source report's producer declared; the parser row's lookup key.</param>
    /// <param name="ObjectKey">The verified object the bytes are fetched from.</param>
    /// <param name="MaxBytes">The fetch and child cap: the kind policy and the profile's ceiling, whichever is smaller.</param>
    /// <param name="ParserId">The release the request pinned; never the newest version of the dialect.</param>
    /// <param name="ParserVersion">The pinned release version; stamped on every row and event this attempt writes.</param>
    /// <param name="Mapper">The loaded mapper for exactly that pair.</param>
    /// <param name="Plan">The run's planned executions, as the hint lookup the mapper resolves through.</param>
    /// <param name="ProducerId">The synthetic producer identity this request's events carry.</param>
    private sealed record Prepared(
        Guid ParsingId,
        Guid RunId,
        Guid ArtifactId,
        string ProducerKind,
        string ObjectKey,
        long MaxBytes,
        string ParserId,
        string ParserVersion,
        IJUnitMapper Mapper,
        PlannedExecutionIndex Plan,
        string ProducerId);

    /// <summary>The request's own view of one reprocess: the request joined to the producer kind of its source report.</summary>
    /// <param name="RunId">The run the request belongs to.</param>
    /// <param name="ArtifactId">The artifact whose bytes are re-read.</param>
    /// <param name="SourceRawReportId">The report the request re-reads; kept for the row the mapper stamps.</param>
    /// <param name="ParserId">The pinned dialect.</param>
    /// <param name="ParserVersion">The pinned release.</param>
    /// <param name="ProducerKind">The source report's producer kind.</param>
    private sealed record RequestRow(
        Guid RunId,
        Guid ArtifactId,
        Guid SourceRawReportId,
        string ParserId,
        string ParserVersion,
        string ProducerKind);

    /// <summary>Either the resolved inputs, or the terminal outcome of an attempt that had nothing to parse.</summary>
    /// <param name="Prepared">The resolved inputs, or <see langword="null"/> when the attempt stops here.</param>
    /// <param name="Stopped">The outcome that ends the attempt, or <see langword="null"/> when it may go on.</param>
    private sealed record Preparation(Prepared? Prepared, ParseRawReportOutcome? Stopped);

    /// <summary>
    /// Resolves the request, the artifact, the pinned release and the plan inside the read scope, then closes it. Every
    /// check that can be answered by a row is answered here, so the transaction later carries parameters only.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The reprocess request.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The resolved inputs, or the terminal outcome of an attempt that had nothing to parse.</returns>
    private async Task<Preparation> PrepareAsync(
        Guid orgId,
        ReprocessReportPayload payload,
        CancellationToken cancellationToken)
    {
        await using var scope = await scopes.BeginAsync(orgId, projectId: null, readOnlyTransaction: true, cancellationToken)
            .ConfigureAwait(false);

        var request = await db.QuerySingleOrDefaultAsync<RequestRow>(
            RequestSql,
            new { org_id = orgId, parsing_id = payload.ParsingId },
            cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            // The command committed the request before it enqueued, so an absent row is a claim on something that was
            // never written: there is nothing to retry toward.
            return new Preparation(null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, RequestAbsent));
        }

        await using var context = contexts.Create();
        var artifact = await context.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == request.ArtifactId, cancellationToken)
            .ConfigureAwait(false);
        if (artifact is not { } ready)
        {
            return new Preparation(null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, ArtifactAbsent));
        }

        if (ready.State is not ArtifactState.Ready
            || ready.Kind is not ArtifactKind.RawReport
            || ready.RunId != request.RunId)
        {
            // The bytes were retracted or replaced between the command and this attempt: nothing may be derived from an
            // artifact that no longer is the run's ready raw report.
            return new Preparation(null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, ArtifactLeftReady));
        }

        if (!ArtifactObjectKey.IsMintedFor(ready))
        {
            throw new InvalidOperationException($"artifact {ready.Id} has an object key that was not minted for it");
        }

        var registered = await db.ScalarAsync<int?>(
            RegistrySql,
            new { parser_id = request.ParserId, parser_version = request.ParserVersion },
            cancellationToken).ConfigureAwait(false);
        if (registered is null)
        {
            throw new InvalidOperationException(
                "the pinned parser release is not visible in this tenant's registry, so the report was not parsed; the task is retried");
        }

        var mapper = mappers.FirstOrDefault(candidate =>
            string.Equals(candidate.ParserId, request.ParserId, StringComparison.Ordinal)
            && string.Equals(candidate.Version, request.ParserVersion, StringComparison.Ordinal));
        if (mapper is null)
        {
            // The verdict named a release this deployment does not ship. Retrying cannot load it, so the attempt
            // dead-letters rather than reporting a mapped report that was never mapped.
            throw new InvalidOperationException(
                "no JUnit mapper is loaded for the pinned parser release, so the report was not parsed; the task is retried");
        }

        var planned = await db.QueryAsync<PlannedExecutionHint>(
            PlannedExecutionIndex.SelectSql,
            new { org_id = orgId, run_id = request.RunId },
            cancellationToken).ConfigureAwait(false);

        var cap = await ArtifactCapturePolicy.ResolveAsync(context, ready.ProjectId, ready.Kind, cancellationToken)
            .ConfigureAwait(false);

        return new Preparation(
            new Prepared(
                payload.ParsingId,
                request.RunId,
                request.ArtifactId,
                request.ProducerKind,
                ready.ObjectKey,
                Math.Min(cap.MaxBytes, ParsingLimits.MaxReportBytes),
                request.ParserId,
                request.ParserVersion,
                mapper,
                PlannedExecutionIndex.Build(planned),
                ReprocessReportProducer.IdFor(payload.ParsingId)),
            null);
    }
}
