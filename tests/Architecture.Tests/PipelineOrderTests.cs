using Architecture.Tests.Rules;
using Evidgate.BuildingBlocks.Pipeline;
using Evidgate.BuildingBlocks.Pipeline.Behaviours;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// AC4 (T49): the C2 pipeline order is a build assertion, not a comment. The
/// registration is what Mediator's generated mediator consumes —
/// <c>IEnumerable&lt;IPipelineBehavior&lt;TMessage,TResponse&gt;&gt;</c> from DI,
/// in registration order — so asserting the registered open generics asserts the
/// order every dispatch runs in (00 §5.5 C2).
/// </summary>
public class PipelineOrderTests
{
    /// <summary>
    /// The pinned 00 §5.5 C2 order: telemetry → validation → tenant transaction →
    /// authorization → idempotency, then the handler, outbox flush and commit that
    /// close the chain. Hard-coded on purpose — a deviation from the contract must
    /// fail this test, so it can never read the order back from the code it guards.
    /// </summary>
    private static readonly Type[] C2 =
    [
        typeof(TelemetryBehaviour<,>),
        typeof(ValidationBehaviour<,>),
        typeof(TenantTransactionBehaviour<,>),
        typeof(AuthorizationBehaviour<,>),
        typeof(IdempotencyBehaviour<,>),
    ];

    [Fact]
    public void Pipeline_Order_Is_C2()
    {
        var services = new ServiceCollection();

        services.AddEvidgatePipelineBehaviours();

        var registered = services
            .Where(descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<,>))
            .Select(descriptor => descriptor.ImplementationType)
            .ToList();

        registered.ShouldBe(C2, ignoreOrder: false);
    }

    /// <summary>
    /// The registration reads <see cref="PipelineBehaviourOrder.C2"/>, so that
    /// array — the one thing the code and the contract both name — must equal the
    /// pinned order as well. Reordering it without touching the test is the one
    /// deviation <see cref="Pipeline_Order_Is_C2"/> alone could not catch.
    /// </summary>
    [Fact]
    public void Canonical_Order_Matches_The_Contract()
    {
        PipelineBehaviourOrder.C2.ShouldBe(C2, ignoreOrder: false);
    }
}
