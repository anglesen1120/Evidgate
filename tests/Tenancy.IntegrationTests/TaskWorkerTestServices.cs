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

    internal static void Configure(IServiceCollection services, string appConnectionString)
    {
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource)
            && descriptor.IsKeyedService && Equals(descriptor.ServiceKey, TaskWorkerDataSource.Key)))
        {
            return;
        }

        var workerConnectionString = Connections[appConnectionString];
        services.AddKeyedSingleton<NpgsqlDataSource>(TaskWorkerDataSource.Key, (_, _) =>
            new NpgsqlDataSourceBuilder(workerConnectionString) { Name = TaskWorkerDataSource.DataSourceName }.Build());
    }
}
