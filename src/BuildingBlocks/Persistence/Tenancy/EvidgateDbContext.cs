using Microsoft.EntityFrameworkCore;

namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// The EF Core write model of a module (00 §5.5 C4): created by
/// <see cref="ITenantDbContextFactory{TContext}"/>, which binds it to the ambient
/// tenant scope's connection and transaction. A module context is
/// <c>sealed class X(DbContextOptions&lt;X&gt; options) : EvidgateDbContext(options)</c>
/// and ships its own <see cref="IEntityTypeConfiguration{TEntity}"/> classes, which
/// the factory hands to <c>UseTenantModel</c> at registration.
/// </summary>
public abstract class EvidgateDbContext : DbContext
{
    private readonly DbContextOptions _options;

    /// <summary>Creates the context over the options the tenant factory built.</summary>
    protected EvidgateDbContext(DbContextOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var assembly in _options.Extensions
            .OfType<TenantModelOptionsExtension>()
            .SelectMany(extension => extension.ConfigurationAssemblies)
            .Distinct())
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }

        // 01.4.2-T1.3: the conventions run last and are authoritative, so a module
        // cannot map itself outside the snake_case / app-generated id /
        // concurrency-token contract of 00 §7 and §5.5 C4.
        TenantModelConventions.Apply(modelBuilder);
    }
}
