using Microsoft.EntityFrameworkCore;

namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// Creates a module's EF Core write model bound to the ambient tenant scope
/// (00 §5.5 C3/C4): the scope's connection, the scope's transaction through
/// <c>Database.UseTransaction</c>, the tenant interceptor and the module's model
/// conventions. Registered by <c>AddEvidgateDbContext</c>.
/// </summary>
/// <typeparam name="TContext">The module's context, derived from <see cref="EvidgateDbContext"/>.</typeparam>
public interface ITenantDbContextFactory<out TContext>
    where TContext : DbContext
{
    /// <summary>
    /// Creates the context. Throws <see cref="TenantScopeMissingException"/> when
    /// the current async flow has no active scope — the EF half of AC1 — so a
    /// handler cannot obtain a context that would run without a tenant setting.
    /// The caller owns the returned context and disposes it.
    /// </summary>
    TContext Create();
}
