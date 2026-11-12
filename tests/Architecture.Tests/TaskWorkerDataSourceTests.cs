using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// The worker data source is the one place the queue role's credential is enforced:
/// the task machinery resolves it lazily, so only a Worker (or a test that starts the
/// queue) can fail, and every failure names the configuration key without echoing the
/// value.
/// </summary>
public sealed class TaskWorkerDataSourceTests
{
    private const string Marker = "worker-test-marker";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Worker_Connection_MissingOrEmpty_Refuses_NamingTheKey(string? value)
    {
        using var provider = Provider(values =>
            values[WorkerKey] = value);

        var failure = Should.Throw<InvalidOperationException>(
            () => provider.GetRequiredKeyedService<NpgsqlDataSource>(TaskWorkerDataSource.Key));

        failure.Message.ShouldContain("EVIDGATE__TASKS__WORKERCONNECTIONSTRING");
    }

    [Fact]
    public void Worker_Connection_WrongRole_Refuses_NamingTheKey_AndNeverTheValue()
    {
        using var provider = Provider(values =>
            values[WorkerKey] = Connection("evidgate_app"));

        var failure = Should.Throw<InvalidOperationException>(
            () => provider.GetRequiredKeyedService<NpgsqlDataSource>(TaskWorkerDataSource.Key));

        failure.Message.ShouldContain("EVIDGATE__TASKS__WORKERCONNECTIONSTRING");
        failure.Message.ShouldContain("evidgate_worker");
        failure.Message.ShouldNotContain(Marker, Case.Sensitive);
    }

    [Fact]
    public void Worker_Connection_Malformed_Refuses_NamingTheKey_AndNeverTheValue()
    {
        using var provider = Provider(values =>
            values[WorkerKey] = "Host=127.0.0.1;Port=not-a-port;Username=evidgate_worker;Password=" + Marker);

        var failure = Should.Throw<InvalidOperationException>(
            () => provider.GetRequiredKeyedService<NpgsqlDataSource>(TaskWorkerDataSource.Key));

        failure.Message.ShouldContain("EVIDGATE__TASKS__WORKERCONNECTIONSTRING");
        failure.Message.ShouldNotContain(Marker, Case.Sensitive);
    }

    [Fact]
    public void Worker_Connection_CorrectRole_Resolves_WithItsOwnPoolName()
    {
        using var provider = Provider(values =>
            values[WorkerKey] = Connection(TaskWorkerDataSource.Role));

        using var source = provider.GetRequiredKeyedService<NpgsqlDataSource>(TaskWorkerDataSource.Key);

        new NpgsqlConnectionStringBuilder(source.ConnectionString).Username.ShouldBe(TaskWorkerDataSource.Role);
    }

    /// <summary>
    /// The Api's option registration alone never builds the worker source: a process that
    /// never runs the queue needs no worker credential.
    /// </summary>
    [Fact]
    public void TaskOptions_Registration_Alone_RegistersNoWorkerSource()
    {
        using var provider = new ServiceCollection()
            .AddEvidgateTaskOptions(Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)))
            .BuildServiceProvider();

        provider.GetKeyedService<NpgsqlDataSource>(TaskWorkerDataSource.Key).ShouldBeNull();
    }

    private static string WorkerKey => $"{TasksOptions.SectionName}:WorkerConnectionString";

    private static string Connection(string username) => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1",
        Port = 1,
        Database = "evidgate",
        Username = username,
        Password = Marker,
        Timeout = 2,
    }.ConnectionString;

    private static IConfiguration Configuration(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceProvider Provider(Action<Dictionary<string, string?>> configure)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        configure(values);
        return new ServiceCollection().AddEvidgateTasks(Configuration(values)).BuildServiceProvider();
    }
}
