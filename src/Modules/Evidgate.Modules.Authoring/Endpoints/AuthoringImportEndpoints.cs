using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Import;
using Evidgate.Modules.Authoring.Application.Queries;
using Evidgate.Modules.Authoring.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace Evidgate.Modules.Authoring.Endpoints;

/// <summary>
/// Route 25 of 13.1.1 §5 — the import/export pair of the feature tree — mapped onto the authoring group
/// (<c>/v1/projects/{p}/authoring</c>) like every other route of the feature.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two raw-body details make this route different from its neighbours.</b> The import takes the <em>file itself</em> as
/// the request body — <c>text/csv</c>, <c>application/json</c> or the XLSX media type, never a JSON envelope — so its
/// delegate reads the bytes with a hard cap instead of <see cref="AuthoringEndpointSupport.ReadBodyAsync"/>, and the export
/// answers with a stream rather than a DTO. Everything else follows the module's contract: the delegates bind 00 §9.1's
/// parameters and answer in <c>Result</c> terms, <c>[Permission]</c> sits on the handler class, and the 00 §9.2 envelope is
/// the host's <c>ResultEndpointFilter</c>.
/// </para>
/// <para>
/// <b>What the delegate refuses, and why here.</b> The request's shape and size are the edge's: a missing or unknown
/// <c>format</c> is 422 <c>request_invalid</c> <c>/format</c>, a body over <c>ImportLimits.MaxBodyBytes</c> is 413
/// <c>payload_too_large</c> before a byte is parsed or a statement runs (D-S55-Q3's rule, the same one the JSON routes
/// follow for their 64 KiB cap), and a malformed <c>node_id</c>/<c>include</c>/<c>dry_run</c> is 422 naming the member.
/// The file's <em>values</em> are the handler's, which is where the readers, the importer and the steps schema live.
/// </para>
/// </remarks>
internal static class AuthoringImportEndpoints
{
    /// <summary>Maps the import and export routes onto the authoring group.</summary>
    /// <param name="authoring">The <c>/v1/projects/{p}/authoring</c> group the host already created.</param>
    internal static void MapAuthoringImportEndpoints(RouteGroupBuilder authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);

        // The body is the file, so the accepted media types are declared without a body schema: the document a JSON
        // import carries is contracts/schemas/authoring-import.v1.json, referenced by the contract operation itself.
        // They are the contract's own tokens, parameter for parameter (openapi.yaml:6001-6008): the bare `text/csv` is
        // what the contract declares, and the route accepts the same type with `charset=utf-8` as well, because that is
        // the header a spreadsheet or browser upload actually sends and the accepted list is matched literally. The
        // other two are declared bare, which matches any charset.
        authoring.MapPost("/import", ImportAsync)
            .WithMetadata(new AcceptsMetadata([
                ImportFormats.CsvMediaType,
                ImportFormats.CsvContentType,
                ImportFormats.JsonContentType,
                ImportFormats.XlsxContentType]))
            .Produces<ImportResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Import a feature tree from a CSV, XLSX or JSON file as one batch (route 25, AC13/AC15)");

        authoring.MapGet("/export", ExportAsync)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Export a project's feature tree, or one node's subtree, as CSV, XLSX or JSON (route 25, AC14)");
    }

    /// <summary>Route 25 (import): read the file under its cap, then hand it to the command.</summary>
    /// <param name="p">The project of the URL.</param>
    /// <param name="context">The request.</param>
    /// <param name="mediator">The pipeline.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>The report, or the 413/422 the delegate refuses before the pipeline runs.</returns>
    private static async Task<Result<ImportResponse>> ImportAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (ImportFormats.ReadFormat(context.Request.Query["format"].ToString()) is not { } format)
        {
            return Result.Failure<ImportResponse>(ImportFormats.Unsupported());
        }

        if (ImportFormats.DryRun(context.Request.Query["dry_run"].ToString(), out var dryRun) is { } dryRunError)
        {
            return Result.Failure<ImportResponse>(dryRunError);
        }

        var (body, bodyError) = await ReadFileAsync(context, ImportLimits.MaxBodyBytes, cancellationToken)
            .ConfigureAwait(false);

        if (bodyError is { } refusal)
        {
            return Result.Failure<ImportResponse>(refusal);
        }

        return await mediator.Send(
            new ImportAuthoringCommand(p, format, dryRun, body, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Route 25 (export): bind the format, the subtree and the kinds, then stream the file.</summary>
    /// <param name="p">The project of the URL.</param>
    /// <param name="context">The request.</param>
    /// <param name="mediator">The pipeline.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>The file as an <see cref="IResult"/>, or the 404/413/422 envelope.</returns>
    private static async Task<Result<IResult>> ExportAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (ImportFormats.ReadFormat(context.Request.Query["format"].ToString()) is not { } format)
        {
            return Result.Failure<IResult>(ImportFormats.Unsupported());
        }

        if (AuthoringEndpointText.OptionalId(context.Request.Query, "node_id", out var nodeId) is { } nodeError)
        {
            return Result.Failure<IResult>(nodeError);
        }

        if (ImportFormats.Include(context.Request.Query["include"].ToString(), out var include) is { } includeError)
        {
            return Result.Failure<IResult>(includeError);
        }

        // An export is generated for this response only: never stored, never served from a cache (13.1.1 AC14).
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";

        return await mediator.Send(new ExportAuthoringQuery(p, format, nodeId, include), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the import body as the raw file it is, refusing anything over <paramref name="maxBytes"/> before it is
    /// buffered whole: a declared <c>Content-Length</c> over the cap is answered without reading, and an undeclared
    /// (chunked) body is measured chunk by chunk, so the buffer never holds more than the cap.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="maxBytes">The byte cap (<see cref="ImportLimits.MaxBodyBytes"/>).</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>The bytes, or the 413/422 the caller must return unchanged.</returns>
    private static async Task<(byte[] Body, Error? Error)> ReadFileAsync(
        HttpContext context,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength > maxBytes)
        {
            return ([], TooLarge(maxBytes));
        }

        // The declared length is the body's exact size (a longer one was refused above), so the buffer is sized up front;
        // an undeclared body starts at the shared initial size and grows chunk by chunk, each chunk measured before it is
        // written.
        var capacity = Math.Min(maxBytes, AuthoringEndpointSupport.InitialUndeclaredBodyBytes);
        if (context.Request.ContentLength is { } declared)
        {
            capacity = declared <= 0 ? 0 : (int)declared;
        }

        using var buffer = new MemoryStream(capacity);
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return ([], TooLarge(maxBytes));
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (buffer.Length == 0)
        {
            return ([], Error.Invalid("the file is required", [new ErrorDetail("/file", "required")]));
        }

        return (buffer.ToArray(), null);
    }

    /// <summary>413 <c>payload_too_large</c>; the message names the cap, never the payload (00 §9.2).</summary>
    /// <param name="maxBytes">The byte cap.</param>
    private static Error TooLarge(int maxBytes) => new(
        ApiErrorCode.PayloadTooLarge,
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the request body is larger than {maxBytes} bytes"),
        StatusCodes.Status413PayloadTooLarge,
        Retryable: false,
        Details: [new ErrorDetail("/file", "payload_too_large")]);
}
