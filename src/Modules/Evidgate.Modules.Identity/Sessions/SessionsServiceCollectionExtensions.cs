using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Sessions;

/// <summary>
/// Composition of the two persistence seams a completed federated login needs (02.3.1-T1.4): the identity
/// upsert (<see cref="ILoginIdentityStore"/>) and session creation (<see cref="ISessions"/>), both over the
/// app-role <c>NpgsqlDataSource</c> and the owner-scoped <c>evidgate_resolve</c> functions of migration 0042.
/// </summary>
/// <remarks>
/// <para>
/// Follows the CI-authentication seam (<c>IdentityModule.AddCiAuthentication</c>) member by member: options
/// bound and validated at boot, the pepper resolved through a post-configurer (never by binding), the
/// collaborators singletons over the app-role data source. The clock is <see cref="TimeProvider.System"/>
/// unless the host already registered one, so the two expiries of a session are moved by a test clock.
/// </para>
/// <para>
/// Called by <c>FederatedLoginServiceCollectionExtensions.AddFederatedLogin</c>; every registration uses
/// <c>TryAdd</c>, so a host that replaces a seam (a test host swapping <see cref="ISessions"/>) keeps its own.
/// </para>
/// </remarks>
internal static class SessionsServiceCollectionExtensions
{
    /// <summary>Registers the session options, the pepper, the identity store and the session store.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's configuration, for <see cref="SessionOptions"/>.</param>
    internal static IServiceCollection AddSessions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<SessionOptions>()
            .Bind(configuration.GetSection(SessionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<SessionOptions>, SessionOptionsValidator>());

        // 10.1.1-T2: the pepper is resolved through ISecretProvider after the binder (which skips the
        // internal setter) and before validation runs.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<SessionOptions>, SessionOptionsSecretPostConfigure>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SessionSecrets>();
        services.TryAddSingleton<ILoginIdentityStore, NpgsqlLoginIdentityStore>();
        services.TryAddSingleton<ISessions, SessionStore>();

        return services;
    }
}
