using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
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
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using FluentValidation;
using Mediator;

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
/// <param name="audit">The audit trail the accepted request is recorded on, in the same transaction.</param>
/// <param name="principals">The caller, for the project its credential is bound to.</param>
/// <param name="tenant">The request's tenant context, for the project the request is narrowed to.</param>
/// <remarks>
/// The report's project is checked like the comparison read's: row level security hides another tenant's report, the
/// run predicate hides another run's, and the project scope hides a sibling project's report of the same org — all
/// three are the one 404, so a caller who may not see the report cannot learn it exists by asking to reprocess it.
/// </remarks>
[AuditedAction("raw_report.reprocess")]
[Permission(Permissions.Run.Create)]
internal sealed class ReprocessRawReportHandler(
    ITenantDb db,
    IOutboxWriter outbox,
    IAuditTrail audit,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : ICommandHandler<ReprocessRawReport, Result<ReprocessResponse>>
{
    /// <summary>The outbox aggregate the task row belongs to.</summary>
    private const string AggregateType = "raw_report";

    /// <summary>The audit object type of the request row this command writes.</summary>
    private const string AuditObjectType = "raw_report_reprocess";

    /// <inheritdoc />
    public async ValueTask<Result<ReprocessResponse>> Handle(
        ReprocessRawReport command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Evidence, nameof(ReprocessRawReport));

        var context = tenant.Require();
        var source = await LoadSourceAsync(command, cancellationToken).ConfigureAwait(false);

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

        var requested = new ReprocessTarget(source.OrgId, source.ArtifactId, parserId, command.ParserVersion);

        if (!ParserVersionOrder.IsNewer(command.ParserVersion, sourceVersion)
            || !await IsParserVisibleAsync(requested, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<ReprocessResponse>(EvidenceErrors.ParserVersionUnknown);
        }

        return await RecordOrAnswerAsync(command, source, requested, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The report the route names, with the project its artifact belongs to; null when no row matches.</summary>
    private Task<SourceReport?> LoadSourceAsync(ReprocessRawReport command, CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<SourceReport>(
            ReprocessRawReportSql.Source,
            new { raw_report_id = command.RawReportId, run_id = command.RunId },
            cancellationToken);

    /// <summary>Whether the org may parse with the requested release; a foreign or unknown release is not visible.</summary>
    private Task<bool> IsParserVisibleAsync(ReprocessTarget target, CancellationToken cancellationToken) =>
        db.ScalarAsync<bool>(ReprocessRawReportSql.ParserVisible, Parameters(target), cancellationToken);

    /// <summary>
    /// The two natural keys first, then the insert: an existing report row or request row is the answer itself, and
    /// only a genuinely new pair inserts and enqueues. A lost insert race answers the winner's row.
    /// </summary>
    private async Task<Result<ReprocessResponse>> RecordOrAnswerAsync(
        ReprocessRawReport command,
        SourceReport source,
        ReprocessTarget target,
        CancellationToken cancellationToken)
    {
        var parameters = Parameters(target);

        if (await db.ScalarAsync<Guid?>(ReprocessRawReportSql.StoredReport, parameters, cancellationToken)
                .ConfigureAwait(false) is { } storedId)
        {
            return Answered(storedId, changed: false);
        }

        if (await db.ScalarAsync<Guid?>(ReprocessRawReportSql.ExistingRequest, parameters, cancellationToken)
                .ConfigureAwait(false) is { } existingId)
        {
            return Answered(existingId, changed: false);
        }

        // The id is minted here, not by the task: the caller is answered with it, and the task writes the report
        // row under it, so the id a client holds names the evidence the reprocess will produce.
        var parsingId = Guid.CreateVersion7();

        if (await InsertRequestAsync(command, source, target, parsingId, cancellationToken).ConfigureAwait(false)
            is null)
        {
            // A concurrent identical request won the insert and enqueued the task; this one answers it.
            return await AnswerWinnerAsync(parameters, cancellationToken).ConfigureAwait(false);
        }

        await EnqueueAsync(command, parsingId, cancellationToken).ConfigureAwait(false);

        return Answered(parsingId, changed: true);
    }

    /// <summary>Inserts the request row under the natural key; null when a concurrent request won the key.</summary>
    private Task<Guid?> InsertRequestAsync(
        ReprocessRawReport command,
        SourceReport source,
        ReprocessTarget target,
        Guid parsingId,
        CancellationToken cancellationToken) =>
        db.ScalarAsync<Guid?>(
            ReprocessRawReportSql.Insert,
            new
            {
                id = parsingId,
                org_id = source.OrgId,
                run_id = source.RunId,
                artifact_id = source.ArtifactId,
                source_raw_report_id = command.RawReportId,
                parser_id = target.ParserId,
                parser_version = target.ParserVersion,
            },
            cancellationToken);

    /// <summary>Reads the row the concurrent winner inserted, or refuses when even that read finds nothing.</summary>
    private async Task<Result<ReprocessResponse>> AnswerWinnerAsync(
        object parameters,
        CancellationToken cancellationToken)
    {
        var winner = await db
            .ScalarAsync<Guid?>(ReprocessRawReportSql.ExistingRequest, parameters, cancellationToken)
            .ConfigureAwait(false);

        return winner is { } winnerId
            ? Answered(winnerId, changed: false)
            : Result.Failure<ReprocessResponse>(EvidenceErrors.ParserVersionUnknown);
    }

    /// <summary>The one task of an accepted pair, written in this transaction under the natural key.</summary>
    private Task<Guid> EnqueueAsync(ReprocessRawReport command, Guid parsingId, CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(
            ReprocessReportPayload.TaskType,
            JsonSerializer.Serialize(new { parsing_id = parsingId }),
            AggregateType,
            parsingId,
            $"reprocess:{command.RawReportId:D}:{command.ParserVersion}",
            cancellationToken);

    /// <summary>
    /// The 202 answer: the id the report row will be written under, whether it exists yet or not. An answer that
    /// wrote nothing describes no change to the trail, so the audit entry of the command is dropped instead.
    /// </summary>
    private Result<ReprocessResponse> Answered(Guid parsingId, bool changed)
    {
        if (changed)
        {
            audit.Describe(new AuditObject(AuditObjectType, parsingId.ToString("D")));
        }
        else
        {
            audit.Suppress();
        }

        return Result.Success(new ReprocessResponse(parsingId), 202);
    }

    /// <summary>The statement parameters of one (report artifact, release) pair.</summary>
    private static object Parameters(ReprocessTarget target) => new
    {
        org_id = target.OrgId,
        artifact_id = target.ArtifactId,
        parser_id = target.ParserId,
        parser_version = target.ParserVersion,
    };

    /// <summary>The pair this request names: the stored report's artifact and the release asked to re-read it.</summary>
    /// <param name="OrgId">The row's tenant, written onto the request row.</param>
    /// <param name="ArtifactId">The report artifact the release is asked to parse again.</param>
    /// <param name="ParserId">The release family that judged the report.</param>
    /// <param name="ParserVersion">The release the caller asked for.</param>
    private sealed record ReprocessTarget(
        Guid OrgId,
        Guid ArtifactId,
        string ParserId,
        string ParserVersion);

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
