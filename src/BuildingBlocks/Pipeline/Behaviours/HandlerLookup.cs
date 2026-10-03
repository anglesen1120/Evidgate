using System.Reflection;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.BuildingBlocks.Pipeline.Behaviours;

/// <summary>
/// Resolves the permission the handler of a closed (message, response) pair
/// declares, through the same handler interface Mediator's generated
/// <c>AddMediator</c> registers. The interface follows the message's own marker
/// (<c>IBaseQuery</c>/<c>IBaseCommand</c>/<c>IBaseRequest</c>) rather than a probe
/// of all three: each handler interface constrains its health message
/// (<c>ICommandHandler&lt;TCommand,TResponse&gt;</c> demands <c>ICommand&lt;TResponse&gt;</c>),
/// so closing the wrong one throws instead of answering "no handler". No assembly
/// scan — the registrations are already there.
/// </summary>
internal static class HandlerLookup
{
    /// <summary>
    /// The permission the handler of <paramref name="messageType"/> declares, or
    /// null when no handler resolves or none is declared (both denied by the C2
    /// authorization stage).
    /// </summary>
    internal static string? PermissionOf(IServiceProvider services, Type messageType, Type responseType) =>
        HandlerTypeOf(services, messageType, responseType)
            ?.GetCustomAttribute<PermissionAttribute>(inherit: true)?.Permission;

    /// <summary>
    /// Whether the handler of <paramref name="messageType"/> is one of the
    /// tenant-creating handlers marked <see cref="AuthenticatedMemberAttribute"/>
    /// (02.1.1 <c>POST /v1/orgs</c>). False when no handler resolves.
    /// </summary>
    internal static bool IsAuthenticatedMemberOnly(IServiceProvider services, Type messageType, Type responseType) =>
        HandlerTypeOf(services, messageType, responseType)
            ?.GetCustomAttribute<AuthenticatedMemberAttribute>(inherit: false) is not null;

    private static Type? HandlerTypeOf(IServiceProvider services, Type messageType, Type responseType)
    {
        ArgumentNullException.ThrowIfNull(services);

        var open = HandlerInterfaceFor(messageType);
        if (open is null)
        {
            return null;
        }

        var face = open.MakeGenericType(messageType, responseType);

        return services.GetService(face)?.GetType();
    }

    private static Type? HandlerInterfaceFor(Type messageType) =>
        typeof(IBaseQuery).IsAssignableFrom(messageType) ? typeof(IQueryHandler<,>)
        : typeof(IBaseCommand).IsAssignableFrom(messageType) ? typeof(ICommandHandler<,>)
        : typeof(IBaseRequest).IsAssignableFrom(messageType) ? typeof(IRequestHandler<,>)
        : null;
}
