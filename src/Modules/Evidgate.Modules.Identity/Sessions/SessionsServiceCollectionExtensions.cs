using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Sessions;

/// <summary>
/// Composition of the persistence seams a session needs (02.3.1-T1.4, completed by 02.3.2-T1.1): the identity
/// upsert (<see cref="ILoginIdentityStore"/>), creation and rotation (<see cref="ISessions"/>), cookie
/// verification (<see cref="ISessionAuthentication"/>) and the two writes another module's transaction calls
/// (<see cref="ISessionRevocation"/>, <see cref="ISessionPrivilegeChange"/>), all over the app-role
/// <c>NpgsqlDataSource</c> and the owner-scoped <c>evidgate_resolve</c> functions of migrations 0042/0045;
/// 02.3.2-T2.1 adds the four session routes and the cleanup batch (<see cref="ISessionRoutesStore"/>, 0048),
/// whose cleanup runs on the <c>evidgate_worker_sched</c> connection instead.
/// </summary>
/// <remarks>
/// <para>
/// Follows the CI-authentication seam (<c>IdentityModule.AddCiAuthentication</c>) member by member: options
/// bound and validated at boot, the peppers resolved through a post-configurer (never by binding), the
/// collaborators singletons over the app-role data source. The clock is <see cref="TimeProvider.System"/> unless
/// the host already registered one, so every instant of a session (creation, slide, rotation, revocation) is
/// moved by a test clock.
/// </para>
/// <para>
/// Called by <c>FederatedLoginServiceCollectionExtensions.AddFederatedLogin</c>; every registration uses
/// <c>TryAdd</c>, so a host that replaces a seam (a test host swapping <see cref="ISessions"/>) keeps its own.
/// The revocation and privilege-change seams share ONE <see cref="SessionRevocationStore"/> instance: both run
/// on the ambient tenant scope, so a second instance would only be a second object with the same state.
/// </para>
/// </remarks>
internal static class SessionsServiceCollectionExtensions
{
    /// <summary>Registers the session options, the peppers, the identity store and the session seams.</summary>
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

        // 10.1.1-T2: the peppers are resolved through ISecretProvider after the binder (which skips the
        // internal setters) and before validation runs.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<SessionOptions>, SessionOptionsSecretPostConfigure>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SessionSecrets>();
        services.TryAddSingleton<ILoginIdentityStore, NpgsqlLoginIdentityStore>();

        services.TryAddSingleton<ISessions, SessionStore>();
        services.TryAddSingleton<ISessionAuthentication, SessionAuthenticationStore>();

        // One instance behind both contracts: they are the same two statements on the caller's tenant scope.
        services.TryAddSingleton<SessionRevocationStore>();
        services.TryAddSingleton<ISessionRevocation>(
            provider => provider.GetRequiredService<SessionRevocationStore>());
        services.TryAddSingleton<ISessionPrivilegeChange>(
            provider => provider.GetRequiredService<SessionRevocationStore>());

        // 02.3.2-T2.1: the four session routes and the cleanup sweep. The route statements run as the
        // app role; the cleanup batch runs as `evidgate_worker_sched`, because EXECUTE on
        // `evidgate.session_cleanup` is granted to the sweep role alone (TL T80, the
        // `CiTokenIssuePurge` posture). `WorkerSchedDataSource` is itself the fail-closed seam: without
        // `TasksOptions.SweepConnectionString` it holds no source and refuses at `OpenAsync` with the key
        // named, so the Api (which serves the routes and never sweeps) resolves this registration
        // harmlessly, and a Worker that calls `AddEvidgateTasks` keeps its own instance through TryAdd.
        services.TryAddSingleton<IWorkerSchedDataSource, WorkerSchedDataSource>();
        services.TryAddSingleton<ISessionRoutesStore, SessionRoutesStore>();

        return services;
    }
}
