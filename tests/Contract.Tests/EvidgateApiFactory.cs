using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Contract.Tests;

/// <summary>
/// The Api booted in process for the contract suites (01.3.2-T3, AC4): the same
/// composition the deployed host runs (<c>Program</c> → <c>ApiHost</c>), pinned to
/// Production so the JSON surface under test is the shipping one — the
/// <c>tests/Hosts.IntegrationTests</c> pattern, kept here so this project owns no
/// dependency on that assembly.
/// </summary>
/// <remarks>
/// The connection string points at a closed port on purpose: every path the
/// contract suites exercise (the 00 §9.2 envelope and the served document) is
/// answered before a handler reaches PostgreSQL. The database-backed cases stay
/// in <c>tests/Hosts.IntegrationTests</c>, which owns the container fixture —
/// this project stays Docker-free so <c>verify.sh</c> runs it everywhere.
/// </remarks>
internal sealed class EvidgateApiFactory : WebApplicationFactory<Program>
{
    /// <summary>A closed port, so a route that does reach the database fails fast instead of hanging.</summary>
    public const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2";

    /// <summary>Creates a factory over the dead connection string (or an explicit one).</summary>
    public EvidgateApiFactory(string? connectionString = null)
        => ConnectionString = connectionString ?? DeadConnectionString;

    /// <summary>The <c>EVIDGATE:Database:ConnectionString</c> the host starts with.</summary>
    public string ConnectionString { get; }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");

        // The response-vs-spec suite needs an anonymous JSON route whose bad body
        // still answers the 00 §9.2 envelope; the host owns no such route.
        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter, Evidgate.Testing.TransportProbeStartupFilter>());
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Database:ConnectionString"] = ConnectionString,
                // 02.6.1-T1.4 (AC5): the boot-time isolation self-check defaults to
                // Required and would refuse a host that cannot read the catalogue — the
                // dead connection string above is deliberate, because every path the
                // contract suites exercise is answered before a handler reaches
                // PostgreSQL. The refusal itself is proven against a real database and
                // a real process in tests/Hosts.IntegrationTests.
                ["EVIDGATE:Isolation:SelfCheck"] = "Disabled",
            }));
    }
}
