using Evidgate.Api.Endpoints;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

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

        // 02.3.2-T1 (D2): a credential the host RECOGNISES and refuses answers its own code — 401
        // session_expired / session_revoked, 403 account_disabled — instead of the generic 401 the pipeline
        // produces for a caller it never resolved. Asked before authentication, because authenticating would
        // only say "no caller" and lose the reason.
        if (await RefusalAsync(http).ConfigureAwait(false) is { } refusal)
        {
            return Result.Failure(refusal).ToHttpResult(http);
        }

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

        if (RequestedOrg(http, allowHeader: caller.Principal.Kind == PrincipalKind.Member) is not { } requested)
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
            if (await authenticator.AuthenticateAsync(new HttpCallerRequest(http), http.RequestAborted).ConfigureAwait(false) is { } caller)
            {
                return caller;
            }
        }

        return null;
    }

    /// <summary>
    /// The typed refusal of the request's credential (02.3.2-T1): the first
    /// <see cref="ICallerRefusalAuthenticator"/> among the registered authenticators that refuses it, or null
    /// when none does. Deliberately the SAME set the host authenticates with, so a substituted authenticator
    /// cannot leave a stale refusal behind.
    /// </summary>
    private async ValueTask<Error?> RefusalAsync(HttpContext http)
    {
        foreach (var authenticator in authenticators)
        {
            if (authenticator is ICallerRefusalAuthenticator refusal
                && await refusal.RefuseAsync(new HttpCallerRequest(http), http.RequestAborted).ConfigureAwait(false) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>
    /// The org the request names: the route's <c>{o}</c>, else the org header.
    /// Returns null (not "no org") when a value is present but is not a uuid.
    /// </summary>
    private static (Guid? Org, bool Named)? RequestedOrg(HttpContext http, bool allowHeader)
    {
        if (http.Request.RouteValues.TryGetValue("o", out var route))
        {
            return Guid.TryParse(route?.ToString(), out var routeOrg) ? (routeOrg, true) : null;
        }

        // Only a member picks an org by header; a service token's org is its claim.
        var header = allowHeader ? http.Request.Headers[TenantResolver.OrgHeader].ToString() : string.Empty;

        if (string.IsNullOrEmpty(header))
        {
            return (null, false);
        }

        return Guid.TryParse(header, out var headerOrg) ? (headerOrg, true) : null;
    }
}

/// <summary>Adapts an <see cref="HttpContext"/> to the host-neutral <see cref="ICallerRequest"/>.</summary>
/// <param name="context">The request.</param>
internal sealed class HttpCallerRequest(HttpContext context) : ICallerRequest
{
    /// <inheritdoc />
    public string? Header(string name)
    {
        var value = context.Request.Headers[name];

        return value.Count == 0 ? null : value[0];
    }
}
