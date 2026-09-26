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

        services.AddSingleton(provider =>
        {
            var connectionString =
                provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString;

            // Same role pin the sweep DSN carries (WorkerSchedDataSource, TL T80):
            // deploying an owner (or any non-app) role string here must fail the
            // boot, not silently collapse the owner/app privilege split that
            // 00 §5.4.3 relies on. The parse failure re-throws without echoing
            // the value — it can carry a password.
            NpgsqlConnectionStringBuilder builder;
            try
            {
                builder = new NpgsqlConnectionStringBuilder(connectionString);
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException(
                    $"{DatabaseOptions.SectionName} is not a valid connection string "
                    + "(check quoting and keywords; the value is not echoed because it carries a password).");
            }

            if (builder.Username != "evidgate_app")
            {
                throw new InvalidOperationException(
                    $"{DatabaseOptions.SectionName} must connect as evidgate_app "
                    + $"but names '{builder.Username ?? "<none>"}': the app role is the only DML role "
                    + "in the system (00 §5.4.3).");
            }

            return NpgsqlDataSource.Create(builder);
        });

        services.AddSingleton<ReadinessProbe>();

        return services;
    }
}
