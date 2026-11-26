using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Healing.Domain;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Healing.Infrastructure.Persistence;

/// <summary>
/// The EF write model of healing. Created per command through <see cref="ITenantDbContextFactory{TContext}"/>, so it
/// runs on the request's tenant connection and transaction and RLS scopes every statement.
/// </summary>
internal sealed class HealingDbContext(DbContextOptions<HealingDbContext> options) : EvidgateDbContext(options)
{
    /// <summary>The tenant's incidents.</summary>
    public DbSet<Incident> Incidents => Set<Incident>();
}
