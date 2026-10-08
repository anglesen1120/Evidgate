using Evidgate.BuildingBlocks.Persistence.Isolation;
using Evidgate.BuildingBlocks.Persistence.Options;
using Evidgate.BuildingBlocks.Persistence.Readiness;
using Evidgate.BuildingBlocks.Persistence.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence;

/// <summary>
/// Persistence wiring shared by <c>Evidgate.Api</c> and <c>Evidgate.Worker</c>
/// (01.1.2-T1.1/T1.3): validated database options, the app-role
/// <see cref="NpgsqlDataSource"/>, the single <see cref="ReadinessProbe"/> and the
/// boot-time isolation self-check of 02.6.1-T1.4 (AC5).
/// No connection is opened at startup for readiness — it reports instead (AC3) —
/// while the isolation self-check deliberately runs once before the host serves,
/// and refuses the start when a table is not under database-enforced isolation.
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

        // 10.1.1-T2: the connection string is a secret, so the provider and the
        // post-configurer that resolves it are registered before the options that
        // consume it. Idempotent (TryAdd*): the hosts and the other composition
        // extensions call it too, and a test or a managed store (O3) replaces the
        // single registration.
        services.AddEvidgateSecrets(configuration);

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

        services.AddSingleton<Resolve.IMemberOrgResolver, Resolve.NpgsqlMemberOrgResolver>();
        services.AddSingleton<ReadinessProbe>();

        // 02.6.1-T1.4 (AC5): the boot-time isolation self-check. The exception list
        // is the one embedded at build time from contracts/rls-exceptions.txt, so
        // the published process judges the same list the CI lint does and cannot be
        // started without it. It is registered as a hosted service, so it runs in
        // both entry points before any tenant traffic is served; a failure surfaces
        // from Host.StartAsync as an IsolationSelfCheckException with a non-zero
        // exit code (ApiHost/WorkerHost translate it into one named line).
        services
            .AddOptions<IsolationSelfCheckOptions>()
            .Bind(configuration.GetSection(IsolationSelfCheckOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(_ => RlsExceptionList.LoadEmbedded());
        services.AddSingleton<IsolationSelfCheck>();
        services.AddHostedService<IsolationSelfCheckHostedService>();

        return services;
    }
}
