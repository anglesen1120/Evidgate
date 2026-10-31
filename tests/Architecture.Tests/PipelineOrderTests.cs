using Architecture.Tests.Rules;
using Evidgate.BuildingBlocks.Pipeline;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Behaviours;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
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

    [Fact]
    public void Behaviours_Are_Scoped_So_Handler_Lookups_Use_The_Request_Scope()
    {
        var services = new ServiceCollection();

        services.AddEvidgatePipelineBehaviours();

        var behaviours = services
            .Where(descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<,>))
            .ToList();

        behaviours.Count.ShouldBe(PipelineBehaviourOrder.C2.Count);
        behaviours.ShouldAllBe(descriptor => descriptor.Lifetime == ServiceLifetime.Scoped);
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

    /// <summary>
    /// 02.2.1-T2.2 (S2, 00 §5.5 C2 stage 4): a host that composes the pipeline
    /// without the Identity module — which is the only thing that replaces this
    /// registration — keeps a checker that denies every permission, so a missing
    /// module or a forgotten registration fails closed instead of granting. The
    /// module's own evaluator is what the entry points resolve (asserted end to end
    /// by <c>Identity.IntegrationTests</c>' route suites).
    /// </summary>
    [Fact]
    public async Task Pipeline_Without_The_Identity_Module_Fails_Closed()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddEvidgatePipeline();

        using var provider = services.BuildServiceProvider();
        var evaluator = provider.GetRequiredService<IPermissionEvaluator>();

        evaluator.ShouldBeOfType<DenyAllPermissionEvaluator>();

        var decision = await evaluator.EvaluateAsync(
            "role:read",
            RequestPrincipal.Member(Guid.CreateVersion7()),
            new RequestTenantContext(Guid.CreateVersion7(), ProjectId: null),
            TestContext.Current.CancellationToken);

        decision.IsGranted.ShouldBeFalse();
        decision.DenyCode.ShouldBe(ApiErrorCode.Forbidden);
    }
}
