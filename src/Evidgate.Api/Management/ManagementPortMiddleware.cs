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

    private static readonly string[] ManagementOnlyPaths = ["/metrics"];

    private static readonly string[] SharedPaths = ["/internal/health/live", "/internal/health/ready"];

    /// <summary>True when the request arrived on the management listener.</summary>
    public static bool IsManagement(HttpContext context) =>
        context.Items.TryGetValue(ItemsKey, out var flag) && flag is true;

    /// <inheritdoc cref="RequestDelegate" />
    public Task Invoke(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var onManagementPort = options.Value.ManagementPort is { } port && context.Connection.LocalPort == port;
        var path = context.Request.Path;

        if (onManagementPort)
        {
            context.Items[ItemsKey] = true;
            if (!Matches(path, ManagementOnlyPaths) && !Matches(path, SharedPaths))
            {
                return Refuse(context);
            }
        }
        else if (Matches(path, ManagementOnlyPaths))
        {
            return Refuse(context);
        }

        // /metrics is also refused when no management port is configured: the surface is off, not public.
        return next(context);
    }

    private static bool Matches(PathString path, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (path.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Task Refuse(HttpContext context) =>
        ErrorEnvelopeWriter.WriteAsync(
            context,
            StatusCodes.Status404NotFound,
            ErrorEnvelopeWriter.CodeForStatus(StatusCodes.Status404NotFound),
            ErrorEnvelopeWriter.MessageForStatus(StatusCodes.Status404NotFound));
}
