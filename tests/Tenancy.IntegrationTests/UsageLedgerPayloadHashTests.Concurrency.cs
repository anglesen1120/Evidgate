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
/// The row-lock race behind a billing key: once both transactions hold the same key, exactly one of them can be the
/// bill. The waiter's answer must be a conflict (never a silent duplicate with the other payload), and a rollback must
/// leave the key free for the waiter.
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

        var marker = Unique.Slug("race-commit");
        var second = StartContender(world, key, quantity: 2m, marker);
        await WaitForPendingLedgerInsertAsync(world, marker);

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

            var marker = Unique.Slug("race-rollback");
            var second = StartContender(world, key, quantity: 2m, marker);
            await WaitForPendingLedgerInsertAsync(world, marker);

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
    /// <c>lock_timeout</c> bounds how long its insert may wait for the first transaction, and its
    /// <c>application_name</c> lets the lock probe name this backend and no other.
    /// </summary>
    /// <param name="world">The world.</param>
    /// <param name="key">The billing key both transactions claim.</param>
    /// <param name="quantity">The contender's quantity, which is what makes its payload differ.</param>
    /// <param name="applicationName">The transaction-local marker of the contender's backend.</param>
    private static Task<UsageBatchResult> StartContender(
        World world,
        string key,
        decimal quantity,
        string applicationName)
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

                await MarkContenderAsync(scope, applicationName, token);

                var result = await provider.GetRequiredService<IUsageLedger>().RecordAsync(
                    new RecordUsageBatchRequest([Entry(key, world.Project, quantity)]), token);
                await scope.CommitAsync(token);

                return result;
            });
        }
    }

    /// <summary>
    /// Prepares the contender's transaction: a transaction-local <c>lock_timeout</c> so a broken ordering fails in 15
    /// seconds instead of hanging the suite, and a transaction-local <c>application_name</c> the probe filters by.
    /// </summary>
    /// <param name="scope">The contender's open tenant scope.</param>
    /// <param name="applicationName">The marker the lock probe matches.</param>
    /// <param name="token">The test token.</param>
    private static async Task MarkContenderAsync(ITenantScope scope, string applicationName, CancellationToken token)
    {
        await using var command = scope.Connection.CreateCommand();
        command.Transaction = scope.Transaction;
        command.CommandText =
            "select set_config('lock_timeout', '15s', true), set_config('application_name', @application_name, true)";
        command.Parameters.AddWithValue("application_name", applicationName);

        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// Waits until this contender's own ledger insert is blocked on a lock, so the test never races on timing: the
    /// contender is inside the store when this returns. The marker and the statement keep another test's waiting insert
    /// from answering for it.
    /// </summary>
    /// <param name="world">The world.</param>
    /// <param name="applicationName">The marker of the contender's backend.</param>
    private async Task WaitForPendingLedgerInsertAsync(World world, string applicationName)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var waiting = await _fixture.ScalarAsync<long>(
                world.Database,
                $"""
                select count(*) from pg_catalog.pg_stat_activity
                where application_name = '{applicationName}'
                  and wait_event_type = 'Lock'
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
