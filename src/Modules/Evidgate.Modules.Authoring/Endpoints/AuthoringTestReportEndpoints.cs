using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Queries;
using Evidgate.Modules.Authoring.Application.Reports;
using Evidgate.Modules.Authoring.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Authoring.Endpoints;

/// <summary>
/// The report routes of the authoring group (<c>/v1/projects/{p}/authoring/reports</c>). Delegates only bind the
/// route and the body and answer in <see cref="Result"/> terms: authorization is the pipeline, the tenant is the
/// request's, and the envelope is the host's filter.
/// </summary>
internal static class AuthoringTestReportEndpoints
{
    /// <summary>The body cap of the create route: 1000 ids plus two 2000 character texts fit with room to spare.</summary>
    internal const int WriteBodyCap = 128 * 1024;

    /// <summary>Maps the report routes onto the authoring group.</summary>
    /// <param name="authoring">The <c>/v1/projects/{p}/authoring</c> group the host already created.</param>
    internal static void MapAuthoringTestReportEndpoints(RouteGroupBuilder authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);

        MapReportReadEndpoints(authoring);
        MapReportWriteEndpoints(authoring);
    }

    private static void MapReportReadEndpoints(RouteGroupBuilder authoring)
    {
        authoring.MapGet("/reports", ListAsync)
            .Produces<TestReportListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Keyset page of a project's test case reports, newest first");

        authoring.MapGet("/reports/jobs/{j}", JobAsync)
            .Produces<TestReportJobDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Where a report job stands: pending, running, retry, failed or complete");

        authoring.MapGet("/reports/{r}", GetAsync)
            .Produces<TestReportDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("One report with its immutable snapshot");
    }

    private static void MapReportWriteEndpoints(RouteGroupBuilder authoring)
    {
        // The bodiless `.Produces(200)` keeps the C8 `Result<T>` wrapper out of the served OpenAPI document.
        authoring.MapPost("/reports", CreateAsync)
            .Accepts<CreateTestReportRequest>("application/json")
            .Produces<TestReportJobAcceptedDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Queue the build of an immutable report snapshot");
    }

    private static async Task<Result<TestReportListResponse>> ListAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        var requestError = AuthoringEndpointSupport.LimitError(context.Request.Query["limit"], out var limit);

        // The pipeline authorizes the caller before the handler answers the query error.
        return await mediator.Send(
            new ListTestReportsQuery(p, AuthoringEndpointText.Blank(context.Request.Query["cursor"]), limit, requestError),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<TestReportDetailDto>> GetAsync(
        Guid p,
        Guid r,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        return await mediator.Send(new GetTestReportQuery(p, r), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<TestReportJobDto>> JobAsync(
        Guid p,
        Guid j,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        return await mediator.Send(new GetTestReportJobQuery(p, j), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<TestReportJobAcceptedDto>> CreateAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Refuse an unresolved caller before anything else reads the request.
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        var (body, bodyError) = await AuthoringEndpointSupport.ReadBodyAsync(context, WriteBodyCap, cancellationToken).ConfigureAwait(false);
        if (bodyError is not null)
        {
            return Result.Failure<TestReportJobAcceptedDto>(bodyError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, TestReportBody.WriteMembers, "report") is { } unknown)
        {
            return Result.Failure<TestReportJobAcceptedDto>(unknown);
        }

        return await mediator.Send(
            new CreateTestReportCommand(p, body, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }
}
