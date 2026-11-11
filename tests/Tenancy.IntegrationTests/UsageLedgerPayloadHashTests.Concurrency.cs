// Two transactions holding the same billing key with different payloads, in a fixed order: the second insert waits on
// the first's uncommitted row, so the outcome is decided by what the first transaction does. Commit -> one row and a
// conflict for the second; rollback -> the second inserts. `lock_timeout` bounds the wait, and no case sleeps to guess.
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The row-lock race behind CR-21: once both transactions hold the same key, exactly one of them can be the bill. The
/// waiter's answer must be a conflict (never a silent duplicate with the other payload), and a rollback must leave the
/// key free for the waiter.
/// </summary>
public sealed partial class UsageLedgerPayloadHashTests
{
    [Fact]
    public async Task Concurrent_SameKeyDifferentPayloads_FirstCommits_SecondConflicts()
    {
        await using var world = await StartAsync();
        var token = TestContext.Current.CancellationToken;
        var key = Unique.Slug("race-commit");

        await using var firstServices = world.Commands.Services.CreateAsyncScope();
        var firstProvider = firstServices.ServiceProvider;
        var firstScopes = firstProvider.GetRequiredService<ITenantScopeFactory>();
        await using var firstScope = await firstScopes.BeginAsync(world.Org, null, false, token);

        var first = await firstProvider.GetRequiredService<IUsageLedger>().RecordAsync(
            new RecordUsageBatchRequest([Entry(key, world.Project, quantity: 1m)]), token);
        first.Inserted.ShouldBe([key], "the row exists inside the transaction, not yet committed");

        var second = StartContender(world, key, quantity: 2m);
        await WaitForPendingLedgerInsertAsync(world);

        await firstScope.CommitAsync(token);

        var outcome = await second.WaitAsync(TimeSpan.FromSeconds(30), token);

        outcome.Conflicts.ShouldBe([key], "the committed payload is the bill; the second claims another one");
        outcome.Inserted.ShouldBeEmpty();
        outcome.Duplicates.ShouldBeEmpty();
        (await LedgerCountAsync(world)).ShouldBe(1);
        (await ColumnAsync(world, key, "quantity::text")).ShouldBe("1");
    }

    [Fact]
    public async Task Concurrent_SameKeyDifferentPayloads_FirstRollsBack_SecondInserts()
    {
        await using var world = await StartAsync();
        var token = TestContext.Current.CancellationToken;
        var key = Unique.Slug("race-rollback");

        var firstServices = world.Commands.Services.CreateAsyncScope();
        var firstProvider = firstServices.ServiceProvider;
        var firstScopes = firstProvider.GetRequiredService<ITenantScopeFactory>();
        var firstScope = await firstScopes.BeginAsync(world.Org, null, false, token);
        var released = false;

        try
        {
            var first = await firstProvider.GetRequiredService<IUsageLedger>().RecordAsync(
                new RecordUsageBatchRequest([Entry(key, world.Project, quantity: 1m)]), token);
            first.Inserted.ShouldBe([key]);

            var second = StartContender(world, key, quantity: 2m);
            await WaitForPendingLedgerInsertAsync(world);

            // Disposing without committing rolls the first transaction back, which releases the key.
            await firstScope.DisposeAsync();
            await firstServices.DisposeAsync();
            released = true;

            var outcome = await second.WaitAsync(TimeSpan.FromSeconds(30), token);

            outcome.Inserted.ShouldBe([key]);
            outcome.Conflicts.ShouldBeEmpty();
            (await LedgerCountAsync(world)).ShouldBe(1);
            (await ColumnAsync(world, key, "quantity::text")).ShouldBe("2");
        }
        finally
        {
            if (!released)
            {
                await firstScope.DisposeAsync();
                await firstServices.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Starts the contending transaction on another flow and returns its pending answer. The contender's own
    /// <c>lock_timeout</c> bounds how long its insert may wait for the first transaction.
    /// </summary>
    /// <param name="world">The world.</param>
    /// <param name="key">The billing key both transactions claim.</param>
    /// <param name="quantity">The contender's quantity, which is what makes its payload differ.</param>
    private static Task<UsageBatchResult> StartContender(World world, string key, decimal quantity)
    {
        var token = TestContext.Current.CancellationToken;

        // A Task.Run inherits this flow's ExecutionContext and with it the first transaction's ambient tenant scope,
        // which the factory refuses to open twice (00 §5.4.2). Suppress the flow for the creation only, so the
        // contender publishes its own scope inside its own transaction.
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(async () =>
            {
                await using var services = world.Commands.Services.CreateAsyncScope();
                var provider = services.ServiceProvider;
                var scopes = provider.GetRequiredService<ITenantScopeFactory>();
                await using var scope = await scopes.BeginAsync(world.Org, null, false, token);

                await SetLockTimeoutAsync(scope, "15s", token);

                var result = await provider.GetRequiredService<IUsageLedger>().RecordAsync(
                    new RecordUsageBatchRequest([Entry(key, world.Project, quantity)]), token);
                await scope.CommitAsync(token);

                return result;
            });
        }
    }

    /// <summary>
    /// Bounds how long the contender may wait on the first transaction's row lock, transaction-locally, so a broken
    /// ordering fails in 15 seconds instead of hanging the suite.
    /// </summary>
    /// <param name="scope">The contender's open tenant scope.</param>
    /// <param name="value">The timeout, e.g. <c>15s</c>.</param>
    /// <param name="token">The test token.</param>
    private static async Task SetLockTimeoutAsync(ITenantScope scope, string value, CancellationToken token)
    {
        await using var command = scope.Connection.CreateCommand();
        command.Transaction = scope.Transaction;
        command.CommandText = "select set_config('lock_timeout', @value, true)";
        command.Parameters.AddWithValue("value", value);

        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// Waits until a ledger insert is blocked on a lock, so the test never races on timing: the contender is inside the
    /// store when this returns.
    /// </summary>
    /// <param name="world">The world.</param>
    private async Task WaitForPendingLedgerInsertAsync(World world)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var waiting = await _fixture.ScalarAsync<long>(
                world.Database,
                """
                select count(*) from pg_catalog.pg_stat_activity
                where wait_event_type = 'Lock'
                  and query like '%insert into public.usage_ledger_entry%'
                """);

            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException("the contending transaction never reached the ledger insert");
    }
}
