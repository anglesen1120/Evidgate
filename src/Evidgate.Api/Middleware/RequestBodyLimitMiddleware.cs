using Evidgate.BuildingBlocks.Contracts.Validation;
using Microsoft.AspNetCore.Http.Features;

namespace Evidgate.Api.Middleware;

/// <summary>
/// Applies the route's body limit before any handler reads a byte (00 §5.7 P8). The limit is the route's
/// <see cref="RequestBodyLimitMetadata"/> or, without one, the host default Kestrel was configured with. It is
/// pushed into Kestrel's own feature, so a chunked or understated body is refused at the limit while it is read,
/// and a declared <c>Content-Length</c> over it is answered 413 here without touching the body. A handler that
/// counts the body itself stays in force as a second line.
/// </summary>
public sealed class RequestBodyLimitMiddleware(RequestDelegate next)
{
    /// <summary>The default body limit for a route that declares none: every body route below it counts its own cap.</summary>
    public const long DefaultBodyBytes = 1024 * 1024;

    /// <summary>
    /// How far past its cap a handler-refusing route may read before Kestrel stops it: more than one read
    /// chunk of any such handler and than the chunk framing Kestrel counts, so the handler's own 413 comes first.
    /// </summary>
    public const long HandlerBackstopSlackBytes = 64 * 1024;

    /// <inheritdoc cref="RequestDelegate" />
    public Task Invoke(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        long? limit = feature?.MaxRequestBodySize;
        var handlerRefusesFirst = false;

        if (context.GetEndpoint()?.Metadata.GetMetadata<RequestBodyLimitMetadata>() is { } metadata)
        {
            if (metadata.HandlerOwned)
            {
                return next(context);
            }

            limit = metadata.Resolve(context.RequestServices.GetRequiredService<ContractSchemaSet>());
            handlerRefusesFirst = metadata.HandlerRefusesFirst;
        }

        if (limit is not { } bytes)
        {
            return next(context);
        }

        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = handlerRefusesFirst ? bytes + HandlerBackstopSlackBytes : bytes;
        }

        return !handlerRefusesFirst && context.Request.ContentLength > bytes
            ? ErrorEnvelopeWriter.WriteAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                ErrorEnvelopeWriter.CodeForStatus(StatusCodes.Status413PayloadTooLarge),
                ErrorEnvelopeWriter.MessageForStatus(StatusCodes.Status413PayloadTooLarge))
            : next(context);
    }
}
