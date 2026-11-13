using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Evidgate.Modules.Healing.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace Evidgate.Modules.Healing;

/// <summary>
/// Composition seam of the Healing module: internal by design and reached through <c>InternalsVisibleTo</c>.
/// </summary>
internal static class HealingModule
{
    /// <summary>Registers the module's services in the calling host.</summary>
    /// <param name="services">The host's collection.</param>
    /// <param name="configuration">The host configuration the <c>Healing</c> sections are bound from.</param>
    /// <param name="environment">
    /// The host environment: it decides what an unset provider means and whether the mock adapter may be registered at all.
    /// </param>
    public static IServiceCollection AddHealingModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // The V1 clamp: `Healing:Apply` (absent or false in every shipped configuration, because V1 runs shadow
        // healing) is the only thing that lets `enabled` be set. A section of its own, like the Identity module's
        // `Identity:CustomRoles`.
        services.Configure<HealingOptions>(configuration.GetSection(HealingOptions.SectionName));
        services.TryAddSingleton<IHealingApplyPolicy, HealingApplyPolicy>();

        // The switch store is the module's only Dapper path: the tenant/project upsert and the three-row resolution
        // read. No EF write model exists — the row is configuration, written through one statement.
        services.TryAddScoped<HealingSwitchStore>();

        // The Contracts seam other modules resolve to ask "may healing act for this project?".
        services.TryAddScoped<IHealingAvailabilityResolver, HealingAvailabilityResolver>();

        // The cost read's token seam: what one org's attempts spent in a month, answered from this module's own
        // ledger instead of letting another module read `healing_llm_call`.
        services.TryAddScoped<IHealingTokenUsage, HealingTokenUsageReader>();

        // The operator's only writer of the platform row, resolved by the operator ingress route.
        services.TryAddSingleton<HealingSystemSwitchWriter>();

        AddHealingLlmGateway(services, configuration, environment);

