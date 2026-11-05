using Evidgate.Modules.Orchestrator.Application.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The kill handler's contract without a database or a queue: the row is tenant-bound, so a claim that carries no tenant
/// scope is refused before any statement runs — the consumer dead-letters it as <c>missing_tenant</c> instead of moving a
/// request it cannot scope — and its type is the one the producer wrote. Pure unit test: no container.
/// </summary>
public sealed class CancelKillTaskHandlerUnitTests
{
    [Fact]
    public void Handler_Is_TenantBound_For_The_Kill_Type()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var handler = Handler(provider.GetRequiredService<IServiceScopeFactory>());

        handler.TaskType.ShouldBe(CancelKillContract.TaskType);
        handler.RequiresTenant.ShouldBeTrue("a kill moves one tenant's request row and must never run unscoped");
    }

    /// <summary>
    /// <see cref="CancelKillTaskHandler.HandleAsync"/> throws before it opens an attempt scope when the claimed row has no
    /// tenant scope, so no request row is read: the message names the dead-letter verdict.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WithoutTenantScope_ThrowsBeforeKilling()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var handler = Handler(provider.GetRequiredService<IServiceScopeFactory>());
        var payload = new CancelKillPayload(Guid.CreateVersion7(), Guid.CreateVersion7());

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await handler.HandleAsync(payload, scope: null, TestContext.Current.CancellationToken));

        error.Message.ShouldContain("missing_tenant", Case.Insensitive);
    }

    /// <summary>The handler under test: a real scope factory that the guard never reaches, and a no-op logger.</summary>
    private static CancelKillTaskHandler Handler(IServiceScopeFactory scopeFactory) =>
        new(scopeFactory, NullLogger<CancelKillTaskHandler>.Instance);
}
