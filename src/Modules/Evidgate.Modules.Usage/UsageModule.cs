using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Application;
using Evidgate.Modules.Usage.Application.Queries;
using Evidgate.Modules.Usage.Application.Rollup;
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

        // The rate card's read path and the internal loader that writes one card as one unit of work.
        services.TryAddScoped<UsagePriceStore>();
        services.TryAddScoped<IUsagePriceResolver, UsagePriceResolver>();
        services.TryAddScoped<UsagePriceLoader>();

        // The billing operator's two audited reads: one Dapper store over the tenant scope, and the services that own
        // their bounds, audit row and transaction boundary.
        services.TryAddScoped<UsageReadStore>();
        services.TryAddScoped<UsageLedgerReader>();
        services.TryAddScoped<PeriodReconciliationReader>();

        // The roll-up job: the aggregate store seam, its platform handler and the two schedules (daily and on period
        // close). The datasource is the sweep role the mechanism's other platform tasks read through; `AddTaskSchedule`
        // is idempotent per host and the schedule row's unique (schedule_name, fire_at) key is what makes one fire
        // happen once.
        services.TryAddSingleton<IWorkerSchedDataSource, WorkerSchedDataSource>();
        services.TryAddSingleton<IUsageRollupRecompute, UsageRollupRecompute>();
        services.AddTaskHandler<UsageRollupPayload, UsageRollupTaskHandler>();
        services.AddTaskSchedule(UsageRollupSweeps.Compute);
        services.AddTaskSchedule(UsageRollupSweeps.ComputeOnPeriodClose);

        // IQuotaGate stays unregistered until 12.3.1-T4 implements it: a hot path that asks for it must fail at
        // resolution rather than meter against a stub that always answers Ok. Fail closed is the contract (IQuotaGate's
        // docs), so nothing here substitutes an always-Ok default, now or later.

        return services;
    }
}
