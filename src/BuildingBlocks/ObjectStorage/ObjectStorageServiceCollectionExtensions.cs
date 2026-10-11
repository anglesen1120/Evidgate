using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Evidgate.BuildingBlocks.ObjectStorage;

/// <summary>
/// The single registration of the object-storage adapter (06.1.2-T1): the options
/// (bound from <c>EVIDGATE__OBJECT__*</c>), the lazy validator, the secret
/// post-configurer and <see cref="IObjectStorage"/> itself. <c>TryAdd*</c>
/// everywhere, so a test or a future managed store replaces one registration and
/// nothing else.
/// </summary>
public static class ObjectStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IObjectStorage"/> and its options. The options are
    /// validated lazily — when the adapter is first resolved — not at host start,
    /// so a host that never touches object storage needs no configuration.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    public static IServiceCollection AddEvidgateObjectStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ObjectStorageOptions>().BindConfiguration(ObjectStorageOptions.SectionName);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ObjectStorageOptions>, ObjectStorageOptionsValidator>());
        // 10.1.1-T2: the credentials are resolved through ISecretProvider after the
        // binder (which skips the internal setters) and before validation runs.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<ObjectStorageOptions>, ObjectStorageOptionsSecretPostConfigure>());
        services.TryAddSingleton<IObjectStorage, S3ObjectStorage>();

        return services;
    }
}
