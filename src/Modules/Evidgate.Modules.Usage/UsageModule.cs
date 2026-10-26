using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Application;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Usage;

/// <summary>
/// Composition seam of the Usage module (12.3.1-T1.3, the 01.1.2-T2.1 shape every module follows): internal by design and
/// reached through <c>InternalsVisibleTo</c> (00 §5.5), so the Api and the Worker register the same services through this
/// one extension and no host can compose a different write path.
/// </summary>
internal static class UsageModule
{
    /// <summary>Registers the module's services in the calling host.</summary>
    /// <param name="services">The host's service collection.</param>
    public static IServiceCollection AddUsageModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 12.3.1-T1.4: the ledger's EF write model — the tenant-bound context the one-statement insert runs on
        // (00 §5.5 C4/C5; the Dapper path is read-only), and the store that owns the statement.
        services.AddEvidgateDbContext<UsageDbContext>(typeof(UsageDbContext).Assembly);
        services.TryAddScoped<UsageLedgerStore>();

        // 12.3.1 §5: the in-process surface the metering callers resolve (00 §5.5).
        services.TryAddScoped<IUsageLedger, UsageContracts>();

        // IQuotaGate stays unregistered until 12.3.1-T4 implements it: a hot path that asks for it must fail at
        // resolution rather than meter against a stub that always answers Ok.

        return services;
    }
}
