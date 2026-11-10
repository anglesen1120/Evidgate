using Xunit;

[assembly: AssemblyFixture(typeof(Evidgate.Testing.LeanLibraryMetrics))]

namespace Evidgate.Testing;

/// <summary>
/// Switches the library meters (runtime, Npgsql, ASP.NET Core hosting) off for the process before any host
/// is built. A suite builds hundreds of API hosts, and every live meter provider receives every database
/// command's measurement; none of those providers is ever scraped, so the subscriptions only cost memory
/// and CPU. The catalogue tests that need them run in <c>Hosts.IntegrationTests</c>, which does not link this.
/// </summary>
public sealed class LeanLibraryMetrics
{
    /// <summary>Arms the switch for the lifetime of the test process.</summary>
    public LeanLibraryMetrics() =>
        Environment.SetEnvironmentVariable("EVIDGATE__TELEMETRY__LIBRARYMETRICS", "false");
}
