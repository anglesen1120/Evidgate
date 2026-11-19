using Evidgate.BuildingBlocks.Contracts.Validation;
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
/// The catalog routes. Like the Tenancy and Identity routes the delegates only bind and answer in <see cref="Result"/>
/// terms: authorization is the pipeline, the tenant is the request's (never the URL's), and the envelope is the host's
/// <c>ResultEndpointFilter</c>.
/// </summary>
public static class CatalogEndpoints
{
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 200;

    /// <summary>The members <c>PATCH /v1/cases/{c}</c> accepts; the identity members are named for a precise 422.</summary>
    private static readonly string[] Mutable = ["name", "owner_membership_id", "status", "labels"];

    private static readonly string[] Identity = ["id", "org_id", "project_id", "annotation_id", "file_path", "first_seen_commit", "last_seen_commit", "framework", "language"];

    /// <summary>Maps the catalog routes and returns the <c>/v1</c> group so the host can apply its filters.</summary>
    public static RouteGroupBuilder MapCatalogEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("catalog");

        group.MapGet("/projects/{p}/cases", ListCasesAsync)
            .Produces<CaseListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of a project's test cases (03)");

        group.MapPatch("/projects/{p}/cases/{c}", SetCaseRequirementRefAsync)
            .Accepts<SetCaseRequirementRequest>("application/json")
            .Produces<CaseDto>(StatusCodes.Status200OK)
            .WithSummary("Set or clear a case's external requirement reference (03)");

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

        group.MapPost("/projects/{p}/suites", CreateSuiteAsync)
            .Accepts<CreateSuiteRequest>("application/json")
            .Produces<SuiteDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Create a suite in a project (03)");

        group.MapGet("/projects/{p}/suites", ListSuitesAsync)
            .Produces<SuiteListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of a project's suites (03)");

