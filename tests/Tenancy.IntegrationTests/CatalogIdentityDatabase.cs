using Xunit;

namespace Tenancy.IntegrationTests;

// Resolver/annotation lookups and owner-role SQL need a dedicated database, not one per test;
// fresh organizations isolate each test's rows while the class shares its host.
/// <summary>One migrated database and one real host for a whole test class; each test still gets its own org.</summary>
public sealed class CatalogIdentityDatabase(PostgresTenancyFixture fixture) : IAsyncLifetime
{
    private DedicatedDatabase? _database;
    private TenancyApiFactory? _api;
    private HttpClient? _client;

    public string Name => _database?.Name ?? throw new InvalidOperationException("The class database has not started.");
    internal TenancyApiFactory Api => _api ?? throw new InvalidOperationException("The class host has not started.");
    public HttpClient Client => _client ?? throw new InvalidOperationException("The class client has not started.");

    public async ValueTask InitializeAsync()
    {
        var database = await fixture.CreateDedicatedDatabaseAsync();
        _database = database;
        try
        {
            _api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database.Name));
            _client = _api.CreateClient();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;

        var api = _api;
        _api = null;
        try
        {
            if (api is not null)
            {
                await api.DisposeAsync();
            }
        }
        finally
        {
            // The database is dropped even when the host's own disposal throws.
            var database = _database;
            _database = null;
            if (database is not null)
            {
                await database.DisposeAsync();
            }
        }
    }
}
