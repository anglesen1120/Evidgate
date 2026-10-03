using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Application;
using Evidgate.Modules.Catalog.Application.Commands;
using Evidgate.Modules.Catalog.Application.Queries;
using Evidgate.Modules.Catalog.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Catalog.Endpoints;

/// <summary>
/// The catalog routes of 03.1.1 §5. Like the Tenancy and Identity routes the delegates only bind 00 §9.1's
/// parameters and answer in <see cref="Result"/> terms: authorization is the C2 pipeline, the tenant is the
/// request's (never the URL's), and the 00 §9.2 envelope is the host's <c>ResultEndpointFilter</c>.
/// </summary>
public static class CatalogEndpoints
{
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 200;

    /// <summary>The members <c>PATCH /v1/cases/{c}</c> accepts; the identity members are named for a precise 422.</summary>
    private static readonly string[] Mutable = ["name", "owner_membership_id", "status", "labels"];

    private static readonly string[] Identity = ["id", "org_id", "project_id", "annotation_id", "file_path", "first_seen_commit", "last_seen_commit", "framework", "language"];

    /// <summary>Maps the catalog routes and returns the <c>/v1</c> group so the host can apply its filters (00 §5.5 C8).</summary>
    public static RouteGroupBuilder MapCatalogEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("catalog");

        group.MapGet("/projects/{p}/cases", ListCasesAsync)
            .Produces<CaseListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of a project's test cases (03)");

        group.MapGet("/cases/{c}", GetCaseAsync)
            .Produces<CaseDetailDto>(StatusCodes.Status200OK)
            .WithSummary("One test case with its identity trail; another org's id is 404 (03)");

        // The delegate reads the raw JSON so identity members can be named in a precise 422; the documented body is the DTO.
        group.MapPatch("/cases/{c}", UpdateCaseAsync)
            .Accepts<UpdateCaseRequest>("application/json")
            .Produces<CaseDto>(StatusCodes.Status200OK)
            .WithSummary("Change a case's name, owner, status or labels under If-Match (03)");

        group.MapGet("/cases/{c}/revisions", GetCaseRevisionsAsync)
            .Produces<CaseRevisionListResponse>(StatusCodes.Status200OK)
            .WithSummary("Stored versions of a case, newest first (03)");

        return group;
    }

    private static async Task<Result<CaseListResponse>> ListCasesAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<CaseListResponse>(error);
        }

        var query = context.Request.Query;

        return await mediator.Send(
            new ListCasesQuery(p, Blank(query["status"]), Blank(query["framework"]), Blank(query["cursor"]), limit),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<CaseDetailDto>> GetCaseAsync(
        Guid c,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCaseQuery(c), cancellationToken).ConfigureAwait(false);

        return WithETag(context, result, detail => detail.Case.Version);
    }

    private static async Task<Result<CaseDto>> UpdateCaseAsync(
        Guid c,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (IfMatchError(context, "case", out var version) is { } ifMatch)
        {
            return Result.Failure<CaseDto>(ifMatch);
        }

        var (body, readError) = await ReadObjectAsync(context, cancellationToken).ConfigureAwait(false);
        if (readError is not null)
        {
            return Result.Failure<CaseDto>(readError);
        }

        if (BodyError(body) is { } bodyError)
        {
            return Result.Failure<CaseDto>(bodyError);
        }

        UpdateCaseRequest request;
        try
        {
            request = body.Deserialize<UpdateCaseRequest>(RequestJson) ?? new UpdateCaseRequest(null, null, null, null);
        }
        catch (JsonException)
        {
            return Result.Failure<CaseDto>(Error.Invalid("the body does not match the case update shape", [new ErrorDetail(string.Empty, "invalid_json")]));
        }

        var result = await mediator.Send(
            new UpdateCaseCommand(c, request.Name, request.OwnerMembershipId, request.Status, request.Labels, version, IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        return WithETag(context, result, found => found.Version);
    }

    private static async Task<Result<CaseRevisionListResponse>> GetCaseRevisionsAsync(
        Guid c,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<CaseRevisionListResponse>(error);
        }

        return await mediator.Send(new GetCaseRevisionsQuery(c, Blank(context.Request.Query["cursor"]), limit), cancellationToken)
            .ConfigureAwait(false);
    }

    private const int MaxBodyBytes = 16 * 1024;

    /// <summary>The raw JSON body, capped at 16 KiB (P8), so identity members can be named in a precise 422.</summary>
    private static async Task<(JsonElement Body, Error? Error)> ReadObjectAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return (default, new Error(ApiErrorCode.PayloadTooLarge, "the request body is too large", StatusCodes.Status413PayloadTooLarge));
        }

        try
        {
            using var limited = new MemoryStream();
            var buffer = new byte[1024];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await limited.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (limited.Length > MaxBodyBytes)
                {
                    return (default, new Error(ApiErrorCode.PayloadTooLarge, "the request body is too large", StatusCodes.Status413PayloadTooLarge));
                }
            }

            using var document = JsonDocument.Parse(limited.ToArray());

            return (document.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return (default, Error.BadRequest("the body is not valid JSON"));
        }
    }

    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The body must be an object of known members; an identity member is the dedicated 422.</summary>
    private static Error? BodyError(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return Error.Invalid("the body must be a JSON object", [new ErrorDetail(string.Empty, "invalid_type")]);
        }

        foreach (var member in body.EnumerateObject())
        {
            if (Identity.Contains(member.Name, StringComparer.Ordinal))
            {
                return CatalogErrors.ImmutableField(member.Name);
            }

            if (!Mutable.Contains(member.Name, StringComparer.Ordinal))
            {
                return Error.Invalid($"'{member.Name}' is not a field of a test case", [new ErrorDetail(member.Name, "unknown_field")]);
            }
        }

        return null;
    }

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string IdempotencyKey(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();

    private static Result<T> WithETag<T>(HttpContext context, Result<T> result, Func<T, int> version)
    {
        if (result.IsSuccess && result.SuccessValue is T value)
        {
            context.Response.Headers.ETag = string.Create(CultureInfo.InvariantCulture, $"\"{version(value)}\"");
        }

        return result;
    }

    private static Error? IfMatchError(HttpContext context, string what, out int version)
    {
        version = 0;
        var raw = context.Request.Headers.IfMatch.ToString();

        if (string.IsNullOrEmpty(raw))
        {
            return Error.Invalid($"If-Match is required: send the ETag of the {what} you read (00 §9.1)", [new ErrorDetail("If-Match", "required")]);
        }

        var value = raw.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        value = value.Trim('"');

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out version) && version >= 1
            ? null
            : Error.Invalid($"If-Match must be the ETag this endpoint issued: the {what}'s version (00 §9.1)", [new ErrorDetail("If-Match", "invalid")]);
    }

    private static Error? LimitError(string? raw, out int limit)
    {
        limit = DefaultLimit;

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1 || parsed > MaxLimit)
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"limit must be an integer between 1 and {MaxLimit} (00 §9.1)"),
                [new ErrorDetail("limit", "out_of_range")]);
        }

        limit = parsed;

        return null;
    }
}
