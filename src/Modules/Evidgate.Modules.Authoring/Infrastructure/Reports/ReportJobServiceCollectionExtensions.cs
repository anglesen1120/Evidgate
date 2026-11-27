using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Authoring.Application.Reports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Authoring.Infrastructure.Reports;

/// <summary>Registers the report build job.</summary>
internal static class ReportJobServiceCollectionExtensions
{
    /// <summary>
    /// Registers the task handler that claims <c>authoring_test_report_build</c> tasks and the scoped job it runs. Only
    /// the Worker composes it; the Api composes the registry and the reads through <c>AddAuthoringModule</c>.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    internal static IServiceCollection AddAuthoringReportJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<TestReportBuildJob>();
        services.AddTaskHandler<ReportJobPayload, TestReportTaskHandler>();

        return services;
    }
}