        group.MapGet("/suites/{s}", GetSuiteAsync)
            .Produces<SuiteDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status304NotModified)
            .WithSummary("One suite; ETag is the head revision number, If-None-Match honours 304 (03)");

        // The delegate reads the raw JSON (strict, capped) so unknown members are refused; the documented body is the DTO.
        group.MapPost("/suites/{s}/revisions", CreateSuiteRevisionAsync)
            .WithBodyLimit(MaxRevisionBodyBytes)
            .Accepts<CreateSuiteRevisionRequest>("application/json")
            .Produces<SuiteRevisionHead>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Create an immutable suite revision, or preview it with dry_run=true (03)");

        group.MapGet("/suite-revisions/{r}", GetSuiteRevisionAsync)
            .Produces<SuiteRevisionDto>(StatusCodes.Status200OK)
            .WithSummary("The full pinned content of a suite revision (03)");

        group.MapGet("/suite-revisions/{r}/diff", GetSuiteRevisionDiffAsync)
            .Produces<SuiteRevisionDiff>(StatusCodes.Status200OK)
            .WithSummary("What changed from another revision of the same suite (03)");

        group.MapExecutionPlanEndpoints();

        group.MapFixtureEndpoints();

        group.MapGitRepoBindingEndpoints();

        return group;
    }

    private static async Task<Result<SuiteDto>> CreateSuiteAsync(
        Guid p,
        CreateSuiteRequest request,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new CreateSuiteCommand(p, request.Name, request.Slug, IdempotencyKey(context), request.Platform, request.Level, request.Purpose),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess && result.SuccessValue is SuiteDto created)
        {
            context.Response.Headers.ETag = string.Create(CultureInfo.InvariantCulture, $"\"{created.HeadRevisionNo ?? 0}-{created.Version}\"");
        }

        return result;
    }

    private static async Task<Result<SuiteListResponse>> ListSuitesAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (CatalogRequestRules.Limit(context.Request.Query["limit"], DefaultLimit, MaxLimit, out var limit) is { } error)
        {
            return Result.Failure<SuiteListResponse>(error);
        }

        return await mediator.Send(
            new ListSuitesQuery(
                p,
                Blank(context.Request.Query["cursor"]),
                limit,
                Blank(context.Request.Query["platform"]),
                Blank(context.Request.Query["level"]),
                Blank(context.Request.Query["purpose"])),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result> GetSuiteAsync(
        Guid s,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSuiteQuery(s), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result;
        }

        var tagText = string.Create(CultureInfo.InvariantCulture, $"{result.Value.HeadRevisionNo ?? 0}-{result.Value.Version}");
        var etag = $"\"{tagText}\"";
        context.Response.Headers.ETag = etag;

        // 304 when the caller already holds this head: the suite row itself is cheap, the saving is the body and the client's parse.
        var presented = context.Request.Headers.IfNoneMatch.ToString();

        return !string.IsNullOrEmpty(presented)
            && presented.Split(',').Select(tag => tag.Trim()).Any(tag => tag == "*" || (tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag).Trim('"') == tagText)
            ? Result.Success(StatusCodes.Status304NotModified)
            : result;
    }

    private static async Task<Result> CreateSuiteRevisionAsync(
        Guid s,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var (body, readError) = await CatalogRequestRules.ReadJsonAsync(context, MaxRevisionBodyBytes, cancellationToken).ConfigureAwait(false);
        if (readError is not null)
        {
            return Result.Failure<SuiteRevisionHead>(readError);
        }

        CreateSuiteRevisionRequest request;
        try
        {
            request = body.Deserialize<CreateSuiteRevisionRequest>(RequestJson)
                ?? new CreateSuiteRevisionRequest(null, null, null, null, null);
        }
        catch (JsonException exception)
        {
            return Result.Failure<SuiteRevisionHead>(Error.Invalid(
                "the body does not match the suite revision shape",
                [new ErrorDetail(Domain.TextRules.Brief(exception.Path ?? string.Empty, 128), "invalid")]));
        }

        var dryRunText = context.Request.Query["dry_run"].ToString();
        if (dryRunText.Length > 0 && !bool.TryParse(dryRunText, out _))
        {
            return Result.Failure<SuiteRevisionHead>(Error.BadRequest("dry_run must be true or false", [new ErrorDetail("dry_run", "invalid")]));
        }

        var dryRun = dryRunText.Length > 0 && bool.Parse(dryRunText);
        var result = dryRun
            ? await mediator.Send(new PreviewSuiteRevisionQuery(s, request), cancellationToken).ConfigureAwait(false)
            : await mediator.Send(new CreateSuiteRevisionCommand(s, request, IdempotencyKey(context)), cancellationToken).ConfigureAwait(false);

        return result;
    }

    private static async Task<Result<SuiteRevisionDto>> GetSuiteRevisionAsync(
        Guid r,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSuiteRevisionQuery(r), cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess && result.SuccessValue is SuiteRevisionDto revision)
        {
            // A revision never changes: its hash is its ETag and it caches privately for good.
            context.Response.Headers.ETag = $"\"{revision.RevisionHash}\"";
            context.Response.Headers.CacheControl = "private, immutable";
        }

        return result;
    }

    private static async Task<Result<SuiteRevisionDiff>> GetSuiteRevisionDiffAsync(
        Guid r,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Request.Query["against"], out var against))
        {
            return Result.Failure<SuiteRevisionDiff>(Error.Invalid("against must be the id of another revision of the same suite", [new ErrorDetail("against", "required")]));
        }

        return await mediator.Send(new GetSuiteRevisionDiffQuery(r, against), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<CaseListResponse>> ListCasesAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (CatalogRequestRules.Limit(context.Request.Query["limit"], DefaultLimit, MaxLimit, out var limit) is { } error)
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

    private static async Task<Result<CaseDto>> SetCaseRequirementRefAsync(
        Guid p,
        Guid c,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var (body, readError) = await CatalogRequestRules.ReadJsonAsync(context, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (readError is not null)
        {
            return Result.Failure<CaseDto>(readError);
        }

        if (RequirementBodyError(body) is { } bodyError)
        {
            return Result.Failure<CaseDto>(bodyError);
        }

        var member = body.GetProperty("requirement_ref");
        string? requirementRef;
        if (member.ValueKind is JsonValueKind.Null)
        {
            requirementRef = null;
        }
        else if (member.ValueKind is JsonValueKind.String)
        {
            try
            {
                requirementRef = member.GetString();
            }
            catch (InvalidOperationException)
            {
                // A lone surrogate escape parses as JSON but has no UTF-16 text: refuse it as the 422, never a 500.
                return Result.Failure<CaseDto>(Error.Invalid(
                    "requirement_ref must be well-formed Unicode text",
                    [new ErrorDetail("requirement_ref", "invalid_value")]));
            }
        }
        else
        {
            return Result.Failure<CaseDto>(Error.Invalid(
                "requirement_ref must be a string or null",
                [new ErrorDetail("requirement_ref", "invalid_type")]));
        }

        var result = await mediator.Send(
            new SetCaseRequirementRefCommand(p, c, requirementRef, IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        return WithETag(context, result, found => found.Version);
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

        var (body, readError) = await CatalogRequestRules.ReadJsonAsync(context, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
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
        if (CatalogRequestRules.Limit(context.Request.Query["limit"], DefaultLimit, MaxLimit, out var limit) is { } error)
        {
            return Result.Failure<CaseRevisionListResponse>(error);
        }

        return await mediator.Send(new GetCaseRevisionsQuery(c, Blank(context.Request.Query["cursor"]), limit), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>A case body is a handful of scalars; 16 KiB is far above any legal one.</summary>
    private const int MaxBodyBytes = 16 * 1024;

    /// <summary>A revision of 10 000 members with parameters stays well under this.</summary>
    private const int MaxRevisionBodyBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The only member the requirement route accepts; the body must carry it (null clears).</summary>
    private static readonly string[] RequirementMembers = ["requirement_ref"];

    /// <summary>The requirement route's body: one known member, present; anything else is the dedicated 422.</summary>
    private static Error? RequirementBodyError(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return Error.Invalid("the body must be a JSON object", [new ErrorDetail(string.Empty, "invalid_type")]);
        }

        var present = false;
        foreach (var member in body.EnumerateObject())
        {
            if (!RequirementMembers.Contains(member.Name, StringComparer.Ordinal))
            {
                var shown = Domain.TextRules.Brief(member.Name, 64);

                return Error.Invalid($"'{shown}' is not a field of a requirement update", [new ErrorDetail(shown, "unknown_field")]);
            }

            present = true;
        }

        return present
            ? null
            : Error.Invalid("requirement_ref is required: send null to clear it", [new ErrorDetail("requirement_ref", "required")]);
    }

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
                var shown = Domain.TextRules.Brief(member.Name, 64);

                return Error.Invalid($"'{shown}' is not a field of a test case", [new ErrorDetail(shown, "unknown_field")]);
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
}
