using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Evidgate.Api.Forwarding;

/// <summary>Registers and applies the framework's forwarded-headers middleware from <see cref="TrustedProxyOptions"/>.</summary>
public static class ForwardedHeadersExtensions
{
    /// <summary>Binds and validates the trusted-proxy options and derives the middleware options from them.</summary>
    public static IServiceCollection AddTrustedForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TrustedProxyOptions>().Bind(configuration.GetSection(TrustedProxyOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<TrustedProxyOptions>, TrustedProxyOptionsValidator>();

        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<TrustedProxyOptions>>((middleware, trusted) =>
        {
            // Only the client address is taken: the scheme and host stay the socket's own. The framework
            // ships loopback as trusted, which would let a local process set the client address.
            middleware.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            middleware.KnownProxies.Clear();
            middleware.KnownIPNetworks.Clear();

            foreach (var proxy in trusted.Value.ParseProxies())
            {
                middleware.KnownProxies.Add(proxy);
            }

            foreach (var network in trusted.Value.ParseNetworks())
            {
                middleware.KnownIPNetworks.Add(network);
            }

            // No hop cap: trust is decided by the lists alone, so the middleware walks the header from the
            // right while the hop is a listed proxy and stops at the first one that is not. That hop is the
            // client; anything to its left is client-supplied and ignored.
            middleware.ForwardLimit = null;
        });

        return services;
    }

    /// <summary>Adds the middleware when a proxy is trusted; a direct Api ignores <c>X-Forwarded-For</c> altogether.</summary>
    public static WebApplication UseTrustedForwardedHeaders(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<TrustedProxyOptions>>().Value.HasTrust)
        {
            app.UseForwardedHeaders();
        }

        return app;
    }
}
