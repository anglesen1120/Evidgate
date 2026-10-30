using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Mediator;

namespace Architecture.Tests.Rules;

/// <summary>
/// The message-shape contract of the Mediator pipeline, split from
/// <see cref="ArchitectureRules"/> because it is about handlers rather than the module
/// graph. Every rule takes its types explicitly, so a test proves it on synthetic
/// violations and then runs the same function over the real solution.
/// <para>
/// Notification handlers are not request-scoped pipeline work (integration events travel
/// through the outbox), but they are still scanned for a gate: their messages never run
/// through <c>IPipelineBehavior</c>, so a gate on one could never be enforced.
/// </para>
/// </summary>
public static class PipelineHandlerRules
{
    /// <summary>Mediator's request-handler interface names, without their generic arity.</summary>
    public static readonly IReadOnlySet<string> HandlerInterfaceNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "ICommandHandler",
        "IQueryHandler",
        "IRequestHandler",
        "IStreamCommandHandler",
        "IStreamQueryHandler",
        "IStreamRequestHandler",
    };

    /// <summary>Mediator handler interfaces whose messages never traverse <c>IPipelineBehavior</c>.</summary>
    public static readonly IReadOnlySet<string> PipelineBypassingInterfaceNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "IStreamCommandHandler",
        "IStreamQueryHandler",
        "IStreamRequestHandler",
        "INotificationHandler",
    };

    /// <summary>True for a concrete class implementing one of Mediator's handler interfaces.</summary>
    public static bool IsRequestHandler(Type type) =>
        !type.IsAbstract
        && !type.IsInterface
        && type.GetInterfaces().Any(face =>
            face.Namespace == "Mediator"
            && HandlerInterfaceNames.Contains(face.Name.Split('`')[0]));

    /// <summary>
    /// Every response type the handler's request-handler interfaces answer; a
    /// one-argument command or request handler answers <see cref="Unit"/>. Stream
    /// handlers answer an <see cref="IAsyncEnumerable{T}"/> and are out of scope.
    /// </summary>
    public static IEnumerable<Type> ResponsesOf(Type type)
    {
        foreach (var face in type.GetInterfaces())
        {
            if (!face.IsGenericType)
            {
                continue;
            }

            var definition = face.GetGenericTypeDefinition();
            if (definition == typeof(ICommandHandler<>) || definition == typeof(IRequestHandler<>))
            {
                yield return typeof(Unit);
            }
            else if (definition == typeof(ICommandHandler<,>)
                || definition == typeof(IQueryHandler<,>)
                || definition == typeof(IRequestHandler<,>))
            {
                yield return face.GetGenericArguments()[1];
            }
        }
    }

    /// <summary>True when the handler carries a gate, including an inherited permission.</summary>
    public static bool NamesAuthGate(Type type) =>
        type.GetCustomAttributes(typeof(PermissionAttribute), inherit: true).Length > 0
        || type.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType.FullName == typeof(AuthenticatedMemberAttribute).FullName
            || attribute.AttributeType.FullName == typeof(CredentialAuthenticatedAttribute).FullName);

    /// <summary>
    /// True when the handler's contract is dispatched outside <c>IPipelineBehavior</c>:
    /// streams are consumed incrementally and notifications are published to every
    /// handler, so neither traverses the request pipeline the stages live in. An abstract
    /// base is never dispatched, so it is not reported here; its concrete subclasses are.
    /// </summary>
    public static bool BypassesRequestPipeline(Type type) =>
        !type.IsAbstract
        && !type.IsInterface
        && type.GetInterfaces().Any(face =>
            face.Namespace == "Mediator"
            && PipelineBypassingInterfaceNames.Contains(face.Name.Split('`')[0]));

    /// <summary>
    /// Every request handler answers <c>Result</c>/<c>Result&lt;T&gt;</c>: the pipeline's
    /// tenant-scoped stages only run for that contract, so any other response would run
    /// outside the tenant scope and outside every check those stages perform.
    /// </summary>
    public static IReadOnlyList<string> EveryPipelineHandlerReturnsResult(IEnumerable<Type> types)
    {
        var violations = new List<string>();

        foreach (var type in Ordered(types))
        {
            if (!IsRequestHandler(type))
            {
                continue;
            }

            violations.AddRange(NonResultResponsesOf(type).Select(response =>
                $"{type.FullName} handles a Mediator request but returns {response.Name}: a pipeline handler " +
                "must answer Result/Result<T> so the tenant, authorization and idempotency stages run for it"));
        }

        return violations.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Every gated handler must run through the request pipeline and answer
    /// <c>Result</c>/<c>Result&lt;T&gt;</c>. Stream and notification handlers bypass that
    /// pipeline entirely, so a gate on one is refused whatever its response type;
    /// handlers with no gate are out of scope here.
    /// </summary>
    public static IReadOnlyList<string> EveryGatedHandlerReturnsResult(IEnumerable<Type> types)
    {
        var violations = new List<string>();

        foreach (var type in Ordered(types))
        {
            if (!NamesAuthGate(type))
            {
                continue;
            }

            if (BypassesRequestPipeline(type))
            {
                // No stage would see the message, so the gate this handler names is never enforced.
                violations.Add($"{type.FullName} names an authorization gate but its message never runs through " +
                    "IPipelineBehavior: a stream or notification handler cannot enforce pipeline gates");
                continue;
            }

            if (!IsRequestHandler(type))
            {
                continue;
            }

            violations.AddRange(NonResultResponsesOf(type).Select(response =>
                $"{type.FullName} names an authorization gate but returns {response.Name}: a gated handler " +
                "must answer Result/Result<T> so the pipeline can refuse it"));
        }

        return violations.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<Type> NonResultResponsesOf(Type type) =>
        ResponsesOf(type).Where(response => !typeof(Result).IsAssignableFrom(response));

    private static IEnumerable<Type> Ordered(IEnumerable<Type> types) =>
        types.Where(type => type is not null).OrderBy(type => type.FullName, StringComparer.Ordinal);
}
