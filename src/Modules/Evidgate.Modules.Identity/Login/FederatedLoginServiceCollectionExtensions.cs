using Evidgate.Modules.Identity.Login.Audit;
using Evidgate.Modules.Identity.Login.Completion;
using Evidgate.Modules.Identity.Login.Linking;
using Evidgate.Modules.Identity.Sessions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
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
        // HMAC/AEAD: confidentiality and integrity in one step, already in this module's framework reference.
        // The ring is shared through PostgreSQL (00 §5.7 S8): every replica reads the same keys and a restart
        // keeps them. The repository is the only ring source, so an unreachable database fails the call and
        // never falls back to an ephemeral or file ring. The application name is fixed so every host derives
        // the same purposes.
        services.AddDataProtection().SetApplicationName(LoginStateCookie.ApplicationName);
        services.TryAddSingleton<DataProtectionKeyRepository>();
        services.AddOptions<KeyManagementOptions>()
            .Configure<DataProtectionKeyRepository>((keys, repository) => keys.XmlRepository = repository);
        services.AddHostedService<DataProtectionKeyRingStartupCheck>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LoginStateCookie>();

        // 02.3.1-T2 (Q1): the staged-invite cookie of POST /v1/auth/invite-token. Same Data Protection ring and
        // a purpose of its own, so a login cookie can never be read as an invite one (or the reverse).
        services.TryAddSingleton<InviteTokenCookie>();
        services.TryAddSingleton<ILoginReplayCache, LoginReplayCache>();

        // 02.3.1-T1.4: the real completion — the user/identity upsert, the active-membership check, the
        // session row and the __Host- session cookie — composed with the session store 02.3.2-T1 ships
        // (AddSessions: EVIDGATE:SESSION options, the pepper secret, ILoginIdentityStore, ISessions).
        services.AddSessions(configuration);
        services.TryAddSingleton<CompleteLoginCommandHandler>();

        // The cookie pair and the outage answer both sign-in seams share; it holds only the bound options,
        // the antiforgery service and the clock, so one instance serves every request.
        services.TryAddSingleton<LoginSessionCompletion>();
        services.TryAddSingleton<ILoginCompletion, LoginCommandCompletion>();

        // 02.3.1-T2.2 (Q5): the identity audit writer. Both the login completion and the linking confirm
        // record through it, and it is a singleton for the same reason they are: it opens one tenant scope
        // and one transaction per org itself (the caller is pre-tenant) and holds nothing per request.
        services.TryAddSingleton<IIdentityAuditWriter, IdentityAuditWriter>();

        // 02.3.1-T2.2 (Q4): the explicit second-provider link — the pre-tenant session lookup the linking
        // routes authenticate with, the staged-link cookie, the identity writer and the callback's link seam.
        services.AddIdentityLinking();

        return services;
    }
}
