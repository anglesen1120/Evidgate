using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Persistence.IntegrationTests;

/// <summary>
/// The production wiring under test (01.4.2-T1.1/T1.3): an app-role
/// <c>NpgsqlDataSource</c>, <c>AddEvidgateTenancy</c> and
/// <c>AddEvidgateDbContext</c>, exactly as <c>Evidgate.Api</c>/<c>Evidgate.Worker</c>
/// register the single data-access path.
/// </summary>
internal sealed class TenantTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private TenantTestHost(NpgsqlDataSource dataSource, ServiceProvider provider)
    {
        DataSource = dataSource;
        _provider = provider;
        UnscopedOptions = new DbContextOptionsBuilder<ProbeDbContext>()
            .UseNpgsql(dataSource)
            .AddInterceptors(provider.GetRequiredService<TenantCommandInterceptor>())
            .UseTenantModel(typeof(ProbeDbContext).Assembly)
            .Options;
    }

    internal NpgsqlDataSource DataSource { get; }

    internal ITenantScopeFactory Scopes => _provider.GetRequiredService<ITenantScopeFactory>();

    internal ITenantDb Db => _provider.GetRequiredService<ITenantDb>();

    internal ITenantDbContextFactory<ProbeDbContext> Contexts =>
        _provider.GetRequiredService<ITenantDbContextFactory<ProbeDbContext>>();

    /// <summary>
    /// The options of a context that borrows its own connection from the data
    /// source instead of the scope's: the AC1 case the interceptor has to catch.
    /// </summary>
    internal DbContextOptions<ProbeDbContext> UnscopedOptions { get; }

    /// <summary>Starts the host over one test database; <paramref name="maximumPoolSize"/> bounds the app-role pool.</summary>
    internal static TenantTestHost Start(
        PostgresPersistenceFixture fixture,
        string database,
        int maximumPoolSize = 1)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var dataSource = fixture.OpenAppDataSource(database, maximumPoolSize);
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddEvidgateTenancy();
        services.AddEvidgateDbContext<ProbeDbContext>(typeof(ProbeDbContext).Assembly);

        return new TenantTestHost(dataSource, services.BuildServiceProvider());
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await DataSource.DisposeAsync();
    }
}
