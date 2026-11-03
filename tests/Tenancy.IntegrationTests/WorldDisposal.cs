namespace Tenancy.IntegrationTests;

/// <summary>Disposal shared by the test worlds that own a private database.</summary>
internal static class WorldDisposal
{
    /// <summary>
    /// Disposes the client and then the hosts, before the database: the database is this world's to drop even when a
    /// host's own disposal throws, so a failed world never leaves a scratch database behind for the rest of the run.
    /// </summary>
    /// <param name="client">The world's HTTP client, disposed before its hosts.</param>
    /// <param name="owned">The private database, or null when the world runs on the shared database.</param>
    /// <param name="hosts">The API hosts the world started, disposed in the given order.</param>
    public static async ValueTask DisposeAsync(
        HttpClient client,
        DedicatedDatabase? owned,
        params IAsyncDisposable[] hosts)
    {
        try
        {
            client.Dispose();

            foreach (var host in hosts)
            {
                await host.DisposeAsync();
            }
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }
        }
    }
}
