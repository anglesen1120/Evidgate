using System.Globalization;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Application.Commands;
using Evidgate.Modules.Identity.Application.Queries;
using Evidgate.Modules.Identity.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// The audit read routes of 02.7.1-T3.1: <c>GET /v1/orgs/{o}/audit</c> (cursor page) and
/// <c>GET /v1/orgs/{o}/audit/export</c> (JSONL or CSV). Delegates bind the filters and answer in
/// <see cref="Result"/> terms; the export's body is a streamed <see cref="IResult"/> carried in the
/// success value, which the host's result filter returns as is.
/// </summary>
public static class AuditEndpoints
{
    private const string Jsonl = "application/x-ndjson";
    private const string Csv = "text/csv";

    private static readonly string[] CsvColumns =
    [
        "seq", "created_at", "actor_type", "actor_id", "action", "object_type", "object_id", "request_id", "trace_id", "entry_hash",
    ];

    /// <summary>Maps the audit routes and returns the group so the host applies its filters.</summary>
    /// <param name="routes">The route builder.</param>
    public static RouteGroupBuilder MapAuditEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("audit");

        group.MapGet("/orgs/{o}/audit", ListAuditAsync)
            .Produces<AuditListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of the org's audit events, newest first (02.7.1)");

        group.MapGet("/orgs/{o}/audit/export", ExportAuditAsync)
            .Produces(StatusCodes.Status200OK, contentType: Jsonl, additionalContentTypes: Csv)
            .WithSummary("Stream the filtered audit events as JSONL or CSV; the export is itself audited (02.7.1)");

        return group;
    }

    private static async Task<Result<AuditListResponse>> ListAuditAsync(
        Guid o,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (EdgeInputs.LimitError(context.Request.Query["limit"], out var limit) is { } limitError)
        {
            return Result.Failure<AuditListResponse>(limitError);
        }

        if (FilterError(context, out var filter) is { } filterError)
        {
            return Result.Failure<AuditListResponse>(filterError);
        }

        return await mediator.Send(new ListAuditQuery(o, EdgeInputs.Cursor(context), limit, filter), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Result<IResult>> ExportAuditAsync(
        Guid o,
        HttpContext context,
        IMediator mediator,
        IOptions<JsonOptions> json,
        CancellationToken cancellationToken)
    {
        var format = context.Request.Query["format"].ToString();
        format = string.IsNullOrEmpty(format) ? "jsonl" : format;

        if (format is not ("jsonl" or "csv"))
        {
            return Result.Failure<IResult>(Error.Invalid(
                "format must be jsonl or csv",
                [new ErrorDetail("format", "invalid")]));
        }

        if (FilterError(context, out var filter) is { } filterError)
        {
            return Result.Failure<IResult>(filterError);
        }

        var result = await mediator.Send(new ExportAuditCommand(o, format, filter), cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return Result.Failure<IResult>(result.Error!);
        }

        var file = (AuditExportFile)result.SuccessValue!;
        var options = json.Value.SerializerOptions;

        return Result.Success(file.Format == "csv"
            ? Results.Stream(stream => WriteCsvAsync(stream, file.Rows), Csv, "audit.csv")
            : Results.Stream(stream => WriteJsonlAsync(stream, file.Rows, options), Jsonl, "audit.jsonl"));
    }

    private static async Task WriteJsonlAsync(Stream stream, IReadOnlyList<AuditEntryDto> rows, JsonSerializerOptions options)
    {
        foreach (var row in rows)
        {
            await JsonSerializer.SerializeAsync(stream, row, options).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray()).ConfigureAwait(false);
        }
    }

    private static async Task WriteCsvAsync(Stream stream, IReadOnlyList<AuditEntryDto> rows)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(string.Join(',', CsvColumns)).ConfigureAwait(false);

        foreach (var row in rows)
        {
            string?[] cells =
            [
                row.Seq.ToString(CultureInfo.InvariantCulture),
                row.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
                row.ActorType,
                row.ActorId,
                row.Action,
                row.ObjectType,
                row.ObjectId,
                row.RequestId,
                row.TraceId,
                row.EntryHash,
            ];
            await writer.WriteLineAsync(string.Join(',', cells.Select(CsvCell))).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One CSV cell: quoted when needed, and a formula prefix (<c>= + - @</c>, tab, CR) is
    /// neutralised with a leading quote so a spreadsheet never executes an actor-controlled value.
    /// </summary>
    public static string CsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static Error? FilterError(HttpContext context, out AuditFilter filter)
    {
        var query = context.Request.Query;
        filter = new AuditFilter(null, null, null, null, null);

        if (!TryDate(query["from"].ToString(), out var from) || !TryDate(query["to"].ToString(), out var to))
        {
            return Error.Invalid("from and to must be ISO 8601 timestamps", [new ErrorDetail("from", "invalid")]);
        }

        filter = new AuditFilter(
            NullIfEmpty(query["actor_type"].ToString()),
            NullIfEmpty(query["action"].ToString()),
            NullIfEmpty(query["object_type"].ToString()),
            from,
            to);

        return null;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool TryDate(string raw, out DateTimeOffset? value)
    {
        value = null;

        if (string.IsNullOrEmpty(raw))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return false;
        }

        value = parsed;

        return true;
    }
}
