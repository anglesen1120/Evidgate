using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Authoring.Application.Reports.Sections;

/// <summary>Registers the section builders the report registry serves; a new section is one more line here.</summary>
internal static class ReportSectionRegistration
{
    /// <summary>Adds the test plan, test case, traceability and execution builders; the builders hold no state, so they are singletons.</summary>
    /// <param name="services">The calling host's collection.</param>
    internal static IServiceCollection AddReportSectionBuilders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReportSectionBuilder, TestPlanSectionBuilder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReportSectionBuilder, TestCasesSectionBuilder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReportSectionBuilder, TraceabilitySectionBuilder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReportSectionBuilder, ExecutionSectionBuilder>());
        return services;
    }
}
