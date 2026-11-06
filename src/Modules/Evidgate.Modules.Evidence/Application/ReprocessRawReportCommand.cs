using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Endpoints;
using FluentValidation;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// Reprocess one stored report with a newer parser release (<c>POST /v1/runs/{r}/reprocess</c>): it records the
/// request and hands the parse to the background, answering <c>202</c> with the id the eventual <c>raw_report</c> row
/// will carry. Nothing is parsed in this transaction and no run state is read or written: a canceled, complete or
/// incomplete run accepts a reprocess alike, because the request names bytes the platform already stores.
/// <para>
/// <see cref="IdempotencyKey"/> and <see cref="CredentialId"/> are members so the C2 idempotency hash binds the
/// request to the caller: the same key with the same body replays the first answer, and another principal replaying
/// it is a conflict rather than the first caller's response.
/// </para>
/// </summary>
/// <param name="RunId">The run the route names; the report must belong to it.</param>
/// <param name="RawReportId">The stored report to reprocess.</param>
/// <param name="ParserVersion">The catalogue release to parse it with, strictly newer than the report's own.</param>
/// <param name="CredentialId">The caller's token id (<c>jti</c>), or <c>null</c> when it has none.</param>
/// <param name="IdempotencyKey">The required <c>Idempotency-Key</c> header.</param>
internal sealed record ReprocessRawReport(
    Guid RunId,
    Guid RawReportId,
    string ParserVersion,
    string? CredentialId,
    string IdempotencyKey) : ICommand<Result<ReprocessResponse>>, IIdempotentCommand;

/// <summary>
/// Shape validation of <see cref="ReprocessRawReport"/> (C2 stage 2, before the tenant transaction). Which release is
/// acceptable depends on the stored row — that is the handler's read, inside the transaction — so this stage owns the
/// wire shape only: the ids must be real UUIDs the route carries, and the version must be a non-empty token the
/// catalogue's own column bound admits.
/// </summary>
internal sealed class ReprocessRawReportValidator : AbstractValidator<ReprocessRawReport>
{
    /// <summary>The catalogue's bound on <c>report_parser.version</c> (migration 0075).</summary>
    private const int MaxVersionLength = 64;

    /// <summary>Builds the shape rules of the route.</summary>
    public ReprocessRawReportValidator()
    {
        RuleFor(command => command.RunId).NotEmpty();
        RuleFor(command => command.RawReportId).NotEmpty();
        RuleFor(command => command.ParserVersion)
            .NotEmpty()
            .MaximumLength(MaxVersionLength);
    }
}

