using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// The EF write model of <c>organization</c> and <c>project</c> (00 §5.5 C4, 02.1.1-T2):
/// the org/project commands are its only writers. Created per command through
/// <see cref="ITenantDbContextFactory{TContext}"/>, so it runs on the request's tenant
/// connection and transaction and RLS scopes every statement.
/// </summary>
internal sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options) : EvidgateDbContext(options)
{
    /// <summary>The org rows the tenant can see (its own, T4).</summary>
    public DbSet<Organization> Organizations => Set<Organization>();

    /// <summary>The tenant's project rows.</summary>
    public DbSet<Project> Projects => Set<Project>();
}
