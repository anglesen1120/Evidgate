using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Domain;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Catalog.Infrastructure.Persistence;

/// <summary>
/// The EF write model of the catalog (00 §5.5 C4): case identity (03.1.1), revisions (03.1.3) and suites (03.2.1).
/// Created per command through <see cref="ITenantDbContextFactory{TContext}"/>, so it runs on the request's tenant
/// connection and transaction and RLS scopes every statement.
/// </summary>
internal sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : EvidgateDbContext(options)
{
    /// <summary>The tenant's cases.</summary>
    public DbSet<TestCase> Cases => Set<TestCase>();

    /// <summary>Where each case's id came from (append-only).</summary>
    public DbSet<CaseIdentitySource> CaseIdentitySources => Set<CaseIdentitySource>();

    /// <summary>The moves of each case (append-only).</summary>
    public DbSet<CaseRenameHistory> CaseRenames => Set<CaseRenameHistory>();
}
