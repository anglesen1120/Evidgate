using Evidgate.BuildingBlocks.Persistence.Options;
using Evidgate.BuildingBlocks.Persistence.Readiness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence;

/// <summary>
/// Persistence wiring shared by <c>Evidgate.Api</c> and <c>Evidgate.Worker</c>
/// (01.1.2-T1.1/T1.3): validated database options, the app-role
/// <see cref="NpgsqlDataSource"/> and the single <see cref="ReadinessProbe"/>.
/// No connection is opened at startup — readiness reports it instead (AC3).
/// </summary>
public static class PersistenceExtensions
{
    /// <summary>Binds <c>EVIDGATE__DATABASE__*</c>, validates it at boot and registers the app-role data source.</summary>
    public static IServiceCollection AddEvidgatePersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(provider => NpgsqlDataSource.Create(
            provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        services.AddSingleton<ReadinessProbe>();

        return services;
    }
}
