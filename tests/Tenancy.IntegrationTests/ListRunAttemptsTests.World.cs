using System.Net.Http.Headers;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The world a case arranges: one disposable host on one migrated database, with a fresh org and project created
/// through the real registry routes. Split from the cases so the file that holds them stays within the size limit.
/// </summary>
public sealed partial class ListRunAttemptsTests
{
    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project)
        : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>
    /// One migrated database — the shared one unless <paramref name="database"/> names its own — with a fresh org
    /// and project created through the real registry routes.
    /// </summary>
    private async Task<World> StartAsync(string? database = null)
    {
        database ??= await _fixture.SharedDatabaseAsync();
        var api = _hosts.RealResolver(database);
        var client = api.CreateClient();

        try
        {
            var user = Guid.CreateVersion7();
            var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
            var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

            return new World(api, client, database, user, org, project);
        }
        catch
        {
            // A world that never returns owns nothing: a failed arrange must not leave the host's connections
            // behind for the assembly's database cleanup.
            client.Dispose();
            await api.DisposeAsync();
            throw;
        }
    }
}
