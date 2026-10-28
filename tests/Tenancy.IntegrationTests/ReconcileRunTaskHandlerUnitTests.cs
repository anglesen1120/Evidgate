using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.2-T2.1's handler contract without a database or a queue: the <c>orchestrator.run.reconcile</c> row is
/// tenant-bound, so a claim that carries no tenant scope is refused before any reconcile runs — the consumer
/// dead-letters it as <c>missing_tenant</c> instead of reconciling a run it cannot scope (00 §5.5 C3). Pure unit test: no
/// container, so it stays on the non-Docker side of <c>verify.sh</c>.
/// </summary>
public sealed class ReconcileRunTaskHandlerUnitTests
{
    [Fact]
    public void Handler_Is_TenantBound_For_The_Reconcile_Type()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var handler = Handler(provider.GetRequiredService<IServiceScopeFactory>());

        handler.TaskType.ShouldBe(RunReconcileContract.OutboxTaskType);
        handler.RequiresTenant.ShouldBeTrue("a reconcile reads the run of one tenant and must never run unscoped");
    }

    /// <summary>
    /// <see cref="ReconcileRunTaskHandler.HandleAsync"/> throws before it creates an attempt scope or resolves a
    /// reconciler when the claimed row has no tenant scope, so nothing is read: the message names the dead-letter verdict.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WithoutTenantScope_ThrowsBeforeReconciling()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var handler = Handler(provider.GetRequiredService<IServiceScopeFactory>());
        var payload = new RunReconcilePayload(Guid.CreateVersion7(), "shard-1", 1, 1);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await handler.HandleAsync(payload, scope: null, TestContext.Current.CancellationToken));

        error.Message.ShouldContain("missing_tenant", Case.Insensitive);
    }

    /// <summary>The handler under test: the host's scope factory (real, but never reached by the guard) and a no-op logger.</summary>
    private static ReconcileRunTaskHandler Handler(IServiceScopeFactory scopeFactory) =>
        new(scopeFactory, NullLogger<ReconcileRunTaskHandler>.Instance);
}
