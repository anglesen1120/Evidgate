using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Application;
using Evidgate.Modules.Usage.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Loads under contention and after a refusal: two cards of the same metrics in opposite orders take their advisory
/// locks in one order and both land, and a refused card leaves the caller's unit of work usable. Split out of the suite
/// file to keep each file under the size cap.
/// </summary>
public sealed partial class UsagePriceResolverTests
{
    [Fact]
    public async Task LoadOrg_ConcurrentCardsWithReversedMetricOrder_AllSucceed()
    {
        await using var world = await StartAsync();
        var token = TestContext.Current.CancellationToken;

        // One tenant scope per async flow: the factory refuses two in the same flow, so each load gets its own
        // task, and the rendezvous releases both at the same instant.
        using var arrived = new SemaphoreSlim(0, 2);
        using var go = new SemaphoreSlim(0, 2);

        for (var round = 0; round < 4; round++)
        {
            var start = January.AddDays(2 * round);
            var middle = start.AddDays(1);
            var end = start.AddDays(2);
            var tag = "org-c" + round;

            // Same two metrics, opposite orders, disjoint windows: with card order kept the two statements would
            // wait on each other's advisory locks.
            var forward = CardOfRows(
                tag + "-forward",
                Row("results", "0.0100", start, middle),
                Row("ai_tokens", "0.0200", start, middle));
            var reverse = CardOfRows(
                tag + "-reverse",
                Row("ai_tokens", "0.0300", middle, end),
                Row("results", "0.0400", middle, end));

            async Task<UsagePriceLoadResult> LoadInOwnFlowAsync(string card)
            {
                await using var services = world.Api.Services.CreateAsyncScope();
                var provider = services.ServiceProvider;
                await using var scope = await provider.GetRequiredService<ITenantScopeFactory>()
                    .BeginAsync(world.Org, null, false, token);
                var loader = provider.GetRequiredService<UsagePriceLoader>();

                arrived.Release();
                await go.WaitAsync(token);

                var result = await loader.LoadAsync(UsagePriceScope.Org, Bytes(card), token);
                await scope.CommitAsync(token);
                return result;
            }

            var first = Task.Run(() => LoadInOwnFlowAsync(forward), token);
            var second = Task.Run(() => LoadInOwnFlowAsync(reverse), token);

            await arrived.WaitAsync(token);
            await arrived.WaitAsync(token);
            go.Release(2);

            var results = await Task.WhenAll(first, second);

            results.ShouldAllBe(result => result.Succeeded);
        }
    }

    [Fact]
    public async Task LoadGlobal_ConcurrentCardsWithReversedMetricOrder_AllSucceed()
    {
        await using var world = await StartAsync();
        var token = TestContext.Current.CancellationToken;
        using var arrived = new SemaphoreSlim(0, 2);
        using var go = new SemaphoreSlim(0, 2);

        for (var round = 0; round < 4; round++)
        {
            var start = January.AddDays(2 * round);
            var middle = start.AddDays(1);
            var end = start.AddDays(2);
            var tag = "global-c" + round;

            var forward = CardOfRows(
                tag + "-forward",
                Row("results", "0.0100", start, middle),
                Row("ai_tokens", "0.0200", start, middle));
            var reverse = CardOfRows(
                tag + "-reverse",
                Row("ai_tokens", "0.0300", middle, end),
                Row("results", "0.0400", middle, end));

            async Task<UsagePriceLoadResult> LoadInOwnFlowAsync(string card)
            {
                await using var services = world.Api.Services.CreateAsyncScope();
                var loader = services.ServiceProvider.GetRequiredService<UsagePriceLoader>();

                arrived.Release();
                await go.WaitAsync(token);

                return await loader.LoadAsync(UsagePriceScope.Global, Bytes(card), token);
            }

            var first = Task.Run(() => LoadInOwnFlowAsync(forward), token);
            var second = Task.Run(() => LoadInOwnFlowAsync(reverse), token);

            await arrived.WaitAsync(token);
            await arrived.WaitAsync(token);
            go.Release(2);

            var results = await Task.WhenAll(first, second);

            results.ShouldAllBe(result => result.Succeeded);
        }
    }

    [Fact]
    public async Task Load_RefusedCard_LeavesTheCallerTransactionUsable()
    {
        var token = TestContext.Current.CancellationToken;
        await using var world = await StartAsync();
        (await LoadOrgAsync(world, world.Org, Card("org-v1", to: March))).Succeeded.ShouldBeTrue();

        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, token);
        var loader = provider.GetRequiredService<UsagePriceLoader>();

        var refused = await loader.LoadAsync(
            UsagePriceScope.Org,
            Bytes(Card("org-v2", from: February, to: April)),
            token);

        refused.Succeeded.ShouldBeFalse();

        // The savepoint rolled the statement back, so the caller's unit of work still runs and still commits.
        await using (var command = scope.Connection.CreateCommand())
        {
            command.CommandText = "select 1";
            command.Transaction = scope.Transaction;
            (await command.ExecuteScalarAsync(token)).ShouldBe(1);
        }

        await scope.CommitAsync(token);
        (await CountAsync(world, "org-v2", world.Org)).ShouldBe(0);
    }
}
