using Evidgate.BuildingBlocks.Persistence.Options;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Evidgate.BuildingBlocks.Persistence.Secrets;

/// <summary>
/// The single registration of <see cref="ISecretProvider"/> (10.1.1-T2): every host
/// that composes persistence, the task queue or a module gets the same
/// environment-injection provider, and the options post-configurers that resolve the
/// connection strings and key material are registered beside it.
/// </summary>
/// <remarks>
/// The lookup reads the host configuration by the <c>IConfiguration</c> spelling of
/// the secret name, so the environment-variables source is the injection point and a
/// test host that sets the same keys in memory resolves identically. <c>TryAdd</c>
/// everywhere: a test or a future managed secret store (O3) replaces the single
/// registration and nothing else.
/// </remarks>
public static class SecretProviderExtensions
{
    /// <summary>Registers the provider and the options post-configurers that resolve secrets through it.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host configuration the environment provider reads.</param>
    public static IServiceCollection AddEvidgateSecrets(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<ISecretProvider>(_ => new EnvironmentSecretProvider(
            name => configuration[SecretNames.ToConfigurationKey(name)]));

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<DatabaseOptions>, DatabaseOptionsSecretPostConfigure>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<TasksOptions>, TasksOptionsSecretPostConfigure>());

        return services;
    }
}

/// <summary>
/// Resolves <see cref="DatabaseOptions.ConnectionString"/> through
/// <see cref="ISecretProvider"/> after the binder has run (10.1.1-T2). The property
/// setter is internal, so <c>BindConfiguration</c> skips it and this is its only
/// writer; validation runs after post-configuration and still sees the resolved value.
/// </summary>
internal sealed class DatabaseOptionsSecretPostConfigure : IPostConfigureOptions<DatabaseOptions>
{
    private readonly ISecretProvider _secrets;

    public DatabaseOptionsSecretPostConfigure(ISecretProvider secrets) => _secrets = secrets;

    /// <inheritdoc />
    public void PostConfigure(string? name, DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ConnectionString =
            _secrets.GetSecret(SecretNames.DatabaseConnectionString) ?? string.Empty;
    }
}

/// <summary>
/// Resolves <see cref="TasksOptions.SweepConnectionString"/> through
/// <see cref="ISecretProvider"/> after the binder has run (10.1.1-T2). The property
/// setter is internal, so <c>BindConfiguration</c> skips it and this is its only
/// writer; the value stays optional, so an unset secret leaves the sweep fail-closed.
/// </summary>
internal sealed class TasksOptionsSecretPostConfigure : IPostConfigureOptions<TasksOptions>
{
    private readonly ISecretProvider _secrets;

    public TasksOptionsSecretPostConfigure(ISecretProvider secrets) => _secrets = secrets;

    /// <inheritdoc />
    public void PostConfigure(string? name, TasksOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.SweepConnectionString =
            _secrets.GetSecret(SecretNames.TasksSweepConnectionString) ?? string.Empty;
    }
}
