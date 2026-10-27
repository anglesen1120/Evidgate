using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

/// <summary>
/// Composition seam of the out-of-process parse runner (06.3.6-T1.3, 06.3.6 §7), kept out of
/// <see cref="Evidgate.Modules.Evidence.EvidenceModule"/> because the capability it registers is <em>spawning processes</em>: only the
/// Worker may hold it, and the Api must never be able to resolve <see cref="IParseRunner"/> at all
/// (06.3.6 review, security finding: the Api process must not carry a process-spawn capability).
/// <para>
/// <c>EVIDGATE__PARSE__OUTOFPROCESS</c> decides registration, not a runtime branch: with the flag off nothing is
/// registered and the in-process <c>IParsing</c> path is the only one — the rollback 06.3.6 §11 names. The options
/// are bound either way so the flag-off composition still exposes the documented defaults (<c>ChildPath</c>,
/// <c>CpuSeconds</c>, …) and the flag-on runner resolves them from configuration; they carry no data annotations
/// and are not in <c>WorkerHost.ValidateOptions</c>, so binding them validates nothing by itself.
/// </para>
/// </summary>
internal static class ParseRunnerServiceCollectionExtensions
{
    /// <summary>Registers the runner options and, with the flag on, the one out-of-process implementation.</summary>
    /// <param name="services">The Worker's service collection.</param>
    /// <param name="configuration">
    /// The Worker's configuration, read only for the switch that decides <em>what</em> is registered
    /// (<c>EVIDGATE__PARSE__OUTOFPROCESS</c>); every other runner setting is bound lazily through
    /// <c>BindConfiguration</c>.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddParseRunner(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ParseRunnerOptions>().BindConfiguration(ParseRunnerOptions.SectionName);

        if (configuration.GetValue(
            $"{ParseRunnerOptions.SectionName}:{nameof(ParseRunnerOptions.OutOfProcess)}",
            defaultValue: false))
        {
            services.TryAddSingleton<IParseRunner, ChildProcessParseRunner>();
        }

        return services;
    }
}
