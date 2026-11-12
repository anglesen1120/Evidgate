using System.Collections.Concurrent;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Tenancy.IntegrationTests;

internal static class TaskWorkerTestServices
{
    private static readonly ConcurrentDictionary<string, string> Connections = new(StringComparer.Ordinal);

    internal static string Register(string appConnectionString, string workerConnectionString)
    {
        Connections[appConnectionString] = workerConnectionString;
        return appConnectionString;
    }

    /// <summary>
    /// Gives a host the worker-role source its tests resolve the dispatcher over, built from the worker credential
    /// the fixture registered for this connection string.
    /// </summary>
    /// <param name="services">The host's service collection (the factory's test-service hook).</param>
    /// <param name="appConnectionString">The host's app connection string, the key the fixture registered.</param>
    /// <param name="required">
    /// True for a host whose test drives the queue's own hop. The Api composes neither the queue nor the worker
    /// credential, so such a host asks for the source explicitly; every other host keeps the early return below
    /// and carries no worker source at all.
    /// </param>
    internal static void Configure(IServiceCollection services, string appConnectionString, bool required = false)
    {
        if (!required && !services.Any(descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource)
            && descriptor.IsKeyedService && Equals(descriptor.ServiceKey, TaskWorkerDataSource.Key)))
        {
            return;
        }

        var workerConnectionString = Connections[appConnectionString];
        services.AddKeyedSingleton<NpgsqlDataSource>(TaskWorkerDataSource.Key, (_, _) =>
            new NpgsqlDataSourceBuilder(workerConnectionString) { Name = TaskWorkerDataSource.DataSourceName }.Build());
    }
}
