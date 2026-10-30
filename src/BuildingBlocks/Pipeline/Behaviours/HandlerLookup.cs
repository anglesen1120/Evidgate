using System.Collections.Concurrent;
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
        GateOf(services, messageType, responseType).Permission;

    /// <summary>
    /// Whether the handler of <paramref name="messageType"/> is one of the
    /// tenant-creating handlers marked <see cref="AuthenticatedMemberAttribute"/>
    /// (02.1.1 <c>POST /v1/orgs</c>). False when no handler resolves.
    /// </summary>
    internal static bool IsAuthenticatedMemberOnly(IServiceProvider services, Type messageType, Type responseType) =>
        GateOf(services, messageType, responseType).AuthenticatedMember;

    /// <summary>
    /// The gate a (message, response) pair's handler declares, read by reflection once
    /// per pair and cached (02.1.1 performance review: the attribute lookup allocated on
    /// every request). Handler types are fixed by Mediator's generated registrations; a
    /// pair whose handler did not resolve is not cached, so it is re-checked next time.
    /// </summary>
    internal static bool IsCredentialAuthenticated(IServiceProvider services, Type messageType, Type responseType) =>
        GateOf(services, messageType, responseType).CredentialAuthenticated;

    internal static string? AuditActionOf(IServiceProvider services, Type messageType, Type responseType) =>
        GateOf(services, messageType, responseType).AuditAction;

    /// <summary>
    /// Whether the handler of the pair names one of the authorization gates
    /// (<see cref="PermissionAttribute"/>, <see cref="AuthenticatedMemberAttribute"/>,
    /// <see cref="CredentialAuthenticatedAttribute"/>). A pair with no handler names
    /// none: there is nothing to check and nothing to deny.
    /// </summary>
    internal static bool CarriesAuthGate(IServiceProvider services, Type messageType, Type responseType)
    {
        var gate = GateOf(services, messageType, responseType);
        return gate.Permission is not null || gate.AuthenticatedMember || gate.CredentialAuthenticated;
    }

    /// <summary>
    /// Refuses a gated message whose response cannot carry a Result: the stages that
    /// enforce the gate answer through that contract, so passing the message on would
    /// run its handler unchecked. A message whose handler names no gate still passes
    /// through, because no stage would have checked it either way.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handler names a gate and the response is not a Result.</exception>
    internal static void EnsureAuthGateHasResultContract(IServiceProvider services, Type messageType, Type responseType)
    {
        if (!CarriesAuthGate(services, messageType, responseType))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{messageType} is answered by a handler that names an authorization gate but answers {responseType}: "
            + "a gated pipeline handler must answer Result/Result<T> so the pipeline stages can refuse it");
    }

    private static (string? Permission, bool AuthenticatedMember, string? AuditAction, bool CredentialAuthenticated) GateOf(
        IServiceProvider services,
        Type messageType,
        Type responseType)
    {
        if (Gates.TryGetValue((messageType, responseType), out var cached))
        {
            return cached;
        }

        var handler = HandlerTypeOf(services, messageType, responseType);
        if (handler is null)
        {
            return (null, false, null, false);
        }

        var gate = (
            handler.GetCustomAttribute<PermissionAttribute>(inherit: true)?.Permission,
            handler.GetCustomAttribute<AuthenticatedMemberAttribute>(inherit: false) is not null,
            handler.GetCustomAttribute<AuditedActionAttribute>(inherit: false)?.Action,
            handler.GetCustomAttribute<CredentialAuthenticatedAttribute>(inherit: false) is not null);

        return Gates.GetOrAdd((messageType, responseType), gate);
    }

    private static readonly ConcurrentDictionary<(Type Message, Type Response), (string? Permission, bool AuthenticatedMember, string? AuditAction, bool CredentialAuthenticated)> Gates = new();

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
