using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// Brings the tenant model conventions of 01.4.2-T1.3 into a module context's
/// options: the assemblies whose <c>IEntityTypeConfiguration&lt;&gt;</c> classes
/// the model applies, on top of which <see cref="EvidgateDbContext"/> enforces
/// snake_case table and column names, an application-generated UUIDv7 primary key
/// and the <c>version</c> optimistic-concurrency token.
/// </summary>
public static class TenantModelOptionsBuilderExtensions
{
    /// <summary>
    /// Applies the tenant model conventions and the module's configuration classes.
    /// A context created through <c>AddEvidgateDbContext</c>/
    /// <see cref="ITenantDbContextFactory{TContext}"/> also carries
    /// <see cref="TenantCommandInterceptor"/>; a context whose options are built by
    /// hand must add it with <c>AddInterceptors</c>, or it loses the AC1 guard.
    /// </summary>
    public static DbContextOptionsBuilder UseTenantModel(
        this DbContextOptionsBuilder optionsBuilder,
        params Assembly[] configurationAssemblies)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(configurationAssemblies);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(new TenantModelOptionsExtension(configurationAssemblies));

        return optionsBuilder;
    }

    /// <summary>Applies the tenant model conventions and the module's configuration classes.</summary>
    public static DbContextOptionsBuilder<TContext> UseTenantModel<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        params Assembly[] configurationAssemblies)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        UseTenantModel((DbContextOptionsBuilder)optionsBuilder, configurationAssemblies);

        return optionsBuilder;
    }
}

/// <summary>
/// Carries the module configuration assemblies with the options (01.4.2-T1.3), so
/// <see cref="EvidgateDbContext.OnModelCreating"/> stays a plain override and a
/// context built without DI is still fully configured.
/// </summary>
internal sealed class TenantModelOptionsExtension : IDbContextOptionsExtension
{
    private readonly DbContextOptionsExtensionInfo _info;

    internal TenantModelOptionsExtension(IReadOnlyList<Assembly> configurationAssemblies)
    {
        ConfigurationAssemblies = configurationAssemblies;
        _info = new TenantModelExtensionInfo(this);
    }

    internal IReadOnlyList<Assembly> ConfigurationAssemblies { get; }

    public DbContextOptionsExtensionInfo Info => _info;

    public void ApplyServices(IServiceCollection services)
    {
        // Nothing to register: the conventions are applied in OnModelCreating.
    }

    public void Validate(IDbContextOptions options)
    {
        if (ConfigurationAssemblies.Count == 0)
        {
            throw new InvalidOperationException(
                "UseTenantModel needs at least one module assembly: pass the assembly that holds the module's "
                + "IEntityTypeConfiguration<> classes (01.4.2-T1.3).");
        }
    }

    private sealed class TenantModelExtensionInfo(TenantModelOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "tenantModel={" + Names(extension) + "} ";

        public override int GetServiceProviderHashCode()
        {
            var hash = new HashCode();
            foreach (var assembly in extension.ConfigurationAssemblies)
            {
                hash.Add(assembly.GetName().Name, StringComparer.Ordinal);
            }

            return hash.ToHashCode();
        }

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other is TenantModelExtensionInfo info
            && Names((TenantModelOptionsExtension)info.Extension) == Names(extension);

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            ArgumentNullException.ThrowIfNull(debugInfo);
            debugInfo["Tenant:ModelAssemblies"] = Names(extension);
        }

        private static string Names(TenantModelOptionsExtension extension) =>
            string.Join(",", extension.ConfigurationAssemblies.Select(assembly => assembly.GetName().Name));
    }
}
