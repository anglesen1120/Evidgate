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
            // Without a trusted proxy the framework's own switch (Development only) keeps its defaults.
            if (!trusted.Value.HasTrust)
            {
                return;
            }

            // The client address and the scheme are taken, never the host; a proxy that terminates TLS is what
            // makes Request.IsHttps true for the Secure cookies. Both come from the same listed hops only. Header
            // symmetry stays off (the framework default), so a proxy that sets one header and not the other is fine.
            // The framework ships loopback as trusted, which would let a local process set either value.
            middleware.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
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

    /// <summary>Adds the middleware when a proxy is trusted; a direct Api ignores <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> altogether.</summary>
    public static WebApplication UseTrustedForwardedHeaders(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<TrustedProxyOptions>>().Value.HasTrust)
        {
            app.UseForwardedHeaders();
        }

        return app;
    }
}
