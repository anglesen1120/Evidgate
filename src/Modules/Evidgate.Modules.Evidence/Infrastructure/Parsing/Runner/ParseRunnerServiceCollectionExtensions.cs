using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Evidence.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

/// <summary>
/// Composition seam of the out-of-process parse runner (06.3.6-T1.3/T1.4, 06.3.6 §7), kept out of
/// <see cref="Evidgate.Modules.Evidence.EvidenceModule"/> because the capability it registers is <em>spawning processes</em>: only the
/// Worker may hold it, and the Api must never be able to resolve <see cref="IParseRunner"/> at all
/// (06.3.6 review, security finding: the Api process must not carry a process-spawn capability). Its one consumer,
/// the <c>artifact_parse</c> task handler, is registered here for the same reason: a handler is resolvable where its
/// runner is, so the Api can neither resolve the runner nor claim the task.
/// <para>
/// <c>EVIDGATE__PARSE__OUTOFPROCESS</c> decides registration, not a runtime branch: with the flag off neither the
/// runner nor the handler is registered and the in-process <c>IParsing</c> path is the only one — the rollback
/// 06.3.6 §11 names. The options are bound in both hosts (<c>EvidenceModule</c>, because <c>complete</c> reads the
/// switch to schedule the task) so the flag-off composition still exposes the documented defaults (<c>ChildPath</c>,
/// <c>CpuSeconds</c>, …) and the flag-on runner resolves them from configuration; they carry no data annotations
/// and are not in <c>WorkerHost.ValidateOptions</c>, so binding them validates nothing by itself.
/// </para>
/// </summary>
internal static class ParseRunnerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the runner options and, with the flag on, the one out-of-process implementation together with the one
    /// consumer of it — the <c>artifact_parse</c> task handler (06.3.6-T1.4).
    /// </summary>
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

            // 06.3.6-T1.4: the handler travels with the runner, not with the module. A task handler is registered by
            // its own type and the consumer claims exactly the types it holds, so the Api — which composes the module
            // but never this extension — can neither resolve the runner nor claim or run an `artifact_parse` row. With
            // the flag off neither is registered and such rows stay `queued` for the in-process rollback (06.3.6 §11).
            services.AddTaskHandler<ParseArtifactPayload, ParseArtifactTaskHandler>();
        }

        return services;
    }
}
