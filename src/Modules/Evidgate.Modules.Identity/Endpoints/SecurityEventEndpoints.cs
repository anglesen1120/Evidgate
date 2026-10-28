using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Application.Queries;
using Evidgate.Modules.Identity.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// The security-events read route of 11.5.1-T5.2: <c>GET /v1/orgs/{o}/security-events</c>, a cursor
/// page of the tenant's security events filtered by project, type, severity and time. Delegates bind
/// the filters and answer in <see cref="Result"/> terms; the host applies the tenant and result filters.
/// </summary>
public static class SecurityEventEndpoints
{
    /// <summary>The closed V1 <c>type</c> set (11.5.1 §4; V2 adds <c>injection_attempt</c> in 11.4.1-T2).</summary>
    private static readonly string[] Types =
    [
        "auth_failure", "cross_tenant_probe", "secret_scanner_alert", "kill_switch_used", "support_access",
    ];

    /// <summary>The closed <c>severity</c> set (11.5.1 §4/§6).</summary>
    private static readonly string[] Severities = ["info", "warning", "critical"];

    /// <summary>Maps the security-events route and returns the group so the host applies its filters.</summary>
    /// <param name="routes">The route builder.</param>
    public static RouteGroupBuilder MapSecurityEventEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("security");

        group.MapGet("/orgs/{o}/security-events", ListSecurityEventsAsync)
            .Produces<SecurityEventList>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of the org's security events, newest first (11.5.1)");

        return group;
    }

    private static async Task<Result<SecurityEventList>> ListSecurityEventsAsync(
        Guid o,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (EdgeInputs.LimitError(context.Request.Query["limit"], out var limit) is { } limitError)
        {
            return Result.Failure<SecurityEventList>(limitError);
        }

        if (FilterError(context, out var filter) is { } filterError)
        {
            return Result.Failure<SecurityEventList>(filterError);
        }

        return await mediator.Send(new ListSecurityEventsQuery(o, EdgeInputs.Cursor(context), limit, filter), cancellationToken)
            .ConfigureAwait(false);
    }

    private static Error? FilterError(HttpContext context, out SecurityEventFilter filter)
    {
        var query = context.Request.Query;
        filter = new SecurityEventFilter(null, null, null, null, null);

        Guid? projectId = null;
        var rawProjectId = query["project_id"].ToString();

        if (!string.IsNullOrEmpty(rawProjectId))
        {
            if (!Guid.TryParse(rawProjectId, out var parsedProjectId))
            {
                return Error.Invalid("project_id must be a uuid", [new ErrorDetail("project_id", "invalid")]);
            }

            projectId = parsedProjectId;
        }

        var type = NullIfEmpty(query["type"].ToString());
        if (type is not null && !Types.Contains(type, StringComparer.Ordinal))
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"type must be one of {string.Join(", ", Types)}"),
                [new ErrorDetail("type", "invalid")]);
        }

        var severity = NullIfEmpty(query["severity"].ToString());
        if (severity is not null && !Severities.Contains(severity, StringComparer.Ordinal))
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"severity must be one of {string.Join(", ", Severities)}"),
                [new ErrorDetail("severity", "invalid")]);
        }

        if (!TryDate(query["from"].ToString(), out var from))
        {
            return Error.Invalid("from must be an ISO 8601 timestamp", [new ErrorDetail("from", "invalid")]);
        }

        if (!TryDate(query["to"].ToString(), out var to))
        {
            return Error.Invalid("to must be an ISO 8601 timestamp", [new ErrorDetail("to", "invalid")]);
        }

        filter = new SecurityEventFilter(projectId, type, severity, from, to);

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
