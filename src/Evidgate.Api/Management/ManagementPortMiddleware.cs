using Evidgate.Api.Middleware;
using Microsoft.Extensions.Options;

namespace Evidgate.Api.Management;

/// <summary>
/// Keeps the two listeners apart: the management listener answers only the management paths, and those paths are
/// answered 404 everywhere else. The decision uses the local port of the connection, which a client cannot choose
/// through a header, so a reverse proxy in front of the public port cannot expose <c>/metrics</c> by routing it.
/// </summary>
public sealed class ManagementPortMiddleware(RequestDelegate next, IOptions<ManagementOptions> options)
{
    /// <summary>Items key set to true on a request that arrived on the management listener.</summary>
    public const string ItemsKey = "evidgate.management_request";

    /// <summary>True when the request arrived on the management listener.</summary>
    public static bool IsManagement(HttpContext context) =>
        context.Items.TryGetValue(ItemsKey, out var flag) && flag is true;

    /// <inheritdoc cref="RequestDelegate" />
    public Task Invoke(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var onManagementPort = options.Value.ManagementPort is { } port && context.Connection.LocalPort == port;

        // Routing has already matched the endpoint, so the decision follows what will run, not the spelling of
        // the path (a trailing slash or a case variant reaches the same endpoint).
        var surface = context.GetEndpoint()?.Metadata.GetMetadata<ManagementSurfaceMetadata>();

        if (onManagementPort)
        {
            context.Items[ItemsKey] = true;
            if (surface is null)
            {
                return Refuse(context);
            }
        }
        else if (surface is { PublicToo: false })
        {
            // Also taken when no management port is configured: the surface is off, not public.
            return Refuse(context);
        }

        return next(context);
    }

    private static Task Refuse(HttpContext context) =>
        ErrorEnvelopeWriter.WriteAsync(
            context,
            StatusCodes.Status404NotFound,
            ErrorEnvelopeWriter.CodeForStatus(StatusCodes.Status404NotFound),
            ErrorEnvelopeWriter.MessageForStatus(StatusCodes.Status404NotFound));
}
