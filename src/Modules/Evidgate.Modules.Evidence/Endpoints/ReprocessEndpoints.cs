using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Application;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Evidence.Endpoints;

/// <summary>
/// The reprocess route of the run's ingestion path, kept in its own file so the artifact endpoints stay inside the
/// module's file-size bound. It reads the snake_case body, applies the C2 idempotency rule before the handler's read
/// and dispatches <see cref="ReprocessRawReport"/>; the tenant transaction, the refusal codes and the single task are
/// the command's.
/// </summary>
internal static class ReprocessEndpoints
{
    /// <summary>
    /// <c>POST /v1/runs/{r}/reprocess</c>: records one reprocess request and answers <c>202</c> with the id the report
    /// row will be written under. The body is the same snake_case shape the other write routes bind, and the
    /// <c>Idempotency-Key</c> rule is applied here first so a request the C2 stage would refuse never reaches the
    /// handler's read.
    /// </summary>
    /// <param name="context">The request, for the <c>Idempotency-Key</c> header and the body.</param>
    /// <param name="r">The run the route names; a report of another run is the handler's 404.</param>
    /// <param name="principals">The caller's token identity, carried into the idempotency hash.</param>
    /// <param name="mediator">Dispatches the writer command.</param>
    /// <param name="cancellationToken">Cancels the read and the command.</param>
    internal static async Task<Result<ReprocessResponse>> ReprocessAsync(
        HttpContext context,
        Guid r,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (principals.Current is not { } principal)
        {
            return Result.Failure<ReprocessResponse>(EvidenceEndpoints.Unauthenticated);
        }

        if (EvidenceEndpoints.IdempotencyKeyError(context) is { } keyError)
        {
            return Result.Failure<ReprocessResponse>(keyError);
        }

        ReprocessRequest? request;
        try
        {
            request = await context.Request
                .ReadFromJsonAsync<ReprocessRequest>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return Result.Failure<ReprocessResponse>(Error.BadRequest("the request body is not valid JSON"));
        }

        if (request is null)
        {
            return Result.Failure<ReprocessResponse>(Error.BadRequest("the request body is required"));
        }

        return await mediator.Send(
            new ReprocessRawReport(
                r,
                request.RawReportId,
                request.ParserVersion,
                principal.CredentialId,
                EvidenceEndpoints.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Body of <c>POST /v1/runs/{r}/reprocess</c>: the stored report to parse again and the release to parse it with.
/// The run is the route's, never a body member, so a request cannot name a report of one run and claim another;
/// <c>Idempotency-Key</c> is a header, never a body member.
/// </summary>
/// <param name="RawReportId">The stored report to reprocess.</param>
/// <param name="ParserVersion">The catalogue release to parse it with.</param>
public sealed record ReprocessRequest(
    [property: JsonPropertyName("raw_report_id")] Guid RawReportId,
    [property: JsonPropertyName("parser_version")] string ParserVersion);

/// <summary>
/// 202 body of <c>POST /v1/runs/{r}/reprocess</c>: the id the new <c>raw_report</c> row will carry. It exists before
/// the parse runs, so a client can poll the report by it, and it is the same id a replay of the request returns.
/// </summary>
/// <param name="ParsingId">The id of the report row the accepted reprocess will write.</param>
public sealed record ReprocessResponse([property: JsonPropertyName("parsing_id")] Guid ParsingId);
