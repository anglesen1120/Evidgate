using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace Evidgate.Modules.Healing;

/// <summary>
/// Composition seam of the Healing module (08.6.3-T1, 00 §5.5): internal by design and reached through
/// <c>InternalsVisibleTo</c>.
/// </summary>
internal static class HealingModule
{
    /// <summary>Registers the module's services in the calling host.</summary>
    /// <param name="services">The host's collection.</param>
    /// <param name="configuration">The host configuration the <c>Healing</c> section is bound from (the V1 clamp).</param>
    public static IServiceCollection AddHealingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 08.6.3-T1 AC7: the V1 clamp — `Healing:Apply` (absent or false in every shipped configuration: V1 runs
        // shadow healing) is the only thing that lets `enabled` be set. A section of its own, like the Identity
        // module's `Identity:CustomRoles`.
        services.Configure<HealingOptions>(configuration.GetSection(HealingOptions.SectionName));
        services.TryAddSingleton<IHealingApplyPolicy, HealingApplyPolicy>();

        // The switch store is the module's only Dapper path: the tenant/project upsert and the three-row resolution
        // read (AC9). No EF write model exists — the row is configuration, written through one statement.
        services.TryAddScoped<HealingSwitchStore>();

        // 00 §5.5: the Contracts seam other modules resolve to ask "may healing act for this project?".
        services.TryAddScoped<IHealingAvailabilityResolver, HealingAvailabilityResolver>();

        // 08.6.3: the operator's only writer of the platform row, resolved by the operator ingress route.
        services.TryAddSingleton<HealingSystemSwitchWriter>();

        AddHealingLlmGateway(services);

        return services;
    }

    /// <summary>
    /// 08.5.1-T1: the provider-adapter seam and its gateway. The mock is the default <see cref="IHealingLlmProvider"/>
    /// because O4 (provider, region, egress) has not closed and no real provider call may run before it does (plan §11);
    /// the HTTP adapter is composed as a typed client a host resolves explicitly once that decision lands, so swapping it
    /// in is a registration, not a contract change.
    /// </summary>
    private static void AddHealingLlmGateway(IServiceCollection services)
    {
        // Defaults live in code: there is no configuration key for this slice.
        services.AddOptions<HealingLlmOptions>();

        services.TryAddSingleton<IHealingLlmProvider, MockHealingLlmProvider>();

        // Every attempt is recorded through the module's tenant Dapper path, so both are scoped like the switch store.
        services.TryAddScoped<HealingLlmCallRecorder>();
        services.TryAddScoped<HealingLlmGateway>();

        // The time budget lives in the pipeline, so the client timeout is infinite: a finite client timeout would abort
        // the whole pipeline — including a retry — before the per-attempt timeout inside it could ever fire.
        services
            .AddHttpClient<HttpHealingLlmProvider>((_, client) => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddResilienceHandler(HealingLlmHttpClients.Pipeline, (builder, context) =>
            {
                var options = context.ServiceProvider.GetRequiredService<IOptions<HealingLlmOptions>>().Value;

                // Order is outermost first: the total budget wraps the retry, and the per-attempt timeout sits inside
                // it, so a slow attempt is bounded on its own and the whole call is bounded on top (AC2).
                builder.AddTimeout(options.TotalTimeout);

                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = options.MaxTransportRetries,
                    Delay = TimeSpan.FromMilliseconds(200),
                    BackoffType = DelayBackoffType.Constant,
                });

                builder.AddTimeout(options.AttemptTimeout);
            });
    }
}
