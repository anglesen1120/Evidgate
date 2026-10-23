using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Resolve;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Tenancy.Application.Commands;
using Evidgate.Modules.Tenancy.Application.Queries;
using Evidgate.Modules.Tenancy.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Tenancy.Endpoints;

/// <summary>
/// The registry routes of 02.1.1 §5. Like the Identity routes, the delegates only bind
/// 00 §9.1's parameters, read <c>Idempotency-Key</c>/<c>If-Match</c>/<c>limit</c>/<c>cursor</c>
/// and answer in <see cref="Result"/> terms: authorization is the C2 pipeline, the tenant
/// is the request's (never the URL's), and the 00 §9.2 envelope is the host's
/// <c>ResultEndpointFilter</c>. The one exception is <c>POST /v1/orgs</c>, which has no
/// tenant yet and publishes the id of the org it creates (<see cref="OrganizationIds"/>).
/// </summary>
/// <remarks>
/// <c>GET /v1/orgs</c> (list my organizations) is not mapped: it needs the cross-tenant
/// <c>evidgate_resolve.member_orgs</c> of 02.6.1-T3 — under RLS a membership read can
/// only ever return the request's own org (feature plan §3, deferred).
/// </remarks>
public static class TenancyEndpoints
{
    /// <summary>Default page size of the project list (00 §9.1).</summary>
    internal const int DefaultLimit = 50;

    /// <summary>Largest page size of the project list (00 §9.1).</summary>
    internal const int MaxLimit = 200;

    /// <summary>
    /// Maps the registry routes and returns the <c>/v1</c> group so the host can apply
    /// its <c>ResultEndpointFilter</c> (00 §5.5 C8).
    /// </summary>
    public static RouteGroupBuilder MapTenancyEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("identity");

        // The bodiless `.Produces(200)` replaces the response ASP.NET infers from the
        // `Result<T>` delegate type, which is never the wire shape (see MembershipEndpoints).
        group.MapPost("/orgs", CreateOrganizationAsync)
            .Produces<OrganizationDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithMetadata(TenantlessEndpointMetadata.Instance)
            .WithSummary("Create an organization; the caller becomes tenant_admin and release_approver (02)");

        group.MapGet("/orgs", ListMyOrganizationsAsync)
            .Produces<OrganizationListResponse>(StatusCodes.Status200OK)
            .WithMetadata(TenantlessEndpointMetadata.Instance)
            .WithSummary("The organizations the caller is an active member of (02)");

        group.MapGet("/orgs/{o}", GetOrganizationAsync)
            .Produces<OrganizationDto>(StatusCodes.Status200OK)
            .WithSummary("One organization; another org's id is 404 (02)");

        group.MapPatch("/orgs/{o}", UpdateOrganizationAsync)
            .Produces<OrganizationDto>(StatusCodes.Status200OK)
            .WithSummary("Change an organization's settings under If-Match (02)");

        group.MapPost("/orgs/{o}/projects", CreateProjectAsync)
            .Produces<ProjectDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Provision a project (02)");

        group.MapGet("/orgs/{o}/projects", ListProjectsAsync)
            .Produces<ProjectListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of the org's active projects (02)");

        group.MapGet("/projects/{p}", GetProjectAsync)
            .Produces<ProjectDto>(StatusCodes.Status200OK)
            .WithSummary("One project; another org's id is 404 (02, AC3)");

        group.MapPatch("/projects/{p}", UpdateProjectAsync)
            .Produces<ProjectDto>(StatusCodes.Status200OK)
            .WithSummary("Change a project's settings under If-Match (02, AC4/AC5)");

