using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Application.Commands;
using Evidgate.Modules.Catalog.Application.Queries;
using Evidgate.Modules.Catalog.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Catalog.Endpoints;

/// <summary>
/// The fixture routes, mapped into the catalog group. The delegates only bind and answer in <see cref="Result"/> terms:
/// authorization is the pipeline, the tenant is the request's, and the body is read raw so an unknown member can be
/// named in the 422.
/// </summary>
internal static class FixtureEndpoints
{
    /// <summary>A fixture body is a pointer and a handful of scalars; 64 KiB is far above any legal one.</summary>
    internal const int MaxBodyBytes = 64 * 1024;

    private static readonly string[] CreateMembers = ["fixture_key", "seed_version", "body_ref", "timeout_s", "cleanup_policy"];

    private static readonly string[] RevisionMembers = ["seed_version", "body_ref", "timeout_s", "cleanup_policy"];

    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Maps the fixture routes into the given <c>/v1</c> group and returns it.</summary>
    /// <param name="group">The catalog group.</param>
    public static RouteGroupBuilder MapFixtureEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/projects/{p}/fixtures", CreateFixtureAsync)
            .Accepts<CreateFixtureRequest>("application/json")
            .Produces<FixtureRevisionHead>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Create a fixture with its first revision (03)");

        group.MapGet("/projects/{p}/fixtures", ListFixturesAsync)
            .Produces<FixtureListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of a project's fixtures (03)");

        group.MapGet("/fixtures/{f}", GetFixtureAsync)
            .Produces<FixtureDetailDto>(StatusCodes.Status200OK)
            .WithSummary("One fixture with its head revision; another org's id is 404 (03)");

        group.MapPost("/fixtures/{f}/revisions", CreateFixtureRevisionAsync)
            .Accepts<CreateFixtureRevisionRequest>("application/json")
            .Produces<FixtureRevisionHead>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Append an immutable fixture revision; identical content answers the existing one (03)");

        return group;
    }

    private static async Task<Result<FixtureRevisionHead>> CreateFixtureAsync(
        Guid p,
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var (body, readError) = await CatalogRequestRules.ReadAuthenticatedJsonAsync(context, principals, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (readError is not null)
        {
            return Result.Failure<FixtureRevisionHead>(readError);
        }

        if (CatalogRequestRules.UnknownMember(body, CreateMembers, "a fixture") is { } memberError)
        {
            return Result.Failure<FixtureRevisionHead>(memberError);
        }

        CreateFixtureRequest request;
        try
        {
            request = body.Deserialize<CreateFixtureRequest>(RequestJson)
                ?? new CreateFixtureRequest(string.Empty, string.Empty, string.Empty);
        }
        catch (JsonException exception)
        {
            return Result.Failure<FixtureRevisionHead>(InvalidBody(exception));
        }

        return await mediator.Send(
            new CreateFixtureCommand(
                p,
                request.FixtureKey ?? string.Empty,
                request.SeedVersion ?? string.Empty,
                request.BodyRef ?? string.Empty,
                IdempotencyKey(context),
                request.TimeoutS,
                request.CleanupPolicy),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<FixtureRevisionHead>> CreateFixtureRevisionAsync(
        Guid f,
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var (body, readError) = await CatalogRequestRules.ReadAuthenticatedJsonAsync(context, principals, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (readError is not null)
        {
            return Result.Failure<FixtureRevisionHead>(readError);
        }

        if (CatalogRequestRules.UnknownMember(body, RevisionMembers, "a fixture revision") is { } memberError)
        {
            return Result.Failure<FixtureRevisionHead>(memberError);
        }

        CreateFixtureRevisionRequest request;
        try
        {
            request = body.Deserialize<CreateFixtureRevisionRequest>(RequestJson)
                ?? new CreateFixtureRevisionRequest(string.Empty, string.Empty);
        }
        catch (JsonException exception)
        {
            return Result.Failure<FixtureRevisionHead>(InvalidBody(exception));
        }

        return await mediator.Send(
            new CreateFixtureRevisionCommand(
                f,
                new CreateFixtureRevisionRequest(request.SeedVersion ?? string.Empty, request.BodyRef ?? string.Empty, request.TimeoutS, request.CleanupPolicy),
                IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<FixtureListResponse>> ListFixturesAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (CatalogRequestRules.Limit(context.Request.Query["limit"], CatalogEndpoints.DefaultLimit, CatalogEndpoints.MaxLimit, out var limit) is { } error)
        {
            return Result.Failure<FixtureListResponse>(error);
        }

        var cursor = context.Request.Query["cursor"].ToString();

        return await mediator.Send(
            new ListFixturesQuery(p, string.IsNullOrEmpty(cursor) ? null : cursor, limit),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<FixtureDetailDto>> GetFixtureAsync(
        Guid f,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetFixtureQuery(f), cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess && result.SuccessValue is FixtureDetailDto detail)
        {
            // The head's hash is the identity of what this answer carries; the fixture itself keeps moving, so no immutable caching.
            if (detail.Head is { } head)
            {
                context.Response.Headers.ETag = $"\"{head.RevisionHash}\"";
            }

            context.Response.Headers.CacheControl = "private";
        }

        return result;
    }

    private static Error InvalidBody(JsonException exception) =>
        Error.Invalid(
            "the body does not match the fixture shape",
            [new ErrorDetail(Domain.TextRules.Brief(exception.Path ?? string.Empty, 128), "invalid")]);

    private static string IdempotencyKey(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();
}
