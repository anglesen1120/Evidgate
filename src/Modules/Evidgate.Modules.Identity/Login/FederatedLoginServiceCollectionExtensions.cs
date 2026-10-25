using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Login;

/// <summary>
/// Composition of the federated login PROTOCOL (02.3.1-T1.1): the options of <c>EVIDGATE:Login</c>, the
/// pending-login cookie, the one-time <c>state</c> record and the login-completion seam. The providers
/// themselves are registered by each provider's own extension (slice 02.3.1-T1.2's
/// <c>AddGoogleLogin</c>, 02.3.1-T1.3's <c>AddGitHubLogin</c>), and the routes resolve them as
/// <see cref="ILoginProvider"/>.
/// </summary>
/// <remarks>
/// Called by <c>IdentityModule.AddIdentityModule</c> in both entry points, before the provider
/// registrations, so a provider can rely on the cookie and options already being registered. A host without
/// any provider still boots: both routes then answer 404 <c>not_found</c> for every route value.
/// </remarks>
internal static class FederatedLoginServiceCollectionExtensions
{
    /// <summary>Registers the login protocol's services.</summary>
    /// <param name="services">The host's collection.</param>
    /// <param name="configuration">The host's configuration, for <see cref="LoginOptions"/>.</param>
    public static IServiceCollection AddFederatedLogin(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<LoginOptions>()
            .Bind(configuration.GetSection(LoginOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<LoginOptions>, LoginOptionsValidator>());

        // The pending-login cookie is protected with ASP.NET Core Data Protection rather than a hand-rolled
        // HMAC/AEAD: it is the framework's own mechanism, it needs no new secret in ISecretProvider (so
        // nothing has to be provisioned before the first login), it gives confidentiality AND integrity in
        // one step, and it is already available through this module's Microsoft.AspNetCore.App framework
        // reference. The application name is fixed so every host of the deployment derives the same
        // purposes; no shared ring is configured yet (no PersistKeysTo…), so replicas cannot read each
        // other's state cookies — the open item for the 10.x deployment work.
        services.AddDataProtection().SetApplicationName(LoginStateCookie.ApplicationName);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LoginStateCookie>();
        services.TryAddSingleton<LoginReplayCache>();

        // 02.3.1-T1.4 replaces this registration with the real completion (user upsert, session row,
        // __Host- session cookie, redirect). Until then a verified identity answers 501
        // login_not_completed instead of pretending to be signed in.
        services.TryAddSingleton<ILoginCompletion, LoginNotCompletedCompletion>();

        return services;
    }
}