        return group;
    }

    private static async Task<Result<OrganizationDto>> CreateOrganizationAsync(
        CreateOrganizationRequest request,
        HttpContext context,
        IMediator mediator,
        IRequestPrincipalAccessor principals,
        IRequestTenantContextSetter tenant,
        CancellationToken cancellationToken)
    {
        if (principals.Current is not { Kind: PrincipalKind.Member, UserId: { } userId })
        {
            // No member resolved: there is no key space to derive the new org's id in.
            // A service token or an operator never creates an org (02.1.1 §5).
            return Result.Failure<OrganizationDto>(principals.Current is null
                ? new Error(ApiErrorCode.Unauthenticated, "sign in to create an organization", StatusCodes.Status401Unauthorized)
                : Error.Forbidden("only an authenticated user may create an organization (02.1.1 §5)"));
        }

        var idempotencyKey = IdempotencyKey(context);
        var orgId = OrganizationIds.For(userId, idempotencyKey);

        using var scope = tenant.Publish(orgId, projectId: null);

        var result = await mediator.Send(
            new CreateOrganizationCommand(orgId, request.Name, request.Slug, idempotencyKey),
            cancellationToken).ConfigureAwait(false);

        return WithETag(context, result, organization => organization.Version);
    }

    private static async Task<Result<OrganizationListResponse>> ListMyOrganizationsAsync(
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMemberOrgResolver resolver,
        CancellationToken cancellationToken)
    {
        // A pre-tenant route (02.6.2 §3): there is no tenant scope, so the list comes
        // from the evidgate_resolve function keyed by the authenticated user, never from
        // a membership join under RLS (which could only ever show the current org).
        if (principals.Current is not { Kind: PrincipalKind.Member, UserId: { } userId })
        {
            return Result.Failure<OrganizationListResponse>(principals.Current is null
                ? new Error(ApiErrorCode.Unauthenticated, "sign in to list your organizations", StatusCodes.Status401Unauthorized)
                : Error.Forbidden("only a signed-in user has organizations"));
        }

        if (LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<OrganizationListResponse>(error);
        }

        var rows = await resolver.MemberOrganizationsAsync(userId, limit, cancellationToken).ConfigureAwait(false);

        return Result.Success(new OrganizationListResponse(
            [.. rows.Select(row => new OrganizationSummaryDto(row.OrgId, row.Slug, row.Name, row.Status))]));
    }

    private static async Task<Result<OrganizationDto>> GetOrganizationAsync(
        Guid o,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetOrganizationQuery(o), cancellationToken).ConfigureAwait(false);

        return WithETag(context, result, organization => organization.Version);
    }

    private static async Task<Result<OrganizationDto>> UpdateOrganizationAsync(
        Guid o,
        UpdateOrganizationRequest request,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (IfMatchError(context, "organization", out var version) is { } error)
        {
            return Result.Failure<OrganizationDto>(error);
        }

        var result = await mediator.Send(
            new UpdateOrganizationCommand(
                o,
                request.Name,
                request.Slug,
                request.RetentionDefaultDays,
                request.AllowedEmailDomains,
                version,
                IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        return WithETag(context, result, organization => organization.Version);
    }

    private static async Task<Result<ProjectDto>> CreateProjectAsync(
        Guid o,
        CreateProjectRequest request,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new CreateProjectCommand(o, request.Name, request.Slug, IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        return WithETag(context, result, project => project.Version);
    }

    private static async Task<Result<ProjectListResponse>> ListProjectsAsync(
        Guid o,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<ProjectListResponse>(error);
        }

        var cursor = context.Request.Query["cursor"].ToString();

        return await mediator.Send(
            new ListProjectsQuery(o, string.IsNullOrEmpty(cursor) ? null : cursor, limit),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<ProjectDto>> GetProjectAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetProjectQuery(p), cancellationToken).ConfigureAwait(false);

        return WithETag(context, result, project => project.Version);
    }

    private static async Task<Result<ProjectDto>> UpdateProjectAsync(
        Guid p,
        UpdateProjectRequest request,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (IfMatchError(context, "project", out var version) is { } error)
        {
            return Result.Failure<ProjectDto>(error);
        }

        var result = await mediator.Send(
            new UpdateProjectCommand(
                p,
                request.Name,
                request.Slug,
                request.RetentionDays,
                request.CasePrefix,
                version,
                IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        return WithETag(context, result, project => project.Version);
    }

    /// <summary>The raw <c>Idempotency-Key</c>; the C2 idempotency stage validates it (400 when absent).</summary>
    private static string IdempotencyKey(HttpContext context) =>
        context.Request.Headers["Idempotency-Key"].ToString();

    /// <summary>
    /// Publishes the row's <c>version</c> as the <c>ETag</c> (00 §9.1) so a client can
    /// carry it back in <c>If-Match</c>; a failure carries none.
    /// </summary>
    private static Result<T> WithETag<T>(HttpContext context, Result<T> result, Func<T, int> version)
    {
        if (result.IsSuccess && result.SuccessValue is T value)
        {
            context.Response.Headers.ETag = string.Create(CultureInfo.InvariantCulture, $"\"{version(value)}\"");
        }

        return result;
    }

    /// <summary>
    /// Parses <c>If-Match</c> into the row's <c>version</c> (00 §9.1): the strong and
    /// weak quoted forms (<c>"3"</c>, <c>W/"3"</c>) and a bare number. An absent or
    /// unparsable header is 422 — 00 §9.1 makes it required on every PATCH.
    /// </summary>
    private static Error? IfMatchError(HttpContext context, string what, out int version)
    {
        version = 0;
        var raw = context.Request.Headers.IfMatch.ToString();

        if (string.IsNullOrEmpty(raw))
        {
            return Error.Invalid(
                $"If-Match is required: send the ETag of the {what} you read (00 §9.1)",
                [new ErrorDetail("If-Match", "required")]);
        }

        var value = raw.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        value = value.Trim('"');

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out version) && version >= 1
            ? null
            : Error.Invalid(
                $"If-Match must be the ETag this endpoint issued: the {what}'s version (00 §9.1)",
                [new ErrorDetail("If-Match", "invalid")]);
    }

    /// <summary>The <c>limit</c> query parameter, 1–200, default 50 (00 §9.1); anything else is 422.</summary>
    private static Error? LimitError(string? raw, out int limit)
    {
        limit = DefaultLimit;

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1
            || parsed > MaxLimit)
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"limit must be an integer between 1 and {MaxLimit} (00 §9.1)"),
                [new ErrorDetail("limit", "out_of_range")]);
        }

        limit = parsed;
        return null;
    }
}
