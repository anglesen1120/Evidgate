using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Identity.Application.Commands;
using Evidgate.Modules.Identity.Authorization;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Evidgate.Modules.Identity.Options;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity;

/// <summary>
/// Composition seam of the Identity module (01.1.2-T2.1, 02.2.1-T2.2): internal by
/// design and reached through <c>InternalsVisibleTo</c> (00 §5.5).
/// </summary>
/// <remarks>
/// The module's read queries resolve their collaborators from the host's
/// registration: <c>ITenantDb</c> (the Dapper path over the ambient tenant scope)
/// comes from <c>AddEvidgateTenancy</c>, which every entry point already calls, and
/// Mediator's source generator registers the handlers themselves. T2.2 adds the two
/// pieces this module owns outright: the <see cref="IPermissionEvaluator"/> the C2
/// authorization stage resolves (replacing the pipeline's fail-closed default), the
/// EF write model of <c>role</c>/<c>role_permission</c> behind it, and the
/// <c>EVIDGATE__IDENTITY__*</c> options that flag the custom-role commands.
/// </remarks>
internal static class IdentityModule
{
    /// <summary>Registers the module's services in the calling host.</summary>
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The feature flag of 02.2.1-T2.2 (RECOMMENDATIONS 02-Q3): built-in roles
        // only in V1, so the write commands answer 403 until it is turned on. Bound
        // and validated at boot like every other EVIDGATE__* group.
        services
            .AddOptions<IdentityOptions>()
            .Bind(configuration.GetSection(IdentityOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The EF write model of role/role_permission (00 §5.5 C4): the flagged
        // custom-role commands of T2.2 are its only writers in V1.
        services.AddEvidgateDbContext<IdentityDbContext>(typeof(IdentityDbContext).Assembly);

        // 02.2.1-T2.2: the real evaluator replaces the pipeline's fail-closed
        // default. Replace, not TryAdd: the pipeline registers the deny-all first
        // and the module is the intentional owner of this seam, so the swap must
        // be explicit rather than a registration-order accident. A test's own
        // singleton registered afterwards still wins the single-resolve.
        //
        // W7-SEC-02: one instance answers both seams. The C2 gate resolves
        // IPermissionEvaluator and the write commands' self-escalation rule resolves
        // ICallerPermissionSet, and the set a command compares a grant against must be
        // the set the next request is evaluated with — two registrations could diverge
        // the moment one of them is substituted. `PermissionEvaluator` is therefore the
        // singleton and both interfaces project it; a host that substitutes the evaluator
        // substitutes the set with it (the test host's fakes implement both).
        services.TryAddSingleton<PermissionEvaluator>();
        services.Replace(ServiceDescriptor.Singleton<IPermissionEvaluator>(
            provider => provider.GetRequiredService<PermissionEvaluator>()));
        services.TryAddSingleton<ICallerPermissionSet>(
            provider => provider.GetRequiredService<PermissionEvaluator>());

        // 02.1.2-T1.2 (C2 stage 2): the membership commands' shape validators. The C2
        // validation behaviour resolves IEnumerable<IValidator<TMessage>> from DI, so
        // a validator that is not registered never runs; TryAddEnumerable keeps a
        // test's replacement for one message while the others stay registered.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidator<InviteMemberCommand>, InviteMemberCommandValidator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidator<UpdateMembershipCommand>, UpdateMembershipCommandValidator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidator<GrantRoleCommand>, GrantRoleCommandValidator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidator<RevokeRoleCommand>, RevokeRoleCommandValidator>());

        // 02.2.1-T2.2: the same stage for the flagged custom-role writes, so a
        // malformed body never opens a transaction (the stage-2 contract).
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidator<CreateRoleCommand>, CreateRoleCommandValidator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidator<UpdateRoleCommand>, UpdateRoleCommandValidator>());

        // 02.1.2-T1.3: the exported directory contracts the login flow consumes —
        // member roles and pending invites (IIdentityDirectory) and the membership
        // lookup (IMembership). Stateless services over the ambient ITenantDb, whose
        // answers RLS scopes to the caller's tenant, so a singleton is safe.
        services.TryAddSingleton<MembershipDirectory>();
        services.TryAddSingleton<IIdentityDirectory>(
            provider => provider.GetRequiredService<MembershipDirectory>());
        services.TryAddSingleton<IMembership>(
            provider => provider.GetRequiredService<MembershipDirectory>());

        // 02.1.1-T2 (AC8, T98): the org creator's founder membership, written by
        // Tenancy's CreateOrganization through this contract in the same transaction.
        services.TryAddSingleton<IOrganizationFounder, OrganizationFounder>();

        services.AddCiAuthentication(configuration);

        // 02.4.1-T4.1: the daily purge of the OIDC replay cache. Registered in both hosts like every module
        // piece; only the Worker runs a consumer and a schedule runner.
        services.AddTaskHandler<CiTokenIssuePurgePayload, CiTokenIssuePurgeTaskHandler>();
        services.AddTaskSchedule(CiTokenIssueSweeps.Purge);

        return services;
    }

    /// <summary>02.4.1/02.4.2: key material, token minting and validation, the GitHub OIDC validator and the pre-tenant resolvers.</summary>
    private static IServiceCollection AddCiAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<CiAuthOptions>()
            .Bind(configuration.GetSection(CiAuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CiAuthOptions>, CiAuthOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CiSecrets>();
        services.TryAddSingleton<CiProducerTokens>();
        services.TryAddSingleton<ExchangeRateLimiter>();
        services.TryAddSingleton<ICiResolver, NpgsqlCiResolver>();
        services.TryAddSingleton<GitHubOidcValidator>();

        // The JWKS client: a typed HttpClient with a hard timeout and a bounded response (S4, P6).
        services.AddHttpClient<IGitHubJwksProvider, HttpGitHubJwksProvider>(client => client.Timeout = TimeSpan.FromSeconds(5))
            .AddTypedClient<IGitHubJwksProvider>((http, provider) =>
                new HttpGitHubJwksProvider(http, provider.GetRequiredService<IOptions<CiAuthOptions>>(), provider.GetRequiredService<TimeProvider>()));

        // `ci_producer` bearer tokens authenticate service callers for the tenant routes.
        services.AddSingleton<ICallerAuthenticator, CiProducerAuthenticator>();

        foreach (var validator in new (Type Message, Type Impl)[]
                 {
                     (typeof(CreateCiBindingCommand), typeof(CreateCiBindingCommandValidator)),
                     (typeof(UpdateCiBindingCommand), typeof(UpdateCiBindingCommandValidator)),
                     (typeof(CreateIngestTokenCommand), typeof(CreateIngestTokenCommandValidator)),
                 })
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IValidator<>).MakeGenericType(validator.Message), validator.Impl));
        }

        return services;
    }
}
