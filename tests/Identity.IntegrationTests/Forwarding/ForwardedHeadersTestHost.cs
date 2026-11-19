using System.Net;
using Evidgate.Modules.Identity.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Identity.IntegrationTests.Forwarding;

/// <summary>
/// Reconfigures a booted Api host for the forwarded-headers cases: the trust stance the case needs and the
/// sender's socket address, which TestServer leaves null so every request of one host would share one key.
/// </summary>
/// <remarks>
/// The socket filter is an <see cref="IStartupFilter"/>, so its middleware joins the pipeline before
/// <c>UseTrustedForwardedHeaders</c>: the forwarded-headers middleware must see the sender's address already
/// in place, exactly as Kestrel would have set it from the accepted connection.
/// </remarks>
internal static class ForwardedHeadersTestHost
{
    /// <summary>The request header the socket-address filter reads the sender's address from.</summary>
    internal const string SocketAddressHeader = "X-Test-Socket-Ip";

    /// <summary>The header a proxy appends the client's address to.</summary>
    internal const string ForwardedForHeader = "X-Forwarded-For";

    /// <summary>The header a proxy that terminates TLS sets to <c>https</c>.</summary>
    internal const string ForwardedProtoHeader = "X-Forwarded-Proto";

    /// <summary>
    /// States the case's <c>EVIDGATE:ForwardedHeaders</c> trust and sets the sender address from
    /// <see cref="SocketAddressHeader"/>. Both keys are always written, so no ambient stance reaches a case.
    /// </summary>
    /// <param name="builder">The host builder of the variant under test.</param>
    /// <param name="proxies">The <c>KnownProxies</c> value; blank states no proxy, <c>none</c> a direct Api.</param>
    /// <param name="networks">The <c>KnownNetworks</c> value; blank states no network.</param>
    internal static void Configure(IWebHostBuilder builder, string proxies = "", string networks = "")
    {
        ArgumentNullException.ThrowIfNull(builder);

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:ForwardedHeaders:KnownProxies"] = proxies,
            ["EVIDGATE:ForwardedHeaders:KnownNetworks"] = networks,
        };

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        builder.ConfigureTestServices(services => services.AddTransient<IStartupFilter, SocketAddressStartupFilter>());
    }

    /// <summary>The client these cases use: TLS as the deployment is, no redirects and no cookie jar.</summary>
    /// <param name="host">The variant host.</param>
    /// <param name="baseAddress">The address the client connects to; plain HTTP for the scheme cases.</param>
    internal static HttpClient CreateClient(WebApplicationFactory<Program> host, string baseAddress = "https://localhost")
    {
        ArgumentNullException.ThrowIfNull(host);

        return host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(baseAddress),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
    }

    /// <summary>The login budget the limited routes spend, read from the booted host rather than assumed.</summary>
    /// <param name="host">The variant host.</param>
    internal static int Burst(WebApplicationFactory<Program> host)
    {
        ArgumentNullException.ThrowIfNull(host);

        return host.Services.GetRequiredService<IOptions<RateLimitOptions>>().Value.LoginLimit;
    }

    /// <summary>Places the sender's socket address before any middleware of the Api pipeline runs.</summary>
    private sealed class SocketAddressStartupFilter : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            ArgumentNullException.ThrowIfNull(next);

            return app =>
            {
                app.Use(nextDelegate => context =>
                {
                    if (context.Request.Headers.TryGetValue(SocketAddressHeader, out var header)
                        && IPAddress.TryParse(header.ToString(), out var address))
                    {
                        context.Connection.RemoteIpAddress = address;
                    }

                    return nextDelegate(context);
                });

                next(app);
            };
        }
    }
}
