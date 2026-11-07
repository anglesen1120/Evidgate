using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The advisory-gate and database-clock probes of the quota world, kept apart to hold the file size rule.</summary>
internal sealed partial class UsageQuotaWorld
{
    /// <summary>
    /// Asks the advisory gate one question in its own tenant scope, committed like the caller that follows it with the
    /// authoritative call.
    /// </summary>
    /// <param name="request">The question.</param>
    /// <param name="org">The tenant to ask as, or null for this world's org.</param>
    internal async Task<QuotaCheckResult> PeekAsync(QuotaCheckRequest request, Guid? org = null)
    {
        var token = TestContext.Current.CancellationToken;

        await GateCalls.WaitAsync(token).ConfigureAwait(false);

        try
        {
            await using var services = Api.Services.CreateAsyncScope();
            var provider = services.ServiceProvider;
            var scopes = provider.GetRequiredService<ITenantScopeFactory>();
            await using var scope = await scopes.BeginAsync(org ?? Org, null, false, token);

            var result = await provider.GetRequiredService<IQuotaGate>()
                .PeekAsync(request, token)
                .ConfigureAwait(false);
            await scope.CommitAsync(token).ConfigureAwait(false);

            return result;
        }
        finally
        {
            GateCalls.Release();
        }
    }

    /// <summary>
    /// The gate's transaction month beside the database's own two statements, read in one tenant transaction: the month
    /// of the transaction and the month a row stamped by <c>now()</c> in it carries.
    /// </summary>
    internal async Task<(string Gate, string Transaction, string Event)> PeriodAsync()
    {
        var token = TestContext.Current.CancellationToken;

        await GateCalls.WaitAsync(token).ConfigureAwait(false);

        try
        {
            await using var services = Api.Services.CreateAsyncScope();
            var provider = services.ServiceProvider;
            var scopes = provider.GetRequiredService<ITenantScopeFactory>();
            await using var scope = await scopes.BeginAsync(Org, null, false, token);

            var gate = await provider.GetRequiredService<IQuotaGate>()
                .CurrentPeriodAsync(token)
                .ConfigureAwait(false);
            var db = provider.GetRequiredService<ITenantDb>();
            var transaction = await db
                .ScalarAsync<string>(
                    "select to_char(transaction_timestamp() at time zone 'UTC','YYYY-MM')",
                    cancellationToken: token)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("transaction_timestamp() answered no month");
            var stamped = await db
                .ScalarAsync<string>("select to_char(now() at time zone 'UTC','YYYY-MM')", cancellationToken: token)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("now() answered no month");

            await scope.CommitAsync(token).ConfigureAwait(false);

            return (gate, transaction, stamped);
        }
        finally
        {
            GateCalls.Release();
        }
    }
}
