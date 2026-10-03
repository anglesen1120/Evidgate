using Evidgate.Api.Endpoints;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;

namespace Evidgate.Api.Tenancy;

/// <summary>
/// The API entry point of 02.6.2-T1.1 (00 §5.5 C2): authenticates the caller, resolves
/// the tenant per the §5 matrix and publishes principal and tenant for the Mediator
/// pipeline, whose tenant-transaction behaviour opens the scope next. It is an endpoint
/// filter, not middleware, because the org usually comes from the route (<c>{o}</c>),
/// which only exists after routing.
/// </summary>
/// <remarks>
/// Added to a route group before <c>ResultEndpointFilter</c>. An unauthenticated request
/// is passed through untouched: with no principal and no tenant published, the pipeline
/// fails closed (401). A tenant already published for the request (a test host's) is
/// left alone — nothing in a deployed host publishes before this filter.
/// </remarks>
public sealed class TenantResolutionFilter(
    IEnumerable<ICallerAuthenticator> authenticators,
    TenantResolver resolver,
    IRequestTenantContextAccessor tenants,
    IRequestTenantContextSetter tenantSetter,
    IRequestPrincipalSetter principalSetter) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (tenants.Current is not null)
        {
            return await next(context).ConfigureAwait(false);
        }

        var http = context.HttpContext;
        var caller = await AuthenticateAsync(http).ConfigureAwait(false);

        if (caller is null)
        {
            return await next(context).ConfigureAwait(false);
        }

        using var principalScope = principalSetter.Publish(caller.Principal);

        if (http.GetEndpoint()?.Metadata.GetMetadata<TenantlessEndpointMetadata>() is not null)
        {
            return await next(context).ConfigureAwait(false);
        }

        if (RequestedOrg(http) is not { } requested)
        {
            // An org named but not a uuid: leave it to the framework's own 400.
            return await next(context).ConfigureAwait(false);
        }

        var resolved = await resolver.ResolveAsync(caller, requested.Org, http.RequestAborted).ConfigureAwait(false);

        if (!resolved.IsSuccess)
        {
            return resolved.ToHttpResult(http);
        }

        var tenant = (ResolvedTenant)resolved.SuccessValue!;
        using var tenantScope = tenantSetter.Publish(tenant.OrgId, tenant.ProjectId);

        return await next(context).ConfigureAwait(false);
    }

    private async ValueTask<AuthenticatedCaller?> AuthenticateAsync(HttpContext http)
    {
        foreach (var authenticator in authenticators)
        {
            if (await authenticator.AuthenticateAsync(http, http.RequestAborted).ConfigureAwait(false) is { } caller)
            {
                return caller;
            }
        }

        return null;
    }

    /// <summary>
    /// The org the request names: the route's <c>{o}</c>, else the org header.
    /// Returns null (not "no org") when a value is present but is not a uuid.
    /// </summary>
    private static (Guid? Org, bool Named)? RequestedOrg(HttpContext http)
    {
        if (http.Request.RouteValues.TryGetValue("o", out var route))
        {
            return Guid.TryParse(route?.ToString(), out var routeOrg) ? (routeOrg, true) : null;
        }

        var header = http.Request.Headers[TenantResolver.OrgHeader].ToString();

        if (string.IsNullOrEmpty(header))
        {
            return (null, false);
        }

        return Guid.TryParse(header, out var headerOrg) ? (headerOrg, true) : null;
    }
}