/// <summary>
/// Handler of <see cref="ReprocessRawReport"/>: everything that decides the answer is read and written inside the C2
/// tenant transaction, so a request cannot be admitted against a report that a concurrent commit removed from view.
/// <para>
/// The source row must belong to the routed run; RLS hides another tenant's row and the run predicate hides another
/// run's, so an unknown id, a foreign id and a report of a sibling run are the one 404 and never an oracle. A report
/// no parser could read (<c>unsupported</c>) is refused before the version is looked at: there is no release to be
/// newer than. The requested version must then be a release of the report's own parser that the caller's org can see
/// and that is strictly newer — an unknown, equal, older or unorderable version is one 422, so a client cannot probe
/// which releases other orgs registered.
/// </para>
/// <para>
/// Dedupe happens twice, in the order the two tables would answer: a report already parsed with this release is the
/// answer itself (its id, and no task — the work exists), and a request already recorded for it answers the stored
/// request id. Only a genuinely new pair inserts, and the outbox row is written in the same commit under the natural
/// key, so a concurrent identical request loses the insert and enqueues nothing: exactly one task per
/// (report, release).
/// </para>
/// </summary>
/// <param name="db">The Dapper path every statement of this command runs on, inside the ambient tenant scope.</param>
/// <param name="outbox">The outbox the parse task is handed to, on this same connection and transaction.</param>
/// <param name="principals">The caller, for the project its credential is bound to.</param>
/// <param name="tenant">The request's tenant context, for the project the request is narrowed to.</param>
/// <remarks>
/// The report's project is checked like the comparison read's: row level security hides another tenant's report, the
/// run predicate hides another run's, and the project scope hides a sibling project's report of the same org — all
/// three are the one 404, so a caller who may not see the report cannot learn it exists by asking to reprocess it.
/// </remarks>
[Permission(Permissions.Run.Create)]
internal sealed class ReprocessRawReportHandler(
    ITenantDb db,
    IOutboxWriter outbox,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : ICommandHandler<ReprocessRawReport, Result<ReprocessResponse>>
{
    /// <summary>The outbox aggregate the task row belongs to (00 §5.3).</summary>
    private const string AggregateType = "raw_report";

    /// <summary>
    /// The report the caller named, restricted to the routed run: <c>org_id</c> comes back with it because the request
    /// row is written with the same value, and every later statement re-states it so no read depends on RLS alone.
    /// </summary>
    private const string SourceSql = """
        select r.org_id, r.run_id, r.artifact_id, r.parser_id, r.parser_version, r.outcome, a.project_id
          from public.raw_report r
          join public.artifact a
            on a.org_id = r.org_id
           and a.id = r.artifact_id
           and a.run_id = r.run_id
         where r.id = @raw_report_id
           and r.run_id = @run_id
        """;

    /// <summary>
    /// Whether the requested release is one this org may parse with: a built-in release carries a null org, a
    /// project-owned one only exists for its own org.
    /// </summary>
    private const string ParserVisibleSql = """
        select exists (
            select 1
              from public.report_parser
             where parser_id = @parser_id
               and version = @parser_version
               and (org_id is null or org_id = @org_id))
        """;

    /// <summary>The report already parsed with this release: the id it was written under is the answer.</summary>
    private const string StoredReportSql = """
        select id
          from public.raw_report
         where org_id = @org_id
           and artifact_id = @artifact_id
           and parser_id = @parser_id
           and parser_version = @parser_version
        """;

    /// <summary>The request already recorded for this pair: its id is the answer, and its task is already queued.</summary>
    private const string ExistingRequestSql = """
        select id
          from public.raw_report_reprocess
         where org_id = @org_id
           and artifact_id = @artifact_id
           and parser_id = @parser_id
           and parser_version = @parser_version
        """;

    /// <summary>
    /// Records the request. The conflict target is the natural key, so a concurrent identical request inserts nothing
    /// and reads nothing back: it answers the winner's id below and enqueues no second task.
    /// </summary>
    private const string InsertSql = """
        insert into public.raw_report_reprocess
            (id, org_id, run_id, artifact_id, source_raw_report_id, parser_id, parser_version)
        values
            (@id, @org_id, @run_id, @artifact_id, @source_raw_report_id, @parser_id, @parser_version)
        on conflict (org_id, artifact_id, parser_id, parser_version) do nothing
        returning id
        """;

    /// <inheritdoc />
    public async ValueTask<Result<ReprocessResponse>> Handle(
        ReprocessRawReport command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Evidence, nameof(ReprocessRawReport));

        var context = tenant.Require();

        var source = await db
            .QuerySingleOrDefaultAsync<SourceReport>(
                SourceSql,
                new { raw_report_id = command.RawReportId, run_id = command.RunId },
                cancellationToken)
            .ConfigureAwait(false);

        if (source is null
            || EvidenceProjectScope.Project(source.ProjectId, principals.Current?.ProjectScope, context.ProjectId) is not null)
        {
            return Result.Failure<ReprocessResponse>(EvidenceErrors.ReportNotFound);
        }

        if (source.ParserId is not { } parserId
            || source.ParserVersion is not { } sourceVersion
            || string.Equals(source.Outcome, RawReportOutcomes.Unsupported, StringComparison.Ordinal))
        {
            return Result.Failure<ReprocessResponse>(EvidenceErrors.ReportUnprocessable);
        }

        if (!ParserVersionOrder.IsNewer(command.ParserVersion, sourceVersion))
        {
            return Result.Failure<ReprocessResponse>(EvidenceErrors.ParserVersionUnknown);
        }

        var requested = new
        {
            org_id = source.OrgId,
            artifact_id = source.ArtifactId,
            parser_id = parserId,
            parser_version = command.ParserVersion,
        };

        var parserVisible = await db
            .ScalarAsync<bool>(ParserVisibleSql, requested, cancellationToken)
            .ConfigureAwait(false);

        if (!parserVisible)
        {
            return Result.Failure<ReprocessResponse>(EvidenceErrors.ParserVersionUnknown);
        }

        var stored = await db
            .ScalarAsync<Guid?>(StoredReportSql, requested, cancellationToken)
            .ConfigureAwait(false);

        if (stored is { } storedId)
        {
            return Accepted(storedId);
        }

        var existing = await db
            .ScalarAsync<Guid?>(ExistingRequestSql, requested, cancellationToken)
            .ConfigureAwait(false);

        if (existing is { } existingId)
        {
            return Accepted(existingId);
        }

        // The id is minted here, not by the task: the caller is answered with it, and the task writes the report
        // row under it, so the id a client holds names the evidence the reprocess will produce.
        var parsingId = Guid.CreateVersion7();

        var inserted = await db
            .ScalarAsync<Guid?>(
                InsertSql,
                new
                {
                    id = parsingId,
                    org_id = source.OrgId,
                    run_id = source.RunId,
                    artifact_id = source.ArtifactId,
                    source_raw_report_id = command.RawReportId,
                    parser_id = parserId,
                    parser_version = command.ParserVersion,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (inserted is null)
        {
            // A concurrent identical request won the insert and enqueued the task; this one answers it.
            var winner = await db
                .ScalarAsync<Guid?>(ExistingRequestSql, requested, cancellationToken)
                .ConfigureAwait(false);

            return winner is { } winnerId
                ? Accepted(winnerId)
                : Result.Failure<ReprocessResponse>(EvidenceErrors.ParserVersionUnknown);
        }

        await outbox.EnqueueAsync(
            ReprocessReportPayload.TaskType,
            JsonSerializer.Serialize(new { parsing_id = parsingId }),
            AggregateType,
            parsingId,
            $"reprocess:{command.RawReportId:D}:{command.ParserVersion}",
            cancellationToken).ConfigureAwait(false);

        return Accepted(parsingId);
    }

    /// <summary>The 202 answer: the id the report row will be written under, whether it exists yet or not.</summary>
    private static Result<ReprocessResponse> Accepted(Guid parsingId) =>
        Result.Success(new ReprocessResponse(parsingId), StatusCodes.Status202Accepted);

    /// <summary>One stored report as the decision reads it: where it lives and which release judged it.</summary>
    /// <param name="OrgId">The row's tenant, written onto the request row.</param>
    /// <param name="RunId">The run the row belongs to, which the route already matched.</param>
    /// <param name="ArtifactId">The report artifact the release is asked to parse again.</param>
    /// <param name="ParserId">The release family that judged the report, or null when none could.</param>
    /// <param name="ParserVersion">The release that judged the report, or null when none could.</param>
    /// <param name="Outcome">One of <see cref="RawReportOutcomes"/>.</param>
    private sealed record SourceReport(
        Guid OrgId,
        Guid RunId,
        Guid ArtifactId,
        string? ParserId,
        string? ParserVersion,
        string Outcome,
        Guid ProjectId);
}
