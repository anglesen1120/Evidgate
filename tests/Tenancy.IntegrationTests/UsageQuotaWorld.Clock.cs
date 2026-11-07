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
    internal async Task<QuotaPeekResult> PeekAsync(QuotaCheckRequest request, Guid? org = null)
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

    /// <summary>The database's own month and the one after it, for a test that must survive a month-end rollover.</summary>
    /// <returns>The current month and the next, both <c>YYYY-MM</c> in UTC.</returns>
    internal async Task<(string Month, string NextMonth)> DatabaseMonthsAsync()
    {
        var row = await ScalarAsync<string>(
            """
            select to_char(now() at time zone 'UTC','YYYY-MM') || '|'
                || to_char((date_trunc('month', now() at time zone 'UTC') + interval '1 month')::date,'YYYY-MM')
            """);
        var months = row.Split('|');

        return (months[0], months[1]);
    }
}
