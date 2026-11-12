using Microsoft.AspNetCore.Builder;

namespace Evidgate.BuildingBlocks.Contracts.Validation;

/// <summary>
/// The per-route request-body limit the host applies before a handler reads a byte (00 §5.7 P8). A route declares
/// one of three things: a fixed byte count, a contract schema whose <c>x-max-bytes</c> is the count (so the contract
/// stays the only place the number is written), or that its handler owns the refusal because the cap is
/// configurable and the 413 carries route-specific detail. A route whose handler already refuses an over-cap body
/// with its own error code and counters sets <paramref name="HandlerRefusesFirst"/>: the host then only backstops it
/// one byte past the cap and never answers before the handler does.
/// </summary>
/// <param name="Bytes">The fixed limit, or null.</param>
/// <param name="SchemaKey">The contract schema whose <c>x-max-bytes</c> is the limit, or null.</param>
/// <param name="HandlerOwned">True when the handler counts the body itself and the host must not pre-empt it.</param>
/// <param name="HandlerRefusesFirst">True when the handler refuses over-cap bodies itself and the host is only the backstop.</param>
public sealed record RequestBodyLimitMetadata(long? Bytes, string? SchemaKey, bool HandlerOwned, bool HandlerRefusesFirst = false)
{
    /// <summary>The effective limit for the route, or null when none is declared or the handler owns it.</summary>
    /// <param name="schemas">The loaded contract schemas.</param>
    /// <returns>The byte limit.</returns>
    public long? Resolve(ContractSchemaSet schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        if (HandlerOwned)
        {
            return null;
        }

        if (Bytes is { } bytes)
        {
            return bytes;
        }

        return SchemaKey is null
            ? null
            : schemas.MaxBytes(SchemaKey)
                ?? throw new InvalidOperationException($"contract schema '{SchemaKey}' declares no x-max-bytes");
    }
}

/// <summary>Route-builder conventions that attach a <see cref="RequestBodyLimitMetadata"/>.</summary>
public static class RequestBodyLimitEndpointExtensions
{
    /// <summary>Limits the route's body to <paramref name="bytes"/>, refused 413 before the body is read.</summary>
    /// <typeparam name="TBuilder">The endpoint builder type.</typeparam>
    /// <param name="builder">The route builder.</param>
    /// <param name="bytes">The limit in bytes; positive.</param>
    /// <param name="handlerRefusesFirst">True when the handler's own refusal must answer first (the host backstops at <paramref name="bytes"/> + 1).</param>
    /// <returns>The builder.</returns>
    public static TBuilder WithBodyLimit<TBuilder>(this TBuilder builder, long bytes, bool handlerRefusesFirst = false)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        return builder.WithMetadata(new RequestBodyLimitMetadata(bytes, null, false, handlerRefusesFirst));
    }

    /// <summary>Limits the route's body to the <c>x-max-bytes</c> of <paramref name="schemaKey"/>.</summary>
    /// <typeparam name="TBuilder">The endpoint builder type.</typeparam>
    /// <param name="builder">The route builder.</param>
    /// <param name="schemaKey">A key of <see cref="ContractSchemaKeys"/> whose schema declares a transport cap.</param>
    /// <param name="handlerRefusesFirst">True when the handler's own refusal must answer first (the host backstops at the cap + 1).</param>
    /// <returns>The builder.</returns>
    public static TBuilder WithContractBodyLimit<TBuilder>(this TBuilder builder, string schemaKey, bool handlerRefusesFirst = false)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaKey);

        return builder.WithMetadata(new RequestBodyLimitMetadata(null, schemaKey, false, handlerRefusesFirst));
    }

    /// <summary>
    /// Leaves the refusal to the handler: its cap is configurable and its 413 names the cap. The handler still
    /// counts the body while it reads, so nothing is buffered past the cap.
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint builder type.</typeparam>
    /// <param name="builder">The route builder.</param>
    /// <returns>The builder.</returns>
    public static TBuilder WithHandlerOwnedBodyLimit<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithMetadata(new RequestBodyLimitMetadata(null, null, true));
    }
}
