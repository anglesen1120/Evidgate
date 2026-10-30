using System.Collections.Concurrent;
using System.Reflection;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.BuildingBlocks.Pipeline.Behaviours;

/// <summary>
/// Resolves handler attributes through Mediator's registered interfaces without an assembly scan.
/// The message's own marker interface is tried first, then the other two contracts: a message that
/// implements more than one Mediator marker is found through the contract it was registered under,
/// while a single-marker message cannot satisfy another contract and stays unresolved as before.
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

    /// <summary>Whether the handler authenticates a credential before a tenant membership exists.</summary>
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

    /// <summary>
    /// Caches reflected gates for resolved handlers to avoid per-request attribute allocations.
    /// Unresolved pairs are re-checked because a later service provider may register the handler.
    /// </summary>
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

        return ResolveHandlerType(services, open, messageType, responseType)
            ?? (open != typeof(IQueryHandler<,>) ? ResolveHandlerType(services, typeof(IQueryHandler<,>), messageType, responseType) : null)
            ?? (open != typeof(ICommandHandler<,>) ? ResolveHandlerType(services, typeof(ICommandHandler<,>), messageType, responseType) : null)
            ?? (open != typeof(IRequestHandler<,>) ? ResolveHandlerType(services, typeof(IRequestHandler<,>), messageType, responseType) : null);
    }

    private static Type? ResolveHandlerType(IServiceProvider services, Type open, Type messageType, Type responseType)
    {
        Type face;
        try
        {
            face = open.MakeGenericType(messageType, responseType);
        }
        catch (ArgumentException)
        {
            // A handler contract is only a candidate when the message satisfies its generic constraints.
            return null;
        }

        return services.GetService(face)?.GetType();
    }

    private static Type? HandlerInterfaceFor(Type messageType) =>
        typeof(IBaseQuery).IsAssignableFrom(messageType) ? typeof(IQueryHandler<,>)
        : typeof(IBaseCommand).IsAssignableFrom(messageType) ? typeof(ICommandHandler<,>)
        : typeof(IBaseRequest).IsAssignableFrom(messageType) ? typeof(IRequestHandler<,>)
        : null;
}