        return services;
    }

    /// <summary>
    /// The provider-adapter seam and its gateway. Which adapter runs is deployment configuration — the mock is registered
    /// only in Development, the HTTP adapter needs a base address and a key, and a host that names neither runs the
    /// disabled adapter so no call can be placed by accident.
    /// </summary>
    private static void AddHealingLlmGateway(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Defaults live in code: there is no configuration key for this slice.
        services.AddOptions<HealingLlmOptions>();

        // The daily budget roll runs as the sweep role through an owner function; `AddTaskSchedule` is idempotent per
        // host and the schedule's unique (schedule_name, fire_at) key makes one fire run once.
        services.TryAddSingleton<IWorkerSchedDataSource, WorkerSchedDataSource>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddTaskHandler<HealingLlmBudgetResetPayload, HealingLlmBudgetResetTaskHandler>();
        services.AddTaskSchedule(HealingLlmBudgetResetSweeps.Reset);

        // The selected kind is settled before validation runs, so a host that states a provider it may not run refuses to
        // start naming the key. The group is validated on start as well as by the host's own materialisation.
        services
            .AddOptions<HealingProviderOptions>()
            .Bind(configuration.GetSection(HealingProviderOptions.SectionName))
            .PostConfigure<IHostEnvironment>((options, host) =>
                options.Kind = HealingProviderSelection.Resolve(options.Provider, host))
            .ValidateOnStart();
        // The validator and the post-configuration read the environment from the container; a composition that did not
        // register one (a test) gets the one this module was composed for.
        services.TryAddSingleton(environment);
        services.AddSingleton<IValidateOptions<HealingProviderOptions>, HealingProviderOptionsValidator>();

        // The mock never ships: its registration exists only in Development, so a composed production container cannot
        // resolve it even if a stray configuration value asked for it (the boot validator refuses that value as well).
        if (environment.IsDevelopment())
        {
            services.AddKeyedSingleton<IHealingLlmProvider, MockHealingLlmProvider>(HealingProviderKind.Mock);
        }

        // One seam for every kind. The disabled adapter is the default, so an unselected provider answers with an error
        // result instead of reaching a network.
        services.AddTransient<IHealingLlmProvider>(ProviderFor);

        // Every attempt is recorded through the module's tenant Dapper path, so all three are scoped like the switch
        // store; the gateway reserves the period budget before it places a call, through the same seam.
        services.TryAddScoped<HealingLlmCallRecorder>();
        services.TryAddScoped<HealingLlmBudgetLedger>();
        services.TryAddScoped<HealingLlmTransactions>();
        services.TryAddScoped<HealingLlmGateway>();

        // The time budget lives in the pipeline, so the client timeout is infinite: a finite client timeout would abort
        // the whole pipeline — including a retry — before the per-attempt timeout inside it could ever fire. The
        // handler follows the Identity precedent: no redirect (a redirect would leave the configured endpoint) and a
        // connection lifetime that keeps DNS fresh in a client that lives as long as the host. The response buffer is
        // capped, so a runaway body fails the read instead of being buffered.
        services
            .AddHttpClient<HttpHealingLlmProvider>((provider, client) =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.MaxResponseContentBufferSize = HealingLlmHttpClients.MaxResponseBytes;

                // A base address that does not parse leaves the client unconfigured: the request then fails as an error
                // result instead of taking the host down on the first call. A booted host has a validated one.
                var options = provider.GetRequiredService<IOptions<HealingProviderOptions>>().Value;
                if (Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var baseAddress))
                {
                    client.BaseAddress = baseAddress;
                }
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })

            // Outermost: a request that is not for the configured endpoint is refused before a key is resolved for it.
            .AddHttpMessageHandler(provider => new HealingLlmEgressHandler(
                provider.GetRequiredService<IOptions<HealingProviderOptions>>().Value))

            // Next, so the key is read and presented once per call and a refused key is not retried by the
            // pipeline. The handler resolves it through the secret provider and keeps no copy.
            .AddHttpMessageHandler(provider => new HealingLlmApiKeyHandler(
                provider.GetRequiredService<ISecretProvider>(),
                provider.GetRequiredService<IOptions<HealingProviderOptions>>().Value))
            .AddResilienceHandler(HealingLlmHttpClients.Pipeline, (builder, context) =>
            {
                var options = context.ServiceProvider.GetRequiredService<IOptions<HealingLlmOptions>>().Value;

                // Order is outermost first: the total budget wraps the retry, and the per-attempt timeout sits inside
                // it, so a slow attempt is bounded on its own and the whole call is bounded on top.
                builder.AddTimeout(options.TotalTimeout);

                // The call is a POST, and the strategy itself has no method filter, so nothing is retried by default:
                // it is added only when a host opts in with a positive count (Polly's retry strategy requires at least
                // one attempt). Its ShouldHandle covers only the transient failures — HttpRequestException,
                // TimeoutRejectedException, 408/429/5xx — so a permanent status is never sent twice. Jitter has no
                // effect on a constant delay; it is set so a later exponential backoff is jittered without a second edit.
                if (options.MaxTransportRetries > 0)
                {
                    builder.AddRetry(new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = options.MaxTransportRetries,
                        Delay = TimeSpan.FromMilliseconds(200),
                        BackoffType = DelayBackoffType.Constant,
                        UseJitter = true,
                    });
                }

                builder.AddTimeout(options.AttemptTimeout);
            });
    }

    /// <summary>
    /// The adapter the settled kind names. A mock asked for outside Development has no registration to resolve, so the
    /// composition fails closed rather than running test scaffolding in production.
    /// </summary>
    /// <param name="services">The provider the adapter is resolved from.</param>
    /// <returns>The provider adapter the gateway calls.</returns>
    private static IHealingLlmProvider ProviderFor(IServiceProvider services)
    {
        var kind = services.GetRequiredService<IOptions<HealingProviderOptions>>().Value.Kind;

        return kind switch
        {
            HealingProviderKind.Http => services.GetRequiredService<HttpHealingLlmProvider>(),
            HealingProviderKind.Mock => services.GetRequiredKeyedService<IHealingLlmProvider>(HealingProviderKind.Mock),
            _ => new DisabledHealingLlmProvider(),
        };
    }
}
