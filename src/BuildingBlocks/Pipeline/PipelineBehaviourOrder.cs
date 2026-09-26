using Evidgate.BuildingBlocks.Pipeline.Behaviours;

namespace Evidgate.BuildingBlocks.Pipeline;

/// <summary>
/// The one canonical statement of the 00 §5.5 C2 order —
/// telemetry → validation → tenant transaction → authorization → idempotency →
/// handler → outbox flush → commit. Registration reads this array and the
/// architecture test asserts the produced DI descriptors equal it, so a reordered
/// or dropped stage is a build failure, not a comment (AC4, T49).
/// <para>
/// The last two C2 steps live inside <see cref="TenantTransactionBehaviour{TMessage,TResponse}"/>'s
/// flow: the outbox row is written by the handler through <c>IOutboxWriter</c> on
/// the scope's own connection (01.5.1-T2.2) and becomes durable — or disappears —
/// at the behaviour's commit boundary; there is no separate buffer to flush.
/// </para>
/// </summary>
public static class PipelineBehaviourOrder
{
    /// <summary>The C2 stage types in their fixed outer→inner order, before the handler.</summary>
    public static readonly IReadOnlyList<Type> C2 =
    [
        typeof(TelemetryBehaviour<,>),
        typeof(ValidationBehaviour<,>),
        typeof(TenantTransactionBehaviour<,>),
        typeof(AuthorizationBehaviour<,>),
        typeof(IdempotencyBehaviour<,>),
    ];
}
