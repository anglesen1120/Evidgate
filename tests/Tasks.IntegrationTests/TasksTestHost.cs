using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.5.1-T1/T6.2 test host: the production mechanism (<c>AddEvidgateTasks</c> plus
/// <c>AddEvidgateTenancy</c>, exactly what <c>Evidgate.Worker</c> composes) over one
/// test database, with a recorder the probe handlers write to.
/// </summary>
internal sealed class TasksTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private TasksTestHost(NpgsqlDataSource dataSource, ServiceProvider provider)
    {
        DataSource = dataSource;
        _provider = provider;
    }

    /// <summary>The app-role data source the mechanism runs on.</summary>
    internal NpgsqlDataSource DataSource { get; }

    /// <summary>What the suite's task handlers saw.</summary>
    internal TaskRunRecorder Recorder => _provider.GetRequiredService<TaskRunRecorder>();

    /// <summary>What the blocking handlers saw; a second host shares it through <c>services</c>.</summary>
    internal BlockingTaskProbe Probe => _provider.GetRequiredService<BlockingTaskProbe>();

    internal ITaskClaimer Claimer => _provider.GetRequiredService<ITaskClaimer>();

    internal ITaskQueueWriter Writer => _provider.GetRequiredService<ITaskQueueWriter>();

    internal ITaskLeaseWriter Leases => _provider.GetRequiredService<ITaskLeaseWriter>();

    internal TaskConsumer Consumer => _provider.GetRequiredService<TaskConsumer>();

    internal TaskScheduleRunner Runner => _provider.GetRequiredService<TaskScheduleRunner>();

    internal IScheduleRegistry Schedules => _provider.GetRequiredService<IScheduleRegistry>();

    internal ITaskHandlerRegistry Handlers => _provider.GetRequiredService<ITaskHandlerRegistry>();

    /// <summary>
    /// Starts the host over one test database. <paramref name="configuration"/>
    /// overrides the task options (the defaults keep the poll interval and the
    /// backoff short so a test does not wait), and <paramref name="services"/> adds
    /// a test's own schedules or handlers.
    /// </summary>
    internal static TasksTestHost Start(
        PostgresTasksFixture fixture,
        string database,
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? services = null,
        int maximumPoolSize = 4)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{TasksOptions.SectionName}:PollIntervalMilliseconds"] = "50",
            [$"{TasksOptions.SectionName}:BatchSize"] = "50",
            [$"{TasksOptions.SectionName}:VisibilityTimeoutSeconds"] = "300",
            [$"{TasksOptions.SectionName}:BackoffBaseSeconds"] = "1",
            [$"{TasksOptions.SectionName}:BackoffMaxSeconds"] = "4",
            [$"{TasksOptions.SectionName}:ScheduleLookbackSeconds"] = "300",
            [$"{TasksOptions.SectionName}:SweepConnectionString"] = fixture.WorkerSchedConnectionString(database),
            [$"{TasksOptions.SectionName}:WorkerConnectionString"] = fixture.WorkerConnectionString(database),
        };

        if (configuration is not null)
        {
            foreach (var pair in configuration)
            {
                values[pair.Key] = pair.Value;
            }
        }

        var dataSource = fixture.OpenAppDataSource(database, maximumPoolSize);
        var collection = new ServiceCollection();
        collection.AddSingleton(dataSource);
        collection.AddLogging();
        collection.AddEvidgateTenancy();
        collection.AddEvidgateTasks(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        // Cases shorten the lease to expire it on command; the purge budget guard is not under test here.
        collection.WithoutPurgeLeaseGuard();
        collection.AddSingleton<TaskRunRecorder>();
        collection.AddSingleton<BlockingTaskProbe>();
        collection.AddTaskHandler<RecordingPayload, RecordingTaskHandler>();
        collection.AddTaskHandler<TenantPayload, TenantTaskHandler>();
        collection.AddTaskHandler<BlockingPayload, BlockingTaskHandler>();

        services?.Invoke(collection);

        return new TasksTestHost(dataSource, collection.BuildServiceProvider());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await DataSource.DisposeAsync();
    }
}
