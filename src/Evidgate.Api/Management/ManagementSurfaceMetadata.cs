using Microsoft.AspNetCore.Builder;

namespace Evidgate.Api.Management;

/// <summary>
/// Marks an endpoint as part of the management surface. The management listener serves only marked endpoints;
/// the public listener refuses the ones that are not also public (<paramref name="PublicToo"/> is false), so
/// a new operational route is private until its author says otherwise.
/// </summary>
/// <param name="PublicToo">True for the liveness/readiness probes, which keep answering on the public port.</param>
public sealed record ManagementSurfaceMetadata(bool PublicToo)
{
    /// <summary>Marks the route as management-only (for example <c>/metrics</c>).</summary>
    /// <typeparam name="TBuilder">The endpoint builder type.</typeparam>
    /// <param name="builder">The route builder.</param>
    /// <returns>The builder.</returns>
    public static TBuilder AsManagementOnly<TBuilder>(TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new ManagementSurfaceMetadata(false));

    /// <summary>Marks the route as served on the management listener and on the public one (the probes).</summary>
    /// <typeparam name="TBuilder">The endpoint builder type.</typeparam>
    /// <param name="builder">The route builder.</param>
    /// <returns>The builder.</returns>
    public static TBuilder AsProbe<TBuilder>(TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new ManagementSurfaceMetadata(true));
}
