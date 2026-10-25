using Evidgate.Modules.Identity.Login.Completion;
using Evidgate.Modules.Identity.Sessions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Login;

/// <summary>
/// Composition of the federated login (02.3.1-T1.1, 02.3.1-T1.4): the options of <c>EVIDGATE:Login</c>, the
/// pending-login cookie, the one-time <c>state</c> record, the session store of 02.3.2-T1 and the login
/// completion that turns a verified identity into a session. The providers themselves are registered by each
/// provider's own extension (02.3.1-T1.2's <c>AddGoogleLogin</c>, 02.3.1-T1.3's <c>AddGitHubLogin</c>), and
/// the routes resolve them as <see cref="ILoginProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// Called by <c>IdentityModule.AddIdentityModule</c> in both entry points, before the provider
/// registrations, so a provider can rely on the cookie and options already being registered. A host without
/// any provider still boots: both routes then answer 404 <c>not_found</c> for every route value.
/// </para>
/// <para>
/// The two routes also resolve the CI exchange limiter (<c>ExchangeRateLimiter</c>), which
/// <c>IdentityModule.AddCiAuthentication</c> registers earlier in the same composition: the login routes
/// spend that limiter's per-client budget instead of introducing a second throttle with its own options.
/// </para>
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
        // purposes. No ring is configured here (no PersistKeysTo…), so the key ring is the default one:
        // persisted only where the key directory is writable, and in-memory (thus lost on every restart)
        // where it is not. Either way replicas do not share a ring, so a persisted, shared ring is a deploy
        // requirement tracked as its own item in STATUS (10.x deployment work), not a setting of this
        // composition.
        services.AddDataProtection().SetApplicationName(LoginStateCookie.ApplicationName);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LoginStateCookie>();
        services.TryAddSingleton<LoginReplayCache>();

        // 02.3.1-T1.4: the real completion — the user/identity upsert, the active-membership check, the
        // session row and the __Host- session cookie — composed with the session store 02.3.2-T1 ships
        // (AddSessions: EVIDGATE:SESSION options, the pepper secret, ILoginIdentityStore, ISessions).
        services.AddSessions(configuration);
        services.TryAddSingleton<CompleteLoginCommandHandler>();
        services.TryAddSingleton<ILoginCompletion, LoginCommandCompletion>();

        return services;
    }
}
