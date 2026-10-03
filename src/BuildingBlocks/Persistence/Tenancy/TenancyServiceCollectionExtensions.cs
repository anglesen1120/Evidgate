using System.Reflection;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// Registration of the tenancy data-access path (01.4.2-T1.1–T1.5): the scope
/// factory and its ambient accessor, the Dapper path and the EF interceptor, plus
/// one <see cref="ITenantDbContextFactory{TContext}"/> per module context.
/// </summary>
public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the tenant scope on the app-role <c>NpgsqlDataSource</c> that
    /// <c>AddEvidgatePersistence</c> already put in the container (00 §5.4.2). Also
    /// used by the worker: the dispatcher and its consumers open the same scope
    /// from <c>task.org_id</c> — one data-access path, no second one.
    /// </summary>
    public static IServiceCollection AddEvidgateTenancy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // M1W3-02: the Dapper read path is part of this registration, so its
        // process-wide prerequisites are configured here rather than left to each
        // module:
        //   * the read SQL projects snake_case columns (00 §7) into PascalCase
        //     record parameters, which Dapper matches only when this flag is set;
        //   * a constructor parameter is selected at all only when its type equals
        //     the reader's field type or carries a type handler — `timestamptz`
        //     arrives as DateTime while the DTOs declare DateTimeOffset, and a
        //     `text[]` column arrives as System.Array while the DTO declares
        //     string[], so these handlers are what make the positional-record
        //     constructors reachable.
        // All three live in Dapper's static state (its own design) and all three are
        // idempotent, so a host that registers tenancy twice — or a test that
        // registers it per class — is unaffected.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        if (!SqlMapper.HasTypeHandler(typeof(DateTimeOffset)))
        {
            SqlMapper.AddTypeHandler(new UtcDateTimeOffsetTypeHandler());
        }

        if (!SqlMapper.HasTypeHandler(typeof(string[])))
        {
            SqlMapper.AddTypeHandler(new StringArrayTypeHandler());
        }

        services.TryAddSingleton<ITenantScopeFactory, NpgsqlTenantScopeFactory>();
        services.TryAddSingleton<ITenantScopeAccessor>(provider =>
            provider.GetRequiredService<ITenantScopeFactory>());
        services.TryAddSingleton<ITenantDb, TenantDb>();
        services.TryAddSingleton<Audit.IAuditTrail, Audit.AuditTrail>();
        services.TryAddSingleton<TenantCommandInterceptor>();

        return services;
    }

    /// <summary>
    /// Registers the factory of one module write model. The assemblies passed here
    /// are the modules' own projects: their <c>IEntityTypeConfiguration&lt;&gt;</c>
    /// classes are applied by <see cref="EvidgateDbContext"/> (01.4.2-T1.3).
    /// </summary>
    public static IServiceCollection AddEvidgateDbContext<TContext>(
        this IServiceCollection services,
        params Assembly[] configurationAssemblies)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configurationAssemblies);

        services.TryAddSingleton<ITenantDbContextFactory<TContext>>(provider =>
            new TenantDbContextFactory<TContext>(
                provider.GetRequiredService<ITenantScopeAccessor>(),
                provider.GetRequiredService<TenantCommandInterceptor>(),
                configurationAssemblies));

        return services;
    }
}
